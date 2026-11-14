namespace Benchmark.Telemetry;

/// <summary>
/// Normalised token counts. Anthropic usage categories are disjoint: <see cref="Input"/> excludes cache
/// reads and cache writes, so the total prompt size is Input + CacheRead + CacheCreation (computed once).
/// </summary>
public sealed record TokenCounts
{
    public long Input { get; init; }
    public long Output { get; init; }
    public long CacheRead { get; init; }
    public long CacheCreation { get; init; }
    /// <summary>Portion of CacheCreation written with the 5-minute TTL, when reported.</summary>
    public long? CacheCreation5m { get; init; }
    /// <summary>Portion of CacheCreation written with the 1-hour TTL, when reported.</summary>
    public long? CacheCreation1h { get; init; }
    public long? Thinking { get; init; }
    public long WebSearchRequests { get; init; }

    public static readonly TokenCounts Zero = new();

    public long TotalPromptTokens => Input + CacheRead + CacheCreation;

    public static TokenCounts operator +(TokenCounts a, TokenCounts b) => new()
    {
        Input = a.Input + b.Input,
        Output = a.Output + b.Output,
        CacheRead = a.CacheRead + b.CacheRead,
        CacheCreation = a.CacheCreation + b.CacheCreation,
        CacheCreation5m = AddNullable(a.CacheCreation5m, b.CacheCreation5m),
        CacheCreation1h = AddNullable(a.CacheCreation1h, b.CacheCreation1h),
        Thinking = AddNullable(a.Thinking, b.Thinking),
        WebSearchRequests = a.WebSearchRequests + b.WebSearchRequests,
    };

    public static TokenCounts operator -(TokenCounts a, TokenCounts b) => new()
    {
        Input = a.Input - b.Input,
        Output = a.Output - b.Output,
        CacheRead = a.CacheRead - b.CacheRead,
        CacheCreation = a.CacheCreation - b.CacheCreation,
        CacheCreation5m = a.CacheCreation5m is null || b.CacheCreation5m is null ? a.CacheCreation5m : a.CacheCreation5m - b.CacheCreation5m,
        CacheCreation1h = a.CacheCreation1h is null || b.CacheCreation1h is null ? a.CacheCreation1h : a.CacheCreation1h - b.CacheCreation1h,
        Thinking = a.Thinking is null || b.Thinking is null ? a.Thinking : a.Thinking - b.Thinking,
        WebSearchRequests = a.WebSearchRequests - b.WebSearchRequests,
    };

    public bool AnyNegative => Input < 0 || Output < 0 || CacheRead < 0 || CacheCreation < 0;

    private static long? AddNullable(long? a, long? b) => a is null && b is null ? null : (a ?? 0) + (b ?? 0);
}

/// <summary>Per-model usage with the CLI-reported cost (client-side list-price estimate, not billing).</summary>
public sealed record ModelUsage(string Model, TokenCounts Tokens, decimal? CliCostUsd);

public static class ModelUsageMath
{
    public static Dictionary<string, ModelUsage> Add(IReadOnlyDictionary<string, ModelUsage> a, IReadOnlyDictionary<string, ModelUsage> b)
    {
        var result = new Dictionary<string, ModelUsage>(a, StringComparer.Ordinal);
        foreach (var (model, usage) in b)
        {
            result[model] = result.TryGetValue(model, out var existing)
                ? new ModelUsage(model, existing.Tokens + usage.Tokens, AddCost(existing.CliCostUsd, usage.CliCostUsd))
                : usage;
        }
        return result;
    }

    public static Dictionary<string, ModelUsage> Subtract(IReadOnlyDictionary<string, ModelUsage> later, IReadOnlyDictionary<string, ModelUsage> earlier)
    {
        var result = new Dictionary<string, ModelUsage>(StringComparer.Ordinal);
        foreach (var (model, usage) in later)
        {
            if (earlier.TryGetValue(model, out var e))
                result[model] = new ModelUsage(model, usage.Tokens - e.Tokens, usage.CliCostUsd is null || e.CliCostUsd is null ? null : usage.CliCostUsd - e.CliCostUsd);
            else result[model] = usage;
        }
        // A model present earlier but missing later means the cumulative record went backwards.
        foreach (var (model, usage) in earlier.Where(kv => !later.ContainsKey(kv.Key)))
            result[model] = new ModelUsage(model, TokenCounts.Zero - usage.Tokens, usage.CliCostUsd is null ? null : -usage.CliCostUsd);
        return result;
    }

    public static TokenCounts Total(IEnumerable<ModelUsage> usages) => usages.Aggregate(TokenCounts.Zero, (acc, u) => acc + u.Tokens);

    public static decimal? TotalCost(IEnumerable<ModelUsage> usages)
    {
        decimal? total = 0m;
        foreach (var u in usages) total = AddCost(total, u.CliCostUsd);
        return total;
    }

    private static decimal? AddCost(decimal? a, decimal? b) => a is null || b is null ? null : a + b;
}

public enum UsageSource
{
    /// <summary>result.model_usage_since_last_result — per-invocation delta reported by the CLI.</summary>
    ReportedDelta,
    /// <summary>Difference between consecutive cumulative modelUsage snapshots of the same session.</summary>
    CumulativeDifference,
    /// <summary>Summed from de-duplicated per-message usage (no final record). Output tokens may be understated.</summary>
    MessageSum,
    Unavailable,
}

public sealed class InvocationUsage
{
    public string InvocationId { get; set; } = "";
    public string? SessionId { get; set; }
    public bool IsResume { get; set; }
    public bool Complete { get; set; }
    public UsageSource Source { get; set; }
    public Dictionary<string, ModelUsage> ByModel { get; set; } = new();
    public decimal? CliCostUsd { get; set; }
    public decimal? CumulativeCliCostUsd { get; set; }
    public int? NumTurns { get; set; }
    public double? DurationMs { get; set; }
    public double? DurationApiMs { get; set; }
    public int ApiCalls { get; set; }
    public List<string> Warnings { get; set; } = [];
}

public sealed class SubagentUsage
{
    /// <summary>Usage attributed to subagents. Already included in session totals — never add it again.</summary>
    public TokenCounts Tokens { get; set; } = TokenCounts.Zero;
    public int ApiCalls { get; set; }
    public Dictionary<string, int> CallsByModel { get; set; } = new();
    public int SubagentsObserved { get; set; }
}

public sealed class UsageSummary
{
    public TokenCounts Totals { get; set; } = TokenCounts.Zero;
    public Dictionary<string, ModelUsage> ByModel { get; set; } = new();
    public decimal? CliReportedCostUsd { get; set; }
    public string CliCostSource { get; set; } = "";
    public decimal? EstimatedCostUsd { get; set; }
    public string EstimatedCostBasis { get; set; } = "";
    public List<string> UnpricedModels { get; set; } = [];
    public int ApiCalls { get; set; }
    public int? Turns { get; set; }
    public double? ApiDurationMs { get; set; }
    public List<string> SessionIds { get; set; } = [];
    public List<InvocationUsage> Invocations { get; set; } = [];
    public SubagentUsage Subagents { get; set; } = new();
    public List<string> MainThreadModels { get; set; } = [];
    public List<string> AllObservedModels { get; set; } = [];
    public List<string> ObservedEfforts { get; set; } = [];
    public bool Complete { get; set; }
    public List<string> Warnings { get; set; } = [];
}
