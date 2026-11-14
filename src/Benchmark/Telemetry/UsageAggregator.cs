using Benchmark.Config;

namespace Benchmark.Telemetry;

/// <summary>One managed `claude -p` process: its parsed stream-json output plus process metadata.</summary>
public sealed class InvocationCapture
{
    public string InvocationId { get; set; } = "";
    public bool IsResume { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public bool ProcessSucceeded { get; set; }
    public ParsedSession Parsed { get; set; } = new();
}

/// <summary>
/// Converts Claude Code telemetry into non-double-counted usage totals.
///
/// Accounting rules (Claude Code / Agent SDK cost-tracking semantics, verified against CLI 2.1.323 captures):
///  1. Several assistant lines can share one message.id (one per content block); usage is counted once per id.
///  2. result.modelUsage and result.total_cost_usd are cumulative for the session and include subagents
///     and earlier resumed invocations, so they are never summed across invocations of one session.
///  3. Per-invocation usage is result.model_usage_since_last_result when present, otherwise the difference
///     between consecutive cumulative snapshots. Deltas are reconciled against the final snapshot.
///  4. Subagent usage is attributed separately for reporting but is already part of the session totals.
///  5. Without a final record the run is marked incomplete and falls back to de-duplicated message sums.
/// </summary>
public static class UsageAggregator
{
    private const string SyntheticModel = "<synthetic>";

