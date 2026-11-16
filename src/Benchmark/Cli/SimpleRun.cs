using Benchmark.Claude;
using Benchmark.Config;
using Benchmark.Evaluation;
using Benchmark.Reporting;
using Benchmark.Runs;
using Benchmark.Util;
using Benchmark.Workspace;

namespace Benchmark.Cli;

/// <summary>
/// The simple operator path: `run &lt;workflow&gt;` prepares everything, opens Claude Code with the task, and
/// when Claude Code exits exports the chat, token usage and human-interaction counts, runs the hidden tests
/// and refreshes the comparison report. Nothing has to be logged by hand.
/// </summary>
public static class SimpleRun
{
    public static async Task<int> RunAsync(ExperimentConfig c, RunStore store, PricingTable? pricing, string workflowId, bool activationCheck, Action<string> log, CancellationToken ct)
    {
        var problems = ConfigValidator.Validate(c);
        if (problems.Count > 0) throw new BenchmarkException("Configuration invalid: " + string.Join("; ", problems));
        if (!File.Exists(BasePreparer.BaseFile(store, c)))
        {
            log("First run: preparing the pinned application (clone, build, baseline tests). This takes a few minutes and happens once.");
            var b = await BasePreparer.PrepareAsync(c, store, log);
            if (!b.BuildSucceeded) throw new BenchmarkException("Application base did not build; see the base folder under the data root.");
        }

        log($"Preparing a fresh isolated workspace for {workflowId} ...");
        var run = await WorkspaceManager.PrepareRunAsync(c, workflowId, store, log);
        if (!run.Setup.StaticActivationPassed)
            throw new BenchmarkException($"Workflow setup check failed for {run.RunId}: {string.Join("; ", run.Setup.ActivationFindings)}");
        if (activationCheck && !await ActivationProbe.RunAsync(c, store, run, log))
            throw new BenchmarkException($"Activation check failed for {run.RunId}; the workflow did not load as expected. Nothing was started.");

        Banner(run);
        await SessionExecutor.StartInteractiveAsync(c, store, run, resume: false, ct);
        return await AfterSessionAsync(c, store, pricing, store.Load(run.RunId), log, ct);
    }

    public static async Task<int> ResumeAsync(ExperimentConfig c, RunStore store, PricingTable? pricing, string runId, Action<string> log, CancellationToken ct)
    {
        var run = store.Load(runId);
        Banner(run);
        await SessionExecutor.StartInteractiveAsync(c, store, run, resume: true, ct);
        return await AfterSessionAsync(c, store, pricing, store.Load(run.RunId), log, ct);
    }

    private static void Banner(RunRecord run)
    {
        Console.WriteLine();
        Console.WriteLine($"Run {run.RunId} ({run.WorkflowId}). Claude Code opens with the task already sent.");
        Console.WriteLine("Work with it as you normally would: answer its questions, approve or reject prompts, use the workflow's commands.");
        Console.WriteLine("Answer business questions from experiments/checkout-retry/decisions.json so both workflows get the same answers.");
        Console.WriteLine("Type /exit when the work is done (or when you need a break; you can resume).");
        Console.WriteLine();
    }

    private static async Task<int> AfterSessionAsync(ExperimentConfig c, RunStore store, PricingTable? pricing, RunRecord run, Action<string> log, CancellationToken ct)
    {
        Console.WriteLine();
        Console.WriteLine("Claude Code exited. Is this workflow run complete?");
        Console.WriteLine("  f = finished: export chat and tokens, run hidden tests, update the comparison");
        Console.WriteLine($"  l = later: keep it open; continue with `bench resume {run.RunId}`");
        Console.Write("[f/l]: ");
        var answer = Console.IsInputRedirected ? null : Console.ReadLine();
        if (!string.Equals(answer?.Trim(), "f", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"Left open. Resume: bench resume {run.RunId}    Finish without resuming: bench finish {run.RunId}");
            return 0;
        }
        return await CompleteAsync(c, store, pricing, run, log, ct);
    }

