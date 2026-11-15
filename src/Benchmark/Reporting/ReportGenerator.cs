using System.Globalization;
using System.Text;
using Benchmark.Config;
using Benchmark.Evaluation;
using Benchmark.Human;
using Benchmark.Runs;
using Benchmark.Util;

namespace Benchmark.Reporting;

public sealed class WorkflowAggregate
{
    public string WorkflowId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Version { get; set; } = "";
    public int Runs { get; set; }
    public Dictionary<string, Stat> Stats { get; set; } = new();
}

public sealed class ComparisonReport
{
    public string ReportId { get; set; } = "";
    public DateTimeOffset GeneratedAt { get; set; }
    public string ExperimentId { get; set; } = "";
    public bool Synthetic { get; set; }
    public string Classification { get; set; } = "";
    public string HarnessCommit { get; set; } = "";
    public List<IntegrityCheck> Integrity { get; set; } = [];
    public List<WorkflowAggregate> Workflows { get; set; } = [];
    public List<RunMetrics> Runs { get; set; } = [];
    public List<string> HiddenTests { get; set; } = [];
    public List<string> Limitations { get; set; } = [];
}

public static class ReportGenerator
{
    private static readonly (string Key, Func<RunMetrics, double?> Get)[] NumericMetrics =
    [
        ("hiddenPass", m => m.HiddenPass), ("hiddenFail", m => m.HiddenFail), ("hiddenIndeterminate", m => m.HiddenIndeterminate),
        ("regressions", m => m.Regressions), ("criticalFailures", m => m.CriticalFailures.Count),
        ("wallClockMinutes", m => m.WallClockSeconds / 60), ("agentExecutionMinutes", m => m.AgentExecutionSeconds / 60),
        ("humanWaitMinutes", m => m.HumanWaitSeconds / 60), ("humanInputs", m => m.Interaction.TotalHumanInputs), ("workflowSetupSeconds", m => m.WorkflowSetupSeconds),
        ("inputTokens", m => m.InputTokens), ("outputTokens", m => m.OutputTokens), ("cacheReadTokens", m => m.CacheReadTokens),
        ("cacheCreationTokens", m => m.CacheCreationTokens), ("cliReportedCostUsd", m => (double?)m.CliReportedCostUsd),
        ("estimatedCostUsd", m => (double?)m.EstimatedCostUsd), ("correctivePrompts", m => m.CorrectivePrompts),
        ("agentQuestions", m => m.Interaction.AgentQuestions), ("toolRejections", m => m.Interaction.ToolRejections),
        ("toolingInterventions", m => m.ToolingInterventions), ("verificationChecks", m => m.VerificationChecks.Values.Sum()),
        ("testFilesChanged", m => m.TestFilesChanged), ("productionFilesChanged", m => m.ProductionFilesChanged),
    ];