    public static UsageSummary FromInvocations(IReadOnlyList<InvocationCapture> invocations, string requestedModel, PricingTable? pricing)
    {
        var summary = new UsageSummary { CliCostSource = "Claude Code result.total_cost_usd (cumulative per session; client-side list-price estimate, not billing)" };
        var allMessages = new List<ApiMessage>();
        var sessionGroups = invocations
            .OrderBy(i => i.StartedAt)
            .GroupBy(i => SessionOf(i) ?? $"unknown:{i.InvocationId}");

        var total = new Dictionary<string, ModelUsage>(StringComparer.Ordinal);
        decimal? totalCost = 0m;
        var complete = true;

        foreach (var group in sessionGroups)
        {
            summary.SessionIds.Add(group.Key);
            Dictionary<string, ModelUsage>? prevCumulative = null;
            decimal? prevCost = null;
            var deltaSum = new Dictionary<string, ModelUsage>(StringComparer.Ordinal);
            Dictionary<string, ModelUsage>? sessionFinal = null;
            decimal? sessionFinalCost = null;
            var tailAfterLastResult = new Dictionary<string, ModelUsage>(StringComparer.Ordinal);
            var first = true;

            foreach (var inv in group)
            {
                allMessages.AddRange(inv.Parsed.Messages.Values);
                var usage = new InvocationUsage
                {
                    InvocationId = inv.InvocationId,
                    SessionId = SessionOf(inv),
                    IsResume = inv.IsResume,
                    ApiCalls = inv.Parsed.Messages.Count,
                };
                if (first && inv.IsResume)
                    usage.Warnings.Add("First captured invocation resumes a session whose earlier usage was not captured; per-invocation delta relies on model_usage_since_last_result.");
                if (inv.Parsed.MalformedLines > 0) usage.Warnings.Add($"{inv.Parsed.MalformedLines} malformed stream-json line(s) ignored.");

                var last = inv.Parsed.Results.LastOrDefault();
                if (last is null)
                {
                    usage.Complete = false;
                    usage.Source = UsageSource.MessageSum;
                    usage.ByModel = SumMessages(inv.Parsed.Messages.Values);
                    usage.Warnings.Add("No result event (process crashed, was cancelled or timed out). Tokens are de-duplicated message sums; output tokens may be understated and CLI cost is unknown.");
                    tailAfterLastResult = ModelUsageMath.Add(tailAfterLastResult, usage.ByModel);
                    complete = false;
                }
                else
                {
                    usage.Complete = inv.ProcessSucceeded && !last.IsError;
                    if (last.IsError) usage.Warnings.Add($"Result subtype '{last.Subtype}' reported is_error=true; error results can under-report usage.");
                    if (!inv.ProcessSucceeded) usage.Warnings.Add("Process exited unsuccessfully after emitting a result.");
                    usage.NumTurns = inv.Parsed.Results.Sum(r => r.NumTurns ?? 0);
                    usage.DurationMs = inv.Parsed.Results.Sum(r => r.DurationMs ?? 0);
                    usage.DurationApiMs = inv.Parsed.Results.Sum(r => r.DurationApiMs ?? 0);
                    usage.CumulativeCliCostUsd = last.TotalCostUsd;

                    var reported = inv.Parsed.Results.All(r => r.ModelUsageSinceLastResult is not null);
                    Dictionary<string, ModelUsage>? cumulativeDelta = null;
                    if (prevCumulative is not null || !inv.IsResume)
                        cumulativeDelta = ModelUsageMath.Subtract(last.ModelUsage, prevCumulative ?? new());

                    if (reported)
                    {
                        usage.Source = UsageSource.ReportedDelta;
                        usage.ByModel = inv.Parsed.Results.Aggregate(new Dictionary<string, ModelUsage>(StringComparer.Ordinal),
                            (acc, r) => ModelUsageMath.Add(acc, r.ModelUsageSinceLastResult!));
                        if (cumulativeDelta is not null && !Reconciles(cumulativeDelta, usage.ByModel))
                            usage.Warnings.Add("model_usage_since_last_result does not match the change in cumulative modelUsage.");
                    }
                    else if (cumulativeDelta is not null)
                    {
                        usage.Source = UsageSource.CumulativeDifference;
                        usage.ByModel = cumulativeDelta;
                    }
                    else
                    {
                        usage.Source = UsageSource.MessageSum;
                        usage.ByModel = SumMessages(inv.Parsed.Messages.Values);
                        usage.Warnings.Add("Resumed invocation without a prior cumulative snapshot or reported delta; using message sums.");
                    }

                    if (usage.ByModel.Values.Any(m => m.Tokens.AnyNegative))
                    {
                        usage.Warnings.Add("Negative delta detected (cumulative counter went backwards); falling back to message sums.");
                        usage.Source = UsageSource.MessageSum;
                        usage.ByModel = SumMessages(inv.Parsed.Messages.Values);
                    }

                    usage.CliCostUsd = usage.Source == UsageSource.ReportedDelta
                        ? ModelUsageMath.TotalCost(usage.ByModel.Values)
                        : last.TotalCostUsd is { } c ? c - (prevCost ?? 0m) : null;

                    prevCumulative = last.ModelUsage;
                    prevCost = last.TotalCostUsd;
                    sessionFinal = last.ModelUsage;
                    sessionFinalCost = last.TotalCostUsd;
                    tailAfterLastResult.Clear();
                }

                deltaSum = ModelUsageMath.Add(deltaSum, usage.ByModel);
                summary.Invocations.Add(usage);
                first = false;
            }

            Dictionary<string, ModelUsage> sessionTotal;
            if (sessionFinal is not null)
            {
                // Authoritative cumulative snapshot, plus anything consumed by later invocations that never reported.
                sessionTotal = ModelUsageMath.Add(sessionFinal, tailAfterLastResult);
                var firstInv = group.First();
                if (!firstInv.IsResume && !Reconciles(sessionFinal, ModelUsageMath.Subtract(deltaSum, tailAfterLastResult)))
                    summary.Warnings.Add($"Session {Short(group.Key)}: per-invocation deltas do not reconcile with the final cumulative snapshot.");
                if (tailAfterLastResult.Count > 0) complete = false;
                totalCost = totalCost is null || sessionFinalCost is null ? null : totalCost + sessionFinalCost;
                if (tailAfterLastResult.Count > 0) totalCost = null;
            }
            else
            {
                sessionTotal = deltaSum;
                totalCost = null;
                complete = false;
            }
            total = ModelUsageMath.Add(total, sessionTotal);
        }

        summary.ByModel = total;
        summary.Totals = ModelUsageMath.Total(total.Values);
        summary.CliReportedCostUsd = invocations.Count == 0 ? null : totalCost;
        summary.Complete = complete && invocations.Count > 0;
        summary.Turns = summary.Invocations.Sum(i => i.NumTurns ?? 0);
        summary.ApiDurationMs = summary.Invocations.Sum(i => i.DurationApiMs ?? 0);
        foreach (var inv in summary.Invocations) summary.Warnings.AddRange(inv.Warnings.Select(w => $"[{inv.InvocationId}] {w}"));
        FinishCommon(summary, allMessages, invocations.SelectMany(i => i.Parsed.Inits), requestedModel, pricing);
        if (invocations.Count == 0) summary.Warnings.Add("No managed invocations captured.");
        return summary;
    }

