using Benchmark.Util;

namespace Benchmark.Config;

/// <summary>USD per million tokens. Used only for model-equivalent cost *estimates*.</summary>
public sealed class ModelPrice
{
    public decimal InputPerMTok { get; set; }
    public decimal OutputPerMTok { get; set; }
    public decimal CacheReadPerMTok { get; set; }
    public decimal CacheWrite5mPerMTok { get; set; }
    public decimal CacheWrite1hPerMTok { get; set; }
}

public sealed class PricingTable
{
    public string Source { get; set; } = "";
    public string RetrievedOn { get; set; } = "";
    public string Note { get; set; } = "";
    public Dictionary<string, ModelPrice> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static PricingTable Load(string path) => Json.Read<PricingTable>(path);

    public ModelPrice? Find(string model)
    {
        if (Models.TryGetValue(model, out var p)) return p;
        // Tolerate dated or suffixed IDs ("claude-x-20260101", "claude-x[1m]") by longest-prefix match.
        return Models.Where(kv => model.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(kv => kv.Key.Length).Select(kv => kv.Value).FirstOrDefault();
    }
}
