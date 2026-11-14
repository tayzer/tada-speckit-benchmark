using Benchmark.Config;
using Benchmark.Evaluation;
using Benchmark.Human;
using Benchmark.Runs;
using Benchmark.Telemetry;
using Benchmark.Util;
using Benchmark.Workspace;

namespace Benchmark.Reporting;

public sealed class EngineeringReview
{
    public string RunId { get; set; } = "";
    public string Reviewer { get; set; } = "";
    public bool Blinded { get; set; }
    /// <summary>Dimension -> score 1..5 (null = not assessed). See docs/engineering-quality-rubric.md.</summary>
    public Dictionary<string, int?> Scores { get; set; } = new();
    public Dictionary<string, string> Notes { get; set; } = new();
    public List<string> RisksDiscovered { get; set; } = [];
    public List<string> RisksMissed { get; set; } = [];
    public List<string> IncorrectAssumptions { get; set; } = [];
    public List<string> NecessaryQuestionsIdentified { get; set; } = [];
    public List<string> VerificationOmissions { get; set; } = [];
    public int? ReviewMinutes { get; set; }
    /// <summary>Human inputs from chat.md (H numbers) that corrected an agent mistake rather than answering a business question.</summary>
    public List<int> CorrectivePrompts { get; set; } = [];
    /// <summary>Human inputs (H numbers) that fixed tooling/environment problems rather than the task.</summary>
    public List<int> ToolingInterventions { get; set; } = [];
}

public sealed class RunMetrics
{
    public string RunId { get; set; } = "";
    public string WorkflowId { get; set; } = "";
    public bool Synthetic { get; set; }
    public string Status { get; set; } = "";
    public string? Mode { get; set; }
    public string? EvaluationId { get; set; }
    public string? EvaluatorSuiteSha256 { get; set; }
    public bool EvaluatorApproved { get; set; }

    // Correctness
    public int HiddenTotal { get; set; }
    public int HiddenPass { get; set; }
    public int HiddenFail { get; set; }
    public int HiddenIndeterminate { get; set; }
    public int HiddenNotEvaluated { get; set; }
    public List<string> CriticalFailures { get; set; } = [];
    public Dictionary<string, string> HiddenOutcomes { get; set; } = new();
    public bool? ImplementationBuilds { get; set; }
    public int? Regressions { get; set; }
    public int? OriginalBaselinePassing { get; set; }
    public bool? MutationDetected { get; set; }
    public string? MutationDetail { get; set; }

    // Efficiency
    public double? WallClockSeconds { get; set; }
    public double? AgentExecutionSeconds { get; set; }
    public string? AgentExecutionBasis { get; set; }
    public double HumanActiveSeconds { get; set; }
    public double? EnvironmentSetupSeconds { get; set; }
    public double? WorkflowSetupSeconds { get; set; }
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public long? CacheReadTokens { get; set; }
    public long? CacheCreationTokens { get; set; }
    public long? SubagentPromptTokens { get; set; }
    public int? ApiCalls { get; set; }
    public decimal? CliReportedCostUsd { get; set; }
    public decimal? EstimatedCostUsd { get; set; }
    public bool TelemetryComplete { get; set; }
    public List<string> ModelsObserved { get; set; } = [];
    public List<string> EffortsObserved { get; set; } = [];

    // Workflow quality
    public int AgentQuestionsViaTool { get; set; }
    public int HumanMessages { get; set; }
    public HumanEffortSummary Human { get; set; } = new();
    /// <summary>Derived automatically from the transcript.</summary>
    public InteractionSummary Interaction { get; set; } = new();
    public double HumanWaitSeconds { get; set; }
    /// <summary>From the engineering review when recorded, else from the optional operator log; null when neither exists.</summary>
    public int? CorrectivePrompts { get; set; }
    public int? ToolingInterventions { get; set; }
    public string? ChatExport { get; set; }
    public Dictionary<string, int> VerificationChecks { get; set; } = new();
    public int PermissionDenials { get; set; }
    public int? ProductionFilesChanged { get; set; }
    public int? TestFilesChanged { get; set; }
    public int? ExistingTestFilesModifiedOrDeleted { get; set; }
    public int? WorkflowArtifactFiles { get; set; }
    public int? LinesAdded { get; set; }
    public int? LinesRemoved { get; set; }
    public List<string> ChangedFiles { get; set; } = [];
    public List<string> SkillsInvoked { get; set; } = [];
    public EngineeringReview? Review { get; set; }