    public static ComparisonReport Build(ExperimentConfig c, RunStore store, IReadOnlyList<RunRecord> runs, string harnessCommit)
    {
        var catalogPath = Path.Combine(c.HiddenSuitePath, "catalog.json");
        var catalog = File.Exists(catalogPath) ? Json.Read<HiddenCatalogFile>(catalogPath) : null;
        var metrics = runs.Select(r => MetricsCollector.Collect(c, store, r, catalog)).ToList();
        var decisions = DecisionRegister.Load(c.ResolveExperimentPath(c.DecisionsFile));
        var humanEvents = runs.Select(r => new HumanLog(store.EvidencePath(r.RunId, "human-events.jsonl")).Read()).ToList();
        var synthetic = runs.Any(r => r.Synthetic);
        var perWorkflow = metrics.GroupBy(m => m.WorkflowId).ToDictionary(g => g.Key, g => g.Count());
        var report = new ComparisonReport
        {
            ReportId = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-comparison",
            GeneratedAt = DateTimeOffset.UtcNow,
            ExperimentId = c.ExperimentId,
            Synthetic = synthetic,
            HarnessCommit = harnessCommit,
            Runs = metrics,
            HiddenTests = catalog?.Tests.Select(t => t.Id).ToList() ?? [],
            Integrity = IntegrityChecker.Check(c, runs, metrics, decisions, humanEvents),
            Classification = synthetic
                ? "SYNTHETIC DEMONSTRATION — fixture data, not real workflow executions. No conclusions may be drawn."
                : perWorkflow.Values.DefaultIfEmpty(0).Min() < 5
                    ? $"DEMONSTRATION — {string.Join(", ", perWorkflow.Select(kv => $"{kv.Key} n={kv.Value}"))}. Not statistically meaningful evidence."
                    : $"Repeated runs ({string.Join(", ", perWorkflow.Select(kv => $"{kv.Key} n={kv.Value}"))}). Descriptive statistics only; no significance testing is performed.",
        };
        foreach (var g in metrics.GroupBy(m => m.WorkflowId))
        {
            var w = c.Workflows.FirstOrDefault(x => x.Id == g.Key);
            // Label with the version the runs actually used (recorded at setup), not the current pin.
            var recorded = runs.Where(r => r.WorkflowId == g.Key).Select(r => r.Setup.Version).Where(v => !string.IsNullOrEmpty(v)).Distinct().ToList();
            var version = recorded.Count switch { 0 => w?.Version ?? "", 1 => recorded[0], _ => "MIXED: " + string.Join(" | ", recorded) };
            report.Workflows.Add(new WorkflowAggregate
            {
                WorkflowId = g.Key, DisplayName = w?.DisplayName ?? g.Key, Version = version, Runs = g.Count(),
                Stats = NumericMetrics.ToDictionary(x => x.Key, x => Stat.Of(g.Select(x.Get))),
            });
        }
        report.Limitations.AddRange(StandardLimitations);
        report.Limitations.AddRange(metrics.SelectMany(m => m.TelemetryUnavailable).Distinct());
        return report;
    }

    public static readonly string[] StandardLimitations =
    [
        "No weighted score or winner is computed; raw results and denominators are shown.",
        "Claude Code reported costs are client-side list-price estimates; model-equivalent costs come from the configured pricing table. Neither is subscription expenditure.",
        "Interactive-session telemetry is read from Claude Code's internal transcript format, which is undocumented and may change between CLI versions.",
        "Human time is estimated from transcript timestamps: the time the agent sat idle waiting for each human input (long gaps capped). Reading done while the agent works, and approving permission prompts, are not counted.",
        "Human inputs are counted automatically; whether a message was a correction or a business answer is a reviewer judgement recorded in the review file.",
        "Engineering-quality scores are human judgements using docs/engineering-quality-rubric.md, not automated measurements.",
        "Hidden tests check observable outcomes (orders persisted, responses) against owner-approved expectations; they cannot prove absence of other defects.",
    ];

