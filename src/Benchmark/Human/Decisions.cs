using Benchmark.Util;

namespace Benchmark.Human;

/// <summary>
/// Canonical business decisions. When either workflow asks an equivalent question the operator answers
/// with the registered text, so both arms receive identical business information.
/// </summary>
public sealed class DecisionRegister
{
    public string Note { get; set; } = "";
    public List<Decision> Decisions { get; set; } = [];

    public static DecisionRegister Load(string path) => File.Exists(path) ? Json.Read<DecisionRegister>(path) : new DecisionRegister();

    public Decision Get(string id) => Decisions.FirstOrDefault(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? throw new Config.BenchmarkException($"Decision '{id}' is not in the register. Add it with `benchmark decision add` so both workflows get the same answer.");
}

public sealed class Decision
{
    public string Id { get; set; } = "";
    public string Topic { get; set; } = "";
    public string Question { get; set; } = "";
    /// <summary>Exact text given to the agent. Null while the owner has not decided.</summary>
    public string? Answer { get; set; }
    /// <summary>"proposed" (draft, not usable in official runs) or "approved".</summary>
    public string Status { get; set; } = "proposed";
    public string? ApprovedBy { get; set; }
    public List<string> HiddenTests { get; set; } = [];
    public string? AddedDuringRun { get; set; }

    public string AnswerSha256 => Answer is null ? "" : Fs.Sha256Text(Fs.NormalizeNewlines(Answer).Trim());
}