    // Integrity inputs
    public List<string> Deviations { get; set; } = [];
    public string ActivationStatus { get; set; } = "";
    public int IsolationViolations { get; set; }
    public List<string> TelemetryWarnings { get; set; } = [];
    public List<string> TelemetryUnavailable { get; set; } = [];
    public bool EvidenceIntact { get; set; }
}

public static class MetricsCollector
{
    public static RunMetrics Collect(ExperimentConfig c, RunStore store, RunRecord run, HiddenCatalogFile? catalog)
    {
        var m = new RunMetrics
        {
            RunId = run.RunId, WorkflowId = run.WorkflowId, Synthetic = run.Synthetic, Status = run.Status.ToString(),
            Mode = run.Mode?.ToString(), Deviations = run.Deviations.ToList(),
            EnvironmentSetupSeconds = ParseEnvSetup(run.Setup.Steps), WorkflowSetupSeconds = run.Setup.SetupSeconds,
        };
        if (run.Invocations.Count > 0)
            m.WallClockSeconds = (run.Invocations.Max(i => i.EndedAt) - run.Invocations.Min(i => i.StartedAt)).TotalSeconds;
        m.EvidenceIntact = store.IsSealed(run.RunId) && store.Verify(run.RunId).Count == 0;

        var telemetryPath = store.EvidencePath(run.RunId, "telemetry.json");
        if (File.Exists(telemetryPath))
        {
            var t = Json.Read<TelemetryRecord>(telemetryPath);
            var u = t.Usage;
            m.InputTokens = u.Totals.Input; m.OutputTokens = u.Totals.Output;
            m.CacheReadTokens = u.Totals.CacheRead; m.CacheCreationTokens = u.Totals.CacheCreation;
            m.SubagentPromptTokens = u.Subagents.Tokens.TotalPromptTokens;
            m.ApiCalls = u.ApiCalls; m.CliReportedCostUsd = u.CliReportedCostUsd; m.EstimatedCostUsd = u.EstimatedCostUsd;
            m.TelemetryComplete = u.Complete; m.ModelsObserved = u.AllObservedModels; m.EffortsObserved = u.ObservedEfforts;
            m.TelemetryWarnings = u.Warnings; m.TelemetryUnavailable = t.Unavailable;
            m.AgentExecutionSeconds = t.AgentExecutionSeconds; m.AgentExecutionBasis = t.AgentExecutionBasis;
            m.AgentQuestionsViaTool = t.Activity.AgentQuestions; m.HumanMessages = t.Activity.HumanMessages;
            m.VerificationChecks = t.Activity.VerificationChecks.GroupBy(v => v.Kind).ToDictionary(g => g.Key, g => g.Count());
            m.PermissionDenials = t.Activity.PermissionDenials; m.SkillsInvoked = t.Activity.SkillsInvoked;
            m.ActivationStatus = run.Setup.StaticActivationPassed ? $"static ok; dynamic {t.DynamicActivationStatus}" : "static FAILED";
            if (t.DynamicActivationFindings.Count > 0) m.ActivationStatus += ": " + string.Join("; ", t.DynamicActivationFindings);
            m.IsolationViolations = t.Activity.IsolationViolations.Count;
            if (t.Interaction is { } ia) { m.Interaction = ia; m.HumanWaitSeconds = ia.HumanWaitSeconds; }
            if (t.ChatExport is not null) m.ChatExport = store.EvidencePath(run.RunId, t.ChatExport);
        }
        var changesPath = store.EvidencePath(run.RunId, "changes", "changes.json");
        if (File.Exists(changesPath))
        {
            var ch = Json.Read<ChangeSummary>(changesPath);
            m.ProductionFilesChanged = ch.ProductionFilesChanged; m.TestFilesChanged = ch.TestFilesChanged;
            m.ExistingTestFilesModifiedOrDeleted = ch.ExistingTestFilesModifiedOrDeleted; m.WorkflowArtifactFiles = ch.WorkflowArtifactFiles;
            m.LinesAdded = ch.LinesAdded; m.LinesRemoved = ch.LinesRemoved;
            m.ChangedFiles = ch.Files.Select(f => $"{f.Status} {f.Path}").ToList();
        }
        var human = new HumanLog(store.EvidencePath(run.RunId, "human-events.jsonl"));
        m.Human = HumanLog.Summarise(human.Read(), run.FinishedAt);
        m.HumanActiveSeconds = m.Human.TotalActiveSeconds;

        var eval = LatestEvaluation(store, run.RunId);
        if (eval is not null)
        {
            m.EvaluationId = eval.EvaluationId;
            m.EvaluatorSuiteSha256 = eval.Evaluator.HiddenSuiteSha256;
            m.EvaluatorApproved = eval.Evaluator.SuiteMatchesApproval;
            m.ImplementationBuilds = eval.ImplementationBuilds;
            var tests = eval.Hidden?.Tests ?? [];
            var catalogIds = catalog?.Tests.Select(t => t.Id).ToList() ?? tests.Select(t => t.Id).ToList();
            m.HiddenTotal = catalogIds.Count;
            foreach (var id in catalogIds)
            {
                var t = tests.FirstOrDefault(x => x.Id == id || x.Name == id || x.Name.EndsWith("." + id, StringComparison.Ordinal) || x.Id.EndsWith("." + id, StringComparison.Ordinal));
                var outcome = t is null ? "NOT-EVALUATED" : t.Outcome.ToString().ToUpperInvariant() + (t.Cause is FailureCause.None or FailureCause.Behaviour ? "" : $" ({HumanLog.KebabName(t.Cause)})");
                m.HiddenOutcomes[id] = outcome;
                switch (t?.Outcome)
                {
                    case TestOutcome.Pass: m.HiddenPass++; break;
                    case TestOutcome.Fail: m.HiddenFail++; break;
                    case TestOutcome.Indeterminate: m.HiddenIndeterminate++; break;
                    default: m.HiddenNotEvaluated++; break;
                }
                if (t?.Outcome == TestOutcome.Fail && catalog?.Tests.FirstOrDefault(x => x.Id == id)?.Critical == true) m.CriticalFailures.Add(id);
            }
            m.Regressions = eval.Regressions.Count;
            var baseRecordPath = BasePreparer.BaseFile(store, c);
            if (File.Exists(baseRecordPath)) m.OriginalBaselinePassing = Json.Read<BaseRecord>(baseRecordPath).BaselineTests.Sum(t => t.Passed);
            m.MutationDetected = eval.RevertedProductionControl?.Detected;
            m.MutationDetail = eval.RevertedProductionControl?.Detail;
        }
        var reviewPath = Path.Combine(store.DataRoot, "reviews", run.RunId + ".json");
        if (File.Exists(reviewPath)) m.Review = Json.Read<EngineeringReview>(reviewPath);
        var logged = human.Read().Count > 0;
        var reviewed = m.Review is { Reviewer.Length: > 0 } || m.Review is { CorrectivePrompts.Count: > 0 } || m.Review is { ToolingInterventions.Count: > 0 };
        m.CorrectivePrompts = reviewed ? m.Review!.CorrectivePrompts.Count : logged ? m.Human.CorrectiveInterventions : null;
        m.ToolingInterventions = reviewed ? m.Review!.ToolingInterventions.Count : logged ? m.Human.ToolingInterventions : null;
        return m;
    }

    public static EvaluationRecord? LatestEvaluation(RunStore store, string runId)
    {
        if (!Directory.Exists(store.EvaluationsRoot)) return null;
        return Directory.GetDirectories(store.EvaluationsRoot)
            .Select(d => Path.Combine(d, "evaluation.json")).Where(File.Exists)
            .Select(Json.Read<EvaluationRecord>).Where(e => e.RunId == runId)
            .OrderBy(e => e.EndedAt).LastOrDefault();
    }

    private static double? ParseEnvSetup(IEnumerable<string> steps)
    {
        var s = steps.FirstOrDefault(x => x.StartsWith("Environment setup", StringComparison.Ordinal));
        if (s is null) return null;
        var token = s.Split(' ').LastOrDefault()?.TrimEnd('s');
        return double.TryParse(token, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}

public sealed record Stat(int N, double? Median, double? Min, double? Max)
{
    public static Stat Of(IEnumerable<double?> values)
    {
        var v = values.Where(x => x.HasValue).Select(x => x!.Value).OrderBy(x => x).ToList();
        if (v.Count == 0) return new Stat(0, null, null, null);
        var median = v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2.0;
        return new Stat(v.Count, median, v[0], v[^1]);
    }
}
