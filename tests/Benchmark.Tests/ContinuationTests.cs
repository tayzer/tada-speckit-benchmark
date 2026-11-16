using Benchmark.Config;
using Benchmark.Evaluation;
using Benchmark.Human;
using Benchmark.Reporting;
using Benchmark.Runs;
using Benchmark.Util;
using Xunit;

namespace Benchmark.Tests;

public sealed class ContinuationTests
{
    private static ExperimentConfig ReloadConfig() =>
        ExperimentConfig.Load(Path.Combine(TestSupport.HarnessRoot, "experiments", "checkout-reload"));

    [Fact]
    public void Shipped_continuation_experiment_is_valid_and_pins_one_parent_per_workflow()
    {
        var c = ReloadConfig();
        Assert.Empty(ConfigValidator.Validate(c));
        Assert.NotNull(c.Continuation);
        Assert.Equal("checkout-retry", c.Continuation!.ParentExperiment);
        Assert.Equal(c.Workflows.Select(w => w.Id).OrderBy(x => x), c.Continuation.ParentRuns.Keys.OrderBy(x => x));
        // The regression suite is the parent experiment's approved suite, byte for byte.
        var parent = TestSupport.RealConfig();
        Assert.Equal(parent.Evaluation.ApprovedSuiteSha256, c.Continuation.RegressionSuiteSha256);
        Assert.Equal(Fs.Sha256Directory(c.ResolveHarnessPath(c.Continuation.RegressionSuite)), c.Continuation.RegressionSuiteSha256);
        // Same agent settings and workflow pins as the parent experiment.
        Assert.Equal(Json.Serialize(parent.Agent), Json.Serialize(c.Agent));
        Assert.Equal(Json.Serialize(parent.Workflows), Json.Serialize(c.Workflows));
    }

    [Fact]
    public void Missing_parent_for_a_workflow_is_a_config_error()
    {
        var c = ReloadConfig();
        c.Continuation!.ParentRuns.Remove("tada");
        Assert.Contains(ConfigValidator.Validate(c), e => e.Contains("no parent run for workflow 'tada'"));
    }

    [Fact]
    public void Integrity_requires_each_arm_to_continue_its_pinned_parent()
    {
        var c = ReloadConfig();
        RunRecord Run(string workflow, string? parent) => new()
        {
            RunId = workflow + "-run", WorkflowId = workflow, ParentRunId = parent, ParentFinalCommit = "0123456789abcdef",
            InitialMessage = c.PromptText, Agent = new AgentSettingsSnapshot { SettingSources = "project,local" },
        };
        var good = new List<RunRecord> { Run("speckit", c.Continuation!.ParentRuns["speckit"]), Run("tada", c.Continuation.ParentRuns["tada"]) };
        var metrics = good.Select(r => new RunMetrics { RunId = r.RunId, WorkflowId = r.WorkflowId }).ToList();
        Assert.Equal(CheckStatus.Pass, IntegrityChecker.Check(c, good, metrics, new DecisionRegister(), [[], []]).Single(x => x.Id == "base-commit").Status);

        var bad = new List<RunRecord> { Run("speckit", c.Continuation.ParentRuns["speckit"]), Run("tada", "some-other-run") };
        Assert.Equal(CheckStatus.Fail, IntegrityChecker.Check(c, bad, metrics, new DecisionRegister(), [[], []]).Single(x => x.Id == "base-commit").Status);
    }

    [Fact]
    public void Evaluations_are_selected_per_experiment_and_legacy_records_count_as_the_runs_own()
    {
        var data = TestSupport.TempDir("eval-lookup");
        try
        {
            var store = new RunStore(data);
            void Eval(string id, string? experiment, int minute) =>
                Json.Write(Path.Combine(store.EvaluationsRoot, id, "evaluation.json"), new EvaluationRecord
                {
                    EvaluationId = id, RunId = "r1", ExperimentId = experiment, EndedAt = new DateTimeOffset(2026, 11, 16, 10, minute, 0, TimeSpan.Zero),
                });
            Eval("legacy", null, 1);
            Eval("reload", "checkout-reload", 2);
            Assert.Equal("legacy", MetricsCollector.LatestEvaluation(store, "r1", "checkout-retry", "checkout-retry")!.EvaluationId);
            Assert.Equal("reload", MetricsCollector.LatestEvaluation(store, "r1", "checkout-reload", "checkout-retry")!.EvaluationId);
            Eval("retry-new", "checkout-retry", 3);
            Assert.Equal("retry-new", MetricsCollector.LatestEvaluation(store, "r1", "checkout-retry", "checkout-retry")!.EvaluationId);
        }
        finally { Directory.Delete(data, recursive: true); }
    }

    [Fact]
    public void Regressions_against_a_parent_count_only_tests_that_passed_for_the_parent()
    {
        var parent = new List<TestProjectResult>
        {
            new() { Project = "tests/__HiddenRegression/S.csproj", Tests =
            [
                new TestCaseResult { Id = "HT01", Name = "HT01", Outcome = TestOutcome.Pass },
                new TestCaseResult { Id = "HT08", Name = "HT08", Outcome = TestOutcome.Fail },
            ] },
        };
        var now = new List<TestProjectResult>
        {
            new() { Project = "tests/__HiddenRegression/S.csproj", Tests =
            [
                new TestCaseResult { Id = "HT01", Name = "HT01", Outcome = TestOutcome.Fail },
                new TestCaseResult { Id = "HT08", Name = "HT08", Outcome = TestOutcome.Fail },
            ] },
        };
        var r = Evaluator.Regressions(parent, now);
        Assert.Single(r);
        Assert.Equal("HT01", r[0].Test);
    }
}
