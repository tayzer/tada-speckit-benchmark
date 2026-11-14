using System.Text.Json.Nodes;
using System.Xml.Linq;
using Benchmark.Config;
using Benchmark.Evaluation;
using Benchmark.Human;
using Benchmark.Reporting;
using Benchmark.Runs;
using Benchmark.Telemetry;
using Benchmark.Util;
using Benchmark.Workspace;

namespace Benchmark.DryRun;

/// <summary>
/// End-to-end dry run with SYNTHETIC data. Exercises the evidence store, sealing, telemetry parsing and
/// aggregation (including resumed sessions, subagents, duplicated content blocks and a crashed invocation),
/// human-effort aggregation, TRX parsing, integrity checks and report generation — without any model call.
/// </summary>
public static class DryRunner
{
    public sealed record Result(string DataRoot, string MarkdownPath, string JsonPath, ComparisonReport Report);

    public static async Task<Result> RunAsync(string harnessRoot, string outDir, Action<string> log)
    {
        var config = ExperimentConfig.Load(Path.Combine(harnessRoot, "experiments", "checkout-retry"));
        config.Paths.DataRoot = Path.GetFullPath(Path.Combine(outDir, "data"));
        if (Directory.Exists(config.Paths.DataRoot) && Directory.EnumerateFileSystemEntries(config.Paths.DataRoot).Any())
            throw new BenchmarkException($"Dry-run output {config.Paths.DataRoot} is not empty; choose a new --out directory.");
        var store = new RunStore(config.DataRoot);
        store.EnsureLayout();
        var pricing = PricingTable.Load(config.ResolveHarnessPath(config.PricingFile));
        var catalog = Json.Read<HiddenCatalogFile>(Path.Combine(config.HiddenSuitePath, "catalog.json"));
        log($"SYNTHETIC dry run in {config.DataRoot}");

        // Synthetic frozen base.
        var baseDir = BasePreparer.BaseDir(store, config);
        Directory.CreateDirectory(baseDir);
        var baseline = new BaseRecord
        {
            ApplicationRepository = config.Application.Repository, ApplicationCommit = config.Application.Commit,
            BaseCommit = config.Application.Commit, CreatedAt = DateTimeOffset.UtcNow, BuildSucceeded = true,
            RepositoryPath = "(synthetic)", ExcludedTestProjects = config.Application.ExcludedTestProjects,
            Environment = SyntheticEnvironment(),
            BaselineTests = config.Application.TestProjects.Select(p => new TestProjectResult
            {
                Project = p, ExitCode = 0,
                Tests = Enumerable.Range(1, 5).Select(i => new TestCaseResult { Id = $"{Path.GetFileNameWithoutExtension(p)}.Test{i}", Name = $"Test{i}", Outcome = TestOutcome.Pass }).ToList(),
            }).ToList(),
            KnownIssues = ["SYNTHETIC baseline"],
        };
        Json.Write(BasePreparer.BaseFile(store, config), baseline);

        var scenarios = new[]
        {
            new Scenario("speckit", 1, Interactions: 9, Corrective: 1, HiddenFailures: ["HT05_FailureAfterPersistence_RetryDoesNotDuplicate"], Regression: false, Crash: false),
            new Scenario("speckit", 2, Interactions: 7, Corrective: 0, HiddenFailures: [], Regression: false, Crash: false),
            new Scenario("tada", 1, Interactions: 12, Corrective: 0, HiddenFailures: [], Regression: false, Crash: false),
            new Scenario("tada", 2, Interactions: 11, Corrective: 2, HiddenFailures: ["HT02_ConcurrentDuplicateSubmissions_CreateExactlyOneOrder"], Regression: true, Crash: true),
        };

        var runs = new List<RunRecord>();
        foreach (var s in scenarios)
        {
            var run = CreateRun(config, store, s);
            WriteTelemetry(config, store, run, s, pricing);
            WriteTranscript(config, store, run, s);
            WriteChanges(store, run, s);
            run.Status = RunStatus.Finished;
            run.FinishedAt = run.Invocations.Max(i => i.EndedAt).AddMinutes(1);
            store.Save(run);
            store.Seal(run);
            WriteEvaluation(config, store, run, s, catalog, baseline);
            runs.Add(store.Load(run.RunId));
            log($"  synthetic run {run.RunId} sealed and evaluated");
        }

        var report = ReportGenerator.Build(config, store, runs, await WorkspaceManager.HarnessCommitAsync(harnessRoot));
        var reportDir = Path.Combine(store.ReportsRoot, report.ReportId);
        Directory.CreateDirectory(reportDir);
        var jsonPath = Path.Combine(reportDir, "comparison.json");
        var mdPath = Path.Combine(reportDir, "comparison.md");
        Json.Write(jsonPath, report);
        Fs.WriteAtomic(mdPath, ReportGenerator.Markdown(report));
        return new Result(config.DataRoot, mdPath, jsonPath, report);
    }

