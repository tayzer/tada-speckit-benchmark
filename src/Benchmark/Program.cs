using Benchmark.Claude;
using Benchmark.Cli;
using Benchmark.Config;
using Benchmark.DryRun;
using Benchmark.Evaluation;
using Benchmark.Human;
using Benchmark.Reporting;
using Benchmark.Runs;
using Benchmark.Telemetry;
using Benchmark.Util;
using Benchmark.Workspace;

namespace Benchmark;

public static class Program
{
    public const string Usage = """
        bench <command> [options]              (default --experiment experiments/checkout-retry)

        Everyday use
          run <workflow>          Fresh workspace, open Claude Code with the task, and when you exit:
                                  export chat + token usage, count human inputs, run hidden tests,
                                  update reports/latest/comparison.md. Workflows: speckit, tada
          resume <run-id>         Continue a run you left open
          finish <run-id>         Finish a run you left open without resuming it
          compare                 Rebuild the comparison report from all finished runs
          status                  List runs
          doctor                  Check tools, versions, isolation and configuration (no model calls)

        Advanced
          prepare-base | prepare --workflow <id> [--verify-activation] | verify-activation --run <id>
          start --run <id> --mode interactive|managed [--resume]
          continue --run <id> (--message <text> | --message-file <f> | --decision <D#>) [--kind <kind>]
          import --run <id> --session <uuid>
          finish --run <id> --no-seal           Finish without sealing, evaluation or report
          evaluate --run <id> [--allow-draft] [--mutation-control] | evaluate --control <patch|baseline>
          compare [--runs a,b,...] [--out <dir>]
          review-template --run <id> | verify --run <id> | decision list | dry-run [--out <dir>]
          timer / interaction / answer         Optional manual human-effort logging (not needed: run
                                               derives human inputs and waiting time from the transcript)
          init | validate
          run options: --skip-activation-check (skip the ~$0.01 check that the workflow loaded)
        """;