    public static UsageSummary FromTranscript(ParsedSession transcript, string requestedModel, PricingTable? pricing)
    {
        var summary = new UsageSummary
        {
            CliCostSource = "Claude Code transcript cost-state.totalCostUSD (cumulative per session; internal undocumented format; client-side estimate)",
        };
        summary.SessionIds.AddRange(transcript.SessionIds);
        var messageSum = SumMessages(transcript.Messages.Values);
        var finalState = transcript.CostStates.LastOrDefault();
        if (finalState is not null && finalState.ModelUsage.Count > 0)
        {
            summary.ByModel = finalState.ModelUsage;
            summary.CliReportedCostUsd = finalState.TotalCostUsd;
            summary.ApiDurationMs = finalState.TotalApiDurationMs;
            summary.Complete = true;

            var prev = new Dictionary<string, ModelUsage>(StringComparer.Ordinal);
            decimal prevCost = 0;
            var index = 0;
            foreach (var state in transcript.CostStates)
            {
                var delta = ModelUsageMath.Subtract(state.ModelUsage, prev);
                summary.Invocations.Add(new InvocationUsage
                {
                    InvocationId = $"process-{++index:000}",
                    SessionId = transcript.SessionIds.FirstOrDefault(),
                    IsResume = index > 1,
                    Complete = true,
                    Source = UsageSource.CumulativeDifference,
                    ByModel = delta,
                    CliCostUsd = state.TotalCostUsd - prevCost,
                    CumulativeCliCostUsd = state.TotalCostUsd,
                });
                if (delta.Values.Any(d => d.Tokens.AnyNegative))
                    summary.Warnings.Add($"cost-state snapshot {index} decreased; transcript may contain a compaction/reset boundary.");
                prev = state.ModelUsage;
                prevCost = state.TotalCostUsd ?? prevCost;
            }

            var stateTokens = ModelUsageMath.Total(finalState.ModelUsage.Values);
            var msgTokens = ModelUsageMath.Total(messageSum.Values);
            var mainOnly = ModelUsageMath.Total(SumMessages(transcript.Messages.Values.Where(m => !m.IsSubagent)).Values);
            if (!Close(stateTokens.TotalPromptTokens, msgTokens.TotalPromptTokens))
            {
                summary.Warnings.Add(Close(stateTokens.TotalPromptTokens, mainOnly.TotalPromptTokens)
                    ? "cost-state totals match main-thread messages only; subagent usage may be excluded from cost-state. Subagent usage is reported separately."
                    : $"cost-state prompt tokens ({stateTokens.TotalPromptTokens}) differ from de-duplicated message sums ({msgTokens.TotalPromptTokens}); the session may have been written by several processes or the transcript is incomplete.");
            }
            var lastMessage = transcript.Messages.Values.Where(m => !m.IsSubagent).MaxBy(m => m.Timestamp);
            if (lastMessage?.Timestamp is { } lm && transcript.LastTimestamp is { } lt && lm > lt) summary.Warnings.Add("Messages after final cost-state; final process may not have exited cleanly.");
        }
        else
        {
            summary.ByModel = messageSum;
            summary.Complete = false;
            summary.Warnings.Add("Transcript has no cost-state record (session still running, crashed, or format changed). Totals are de-duplicated message sums; output tokens may be understated; CLI cost unavailable.");
        }
        summary.Totals = ModelUsageMath.Total(summary.ByModel.Values);
        if (transcript.MalformedLines > 0) summary.Warnings.Add($"{transcript.MalformedLines} malformed transcript line(s) ignored.");
        FinishCommon(summary, transcript.Messages.Values, transcript.Inits, requestedModel, pricing);
        return summary;
    }

