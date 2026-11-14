using Benchmark.Config;
using Benchmark.Human;
using Benchmark.Runs;
using Benchmark.Util;

namespace Benchmark.Reporting;

public enum CheckStatus { Pass, Warn, Fail, Unknown }

public sealed record IntegrityCheck(string Id, string Description, CheckStatus Status, string Detail);

/// <summary>Validates that the compared runs are comparable. Differences are reported, never hidden.</summary>
public static class IntegrityChecker
{
    public static List<IntegrityCheck> Check(ExperimentConfig c, IReadOnlyList<RunRecord> runs, IReadOnlyList<RunMetrics> metrics, DecisionRegister decisions, IReadOnlyList<IReadOnlyList<HumanEvent>> humanEvents)
    {
        var checks = new List<IntegrityCheck>();
        void Same<T>(string id, string description, Func<RunRecord, T> selector, CheckStatus onMismatch = CheckStatus.Fail)
        {
            var distinct = runs.Select(selector).Select(v => v?.ToString() ?? "<null>").Distinct().ToList();
            checks.Add(new IntegrityCheck(id, description, distinct.Count <= 1 ? CheckStatus.Pass : onMismatch,
                distinct.Count <= 1 ? distinct.FirstOrDefault() ?? "" : string.Join(" | ", runs.Select(r => $"{r.RunId}: {selector(r)}"))));
        }

        if (runs.Count == 0) return [new IntegrityCheck("runs", "Runs to compare", CheckStatus.Fail, "No runs.")];
        Same("base-commit", "1. Same application starting revision (frozen base incl. compatibility patches)", r => r.BaseCommit);
        Same("app-commit", "1b. Same pinned upstream commit", r => r.ApplicationCommit);
        Same("prompt", "2. Same natural-language task (SHA-256 of prompt)", r => r.PromptSha256);
        var promptDelivered = runs.All(r => r.InitialMessage.Contains(c.PromptText, StringComparison.Ordinal));
        var wrapped = runs.Where(r => r.InitialMessage.Trim() != c.PromptText).Select(r => $"{r.RunId} ({r.WorkflowId})").ToList();
        checks.Add(new IntegrityCheck("prompt-delivery", "2b. Task text delivered verbatim in the first message",
            !promptDelivered ? CheckStatus.Fail : wrapped.Count > 0 ? CheckStatus.Warn : CheckStatus.Pass,
            wrapped.Count > 0 ? "Wrapped by workflow template: " + string.Join(", ", wrapped) : "Verbatim in all runs."));
        Same("model-requested", "3. Same requested model", r => r.Agent.Model);
        var mismatch = metrics.Where(m => m.ModelsObserved.Count > 0 && m.TelemetryWarnings.Any(w => w.StartsWith("MODEL_MISMATCH", StringComparison.Ordinal))).ToList();
        var anyModels = metrics.Any(m => m.ModelsObserved.Count > 0);
        checks.Add(new IntegrityCheck("model-observed", "3b. Main-thread model equals requested (no fallback/switch); detail lists all models incl. subagents",
            mismatch.Count > 0 ? CheckStatus.Fail : anyModels ? CheckStatus.Pass : CheckStatus.Unknown,
            string.Join("; ", metrics.Select(m => $"{m.RunId}: {string.Join(",", m.ModelsObserved)}"))));
        Same("effort-requested", "4. Same requested reasoning effort", r => r.Agent.Effort);
        var effortsKnown = metrics.Where(m => m.EffortsObserved.Count > 0).ToList();
        var effortBad = effortsKnown.Where(m => m.EffortsObserved.Any(e => !e.Equals(runs.First(r => r.RunId == m.RunId).Agent.Effort, StringComparison.OrdinalIgnoreCase))).ToList();
        checks.Add(new IntegrityCheck("effort-observed", "4b. Effective effort observed in telemetry",
            effortBad.Count > 0 ? CheckStatus.Fail : effortsKnown.Count == metrics.Count ? CheckStatus.Pass : CheckStatus.Unknown,
            string.Join("; ", metrics.Select(m => $"{m.RunId}: {(m.EffortsObserved.Count == 0 ? "not reported" : string.Join(",", m.EffortsObserved))}"))));
        Same("permissions", "5. Equivalent permission policy (mode + allow/deny rules, paths templated)", r => r.Agent.PermissionPolicySha256);
        Same("setting-sources", "5b. Same Claude Code setting sources (user-level plugins/hooks excluded equally)", r => r.Agent.SettingSources + "|strictMcp=" + r.Agent.StrictMcpConfig);
        Same("cli-version", "5c. Same Claude Code CLI version", r => r.Agent.ClaudeVersionReported);
        Same("dotnet", "6. Same .NET SDK", r => r.Environment.DotnetSdk);
        Same("os", "6b. Same OS", r => r.Environment.Os);
        Same("toolchain", "6c. Same auxiliary tooling (PowerShell 7, uv, LocalDB, API key presence)",
            r => $"pwsh={r.Environment.PowerShell7 ?? "none"};uv={r.Environment.Uv ?? "none"};localdb={(r.Environment.LocalDb is null ? "none" : "yes")};apikey={r.Environment.AnthropicApiKeyPresent}");
        Same("mode", "6d. Same execution mode", r => r.Mode);

        var suites = metrics.Select(m => m.EvaluatorSuiteSha256 ?? "<not evaluated>").Distinct().ToList();
        checks.Add(new IntegrityCheck("evaluator", "7. Same independent evaluator (hidden suite hash)",
            suites.Contains("<not evaluated>") ? CheckStatus.Unknown : suites.Count == 1 ? CheckStatus.Pass : CheckStatus.Fail, string.Join(" | ", suites)));
        checks.Add(new IntegrityCheck("evaluator-approved", "7b. Hidden acceptance criteria approved by benchmark owner",
            metrics.All(m => m.EvaluatorApproved) ? CheckStatus.Pass : CheckStatus.Fail,
            metrics.All(m => m.EvaluatorApproved) ? "Approved suite hash matched." : "Evaluated with a DRAFT/unapproved suite: results are not official."));

        var adHoc = humanEvents.SelectMany((evts, i) => evts.Where(e => e.Kind == InteractionKind.ClarificationAnswer && e.DecisionId is null).Select(_ => runs[i].RunId)).Distinct().ToList();
        var used = humanEvents.SelectMany(e => e).Select(e => e.DecisionId).OfType<string>()
            .Concat(metrics.SelectMany(m => m.Interaction.DecisionsMatched)).Distinct().ToList();
        var unapproved = used.Where(id => decisions.Decisions.FirstOrDefault(d => d.Id == id) is not { Status: "approved" }).ToList();
        checks.Add(new IntegrityCheck("decisions", "8. Consistent human business decisions (answers taken from the shared register)",
            adHoc.Count > 0 || unapproved.Count > 0 ? CheckStatus.Warn : CheckStatus.Pass,
            (adHoc.Count > 0 ? $"Ad-hoc answers not linked to a register decision in: {string.Join(", ", adHoc)}. " : "")
            + (unapproved.Count > 0 ? $"Decisions used before owner approval: {string.Join(", ", unapproved)}. " : "")
            + "Register answers found in each run: " + string.Join("; ", metrics.Select(m => $"{m.RunId}: [{string.Join(",", m.Interaction.DecisionsMatched.Union(m.Human.DecisionsUsed).OrderBy(x => x))}]"))
            + ". Paraphrased answers are not detected; compare them in chat.md."));
        var localWarnings = metrics.SelectMany(m => m.Interaction.Warnings.Where(w => w.StartsWith("Operator ran", StringComparison.Ordinal)).Select(w => $"{m.RunId}: {w}")).ToList();
        checks.Add(new IntegrityCheck("local-commands", "Operator did not change model, effort or permissions mid-run",
            localWarnings.Count > 0 ? CheckStatus.Warn : CheckStatus.Pass, localWarnings.Count > 0 ? string.Join(" || ", localWarnings) : "None detected."));

        checks.Add(new IntegrityCheck("activation", "Workflow loaded as intended, no cross-contamination",
            metrics.Any(m => m.ActivationStatus.Contains("FAIL", StringComparison.OrdinalIgnoreCase) || m.ActivationStatus.Contains("failed", StringComparison.Ordinal)) ? CheckStatus.Fail
            : metrics.Any(m => m.ActivationStatus.Contains("not-observable", StringComparison.Ordinal)) ? CheckStatus.Warn : CheckStatus.Pass,
            string.Join("; ", metrics.Select(m => $"{m.RunId}: {m.ActivationStatus}"))));
        checks.Add(new IntegrityCheck("isolation", "Agents did not reference the evaluator, harness or other workspaces",
            metrics.Any(m => m.IsolationViolations > 0) ? CheckStatus.Fail : CheckStatus.Pass,
            string.Join("; ", metrics.Select(m => $"{m.RunId}: {m.IsolationViolations} violation(s)"))));
        checks.Add(new IntegrityCheck("telemetry", "Telemetry complete for every run",
            metrics.All(m => m.TelemetryComplete) ? CheckStatus.Pass : CheckStatus.Warn,
            string.Join("; ", metrics.Where(m => !m.TelemetryComplete).Select(m => $"{m.RunId} incomplete"))));
        checks.Add(new IntegrityCheck("evidence", "Run evidence sealed and unmodified",
            metrics.All(m => m.EvidenceIntact) ? CheckStatus.Pass : CheckStatus.Fail,
            string.Join("; ", metrics.Where(m => !m.EvidenceIntact).Select(m => $"{m.RunId} not sealed or modified"))));
        var deviations = runs.SelectMany(r => r.Deviations.Select(d => $"{r.RunId}: {d}")).ToList();
        checks.Add(new IntegrityCheck("deviations", "Recorded run deviations",
            deviations.Count == 0 ? CheckStatus.Pass : CheckStatus.Warn, deviations.Count == 0 ? "None." : string.Join(" || ", deviations)));
        var synthetic = runs.Count(r => r.Synthetic);
        if (synthetic > 0)
            checks.Add(new IntegrityCheck("synthetic", "Synthetic data", CheckStatus.Fail, $"{synthetic} run(s) are SYNTHETIC fixtures, not real workflow executions."));
        return checks;
    }
}