    public static async Task<int> Main(string[] argv)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var args = new Args(argv);
        if (args.Positional.Count == 0 || args.Flag("help")) { Console.WriteLine(Usage); return args.Positional.Count == 0 ? 1 : 0; }
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); Console.Error.WriteLine("Cancellation requested; stopping child process and recording partial evidence ..."); };
        try
        {
            return await RunAsync(args, cts.Token);
        }
        catch (BenchmarkException ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 2;
        }
    }

    private static void Log(string message) => Console.WriteLine(message);

    public static async Task<int> RunAsync(Args args, CancellationToken ct)
    {
        var command = args.Positional[0].ToLowerInvariant();
        if (command == "dry-run")
        {
            var outDir = args.Get("out") ?? Path.Combine(Path.GetTempPath(), "benchmark-dry-run-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
            var harnessRoot = ExperimentConfig.FindHarnessRoot(Directory.GetCurrentDirectory());
            var result = await DryRunner.RunAsync(harnessRoot, outDir, Log);
            Console.WriteLine($"Dry-run report: {result.MarkdownPath}");
            return 0;
        }

        var config = ExperimentConfig.Load(args.Get("experiment") ?? FindDefaultExperiment());
        var store = new RunStore(config.DataRoot);
        var pricing = File.Exists(config.ResolveHarnessPath(config.PricingFile)) ? PricingTable.Load(config.ResolveHarnessPath(config.PricingFile)) : null;
        var register = () => DecisionRegister.Load(config.ResolveExperimentPath(config.DecisionsFile));

        string RunIdArg() => args.Get("run") ?? args.Positional.ElementAtOrDefault(1) ?? throw new BenchmarkException("Give the run id, e.g. `bench finish <run-id>` (see `bench status`).");

        switch (command)
        {
            case "run":
                {
                    var workflow = args.Positional.ElementAtOrDefault(1) ?? args.Get("workflow")
                        ?? throw new BenchmarkException("Which workflow? `bench run speckit` or `bench run tada`.");
                    return await SimpleRun.RunAsync(config, store, pricing, workflow, !args.Flag("skip-activation-check"), Log, ct);
                }

            case "resume":
                return await SimpleRun.ResumeAsync(config, store, pricing, RunIdArg(), Log, ct);

            case "init":
                store.EnsureLayout();
                Console.WriteLine($"Experiment {config.ExperimentId}: {config.Description}");
                Console.WriteLine($"Data root (evidence + workspaces, outside the repo): {store.DataRoot}");
                Console.WriteLine($"Model {config.Agent.Model}, effort {config.Agent.Effort}, CLI {config.Agent.CliVersion}");
                Console.WriteLine("Workflows: " + string.Join(", ", config.Workflows.Select(w => $"{w.Id} ({w.Version})")));
                return PrintErrors(ConfigValidator.Validate(config));

            case "validate":
                return PrintErrors(ConfigValidator.Validate(config));

            case "doctor":
                return await Doctor.RunAsync(config, store);

            case "prepare-base":
                {
                    var errors = ConfigValidator.Validate(config);
                    if (errors.Count > 0) return PrintErrors(errors);
                    var b = await BasePreparer.PrepareAsync(config, store, Log);
                    Console.WriteLine($"Base commit {b.BaseCommit}; build {(b.BuildSucceeded ? "ok" : "FAILED")}");
                    foreach (var t in b.BaselineTests) Console.WriteLine($"  {t.Project}: {t.Passed} passed, {t.Failed} failed, {t.Skipped} skipped{(t.InfrastructureError is null ? "" : " — " + t.InfrastructureError)}");
                    foreach (var issue in b.KnownIssues) Console.WriteLine("  known issue: " + issue);
                    return b.BuildSucceeded ? 0 : 1;
                }

            case "prepare":
                {
                    var run = await WorkspaceManager.PrepareRunAsync(config, args.Require("workflow"), store, Log);
                    if (args.Flag("verify-activation")) await ActivationProbe.RunAsync(config, store, run, Log);
                    Console.WriteLine(run.RunId);
                    return run.Setup.StaticActivationPassed ? 0 : 1;
                }

            case "verify-activation":
                {
                    var run = store.Load(args.Require("run"));
                    return await ActivationProbe.RunAsync(config, store, run, Log) ? 0 : 1;
                }

            case "start":
                {
                    var run = store.Load(args.Require("run"));
                    var mode = (args.Get("mode") ?? "interactive").ToLowerInvariant();
                    var resume = args.Flag("resume");
                    if (mode == "interactive")
                    {
                        var inv = await SessionExecutor.StartInteractiveAsync(config, store, run, resume, ct);
                        Console.WriteLine($"Session ended (exit {inv.ExitCode}, {inv.DurationSeconds / 60:0.0} min). Run `benchmark finish --run {run.RunId}` when the workflow is complete, or `start --resume` to continue.");
                        return 0;
                    }
                    if (mode != "managed") throw new BenchmarkException("--mode must be interactive or managed.");
                    if (resume) throw new BenchmarkException("Use `benchmark continue` to send follow-up messages in managed mode.");
                    var managed = await SessionExecutor.RunManagedAsync(config, store, run, run.InitialMessage, "initial-task", resume: false, ct);
                    return managed.ExitCode == 0 ? 0 : 1;
                }

            case "continue":
                {
                    var run = store.Load(args.Require("run"));
                    var log = new HumanLog(store.EvidencePath(run.RunId, "human-events.jsonl"));
                    var kind = Enum.Parse<InteractionKind>((args.Get("kind") ?? "corrective-prompt").Replace("-", ""), ignoreCase: true);
                    var text = args.Get("message") ?? (args.Get("message-file") is { } f ? File.ReadAllText(f) : null);
                    var (message, k) = SessionExecutor.FollowUp(register(), log, text, args.Get("decision"), kind);
                    var inv = await SessionExecutor.RunManagedAsync(config, store, run, message, HumanLog.KebabName(k), resume: true, ct);
                    return inv.ExitCode == 0 ? 0 : 1;
                }

            case "import":
                {
                    var run = store.Load(args.Require("run"));
                    store.EnsureWritable(run.RunId);
                    if (run.Invocations.Count > 0) throw new BenchmarkException("Run already has harness-launched invocations.");
                    var session = args.Require("session");
                    if (!Guid.TryParse(session, out _)) throw new BenchmarkException("--session must be a UUID.");
                    var (main, _) = ClaudeLaunch.TranscriptPaths(run.WorkspacePath, session);
                    if (!File.Exists(main)) throw new BenchmarkException($"Transcript not found: {main}");
                    var parsed = ClaudeEventParser.ParseTranscripts(main, []);
                    run.SessionId = session;
                    run.Mode = ExecutionMode.Interactive;
                    run.Status = RunStatus.Running;
                    run.Deviations.Add("Session launched outside the harness and imported; launch flags were not harness-controlled. Verify model/effort/permissions manually.");
                    run.Invocations.Add(new InvocationRecord
                    {
                        InvocationId = "001", Mode = ExecutionMode.Interactive,
                        StartedAt = parsed.FirstTimestamp ?? DateTimeOffset.UtcNow, EndedAt = parsed.LastTimestamp ?? DateTimeOffset.UtcNow,
                        MessageKind = "imported",
                    });
                    store.Save(run);
                    Console.WriteLine($"Imported session {session} ({parsed.Messages.Count} API responses). Now run `benchmark finish --run {run.RunId}`.");
                    return 0;
                }

            case "finish":
                {
                    var run = store.Load(RunIdArg());
                    if (!args.Flag("no-seal")) return await SimpleRun.CompleteAsync(config, store, pricing, run, Log, ct);
                    var t = await RunFinisher.FinishAsync(config, store, run, pricing, seal: !args.Flag("no-seal"), Log);
                    Console.WriteLine($"Tokens: in {t.Usage.Totals.Input:N0}, out {t.Usage.Totals.Output:N0}, cache read {t.Usage.Totals.CacheRead:N0}, cache write {t.Usage.Totals.CacheCreation:N0}; CLI cost {(t.Usage.CliReportedCostUsd is null ? "unavailable" : "$" + t.Usage.CliReportedCostUsd.Value.ToString("0.00"))}; complete={t.Usage.Complete}");
                    foreach (var w in t.Usage.Warnings) Console.WriteLine("  telemetry warning: " + w);
                    foreach (var v in t.Activity.IsolationViolations) Console.WriteLine($"  ISOLATION VIOLATION: {v.Tool} referenced {v.Root}");
                    return 0;
                }

            case "timer":
                {
                    var action = args.Positional.ElementAtOrDefault(1) ?? throw new BenchmarkException("timer start|stop");
                    var run = store.Load(args.Require("run"));
                    store.EnsureWritable(run.RunId);
                    var activity = Enum.Parse<ActivityKind>(args.Require("activity"), ignoreCase: true);
                    var log = new HumanLog(store.EvidencePath(run.RunId, "human-events.jsonl"));
                    var open = log.OpenTimers();
                    if (action == "start" && open.Contains(activity)) throw new BenchmarkException($"Timer '{activity}' already running.");
                    if (action == "stop" && !open.Contains(activity)) throw new BenchmarkException($"Timer '{activity}' is not running.");
                    log.Append(new HumanEvent { Timestamp = DateTimeOffset.UtcNow, Kind = action == "start" ? InteractionKind.TimerStart : InteractionKind.TimerStop, Activity = activity, Note = args.Get("note") });
                    Console.WriteLine($"{activity} timer {action}ed at {DateTimeOffset.Now:T}.");
                    return 0;
                }

            case "interaction":
                {
                    var run = store.Load(args.Require("run"));
                    store.EnsureWritable(run.RunId);
                    var kind = Enum.Parse<InteractionKind>(args.Require("kind").Replace("-", ""), ignoreCase: true);
                    var decisionId = args.Get("decision");
                    if (decisionId is not null) register().Get(decisionId);
                    var minutes = args.Get("minutes") is { } m ? double.Parse(m, System.Globalization.CultureInfo.InvariantCulture) : (double?)null;
                    var activity = args.Get("activity") is { } a ? Enum.Parse<ActivityKind>(a, true) : (ActivityKind?)null;
                    new HumanLog(store.EvidencePath(run.RunId, "human-events.jsonl")).Append(new HumanEvent
                    {
                        Timestamp = DateTimeOffset.UtcNow, Kind = kind, DecisionId = decisionId, Note = args.Get("note"),
                        ManualSeconds = minutes * 60, Activity = activity,
                    });
                    Console.WriteLine($"Recorded {HumanLog.KebabName(kind)} for {run.RunId}.");
                    return 0;
                }

            case "answer":
                {
                    var run = store.Load(args.Require("run"));
                    store.EnsureWritable(run.RunId);
                    var d = register().Get(args.Require("decision"));
                    if (d.Answer is null) throw new BenchmarkException($"Decision {d.Id} has no owner-approved answer yet.");
                    if (d.Status != "approved") Console.Error.WriteLine($"WARNING: decision {d.Id} is '{d.Status}', not approved; this run will be flagged.");
                    new HumanLog(store.EvidencePath(run.RunId, "human-events.jsonl")).Append(new HumanEvent
                    {
                        Timestamp = DateTimeOffset.UtcNow, Kind = InteractionKind.ClarificationAnswer, DecisionId = d.Id, Note = d.AnswerSha256[..12],
                    });
                    Console.WriteLine("Paste exactly this answer into the session:\n");
                    Console.WriteLine(d.Answer);
                    return 0;
                }

            case "decision":
                {
                    var path = config.ResolveExperimentPath(config.DecisionsFile);
                    var reg = register();
                    if (args.Positional.ElementAtOrDefault(1) == "add")
                    {
                        var id = args.Require("id");
                        if (reg.Decisions.Any(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) throw new BenchmarkException($"Decision {id} exists.");
                        reg.Decisions.Add(new Decision { Id = id, Topic = args.Get("topic") ?? "", Question = args.Require("question"), Status = "proposed", AddedDuringRun = args.Get("run") });
                        Json.Write(path, reg);
                        Console.WriteLine($"Added {id} as 'proposed'. The benchmark owner must write and approve the answer in {path}.");
                        return 0;
                    }
                    foreach (var x in reg.Decisions) Console.WriteLine($"{x.Id} [{x.Status}] {x.Topic}: {x.Question}\n    answer: {x.Answer ?? "(undecided)"}");
                    return 0;
                }

            case "evaluate":
                {
                    var control = args.Get("control");
                    EvaluationRecord e;
                    if (control is not null)
                        e = await Evaluator.EvaluateAsync(config, store, null, control == "baseline" ? null : control, args.Flag("allow-draft"), false, Log);
                    else
                    {
                        var run = store.Load(args.Require("run"));
                        e = await Evaluator.EvaluateAsync(config, store, run, null, args.Flag("allow-draft"), args.Flag("mutation-control"), Log);
                    }
                    var h = e.Hidden;
                    Console.WriteLine($"Builds: {e.ImplementationBuilds}. Hidden: {h?.Passed} pass, {h?.Failed} fail, {h?.Indeterminate} indeterminate. Regressions: {e.Regressions.Count}.");
                    foreach (var t in h?.Tests ?? []) Console.WriteLine($"  {t.Outcome,-13} {t.Name}{(t.Cause is FailureCause.None or FailureCause.Behaviour ? "" : $" [{t.Cause}]")}");
                    return 0;
                }

            case "compare":
                {
                    // PowerShell passes an unquoted `--runs a,b` as two arguments ("a" "b"), so extra positional
                    // run ids after `compare` are accepted too.
                    var runs = args.Get("runs");
                    var ids = runs is null ? null : runs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Concat(args.Positional.Skip(1)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    if (runs is null && args.Positional.Count > 1) throw new BenchmarkException("Give run ids with --runs, e.g. `bench compare --runs 'a,b'`.");
                    Console.WriteLine("Report: " + await SimpleRun.WriteComparisonAsync(config, store, ids, args.Get("out")));
                    return 0;
                }

            case "review-template":
                {
                    var run = store.Load(args.Require("run"));
                    var path = Path.Combine(store.DataRoot, "reviews", run.RunId + ".json");
                    if (File.Exists(path)) throw new BenchmarkException($"Review already exists: {path}");
                    Json.Write(path, new EngineeringReview
                    {
                        RunId = run.RunId,
                        Scores = Rubric.Dimensions.ToDictionary(d => d, _ => (int?)null),
                        Notes = Rubric.Dimensions.ToDictionary(d => d, _ => ""),
                    });
                    Console.WriteLine($"Fill in {path} (scores 1-5, see docs/engineering-quality-rubric.md). Review diff: {store.EvidencePath(run.RunId, "changes", "final.patch")}");
                    return 0;
                }

            case "verify":
                {
                    var problems = store.Verify(args.Require("run"));
                    foreach (var p in problems) Console.WriteLine(p);
                    Console.WriteLine(problems.Count == 0 ? "Evidence intact." : $"{problems.Count} problem(s).");
                    return problems.Count == 0 ? 0 : 1;
                }

            case "status":
                foreach (var r in store.All())
                    Console.WriteLine($"{r.RunId,-40} {r.WorkflowId,-10} {r.Status,-9} {r.Mode?.ToString() ?? "-",-12} invocations={r.Invocations.Count} sealed={store.IsSealed(r.RunId)}{(r.Synthetic ? " SYNTHETIC" : "")}");
                return 0;

            default:
                Console.Error.WriteLine($"Unknown command '{command}'.\n{Usage}");
                return 1;
        }
    }

    private static string FindDefaultExperiment()
    {
        var root = ExperimentConfig.FindHarnessRoot(Directory.GetCurrentDirectory());
        return Path.Combine(root, "experiments", "checkout-retry");
    }

    private static int PrintErrors(List<string> errors)
    {
        foreach (var e in errors) Console.Error.WriteLine("config error: " + e);
        if (errors.Count == 0) Console.WriteLine("Configuration valid.");
        return errors.Count == 0 ? 0 : 1;
    }
}

public static class Rubric
{
    public static readonly string[] Dimensions =
    [
        "maintainability", "unnecessary-complexity", "architectural-consistency", "error-handling", "test-quality", "security-and-reliability",
    ];
}
