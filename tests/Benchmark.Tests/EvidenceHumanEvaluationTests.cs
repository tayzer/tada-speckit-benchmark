using System.Text.Json.Nodes;
using System.Xml.Linq;
using Benchmark.Config;
using Benchmark.DryRun;
using Benchmark.Evaluation;
using Benchmark.Human;
using Benchmark.Reporting;
using Benchmark.Runs;
using Benchmark.Telemetry;
using Benchmark.Util;
using Benchmark.Workspace;
using Xunit;

namespace Benchmark.Tests;

public sealed class EvidencePreservationTests
{
    private static RunRecord NewRun(string id) => new() { RunId = id, ExperimentId = "x", WorkflowId = "tada", CreatedAt = DateTimeOffset.UtcNow };

    [Fact]
    public void Runs_are_never_overwritten()
    {
        var store = new RunStore(TestSupport.TempDir());
        store.Create(NewRun("r1"));
        Assert.Throws<BenchmarkException>(() => store.Create(NewRun("r1")));
    }

    [Fact]
    public void Sealed_runs_reject_writes_and_detect_tampering()
    {
        var root = TestSupport.TempDir();
        var store = new RunStore(root);
        var run = store.Create(NewRun("r1"));
        store.WriteEvidence("r1", "telemetry.json", "{}");
        store.Seal(run);
        Assert.Empty(store.Verify("r1"));
        Assert.Throws<BenchmarkException>(() => store.WriteEvidence("r1", "late.txt", "x"));
        Assert.Throws<BenchmarkException>(() => store.Save(run));
        Assert.True(File.GetAttributes(store.EvidencePath("r1", "telemetry.json")).HasFlag(FileAttributes.ReadOnly));

        TestSupport.ClearReadOnly(store.RunDir("r1"));
        File.WriteAllText(store.EvidencePath("r1", "telemetry.json"), "{\"tampered\":true}");
        File.WriteAllText(store.EvidencePath("r1", "extra.txt"), "x");
        var problems = store.Verify("r1");
        Assert.Contains(problems, p => p.StartsWith("Modified: telemetry.json", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("Added after sealing: extra.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_ids_are_unique_and_sortable()
    {
        var t = DateTimeOffset.Parse("2026-11-13T10:00:00Z");
        var ids = Enumerable.Range(0, 50).Select(_ => RunStore.NewRunId("tada", t)).ToHashSet();
        Assert.Equal(50, ids.Count);
        Assert.All(ids, id => Assert.StartsWith("20261113-100000-tada-", id, StringComparison.Ordinal));
    }

    [Fact]
    public void Base_record_rejects_changed_compatibility_patches()
    {
        var c = TestSupport.RealConfig();
        var store = new RunStore(TestSupport.TempDir());
        Directory.CreateDirectory(BasePreparer.BaseDir(store, c));
        Json.Write(BasePreparer.BaseFile(store, c), new BaseRecord { ApplicationCommit = c.Application.Commit, CompatibilityPatches = [new PatchRecord { File = "p.patch", Sha256 = "abc" }] });
        var ex = Assert.Throws<BenchmarkException>(() => BasePreparer.Load(store, c));
        Assert.Contains("patches changed", ex.Message, StringComparison.Ordinal);

        Json.Write(BasePreparer.BaseFile(store, c), new BaseRecord { ApplicationCommit = new string('a', 40) });
        Assert.Throws<BenchmarkException>(() => BasePreparer.Load(store, c));
    }
}

public sealed class HumanEffortTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-13T10:00:00Z");

    private static HumanEvent E(int minute, InteractionKind kind, ActivityKind? a = null, string? decision = null) =>
        new() { Timestamp = T0.AddMinutes(minute), Kind = kind, Activity = a, DecisionId = decision };

    [Fact]
    public void Overlapping_timers_are_not_double_counted()
    {
        var s = HumanLog.Summarise(
        [
            E(0, InteractionKind.TimerStart, ActivityKind.Review),
            E(5, InteractionKind.TimerStart, ActivityKind.Answer),
            E(10, InteractionKind.TimerStop, ActivityKind.Review),
            E(12, InteractionKind.TimerStop, ActivityKind.Answer),
        ]);
        Assert.Equal(600, s.ActiveSecondsByActivity["review"]);
        Assert.Equal(420, s.ActiveSecondsByActivity["answer"]);
        Assert.Equal(720, s.TotalActiveSeconds); // union 0..12 minutes
    }

    [Fact]
    public void Distinguishes_productive_clarification_from_correction()
    {
        var s = HumanLog.Summarise(
        [
            E(1, InteractionKind.ClarificationQuestion), E(2, InteractionKind.ClarificationAnswer, decision: "D1"),
            E(3, InteractionKind.CorrectivePrompt), E(4, InteractionKind.CorrectivePrompt),
            E(5, InteractionKind.Approval), E(6, InteractionKind.Rejection), E(7, InteractionKind.ToolingIntervention),
        ]);
        Assert.Equal(1, s.ProductiveClarifications);
        Assert.Equal(2, s.CorrectiveInterventions);
        Assert.Equal(2, s.ApprovalDecisions);
        Assert.Equal(1, s.ToolingInterventions);
        Assert.Equal(["D1"], s.DecisionsUsed);
    }

    [Fact]
    public void Unmatched_and_open_timers_produce_warnings()
    {
        var s = HumanLog.Summarise([E(0, InteractionKind.TimerStop, ActivityKind.Review), E(1, InteractionKind.TimerStart, ActivityKind.Troubleshoot)], runEnd: T0.AddMinutes(11));
        Assert.Contains(s.Warnings, w => w.Contains("without a start", StringComparison.Ordinal));
        Assert.Contains(s.Warnings, w => w.Contains("never stopped", StringComparison.Ordinal));
        Assert.Equal(600, s.TotalActiveSeconds);
    }

    [Fact]
    public void Manual_minutes_are_added()
    {
        var s = HumanLog.Summarise([new HumanEvent { Timestamp = T0, Kind = InteractionKind.SpecReview, Activity = ActivityKind.Review, ManualSeconds = 300 }]);
        Assert.Equal(300, s.TotalActiveSeconds);
        Assert.Equal(1, s.Reviews);
    }

    [Fact]
    public void Log_round_trips_and_tracks_open_timers()
    {
        var log = new HumanLog(Path.Combine(TestSupport.TempDir(), "h.jsonl"));
        log.Append(E(0, InteractionKind.TimerStart, ActivityKind.Review));
        Assert.Equal([ActivityKind.Review], log.OpenTimers());
        log.Append(E(1, InteractionKind.TimerStop, ActivityKind.Review));
        Assert.Empty(log.OpenTimers());
        Assert.Equal(2, log.Read().Count);
    }
}

public sealed class EvaluationOutcomeTests
{
    private static List<TestCaseResult> ParseTrx(params (string Name, string Outcome, string? Message)[] rows)
    {
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var results = new XElement(ns + "Results");
        foreach (var (name, outcome, message) in rows)
        {
            var r = new XElement(ns + "UnitTestResult", new XAttribute("testId", Guid.NewGuid()), new XAttribute("testName", name), new XAttribute("outcome", outcome), new XAttribute("duration", "00:00:01.5000000"));
            if (message is not null) r.Add(new XElement(ns + "Output", new XElement(ns + "ErrorInfo", new XElement(ns + "Message", message))));
            results.Add(r);
        }
        return TrxParser.Parse(new XDocument(new XElement(ns + "TestRun", results)));
    }

    [Fact]
    public void Maps_outcomes_without_confusing_infrastructure_with_behaviour()
    {
        var r = ParseTrx(
            ("A", "Passed", null),
            ("B", "Failed", "Assert.Equal() Failure: Expected 1 Actual 2"),
            ("C", "Failed", "INDETERMINATE: seam not reached"),
            ("D", "NotExecuted", "PENDING-DECISION: D7"),
            ("E", "Timeout", null),
            ("F", "NotExecuted", "skipped for other reason"));
        Assert.Equal((TestOutcome.Pass, FailureCause.None), (r[0].Outcome, r[0].Cause));
        Assert.Equal((TestOutcome.Fail, FailureCause.Behaviour), (r[1].Outcome, r[1].Cause));
        Assert.Equal((TestOutcome.Indeterminate, FailureCause.SeamUnavailable), (r[2].Outcome, r[2].Cause));
        Assert.Equal((TestOutcome.Indeterminate, FailureCause.PendingDecision), (r[3].Outcome, r[3].Cause));
        Assert.Equal((TestOutcome.Indeterminate, FailureCause.Infrastructure), (r[4].Outcome, r[4].Cause));
        Assert.Equal(TestOutcome.Skipped, r[5].Outcome);
        Assert.Equal(1.5, r[0].DurationSeconds);
    }

    [Fact]
    public void Regressions_only_count_tests_that_passed_at_baseline()
    {
        var baseline = new BaseRecord
        {
            BaselineTests = [new TestProjectResult { Project = "tests/U/U.csproj", Tests = [
                new() { Id = "t1", Outcome = TestOutcome.Pass }, new() { Id = "t2", Outcome = TestOutcome.Fail }, new() { Id = "t3", Outcome = TestOutcome.Pass }] }],
        };
        var now = new[] { new TestProjectResult { Project = "tests/U/U.csproj", Tests = [
            new() { Id = "t1", Outcome = TestOutcome.Fail, Message = "boom" }, new() { Id = "t2", Outcome = TestOutcome.Fail }] } };
        var regressions = Evaluator.Regressions(baseline, now);
        Assert.Equal(2, regressions.Count); // t1 now fails, t3 missing; t2 was already failing
        Assert.Contains(regressions, r => r.Test == "t3" && r.NowOutcome == "missing");

        var notBuilt = Evaluator.Regressions(baseline, [new TestProjectResult { Project = "tests/U/U.csproj", InfrastructureError = "Build failed" }]);
        Assert.Single(notBuilt);
        Assert.Equal("not-run", notBuilt[0].NowOutcome);
    }

    [Fact]
    public void Hidden_catalog_matches_suite_test_methods()
    {
        var c = TestSupport.RealConfig();
        var ids = Evaluator.HiddenCatalog(c);
        var source = File.ReadAllText(Path.Combine(c.HiddenSuitePath, "CheckoutRetryTests.cs"));
        Assert.All(ids, id => Assert.Contains($"Task {id}()", source, StringComparison.Ordinal));
        Assert.Equal(System.Text.RegularExpressions.Regex.Matches(source, @"\[Fact\]").Count, ids.Count);
    }
}

public sealed class ActivityAnalyzerTests
{
    [Fact]
    public void Detects_forbidden_path_references_at_path_boundaries_only()
    {
        var tool = new ToolUseRecord { Name = "Bash", Input = new JsonObject { ["command"] = @"cat D:\Dev\Benchmark\evaluator\x.cs" } };
        Assert.Single(ActivityAnalyzer.FindForbidden(tool, [@"D:\Dev\Benchmark"]));
        var posix = new ToolUseRecord { Name = "Bash", Input = new JsonObject { ["command"] = "ls /d/Dev/Benchmark/evaluator" } };
        Assert.Single(ActivityAnalyzer.FindForbidden(posix, [@"D:\Dev\Benchmark"]));
        var sibling = new ToolUseRecord { Name = "Read", Input = new JsonObject { ["file_path"] = @"D:\Dev\BenchmarkData\workspaces\r1\app\x.cs" } };
        Assert.Empty(ActivityAnalyzer.FindForbidden(sibling, [@"D:\Dev\Benchmark"]));
        var token = new ToolUseRecord { Name = "Grep", Input = new JsonObject { ["pattern"] = "CheckoutRetry.HiddenTests" } };
        Assert.Single(ActivityAnalyzer.FindForbidden(token, ["CheckoutRetry.HiddenTests"]));
    }

    [Fact]
    public void Counts_verification_commands_questions_and_denials()
    {
        var b = new StreamJsonBuilder("s", "claude-opus-5-5").Init()
            .Assistant("1", 1, 1, 0, 0, toolName: "Bash", toolInput: new JsonObject { ["command"] = "cd x && dotnet test tests/UnitTests" })
            .Assistant("2", 1, 1, 0, 0, toolName: "PowerShell", toolInput: new JsonObject { ["command"] = "dotnet build eShopOnWeb.slnx" })
            .Assistant("3", 1, 1, 0, 0, toolName: "AskUserQuestion", toolInput: new JsonObject { ["question"] = "?" })
            .Assistant("4", 1, 1, 0, 0, toolName: "Skill", toolInput: new JsonObject { ["skill"] = "tada:tada" })
            .Result(permissionDenials: 2);
        var a = ActivityAnalyzer.Analyze(b.Parse(), []);
        Assert.Equal(["dotnet-test", "dotnet-build"], a.VerificationChecks.Select(v => v.Kind));
        Assert.Equal(1, a.AgentQuestions);
        Assert.Equal(2, a.PermissionDenials);
        Assert.Equal(["tada:tada"], a.SkillsInvoked);
    }

    [Fact]
    public void Activation_checks_detect_missing_and_cross_contaminating_components()
    {
        var c = TestSupport.RealConfig();
        var tadaInit = new InitRecord { Plugins = ["tada"], Skills = ["tada:tada", "speckit-plan"] };
        var findings = WorkflowSetup.DynamicChecks(c.Workflow("tada"), tadaInit);
        Assert.Contains(findings, f => f.StartsWith("FORBIDDEN component 'speckit-plan'", StringComparison.Ordinal));
        var specInit = new InitRecord { Skills = ["speckit-specify", "speckit-plan"] };
        var specFindings = WorkflowSetup.DynamicChecks(c.Workflow("speckit"), specInit);
        Assert.Contains(specFindings, f => f.Contains("MISSING skill 'speckit-implement'", StringComparison.Ordinal));
        Assert.DoesNotContain(specFindings, f => f.StartsWith("FORBIDDEN", StringComparison.Ordinal));
    }
}

public sealed class ReportAndIntegrityTests
{
    [Fact]
    public async Task Dry_run_generates_labelled_synthetic_report_end_to_end()
    {
        var outDir = TestSupport.TempDir("bench-dry");
        var result = await DryRunner.RunAsync(TestSupport.HarnessRoot, outDir, _ => { });
        var md = File.ReadAllText(result.MarkdownPath);
        Assert.Contains("SYNTHETIC DATA", md, StringComparison.Ordinal);
        Assert.Contains("SYNTHETIC DEMONSTRATION", result.Report.Classification, StringComparison.Ordinal);
        Assert.Contains("No weighted score or winner is computed", md, StringComparison.Ordinal);
        Assert.Contains("/ 13", md, StringComparison.Ordinal); // denominators shown
        Assert.Contains(result.Report.Integrity, c => c.Id == "synthetic" && c.Status == CheckStatus.Fail);
        Assert.Contains(result.Report.Integrity, c => c.Id == "evaluator-approved" && c.Status == CheckStatus.Fail);
        Assert.Equal(2, result.Report.Workflows.Count);
        Assert.All(result.Report.Workflows, w => Assert.Equal(2, w.Runs));
        Assert.Equal(4, result.Report.Runs.Count);
        Assert.All(result.Report.Runs, r => Assert.True(r.EvidenceIntact));
        var crashed = result.Report.Runs.Single(r => r.RunId == "SYNTHETIC-tada-2");
        Assert.False(crashed.TelemetryComplete);
        Assert.Equal(1, crashed.Regressions);
        Assert.Contains("HT02_ConcurrentDuplicateSubmissions_CreateExactlyOneOrder", crashed.CriticalFailures);
        Assert.True(File.Exists(result.JsonPath));
        var json = Json.Read<ComparisonReport>(result.JsonPath);
        Assert.True(json.Synthetic);
    }

    [Fact]
    public void Integrity_flags_model_effort_and_permission_differences()
    {
        var c = TestSupport.RealConfig();
        RunRecord Run(string id, string model, string effort, string policy) => new()
        {
            RunId = id, WorkflowId = id, BaseCommit = "b", ApplicationCommit = "a", PromptSha256 = "p", InitialMessage = c.PromptText,
            Agent = new AgentSettingsSnapshot { Model = model, Effort = effort, PermissionPolicySha256 = policy, SettingSources = "project,local" },
        };
        var runs = new List<RunRecord> { Run("speckit", "claude-opus-5-5", "high", "x"), Run("tada", "claude-sonnet-5-5", "max", "y") };
        var metrics = runs.Select(r => new RunMetrics { RunId = r.RunId, WorkflowId = r.WorkflowId }).ToList();
        var checks = IntegrityChecker.Check(c, runs, metrics, new DecisionRegister(), [[], []]);
        Assert.Equal(CheckStatus.Fail, checks.Single(x => x.Id == "model-requested").Status);
        Assert.Equal(CheckStatus.Fail, checks.Single(x => x.Id == "effort-requested").Status);
        Assert.Equal(CheckStatus.Fail, checks.Single(x => x.Id == "permissions").Status);
        Assert.Equal(CheckStatus.Pass, checks.Single(x => x.Id == "base-commit").Status);
        Assert.Equal(CheckStatus.Pass, checks.Single(x => x.Id == "prompt-delivery").Status);
    }

    [Fact]
    public void Integrity_flags_ad_hoc_answers_and_wrapped_prompts()
    {
        var c = TestSupport.RealConfig();
        var runs = new List<RunRecord>
        {
            new() { RunId = "a", WorkflowId = "speckit", InitialMessage = "/speckit-specify " + c.PromptText },
            new() { RunId = "b", WorkflowId = "tada", InitialMessage = c.PromptText },
        };
        var metrics = runs.Select(r => new RunMetrics { RunId = r.RunId, WorkflowId = r.WorkflowId }).ToList();
        var checks = IntegrityChecker.Check(c, runs, metrics, new DecisionRegister(),
            [[new HumanEvent { Kind = InteractionKind.ClarificationAnswer }], []]);
        Assert.Equal(CheckStatus.Warn, checks.Single(x => x.Id == "prompt-delivery").Status);
        Assert.Equal(CheckStatus.Warn, checks.Single(x => x.Id == "decisions").Status);
    }

    [Fact]
    public void Stat_computes_median_and_range()
    {
        var s = Stat.Of([3, 1, null, 2, 10]);
        Assert.Equal(4, s.N);
        Assert.Equal(2.5, s.Median);
        Assert.Equal(1, s.Min);
        Assert.Equal(10, s.Max);
        Assert.Equal(0, Stat.Of([]).N);
    }
}