    private static void FinishCommon(UsageSummary summary, IEnumerable<ApiMessage> messages, IEnumerable<InitRecord> inits, string requestedModel, PricingTable? pricing)
    {
        var list = messages.GroupBy(m => m.Id).Select(g => g.First()).ToList();
        summary.ApiCalls = list.Count;
        var subagent = list.Where(m => m.IsSubagent).ToList();
        summary.Subagents = new SubagentUsage
        {
            Tokens = subagent.Aggregate(TokenCounts.Zero, (acc, m) => acc + m.Usage),
            ApiCalls = subagent.Count,
            CallsByModel = subagent.GroupBy(m => m.Model ?? "?").ToDictionary(g => g.Key, g => g.Count()),
            SubagentsObserved = subagent.Select(m => m.AgentId).Where(a => a is not null).Distinct().Count(),
        };
        summary.MainThreadModels = list.Where(m => !m.IsSubagent && m.Model is not null && m.Model != SyntheticModel)
            .Select(m => m.Model!).Concat(inits.Select(i => i.Model).OfType<string>()).Distinct().OrderBy(m => m).ToList();
        summary.AllObservedModels = list.Select(m => m.Model).OfType<string>().Where(m => m != SyntheticModel)
            .Concat(summary.ByModel.Keys).Distinct().OrderBy(m => m).ToList();
        summary.ObservedEfforts = list.Select(m => m.Effort).Concat(inits.Select(i => i.Effort)).OfType<string>().Distinct().OrderBy(e => e).ToList();

        var unexpectedMain = summary.MainThreadModels.Where(m => !m.Equals(requestedModel, StringComparison.OrdinalIgnoreCase)).ToList();
        if (unexpectedMain.Count > 0)
            summary.Warnings.Add($"MODEL_MISMATCH: main thread used {string.Join(", ", unexpectedMain)} (requested {requestedModel}). Run is potentially incomparable.");
        var others = summary.AllObservedModels.Except(summary.MainThreadModels).ToList();
        if (others.Count > 0)
            summary.Warnings.Add($"Additional models used outside the main thread (subagents/internal calls): {string.Join(", ", others)}.");

        if (pricing is not null) Estimate(summary, pricing);
    }

    public static void Estimate(UsageSummary summary, PricingTable pricing)
    {
        decimal total = 0;
        var assumed5m = false;
        foreach (var usage in summary.ByModel.Values)
        {
            var price = pricing.Find(usage.Model);
            if (price is null) { summary.UnpricedModels.Add(usage.Model); continue; }
            var t = usage.Tokens;
            long cw5, cw1;
            if (t.CacheCreation5m is null && t.CacheCreation1h is null) { cw5 = t.CacheCreation; cw1 = 0; if (t.CacheCreation > 0) assumed5m = true; }
            else { cw5 = t.CacheCreation5m ?? Math.Max(0, t.CacheCreation - (t.CacheCreation1h ?? 0)); cw1 = t.CacheCreation1h ?? 0; }
            total += (t.Input * price.InputPerMTok + t.Output * price.OutputPerMTok + t.CacheRead * price.CacheReadPerMTok
                      + cw5 * price.CacheWrite5mPerMTok + cw1 * price.CacheWrite1hPerMTok) / 1_000_000m;
        }
        summary.EstimatedCostUsd = summary.UnpricedModels.Count == summary.ByModel.Count && summary.ByModel.Count > 0 ? null : Math.Round(total, 8);
        summary.EstimatedCostBasis = $"Model-equivalent estimate from pricing table ({pricing.Source}, {pricing.RetrievedOn}). Not actual subscription expenditure."
            + (assumed5m ? " Cache-write TTL split unavailable; all cache writes priced at the 5-minute rate." : "")
            + (summary.UnpricedModels.Count > 0 ? $" Unpriced models excluded: {string.Join(", ", summary.UnpricedModels)}." : "");
    }

    public static Dictionary<string, ModelUsage> SumMessages(IEnumerable<ApiMessage> messages) =>
        messages.GroupBy(m => m.Model ?? "unknown")
            .Where(g => g.Key != SyntheticModel)
            .ToDictionary(g => g.Key, g => new ModelUsage(g.Key, g.Aggregate(TokenCounts.Zero, (acc, m) => acc + m.Usage), null), StringComparer.Ordinal);

    private static bool Reconciles(IReadOnlyDictionary<string, ModelUsage> a, IReadOnlyDictionary<string, ModelUsage> b)
    {
        var ta = ModelUsageMath.Total(a.Values);
        var tb = ModelUsageMath.Total(b.Values);
        return Close(ta.Input, tb.Input) && Close(ta.Output, tb.Output) && Close(ta.CacheRead, tb.CacheRead) && Close(ta.CacheCreation, tb.CacheCreation);
    }

    private static bool Close(long a, long b) => Math.Abs(a - b) <= Math.Max(2, Math.Max(Math.Abs(a), Math.Abs(b)) / 100);

    private static string? SessionOf(InvocationCapture inv) =>
        inv.Parsed.Inits.FirstOrDefault()?.SessionId ?? inv.Parsed.Results.FirstOrDefault()?.SessionId ?? inv.Parsed.SessionIds.FirstOrDefault();

    private static string Short(string id) => id.Length > 8 ? id[..8] : id;
}