    private sealed record Scenario(string Workflow, int Index, int Interactions, int Corrective, string[] HiddenFailures, bool Regression, bool Crash);

    private static EnvironmentSnapshot SyntheticEnvironment() => new()
    {
        Os = "SYNTHETIC Windows", DotnetSdk = "10.0.403", Git = "git version 2.x", Uv = "uv 0.x", PowerShell7 = "7.5.0",
        LocalDb = "MSSQLLocalDB", UserSettingsExcluded = true, NugetPackagesPath = "<per-run>",
    };

    private static RunRecord CreateRun(ExperimentConfig c, RunStore store, Scenario s)
    {
        var w = c.Workflow(s.Workflow);
        var start = new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero).AddHours(s.Index * 3 + (s.Workflow == "tada" ? 1 : 0));
        var run = new RunRecord
        {
            RunId = $"SYNTHETIC-{s.Workflow}-{s.Index}",
            ExperimentId = c.ExperimentId, WorkflowId = s.Workflow, Synthetic = true, Status = RunStatus.Prepared,
            Mode = ExecutionMode.Managed, CreatedAt = start.AddMinutes(-10), ApplicationCommit = c.Application.Commit,
            BaseCommit = c.Application.Commit, SetupCommit = "synthetic-setup", FinalCommit = "synthetic-final",
            WorkspacePath = "(synthetic)", SessionId = Guid.NewGuid().ToString(), PromptSha256 = Fs.Sha256Text(c.PromptText),
            InitialMessage = c.PromptText, InitialMessageSha256 = Fs.Sha256Text(c.PromptText), HarnessCommit = "synthetic",
            Agent = new AgentSettingsSnapshot
            {
                Model = c.Agent.Model, Effort = c.Agent.Effort, PermissionMode = c.Agent.PermissionMode, SettingSources = c.Agent.SettingSources,
                StrictMcpConfig = c.Agent.StrictMcpConfig, PermissionPolicySha256 = "synthetic-policy", ClaudeVersionReported = c.Agent.CliVersion + " (Claude Code)",
            },
            Setup = new WorkflowSetupRecord
            {
                WorkflowId = w.Id, Kind = w.Kind, Version = w.Version, SetupSeconds = s.Workflow == "tada" ? 4 : 38,
                Steps = ["Environment setup (clone + restore) 52.0s", "SYNTHETIC setup"], StaticActivationPassed = true,
            },
            Environment = SyntheticEnvironment(),
        };
        store.Create(run);
        return run;
    }

    private static void WriteTelemetry(ExperimentConfig c, RunStore store, RunRecord run, Scenario s, PricingTable pricing)
    {
        var w = c.Workflow(s.Workflow);
        var scale = s.Workflow == "tada" ? 1.35 : 1.0;
        var start = run.CreatedAt.AddMinutes(10);
        var skills = w.ExpectedMarkers.Skills;
        var invocations = new List<(string Id, bool Resume, List<string> Lines, bool Ok, double Minutes)>();

        // Invocation 1: initial task; includes duplicated content blocks, a subagent and a verification command.
        var b1 = new StreamJsonBuilder(run.SessionId, c.Agent.Model).Init(w.ExpectedMarkers.Plugins, skills);
        for (var i = 0; i < (int)(14 * scale); i++)
        {
            var tool = i % 5 == 4 ? "Bash" : "Read";
            var input = tool == "Bash" ? new JsonObject { ["command"] = i % 10 == 9 ? "dotnet test tests/UnitTests" : "dotnet build eShopOnWeb.slnx" } : new JsonObject { ["file_path"] = "src/Web/Pages/Basket/Checkout.cshtml.cs" };
            b1.Assistant($"msg_{run.RunId}_1_{i}", 3, 400 + i * 10, 20000 + i * 1500, 1200, blocks: 2, toolName: tool, toolInput: input);
        }
        b1.Assistant($"msg_{run.RunId}_1_sub", 5, 300, 9000, 4000, toolName: "Grep", toolInput: new JsonObject { ["pattern"] = "CreateOrderAsync" }, parentToolUseId: "toolu_parent", modelOverride: "claude-haiku-5-5");
        b1.Assistant($"msg_{run.RunId}_1_q", 2, 250, 40000, 800, toolName: "AskUserQuestion", toolInput: new JsonObject { ["question"] = "Should a retry return the original order confirmation?" });
        b1.Result(permissionDenials: s.Workflow == "tada" ? 2 : 1);
        invocations.Add(("001", false, b1.Lines, true, 18 * scale));

        // Invocation 2: resumed with the owner's answer; cumulative totals continue from invocation 1.
        var b2 = new StreamJsonBuilder(run.SessionId, c.Agent.Model).Init(w.ExpectedMarkers.Plugins, skills);
        for (var i = 0; i < (int)(20 * scale); i++)
            b2.Assistant($"msg_{run.RunId}_2_{i}", 2, 600 + i * 15, 45000 + i * 2000, 1500, blocks: i % 3 == 0 ? 3 : 1, toolName: "Edit", toolInput: new JsonObject { ["file_path"] = "src/ApplicationCore/Services/OrderService.cs" });
        if (s.Crash)
        {
            // Crashed/cancelled invocation: no result event, one malformed line.
            b2.Garbage();
            invocations.Add(("002", true, b2.Lines, false, 25 * scale));
        }
        else
        {
            b2.Result(priorCumulative: b1.LastCumulative);
            invocations.Add(("002", true, b2.Lines, true, 25 * scale));
        }

        var captures = new List<InvocationCapture>();
        var clock = start;
        foreach (var (id, resume, lines, ok, minutes) in invocations)
        {
            var rel = $"invocations/{id}.stdout.jsonl";
            store.WriteEvidence(run.RunId, rel, string.Join("\n", lines) + "\n");
            store.WriteEvidence(run.RunId, $"invocations/{id}.stderr.txt", "");
            var inv = new InvocationRecord
            {
                InvocationId = id, Mode = ExecutionMode.Managed, IsResume = resume, StartedAt = clock, EndedAt = clock.AddMinutes(minutes),
                ExitCode = ok ? 0 : null, Cancelled = !ok, StdoutFile = rel, StderrFile = $"invocations/{id}.stderr.txt",
                MessageKind = resume ? "clarification-answer" : "initial-task",
            };
            run.Invocations.Add(inv);
            captures.Add(new InvocationCapture { InvocationId = id, IsResume = resume, StartedAt = clock, ProcessSucceeded = ok, Parsed = ClaudeEventParser.ParseStreamJson(lines) });
            clock = inv.EndedAt.AddMinutes(6);
        }

        var usage = UsageAggregator.FromInvocations(captures, c.Agent.Model, pricing);
        usage.ObservedEfforts = [c.Agent.Effort];
        var merged = RunFinisher.Merge(captures.Select(x => x.Parsed));
        var telemetry = new TelemetryRecord
        {
            Source = "SYNTHETIC stream-json fixtures",
            Usage = usage,
            Activity = ActivityAnalyzer.Analyze(merged, [c.HarnessRoot]),
            InitEvents = merged.Inits,
            DynamicActivationFindings = merged.Inits.SelectMany(i => WorkflowSetup.DynamicChecks(w, i)).Distinct().ToList(),
            AgentExecutionSeconds = run.Invocations.Sum(i => i.DurationSeconds),
            AgentExecutionBasis = "Sum of managed claude -p process durations (SYNTHETIC).",
            SessionWallClockSeconds = (run.Invocations.Max(i => i.EndedAt) - run.Invocations.Min(i => i.StartedAt)).TotalSeconds,
        };
        telemetry.DynamicActivationStatus = telemetry.DynamicActivationFindings.Count == 0 ? "verified" : "failed";
        if (!usage.Complete) run.Deviations.Add("Telemetry incomplete (see telemetry warnings).");
        store.WriteEvidence(run.RunId, "telemetry.json", Json.Serialize(telemetry));
    }

    /// <summary>
    /// Writes a SYNTHETIC interactive transcript in Claude Code's on-disk format, then exports chat.md and the
    /// derived interaction summary exactly as `finish` does for a real run.
    /// </summary>
    private static void WriteTranscript(ExperimentConfig c, RunStore store, RunRecord run, Scenario s)
    {
        var register = DecisionRegister.Load(c.ResolveExperimentPath(c.DecisionsFile));
        var answer = register.Decisions.FirstOrDefault(d => d.Id == "D2")?.Answer ?? "Show the confirmation for the original order.";
        var t = run.Invocations[0].StartedAt;
        var lines = new List<string>();
        var n = 0;
        string Ts(double addSeconds) { t = t.AddSeconds(addSeconds); return t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"); }
        void User(double after, JsonNode content) => lines.Add(new JsonObject { ["type"] = "user", ["timestamp"] = Ts(after), ["sessionId"] = run.SessionId, ["message"] = new JsonObject { ["role"] = "user", ["content"] = content } }.ToJsonString());
        void Assistant(double after, JsonObject block) => lines.Add(new JsonObject { ["type"] = "assistant", ["timestamp"] = Ts(after), ["sessionId"] = run.SessionId, ["message"] = new JsonObject { ["id"] = $"msg_syn_{++n}", ["role"] = "assistant", ["content"] = new JsonArray(block) } }.ToJsonString());
        JsonObject Text(string text) => new() { ["type"] = "text", ["text"] = text };
        JsonObject Tool(string id, string name, JsonObject input) => new() { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = input };
        JsonArray Result(string id, string text, bool error = false) => new(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = text, ["is_error"] = error });

        User(0, c.PromptText);
        Assistant(8, Text("SYNTHETIC: I'll start by understanding the checkout flow."));
        Assistant(1, Tool("t1", "Skill", new JsonObject { ["skill"] = s.Workflow == "tada" ? "tada:tada" : "speckit-specify" }));
        User(2, Result("t1", "Launching skill"));
        Assistant(30, Tool("t2", "Read", new JsonObject { ["file_path"] = "src/Web/Pages/Basket/Checkout.cshtml.cs" }));
        User(1, Result("t2", "(file content)"));
        Assistant(20, Tool("t3", "AskUserQuestion", new JsonObject { ["questions"] = new JsonArray(new JsonObject { ["question"] = "What should a retried checkout show?", ["options"] = new JsonArray(new JsonObject { ["label"] = "Original confirmation" }, new JsonObject { ["label"] = "An error" }) }) }));
        User(150, Result("t3", "User has answered your questions: \"What should a retried checkout show?\"=\"" + answer + "\""));
        for (var i = 0; i < s.Interactions - 6; i++)
        {
            Assistant(40, Text($"SYNTHETIC: step {i + 1} done. Ready for the next step?"));
            if (s.Workflow == "speckit" && i % 2 == 0) User(60, JsonValue.Create($"<command-message>speckit-{(i == 0 ? "plan" : "implement")}</command-message>\n<command-name>/speckit-{(i == 0 ? "plan" : "implement")}</command-name>\n<command-args></command-args>")!);
            else User(45, JsonValue.Create("Looks good, continue.")!);
        }
        Assistant(30, Tool("t4", "Bash", new JsonObject { ["command"] = "dotnet test eShopOnWeb.sln" }));
        User(90, Result("t4", "Passed!  - Failed: 0, Passed: 59"));
        for (var i = 0; i < s.Corrective; i++)
        {
            Assistant(10, Text("SYNTHETIC: I've added a uniqueness check in the Razor page."));
            User(120, JsonValue.Create("That is the wrong layer: the duplicate check belongs in the ordering service, not the page.")!);
        }
        if (s.Crash)
        {
            Assistant(15, Tool("t5", "Bash", new JsonObject { ["command"] = "git reset --hard" }));
            User(20, Result("t5", "The user doesn't want to proceed with this tool use. The tool use was rejected.", error: true));
            User(5, JsonValue.Create("[Request interrupted by user for tool use]")!);
        }
        Assistant(25, Text("SYNTHETIC: Done. Duplicate submissions now return the original order."));

        var transcript = string.Join("\n", lines) + "\n";
        store.WriteEvidence(run.RunId, "transcripts/session.jsonl", transcript);
        var entries = Conversation.Read(lines);
        var answers = register.Decisions.Where(d => d.Answer is not null).ToDictionary(d => d.Id, d => d.Answer!);
        var summary = Conversation.Analyze(entries, TimeSpan.FromMinutes(c.Evidence.HumanIdleCapMinutes), answers);
        store.WriteEvidence(run.RunId, "chat.md", "> SYNTHETIC dry-run transcript, not a real session.\n\n" + Conversation.RenderMarkdown($"Conversation: {run.WorkflowId} run {run.RunId}", entries, summary, []));
        var telemetryPath = store.EvidencePath(run.RunId, "telemetry.json");
        var telemetry = Json.Read<TelemetryRecord>(telemetryPath);
        telemetry.Interaction = summary;
        telemetry.ChatExport = "chat.md";
        telemetry.TranscriptMain = "transcripts/session.jsonl";
        store.WriteEvidence(run.RunId, "telemetry.json", Json.Serialize(telemetry));

        // A synthetic review that tags the corrective messages by their H number in chat.md.
        var corrective = summary.Turns.Where(x => x.Preview.StartsWith("That is the wrong layer", StringComparison.Ordinal)).Select(x => x.Number).ToList();
        Json.Write(Path.Combine(store.DataRoot, "reviews", run.RunId + ".json"), new EngineeringReview
        {
            RunId = run.RunId, Reviewer = "SYNTHETIC", CorrectivePrompts = corrective,
            Scores = Rubric.Dimensions.ToDictionary(d => d, _ => (int?)null),
            Notes = Rubric.Dimensions.ToDictionary(d => d, _ => ""),
        });
    }

    private static void WriteChanges(RunStore store, RunRecord run, Scenario s)
    {
        var files = new List<ChangedFile>
        {
            new() { Path = "src/Web/Pages/Basket/Checkout.cshtml.cs", Status = "M", Added = 40, Removed = 6 },
            new() { Path = "src/ApplicationCore/Services/OrderService.cs", Status = "M", Added = 55, Removed = 10 },
            new() { Path = "tests/UnitTests/ApplicationCore/Services/OrderServiceTests/CheckoutIdempotency.cs", Status = "A", Added = 120, IsTest = true },
        };
        if (s.Workflow == "speckit") files.Add(new() { Path = "specs/001-checkout-retry/spec.md", Status = "A", Added = 90, IsWorkflowArtifact = true });
        else files.AddRange([
            new() { Path = "features/checkout-retry.feature", Status = "A", Added = 45, IsTest = true, IsWorkflowArtifact = true },
            new() { Path = "tests/Acceptance/Steps/CheckoutSteps.cs", Status = "A", Added = 160, IsTest = true },
        ]);
        if (s.Regression) files.Add(new() { Path = "tests/FunctionalTests/Web/Pages/Basket/CheckoutTest.cs", Status = "M", Added = 3, Removed = 3, IsTest = true });
        var summary = new ChangeSummary { FromCommit = run.SetupCommit, ToCommit = run.FinalCommit!, Files = files };
        store.WriteEvidence(run.RunId, "changes/changes.json", Json.Serialize(summary));
        store.WriteEvidence(run.RunId, "changes/final.patch", "SYNTHETIC — no real diff\n");
    }

    private static void WriteEvaluation(ExperimentConfig c, RunStore store, RunRecord run, Scenario s, HiddenCatalogFile catalog, BaseRecord baseline)
    {
        var evalDir = Path.Combine(store.EvaluationsRoot, $"SYNTHETIC-eval-{run.RunId}");
        Directory.CreateDirectory(evalDir);
        var trxPath = Path.Combine(evalDir, "hidden.trx");
        SyntheticTrx(catalog, s.HiddenFailures).Save(trxPath);
        var hidden = new TestProjectResult { Project = "tests/__HiddenEvaluation/CheckoutRetry.HiddenTests.csproj", ExitCode = s.HiddenFailures.Length == 0 ? 0 : 1, TrxFile = trxPath, Tests = TrxParser.Parse(trxPath) };
        var original = baseline.BaselineTests.Select(p => new TestProjectResult
        {
            Project = p.Project, ExitCode = 0,
            Tests = p.Tests.Select(t => new TestCaseResult { Id = t.Id, Name = t.Name, Outcome = s.Regression && t.Id.EndsWith("Test3", StringComparison.Ordinal) && p.Project.Contains("Functional", StringComparison.Ordinal) ? TestOutcome.Fail : TestOutcome.Pass, Message = s.Regression ? "SYNTHETIC regression" : null }).ToList(),
        }).ToList();
        var record = new EvaluationRecord
        {
            EvaluationId = $"SYNTHETIC-eval-{run.RunId}", Target = run.RunId, RunId = run.RunId, WorkflowId = run.WorkflowId, Synthetic = true,
            EvaluatedCommit = "synthetic-final", StartedAt = DateTimeOffset.UtcNow, EndedAt = DateTimeOffset.UtcNow,
            Evaluator = new EvaluatorIdentity
            {
                HarnessCommit = "synthetic", HiddenSuiteSha256 = Fs.Sha256Directory(c.HiddenSuitePath), SuiteStatus = c.Evaluation.Status,
                SuiteMatchesApproval = false, DotnetSdk = "10.0.403",
            },
            ImplementationBuilds = true, Hidden = hidden, OriginalTestsAgainstImplementation = original,
            Regressions = Evaluator.Regressions(baseline, original),
            RevertedProductionControl = new MutationControlResult { Detected = s.Index == 1, Detail = s.Index == 1 ? "SYNTHETIC: 3 workflow tests fail without the fix (detected)." : "SYNTHETIC: all workflow tests pass without the fix (NOT detected)." },
            Notes = ["SYNTHETIC evaluation generated by dry-run."],
        };
        Json.Write(Path.Combine(evalDir, "evaluation.json"), record);
    }

    public static XDocument SyntheticTrx(HiddenCatalogFile catalog, IReadOnlyCollection<string> failures)
    {
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var defs = new XElement(ns + "TestDefinitions");
        var results = new XElement(ns + "Results");
        foreach (var t in catalog.Tests)
        {
            var id = Guid.NewGuid().ToString();
            defs.Add(new XElement(ns + "UnitTest", new XAttribute("id", id), new XAttribute("name", t.Id),
                new XElement(ns + "TestMethod", new XAttribute("className", "CheckoutRetry.HiddenTests.CheckoutRetryTests"), new XAttribute("name", t.Id))));
            var pending = t.Decisions.Count > 0 && t.Id.Contains("FreshCheckoutPage", StringComparison.Ordinal);
            var outcome = failures.Contains(t.Id) ? "Failed" : pending ? "NotExecuted" : "Passed";
            var r = new XElement(ns + "UnitTestResult", new XAttribute("testId", id), new XAttribute("testName", t.Id), new XAttribute("outcome", outcome), new XAttribute("duration", "00:00:02.5000000"));
            if (outcome == "Failed") r.Add(new XElement(ns + "Output", new XElement(ns + "ErrorInfo", new XElement(ns + "Message", "SYNTHETIC: expected 1 order, found 2"))));
            if (pending) r.Add(new XElement(ns + "Output", new XElement(ns + "ErrorInfo", new XElement(ns + "Message", "PENDING-DECISION: awaiting owner decision"))));
            results.Add(r);
        }
        return new XDocument(new XElement(ns + "TestRun", defs, results));
    }
}
