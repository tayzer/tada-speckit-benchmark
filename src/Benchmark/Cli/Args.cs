using Benchmark.Config;

namespace Benchmark.Cli;

/// <summary>Minimal argument parser: positional words, `--key value`, `--key=value` and boolean `--flag`.</summary>
public sealed class Args
{
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _consumed = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Positional { get; } = [];

    private static readonly HashSet<string> BooleanFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "resume", "allow-draft", "code-only", "process-only", "no-seal", "mutation-control", "json", "verify-activation", "force-new", "help", "keep", "skip-activation-check", "dry-run", "register", "preflight-only", "retry-openspec",
    };

    public Args(IEnumerable<string> argv)
    {
        var list = argv.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var a = list[i];
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                var key = a[2..];
                string? value = null;
                var eq = key.IndexOf('=');
                if (eq >= 0) { value = key[(eq + 1)..]; key = key[..eq]; }
                else if (!BooleanFlags.Contains(key) && i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal)) value = list[++i];
                if (value is null) _flags.Add(key);
                else
                {
                    if (!_options.TryGetValue(key, out var values)) _options[key] = values = [];
                    values.Add(value);
                }
            }
            else Positional.Add(a);
        }
    }

    public string? Get(string key) { _consumed.Add(key); return _options.TryGetValue(key, out var v) ? v[^1] : null; }
    public string Require(string key) => Get(key) ?? throw new BenchmarkException($"Missing required option --{key}.");
    public IReadOnlyList<string> GetAll(string key) { _consumed.Add(key); return _options.TryGetValue(key, out var v) ? v : []; }
    public bool Flag(string key) { _consumed.Add(key); return _flags.Contains(key); }

    public IEnumerable<string> Unconsumed() => _options.Keys.Concat(_flags).Where(k => !_consumed.Contains(k));
}