    /// <summary>Finish, seal, evaluate and refresh the comparison.</summary>
    public static async Task<int> CompleteAsync(ExperimentConfig c, RunStore store, PricingTable? pricing, RunRecord run, Action<string> log, CancellationToken ct)
    {
        var t = await RunFinisher.FinishAsync(c, store, run, pricing, seal: true, log);
        run = store.Load(run.RunId);

        var draft = !c.Evaluation.Status.Equals("approved", StringComparison.OrdinalIgnoreCase);
        log(draft ? "Running hidden tests (suite is still DRAFT, so results are marked unofficial) ..." : "Running hidden tests ...");
        EvaluationRecord? e = null;
        try { e = await Evaluator.EvaluateAsync(c, store, run, null, allowDraft: true, mutationControl: false, log); }
        catch (BenchmarkException ex) { Console.Error.WriteLine("Hidden-test evaluation failed: " + ex.Message + $" Retry with `bench evaluate --run {run.RunId} --allow-draft`."); }

        var reviewPath = Path.Combine(store.DataRoot, "reviews", run.RunId + ".json");
        if (!File.Exists(reviewPath))
            Json.Write(reviewPath, new EngineeringReview
            {
                RunId = run.RunId,
                Scores = Rubric.Dimensions.ToDictionary(d => d, _ => (int?)null),
                Notes = Rubric.Dimensions.ToDictionary(d => d, _ => ""),
            });

        var reportPath = await WriteComparisonAsync(c, store, null, null);

        var u = t.Usage;
        var ia = t.Interaction;
        Console.WriteLine();
        Console.WriteLine($"Run {run.RunId} ({run.WorkflowId}) finished and sealed.");
        Console.WriteLine($"  Tokens      in {u.Totals.Input:N0} | out {u.Totals.Output:N0} | cache read {u.Totals.CacheRead:N0} | cache write {u.Totals.CacheCreation:N0}");
        Console.WriteLine($"  Cost        Claude Code reported {(u.CliReportedCostUsd is { } cost ? "$" + cost.ToString("0.00") : "unavailable")}, pricing-table estimate {(u.EstimatedCostUsd is { } est ? "$" + est.ToString("0.00") : "unavailable")}{(u.Complete ? "" : " (INCOMPLETE telemetry)")}");
        if (ia is not null)
            Console.WriteLine($"  Human       {ia.TotalHumanInputs} input(s): {ia.HumanPrompts} message(s), {ia.SlashCommands} command(s), {ia.ShellCommands} shell command(s), {ia.QuestionsAnswered} answer(s), {ia.ToolRejections} rejection(s); agent waited on you {Telemetry.Conversation.Dur(ia.HumanWaitSeconds)}");
        if (e?.Hidden is { } h)
            Console.WriteLine($"  Hidden tests {h.Passed} pass, {h.Failed} fail, {h.Indeterminate} indeterminate; regressions {e.Regressions.Count}{(draft ? " (DRAFT suite)" : "")}");
        foreach (var w in u.Warnings.Concat(ia?.Warnings ?? [])) Console.WriteLine("  warning: " + w);
        foreach (var v in t.Activity.IsolationViolations) Console.WriteLine($"  ISOLATION VIOLATION: {v.Tool} referenced {v.Root}");
        Console.WriteLine();
        Console.WriteLine("  Chat        " + store.EvidencePath(run.RunId, "chat.md"));
        Console.WriteLine("  Code diff   " + store.EvidencePath(run.RunId, "changes", "final.patch"));
        Console.WriteLine("  Usage       " + store.EvidencePath(run.RunId, "telemetry.json"));
        Console.WriteLine("  Review      " + reviewPath + "  (scores 1-5; list H-numbers of corrective prompts)");
        Console.WriteLine("  Comparison  " + reportPath);
        return 0;
    }

    public static async Task<string> WriteComparisonAsync(ExperimentConfig c, RunStore store, IReadOnlyList<string>? runIds, string? outDir)
    {
        var runs = runIds is null
            ? store.All().Where(r => r.ExperimentId == c.ExperimentId && r.Status is RunStatus.Finished or RunStatus.Sealed).ToList()
            : runIds.Select(store.Load).ToList();
        if (runIds is null)
        {
            // Default comparison pools only runs made with the currently pinned workflow version, so a re-pin
            // never mixes old and new versions under the new version's label. Pass --runs to compare explicitly.
            var stale = runs.Where(r => !MatchesCurrentPin(c, r)).ToList();
            foreach (var r in stale)
                Console.WriteLine($"Skipping {r.RunId}: {r.WorkflowId} version or opening message differs from the current configuration.");
            runs = runs.Except(stale).ToList();
        }
        var report = ReportGenerator.Build(c, store, runs, await WorkspaceManager.HarnessCommitAsync(c.HarnessRoot));
        var markdown = ReportGenerator.Markdown(report);
        var dirs = outDir is not null ? [outDir] : new[] { Path.Combine(store.ReportsRoot, "latest"), Path.Combine(store.ReportsRoot, report.ReportId) };
        foreach (var d in dirs)
        {
            Directory.CreateDirectory(d);
            Json.Write(Path.Combine(d, "comparison.json"), report);
            Fs.WriteAtomic(Path.Combine(d, "comparison.md"), markdown);
        }
        return Path.Combine(dirs[0], "comparison.md");
    }

    internal static bool MatchesCurrentPin(ExperimentConfig c, RunRecord r)
    {
        var w = c.Workflows.FirstOrDefault(x => x.Id == r.WorkflowId);
        if (w is null) return false;
        if (r.Synthetic || string.IsNullOrEmpty(r.Setup.Version)) return true;
        if (r.Setup.Version != w.Version) return false;
        // A changed opening message (e.g. "{prompt}" -> "/speckit-specify {prompt}") is a different protocol.
        if (!string.IsNullOrEmpty(r.InitialMessage)
            && r.InitialMessage != w.InitialMessageTemplate.Replace("{prompt}", c.PromptText, StringComparison.Ordinal)) return false;
        return w.PluginSha256 is null || string.Equals(r.Setup.PluginSha256, w.PluginSha256, StringComparison.OrdinalIgnoreCase);
    }
}