    public static string Markdown(ComparisonReport r)
    {
        var sb = new StringBuilder();
        var wf = r.Workflows.OrderBy(w => w.WorkflowId).ToList();
        var runsByWf = wf.ToDictionary(w => w.WorkflowId, w => r.Runs.Where(m => m.WorkflowId == w.WorkflowId).ToList());
        sb.AppendLine($"# Benchmark comparison — {r.ExperimentId}");
        sb.AppendLine();
        if (r.Synthetic) sb.AppendLine("> **SYNTHETIC DATA.** Every number below comes from fixtures generated for a dry run of the harness. It says nothing about TADA or Spec Kit.\n");
        sb.AppendLine($"**Classification:** {r.Classification}  ");
        sb.AppendLine($"Generated {r.GeneratedAt:u} · harness `{r.HarnessCommit}` · report `{r.ReportId}`");
        sb.AppendLine();
        sb.AppendLine("Workflows: " + string.Join(" · ", wf.Select(w => $"**{w.DisplayName}** `{w.Version}` (n={w.Runs})")));
        sb.AppendLine();

        sb.AppendLine("## Integrity checks");
        sb.AppendLine();
        sb.AppendLine("| Check | Status | Detail |");
        sb.AppendLine("|---|---|---|");
        foreach (var c in r.Integrity) sb.AppendLine($"| {Esc(c.Description)} | **{c.Status.ToString().ToUpperInvariant()}** | {Esc(Clip(c.Detail, 400))} |");
        sb.AppendLine();

        Table(sb, "## Correctness (hidden behavioural suite and existing tests)", wf, runsByWf,
        [
            ("Hidden tests PASS / total", m => $"{m.HiddenPass} / {m.HiddenTotal}"),
            ("Hidden tests FAIL", m => m.HiddenFail.ToString()),
            ("Hidden tests INDETERMINATE", m => m.HiddenIndeterminate.ToString()),
            ("Hidden tests not evaluated", m => m.HiddenNotEvaluated.ToString()),
            ("Critical requirements failed", m => m.CriticalFailures.Count == 0 ? "0" : $"{m.CriticalFailures.Count} ({string.Join(", ", m.CriticalFailures)})"),
            ("Implementation builds", m => Fmt(m.ImplementationBuilds)),
            ("Regressions in original suite (baseline-passing tests)", m => m.Regressions is null ? "n/a" : $"{m.Regressions} of {m.OriginalBaselinePassing}"),
            ("Own tests detect reverted fix (mutation control)", m => m.MutationDetected is null ? "not run" : m.MutationDetected.Value ? "yes" : "NO"),
            ("Evaluator suite approved", m => m.EvaluatorApproved ? "yes" : "NO (draft)"),
        ]);

        if (r.HiddenTests.Count > 0)
        {
            sb.AppendLine("### Hidden test outcomes");
            sb.AppendLine();
            sb.AppendLine("| Test | " + string.Join(" | ", r.Runs.Select(m => $"{m.WorkflowId}<br>`{Short(m.RunId)}`")) + " |");
            sb.AppendLine("|---|" + string.Join("", r.Runs.Select(_ => "---|")));
            foreach (var t in r.HiddenTests)
                sb.AppendLine($"| `{t}` | " + string.Join(" | ", r.Runs.Select(m => m.HiddenOutcomes.GetValueOrDefault(t, "NOT-EVALUATED"))) + " |");
            sb.AppendLine();
        }

        Table(sb, "## Efficiency", wf, runsByWf,
        [
            ("Total wall-clock (session span)", m => Dur(m.WallClockSeconds)),
            ("Agent execution time", m => Dur(m.AgentExecutionSeconds)),
            ("Human time (agent waiting on human, from transcript)", m => Dur(m.HumanWaitSeconds)),
            ("Environment setup (clone + restore)", m => Dur(m.EnvironmentSetupSeconds)),
            ("Workflow-specific setup", m => Dur(m.WorkflowSetupSeconds)),
            ("Input tokens (uncached)", m => N(m.InputTokens)),
            ("Output tokens", m => N(m.OutputTokens)),
            ("Cache-read input tokens", m => N(m.CacheReadTokens)),
            ("Cache-creation input tokens", m => N(m.CacheCreationTokens)),
            ("…of which subagent prompt tokens (already included)", m => N(m.SubagentPromptTokens)),
            ("API calls (unique responses)", m => N(m.ApiCalls)),
            ("Claude Code reported cost (estimate)", m => Usd(m.CliReportedCostUsd)),
            ("Model-equivalent cost (pricing table estimate)", m => Usd(m.EstimatedCostUsd)),
            ("Telemetry complete", m => m.TelemetryComplete ? "yes" : "NO"),
            ("Models observed", m => string.Join(", ", m.ModelsObserved)),
            ("Effort observed", m => m.EffortsObserved.Count == 0 ? "not reported" : string.Join(", ", m.EffortsObserved)),
        ]);

        Table(sb, "## Human involvement (derived from the transcript)", wf, runsByWf,
        [
            ("Human inputs, total", m => m.Interaction.TotalHumanInputs.ToString()),
            ("Typed messages", m => m.Interaction.HumanPrompts.ToString()),
            ("Slash commands", m => m.Interaction.SlashCommands == 0 ? "0" : $"{m.Interaction.SlashCommands} ({string.Join(", ", m.Interaction.SlashCommandNames)})"),
            ("Agent questions (AskUserQuestion) / answered", m => $"{m.Interaction.AgentQuestions} / {m.Interaction.QuestionsAnswered}"),
            ("Tool calls rejected", m => m.Interaction.ToolRejections.ToString()),
            ("Interruptions", m => m.Interaction.Interruptions.ToString()),
            ("Time agent waited on human", m => Dur(m.HumanWaitSeconds) + (m.Interaction.GapsCapped > 0 ? $" ({m.Interaction.GapsCapped} gap(s) capped)" : "")),
            ("Corrective prompts (from review)", m => m.CorrectivePrompts?.ToString() ?? "not reviewed"),
            ("Tooling interventions (from review)", m => m.ToolingInterventions?.ToString() ?? "not reviewed"),
            ("Registered decisions found in answers", m => m.Interaction.DecisionsMatched.Count == 0 ? "none" : string.Join(", ", m.Interaction.DecisionsMatched)),
            ("Local CLI commands run", m => m.Interaction.LocalCommands.Count == 0 ? "none" : string.Join(", ", m.Interaction.LocalCommands.Select(x => $"`{x}`"))),
            ("Chat export", m => m.ChatExport is null ? "n/a" : $"`{m.ChatExport}`"),
        ]);

        Table(sb, "## Workflow quality and change footprint", wf, runsByWf,
        [
            ("Verification commands run (by kind)", m => m.VerificationChecks.Count == 0 ? "none" : string.Join(", ", m.VerificationChecks.Select(kv => $"{kv.Key}×{kv.Value}"))),
            ("Production files changed", m => N(m.ProductionFilesChanged)),
            ("Test files added/changed", m => N(m.TestFilesChanged)),
            ("Existing test files modified/deleted", m => N(m.ExistingTestFilesModifiedOrDeleted)),
            ("Workflow artifact files", m => N(m.WorkflowArtifactFiles)),
            ("Lines added / removed", m => m.LinesAdded is null ? "n/a" : $"+{m.LinesAdded} / -{m.LinesRemoved}"),
            ("Skills invoked", m => m.SkillsInvoked.Count == 0 ? "none observed" : string.Join(", ", m.SkillsInvoked)),
        ]);

        Aggregates(sb, wf);

        sb.AppendLine("## Engineering quality (human review, rubric in docs/engineering-quality-rubric.md)");
        sb.AppendLine();
        var dims = r.Runs.Where(m => m.Review is not null).SelectMany(m => m.Review!.Scores.Keys).Distinct().ToList();
        if (dims.Count == 0) sb.AppendLine("_No engineering reviews recorded yet (`benchmark review-template --run <id>`)._\n");
        else
        {
            sb.AppendLine("| Dimension | " + string.Join(" | ", r.Runs.Select(m => $"{m.WorkflowId} `{Short(m.RunId)}`")) + " |");
            sb.AppendLine("|---|" + string.Join("", r.Runs.Select(_ => "---|")));
            foreach (var d in dims)
                sb.AppendLine($"| {d} | " + string.Join(" | ", r.Runs.Select(m => m.Review?.Scores.GetValueOrDefault(d)?.ToString() ?? "–")) + " |");
            sb.AppendLine();
            foreach (var m in r.Runs.Where(m => m.Review is not null))
            {
                var rv = m.Review!;
                sb.AppendLine($"- **{m.WorkflowId} `{Short(m.RunId)}`** (reviewer {rv.Reviewer}{(rv.Blinded ? ", blinded" : "")}): risks discovered {rv.RisksDiscovered.Count}, risks missed {rv.RisksMissed.Count}, incorrect assumptions {rv.IncorrectAssumptions.Count}, necessary questions identified {rv.NecessaryQuestionsIdentified.Count}, verification omissions {rv.VerificationOmissions.Count}.");
                foreach (var (k, v) in rv.Notes) sb.AppendLine($"  - {k}: {Esc(v)}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Per-run detail");
        sb.AppendLine();
        foreach (var m in r.Runs)
        {
            sb.AppendLine($"### {m.WorkflowId} — `{m.RunId}`{(m.Synthetic ? " (SYNTHETIC)" : "")}");
            sb.AppendLine();
            sb.AppendLine($"- Status {m.Status}, mode {m.Mode ?? "n/a"}, evaluation `{m.EvaluationId ?? "none"}`, activation: {m.ActivationStatus}");
            sb.AppendLine($"- Agent execution basis: {m.AgentExecutionBasis ?? "n/a"}");
            if (m.Deviations.Count > 0) sb.AppendLine("- Deviations: " + string.Join("; ", m.Deviations.Select(Esc)));
            if (m.TelemetryWarnings.Count > 0) sb.AppendLine("- Telemetry warnings: " + string.Join("; ", m.TelemetryWarnings.Take(8).Select(Esc)));
            if (m.Interaction.Warnings.Count > 0) sb.AppendLine("- Interaction warnings: " + string.Join("; ", m.Interaction.Warnings.Select(Esc)));
            if (m.Human.Warnings.Count > 0) sb.AppendLine("- Operator-log warnings: " + string.Join("; ", m.Human.Warnings.Select(Esc)));
            if (m.ChangedFiles.Count > 0) sb.AppendLine($"- Changed files ({m.ChangedFiles.Count}): " + string.Join(", ", m.ChangedFiles.Take(40).Select(f => $"`{f}`")) + (m.ChangedFiles.Count > 40 ? " …" : ""));
            if (m.MutationDetail is not null) sb.AppendLine($"- Mutation control: {m.MutationDetail}");
            sb.AppendLine();
        }

        sb.AppendLine("## Limitations and environmental differences");
        sb.AppendLine();
        foreach (var l in r.Limitations) sb.AppendLine($"- {Esc(l)}");
        return sb.ToString();
    }

    private static void Table(StringBuilder sb, string title, List<WorkflowAggregate> wf, Dictionary<string, List<RunMetrics>> runs, (string Label, Func<RunMetrics, string> Value)[] rows)
    {
        sb.AppendLine(title);
        sb.AppendLine();
        sb.AppendLine("| Metric | " + string.Join(" | ", wf.Select(w => $"{w.DisplayName} (n={w.Runs})")) + " |");
        sb.AppendLine("|---|" + string.Join("", wf.Select(_ => "---|")));
        foreach (var (label, value) in rows)
            sb.AppendLine($"| {label} | " + string.Join(" | ", wf.Select(w => string.Join("<br>", runs[w.WorkflowId].Select(m => runs[w.WorkflowId].Count > 1 ? $"`{Short(m.RunId)}` {Esc(value(m))}" : Esc(value(m)))))) + " |");
        sb.AppendLine();
    }

    private static void Aggregates(StringBuilder sb, List<WorkflowAggregate> wf)
    {
        var multi = wf.Where(w => w.Runs > 1).ToList();
        if (multi.Count == 0) return;
        sb.AppendLine("## Repeated-run aggregates (median [min–max], n)");
        sb.AppendLine();
        sb.AppendLine("Descriptive only; no significance testing and no weighting.");
        sb.AppendLine();
        sb.AppendLine("| Metric | " + string.Join(" | ", multi.Select(w => w.DisplayName)) + " |");
        sb.AppendLine("|---|" + string.Join("", multi.Select(_ => "---|")));
        foreach (var key in multi[0].Stats.Keys)
            sb.AppendLine($"| {key} | " + string.Join(" | ", multi.Select(w => StatText(w.Stats[key], "") + $" (n={w.Stats[key].N})")) + " |");
        sb.AppendLine();
    }

    private static string StatText(Stat s, string unit) => s.N == 0 ? "n/a" : $"{s.Median:0.#}{unit} [{s.Min:0.#}–{s.Max:0.#}]";
    private static string Fmt(bool? b) => b is null ? "n/a" : b.Value ? "yes" : "NO";
    private static string N(long? v) => v?.ToString("N0", CultureInfo.InvariantCulture) ?? "n/a";
    private static string N(int? v) => v?.ToString("N0", CultureInfo.InvariantCulture) ?? "n/a";
    private static string Usd(decimal? v) => v is null ? "unavailable" : "$" + v.Value.ToString("0.00", CultureInfo.InvariantCulture);
    private static string Dur(double? s) => s is null ? "n/a" : s < 120 ? $"{s:0}s" : s < 7200 ? $"{s / 60:0.0} min" : $"{s / 3600:0.00} h";
    private static string Short(string id) => id.Length > 26 ? id[..15] + "…" + id[^6..] : id;
    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";
    private static string Esc(string s) => s.Replace("|", "\\|").ReplaceLineEndings(" ");
}
