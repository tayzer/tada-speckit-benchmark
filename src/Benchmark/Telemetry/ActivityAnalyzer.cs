using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Benchmark.Telemetry;

public sealed class ActivitySummary
{
    public Dictionary<string, int> ToolCalls { get; set; } = new();
    public int SubagentToolCalls { get; set; }
    public List<string> SkillsInvoked { get; set; } = [];
    public List<string> SlashCommandsTyped { get; set; } = [];
    public int AgentQuestions { get; set; }
    public int HumanMessages { get; set; }
    public List<VerificationCheck> VerificationChecks { get; set; } = [];
    public int PermissionDenials { get; set; }
    public List<string> PermissionDenialTools { get; set; } = [];
    public List<IsolationViolation> IsolationViolations { get; set; } = [];
    public int ApiRetries { get; set; }
}

public sealed record VerificationCheck(string Kind, string Command, bool IsSubagent);

public sealed record IsolationViolation(string Tool, string Root, string Evidence);

public static partial class ActivityAnalyzer
{
    [GeneratedRegex(@"\bdotnet(\.exe)?\s+test\b", RegexOptions.IgnoreCase)] private static partial Regex DotnetTest();
    [GeneratedRegex(@"\bdotnet(\.exe)?\s+build\b", RegexOptions.IgnoreCase)] private static partial Regex DotnetBuild();
    [GeneratedRegex(@"\b(dotnet\s+tool\s+run\s+tada|tada(\.exe)?)\s+(verify|run|gate|baseline|plan)\b", RegexOptions.IgnoreCase)] private static partial Regex TadaVerify();
    [GeneratedRegex(@"\bdotnet(\.exe)?\s+format\b", RegexOptions.IgnoreCase)] private static partial Regex DotnetFormat();

    private static readonly string[] ShellTools = ["Bash", "PowerShell"];
    private static readonly string[] QuestionTools = ["AskUserQuestion"];

    public static ActivitySummary Analyze(ParsedSession session, IReadOnlyList<string> forbiddenRoots)
    {
        var summary = new ActivitySummary();
        foreach (var tool in session.ToolUses)
        {
            summary.ToolCalls[tool.Name] = summary.ToolCalls.GetValueOrDefault(tool.Name) + 1;
            if (tool.IsSubagent) summary.SubagentToolCalls++;
            if (tool.Name == "Skill" && tool.Input is JsonObject si && (si["skill"] ?? si["name"] ?? si["command"]) is JsonValue sv)
                summary.SkillsInvoked.Add(sv.ToString());
            if (QuestionTools.Contains(tool.Name)) summary.AgentQuestions++;
            if (ShellTools.Contains(tool.Name) && tool.Input is JsonObject shell && shell["command"]?.ToString() is { } cmd)
            {
                if (DotnetTest().IsMatch(cmd)) summary.VerificationChecks.Add(new("dotnet-test", Trim(cmd), tool.IsSubagent));
                else if (DotnetBuild().IsMatch(cmd)) summary.VerificationChecks.Add(new("dotnet-build", Trim(cmd), tool.IsSubagent));
                if (TadaVerify().IsMatch(cmd)) summary.VerificationChecks.Add(new("tada-cli", Trim(cmd), tool.IsSubagent));
                if (DotnetFormat().IsMatch(cmd)) summary.VerificationChecks.Add(new("dotnet-format", Trim(cmd), tool.IsSubagent));
            }
            foreach (var violation in FindForbidden(tool, forbiddenRoots)) summary.IsolationViolations.Add(violation);
        }
        summary.HumanMessages = session.HumanMessages.Count;
        summary.SlashCommandsTyped = session.HumanMessages.Where(m => m.IsSlashCommand)
            .Select(m => m.Text.TrimStart().Split([' ', '\n'], 2)[0]).ToList();
        summary.PermissionDenials = session.Results.Sum(r => r.PermissionDenials) + session.SystemEvents.Count(e => e == "permission_denied");
        summary.PermissionDenialTools = session.Results.SelectMany(r => r.PermissionDenialTools).Distinct().ToList();
        summary.ApiRetries = session.SystemEvents.Count(e => e == "api_retry");
        summary.SkillsInvoked = summary.SkillsInvoked.Distinct().ToList();
        return summary;
    }

    /// <summary>
    /// Flags tool inputs that reference a forbidden root (hidden evaluator, harness, other workspaces).
    /// Matching is done on normalised, lower-cased path text so `D:\x`, `d:/x` and `/d/x` all match.
    /// This is an audit (detection), complementing the deny rules (prevention).
    /// </summary>
    public static IEnumerable<IsolationViolation> FindForbidden(ToolUseRecord tool, IReadOnlyList<string> forbiddenRoots)
    {
        var text = NormalisePathText(tool.InputText);
        foreach (var root in forbiddenRoots)
        {
            foreach (var variant in PathVariants(root))
            {
                var idx = IndexOfPath(text, variant);
                if (idx < 0) continue;
                var start = Math.Max(0, idx - 30);
                yield return new IsolationViolation(tool.Name, root, text.Substring(start, Math.Min(text.Length - start, variant.Length + 60)));
                break;
            }
        }
    }

    /// <summary>Finds <paramref name="path"/> only where it ends at a path boundary ("d:/x" must not match "d:/xy").</summary>
    public static int IndexOfPath(string text, string path)
    {
        for (var idx = text.IndexOf(path, StringComparison.Ordinal); idx >= 0; idx = text.IndexOf(path, idx + 1, StringComparison.Ordinal))
        {
            var end = idx + path.Length;
            if (end >= text.Length || !(char.IsLetterOrDigit(text[end]) || text[end] is '-' or '_' or '.')) return idx;
        }
        return -1;
    }

    public static IEnumerable<string> PathVariants(string root)
    {
        // Non-rooted entries are plain tokens (e.g. the hidden suite's project name).
        if (!Path.IsPathRooted(root)) { yield return root.ToLowerInvariant(); yield break; }
        var full = NormalisePathText(Path.GetFullPath(root)).TrimEnd('/');
        yield return full; // d:/dev/benchmark
        if (full.Length > 2 && full[1] == ':') yield return "/" + full[0] + full[2..]; // /d/dev/benchmark (git bash / permission rule form)
    }

    public static string NormalisePathText(string s) => s.Replace("\\\\", "/").Replace('\\', '/').ToLowerInvariant();

    private static string Trim(string cmd) => cmd.Length > 200 ? cmd[..200] + "…" : cmd;
}
