using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Benchmark.Util;

namespace Benchmark.Telemetry;

public enum EntryKind
{
    /// <summary>Free text typed by the human.</summary>
    HumanPrompt,
    /// <summary>A slash command typed by the human that reaches the model (skills such as /speckit-plan).</summary>
    SlashCommand,
    /// <summary>A local CLI command (/model, /effort, /clear ...) that never reaches the model.</summary>
    LocalCommand,
    /// <summary>The human pressed Esc / interrupted the agent.</summary>
    Interrupt,
    /// <summary>The human answered an AskUserQuestion prompt.</summary>
    QuestionAnswer,
    /// <summary>The human rejected a tool call at a permission prompt.</summary>
    ToolRejection,
    AssistantText,
    ToolCall,
    ToolResult,
    CompactSummary,
}

public sealed class ConversationEntry
{
    public DateTimeOffset? Timestamp { get; set; }
    public EntryKind Kind { get; set; }
    public string Text { get; set; } = "";
    public string? ToolName { get; set; }
    public string? ToolUseId { get; set; }
    public JsonNode? ToolInput { get; set; }
    public bool IsError { get; set; }

    public bool IsHumanInput => Kind is EntryKind.HumanPrompt or EntryKind.SlashCommand or EntryKind.QuestionAnswer or EntryKind.ToolRejection or EntryKind.Interrupt;
}

public sealed class HumanTurn
{
    public int Number { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public string Kind { get; set; } = "";
    /// <summary>Seconds the agent had been idle before this input (time the human spent reading/deciding), before capping.</summary>
    public double WaitSeconds { get; set; }
    public string Preview { get; set; } = "";
}

/// <summary>Human interaction derived automatically from the transcript. No manual logging required.</summary>
public sealed class InteractionSummary
{
    public int HumanPrompts { get; set; }
    public int SlashCommands { get; set; }
    public List<string> SlashCommandNames { get; set; } = [];
    public int AgentQuestions { get; set; }
    public int QuestionsAnswered { get; set; }
    public int ToolRejections { get; set; }
    public int Interruptions { get; set; }
    public List<string> LocalCommands { get; set; } = [];
    /// <summary>Sum of agent-idle gaps before each human input, each gap capped at <see cref="GapCapSeconds"/>.</summary>
    public double HumanWaitSeconds { get; set; }
    public double GapCapSeconds { get; set; }
    public int GapsCapped { get; set; }
    public List<string> DecisionsMatched { get; set; } = [];
    public List<HumanTurn> Turns { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public int TotalHumanInputs => HumanPrompts + SlashCommands + QuestionsAnswered + ToolRejections + Interruptions;
}

/// <summary>
/// Reads Claude Code transcripts into an ordered conversation, derives human-interaction counts from it
/// and renders a readable Markdown export. The transcript format is internal and undocumented; unknown
/// entries are skipped.
/// </summary>
public static partial class Conversation
{
    private const string RejectionMarker = "The user doesn't want to proceed with this tool use";
    private static readonly string[] SkippedPrefixes = ["<local-command-stdout", "<local-command-stderr", "<local-command-caveat", "<system-reminder", "<bash-stdout", "<bash-stderr", "Caveat: "];
    private static readonly string[] ModelAffectingCommands = ["/model", "/effort", "/fast", "/config", "/permissions", "/plugin", "/mcp", "/hooks", "/output-style", "/agents"];

    [GeneratedRegex(@"<command-name>\s*(?<name>[^<]*?)\s*</command-name>", RegexOptions.Singleline)] private static partial Regex CommandName();
    [GeneratedRegex(@"<command-args>(?<args>.*?)</command-args>", RegexOptions.Singleline)] private static partial Regex CommandArgs();
    [GeneratedRegex(@"<bash-input>(?<cmd>.*?)</bash-input>", RegexOptions.Singleline)] private static partial Regex BashInput();
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();

    public static List<ConversationEntry> Read(IEnumerable<string> transcriptLines)
    {
        var entries = new List<ConversationEntry>();
        var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in transcriptLines)
        {
            if (string.IsNullOrWhiteSpace(raw) || Json.TryParseNode(raw) is not JsonObject o) continue;
            var ts = Time(o);
            switch (Str(o, "type"))
            {
                case "assistant" when (o["message"]?["content"]) is JsonArray blocks:
                    foreach (var b in blocks.OfType<JsonObject>())
                    {
                        switch (Str(b, "type"))
                        {
                            case "text" when Str(b, "text") is { Length: > 0 } t && !string.IsNullOrWhiteSpace(t):
                                entries.Add(new ConversationEntry { Timestamp = ts, Kind = EntryKind.AssistantText, Text = t });
                                break;
                            case "tool_use":
                                var id = Str(b, "id") ?? "";
                                var name = Str(b, "name") ?? "?";
                                toolNames[id] = name;
                                entries.Add(new ConversationEntry { Timestamp = ts, Kind = EntryKind.ToolCall, ToolName = name, ToolUseId = id, ToolInput = b["input"]?.DeepClone() });
                                break;
                        }
                    }
                    break;

                case "user":
                    if (o["isMeta"]?.GetValueKind() == System.Text.Json.JsonValueKind.True) break; // skill bodies, injected context
                    if (o["isCompactSummary"]?.GetValueKind() == System.Text.Json.JsonValueKind.True)
                    {
                        entries.Add(new ConversationEntry { Timestamp = ts, Kind = EntryKind.CompactSummary, Text = "(context was compacted by Claude Code)" });
                        break;
                    }
                    switch (o["message"]?["content"])
                    {
                        case JsonValue v when v.TryGetValue<string>(out var s):
                            AddUserText(entries, ts, s);
                            break;
                        case JsonArray arr:
                            foreach (var b in arr.OfType<JsonObject>())
                            {
                                switch (Str(b, "type"))
                                {
                                    case "text": AddUserText(entries, ts, Str(b, "text") ?? ""); break;
                                    case "image": entries.Add(new ConversationEntry { Timestamp = ts, Kind = EntryKind.HumanPrompt, Text = "[image pasted]" }); break;
                                    case "tool_result": AddToolResult(entries, ts, b, toolNames); break;
                                }
                            }
                            break;
                    }
                    break;

                case "system" when Str(o, "subtype") == "local_command" && Str(o, "content") is { } content && CommandName().Match(content) is { Success: true } m:
                    entries.Add(new ConversationEntry { Timestamp = ts, Kind = EntryKind.LocalCommand, Text = FormatCommand(m.Groups["name"].Value, content) });
                    break;
            }
        }
        return entries;
    }

    private static void AddUserText(List<ConversationEntry> entries, DateTimeOffset? ts, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith("[Request interrupted by user", StringComparison.Ordinal))
        {
            entries.Add(new ConversationEntry { Timestamp = ts, Kind = EntryKind.Interrupt, Text = trimmed.Trim() });
            return;
        }
        if (CommandName().Match(text) is { Success: true } m)
        {
            entries.Add(new ConversationEntry { Timestamp = ts, Kind = EntryKind.SlashCommand, Text = FormatCommand(m.Groups["name"].Value, text) });
            return;
        }
        if (BashInput().Match(text) is { Success: true } bash)
        {
            entries.Add(new ConversationEntry { Timestamp = ts, Kind = EntryKind.LocalCommand, Text = "! " + bash.Groups["cmd"].Value.Trim() });
            return;
        }
        if (SkippedPrefixes.Any(p => trimmed.StartsWith(p, StringComparison.Ordinal))) return;
        entries.Add(new ConversationEntry { Timestamp = ts, Kind = EntryKind.HumanPrompt, Text = text.Trim() });
    }

    private static void AddToolResult(List<ConversationEntry> entries, DateTimeOffset? ts, JsonObject b, Dictionary<string, string> toolNames)
    {
        var id = Str(b, "tool_use_id") ?? "";
        var name = toolNames.GetValueOrDefault(id, "?");
        var text = b["content"] switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonArray a => string.Join("\n", a.OfType<JsonObject>().Select(x => Str(x, "text")).OfType<string>()),
            _ => "",
        };
        var isError = b["is_error"]?.GetValueKind() == System.Text.Json.JsonValueKind.True;
        var kind = text.Contains(RejectionMarker, StringComparison.Ordinal) ? EntryKind.ToolRejection
            : name == "AskUserQuestion" && !isError ? EntryKind.QuestionAnswer
            : EntryKind.ToolResult;
        entries.Add(new ConversationEntry { Timestamp = ts, Kind = kind, ToolName = name, ToolUseId = id, Text = text, IsError = isError });
    }

    private static string FormatCommand(string name, string content)
    {
        var args = CommandArgs().Match(content) is { Success: true } a ? a.Groups["args"].Value.Trim() : "";
        name = name.Trim();
        if (!name.StartsWith('/')) name = "/" + name;
        return args.Length == 0 ? name : $"{name} {args}";
    }

    public static InteractionSummary Analyze(IReadOnlyList<ConversationEntry> entries, TimeSpan gapCap, IReadOnlyDictionary<string, string>? decisionAnswers = null)
    {
        var s = new InteractionSummary { GapCapSeconds = gapCap.TotalSeconds };
        DateTimeOffset? previous = null;
        var n = 0;
        foreach (var e in entries)
        {
            switch (e.Kind)
            {
                case EntryKind.HumanPrompt: s.HumanPrompts++; break;
                case EntryKind.SlashCommand: s.SlashCommands++; s.SlashCommandNames.Add(e.Text.Split(' ')[0]); break;
                case EntryKind.QuestionAnswer: s.QuestionsAnswered++; break;
                case EntryKind.ToolRejection: s.ToolRejections++; break;
                case EntryKind.Interrupt: s.Interruptions++; break;
                case EntryKind.ToolCall when e.ToolName == "AskUserQuestion": s.AgentQuestions++; break;
                case EntryKind.LocalCommand:
                    s.LocalCommands.Add(e.Text);
                    var cmd = e.Text.Split(' ')[0];
                    if (ModelAffectingCommands.Contains(cmd, StringComparer.OrdinalIgnoreCase))
                        s.Warnings.Add($"Operator ran `{e.Text}` during the run; it may change model, effort or permissions. Check comparability.");
                    break;
            }
            if (e.IsHumanInput)
            {
                // Interrupts happen while the agent is busy, and the very first prompt has nothing before it.
                var wait = e.Kind != EntryKind.Interrupt && previous is { } p && e.Timestamp is { } t && t > p ? (t - p).TotalSeconds : 0;
                if (wait > gapCap.TotalSeconds) s.GapsCapped++;
                s.HumanWaitSeconds += Math.Min(wait, gapCap.TotalSeconds);
                s.Turns.Add(new HumanTurn { Number = ++n, Timestamp = e.Timestamp, Kind = KindName(e.Kind), WaitSeconds = wait, Preview = Preview(e.Text, 120) });
            }
            if (e.Timestamp is { } ts && e.Kind != EntryKind.LocalCommand) previous = ts;
        }
        s.SlashCommandNames = s.SlashCommandNames.Distinct().ToList();
        if (s.GapsCapped > 0)
            s.Warnings.Add($"{s.GapsCapped} idle gap(s) longer than {gapCap.TotalMinutes:0} min were capped (operator away, or a long review). Check the chat export.");
        if (decisionAnswers is not null)
        {
            var human = entries.Where(e => e.Kind is EntryKind.HumanPrompt or EntryKind.QuestionAnswer).Select(e => Normalise(e.Text)).ToList();
            s.DecisionsMatched = decisionAnswers.Where(kv => kv.Value.Length > 0 && human.Any(h => h.Contains(Normalise(kv.Value), StringComparison.Ordinal)))
                .Select(kv => kv.Key).OrderBy(k => k).ToList();
        }
        return s;
    }

    public static string RenderMarkdown(string title, IReadOnlyList<ConversationEntry> entries, InteractionSummary summary,
        IReadOnlyList<(string Name, IReadOnlyList<ConversationEntry> Entries)> subagents)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(title).Append('\n').Append('\n');
        var first = entries.FirstOrDefault(e => e.Timestamp is not null)?.Timestamp;
        var last = entries.LastOrDefault(e => e.Timestamp is not null)?.Timestamp;
        if (first is not null) sb.Append($"{first:yyyy-MM-dd HH:mm:ss} to {last:yyyy-MM-dd HH:mm:ss} UTC. ");
        sb.Append("Generated from the redacted transcript. Thinking blocks and successful tool output are omitted.\n\n");
        sb.Append($"Human inputs: {summary.HumanPrompts} typed message(s), {summary.SlashCommands} slash command(s)");
        if (summary.SlashCommandNames.Count > 0) sb.Append($" ({string.Join(", ", summary.SlashCommandNames)})");
        sb.Append($", {summary.QuestionsAnswered} answer(s) to {summary.AgentQuestions} agent question(s), {summary.ToolRejections} rejected tool call(s), {summary.Interruptions} interruption(s). ");
        sb.Append($"Time the agent waited on the human: {Dur(summary.HumanWaitSeconds)}.\n\n");
        foreach (var w in summary.Warnings) sb.Append("> Warning: ").Append(w).Append('\n');
        if (summary.Warnings.Count > 0) sb.Append('\n');
        sb.Append("Human inputs are numbered H1, H2 ... so a review can refer to them.\n\n---\n\n");

        var turns = new Queue<HumanTurn>(summary.Turns);
        var lastSpeaker = "";
        foreach (var e in entries)
        {
            if (e.IsHumanInput)
            {
                var turn = turns.Count > 0 ? turns.Dequeue() : null;
                var label = e.Kind switch
                {
                    EntryKind.SlashCommand => "slash command",
                    EntryKind.QuestionAnswer => "answer to agent question",
                    EntryKind.ToolRejection => "rejected tool call",
                    EntryKind.Interrupt => "interrupted the agent",
                    _ => "message",
                };
                sb.Append($"### H{turn?.Number} Human, {label} ({Clock(e.Timestamp)}{(turn is { WaitSeconds: >= 1 } ? $", after {Dur(turn.WaitSeconds)}" : "")})\n\n");
                sb.Append(Quote(e.Kind == EntryKind.ToolRejection ? $"Rejected `{e.ToolName}`. {e.Text}" : e.Text)).Append("\n\n");
                lastSpeaker = "human";
                continue;
            }
            switch (e.Kind)
            {
                case EntryKind.AssistantText:
                    if (lastSpeaker != "assistant") sb.Append($"### Claude ({Clock(e.Timestamp)})\n\n");
                    sb.Append(e.Text.Trim()).Append("\n\n");
                    lastSpeaker = "assistant";
                    break;
                case EntryKind.ToolCall:
                    if (lastSpeaker != "assistant") sb.Append($"### Claude ({Clock(e.Timestamp)})\n\n");
                    sb.Append("- Tool `").Append(e.ToolName).Append("`: ").Append(DescribeTool(e)).Append('\n');
                    if (e.ToolName == "AskUserQuestion") sb.Append('\n');
                    lastSpeaker = "assistant";
                    break;
                case EntryKind.ToolResult when e.IsError:
                    sb.Append("  - Error from `").Append(e.ToolName).Append("`: ").Append(Inline(Preview(e.Text, 300))).Append('\n');
                    break;
                case EntryKind.LocalCommand:
                    sb.Append($"\n_Operator ran local command `{e.Text}` ({Clock(e.Timestamp)})._\n\n");
                    lastSpeaker = "";
                    break;
                case EntryKind.CompactSummary:
                    sb.Append($"\n_{e.Text} ({Clock(e.Timestamp)})._\n\n");
                    lastSpeaker = "";
                    break;
            }
        }

        if (subagents.Count > 0)
        {
            sb.Append("\n---\n\n## Subagents\n\n");
            foreach (var (name, sub) in subagents)
            {
                sb.Append($"### {name}\n\n");
                foreach (var e in sub)
                {
                    if (e.Kind == EntryKind.AssistantText) sb.Append(e.Text.Trim()).Append("\n\n");
                    else if (e.Kind == EntryKind.ToolCall) sb.Append("- Tool `").Append(e.ToolName).Append("`: ").Append(DescribeTool(e)).Append('\n');
                    else if (e.Kind == EntryKind.ToolResult && e.IsError) sb.Append("  - Error: ").Append(Inline(Preview(e.Text, 200))).Append('\n');
                }
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    public static string DescribeTool(ConversationEntry e)
    {
        var input = e.ToolInput as JsonObject;
        string? Get(string k) => input is null ? null : Str(input, k);
        switch (e.ToolName)
        {
            case "Bash" or "PowerShell":
                return Code(Get("command") ?? "");
            case "Read" or "Write" or "Edit" or "MultiEdit" or "NotebookEdit":
                return Code(Get("file_path") ?? Get("notebook_path") ?? "");
            case "Glob" or "Grep":
                return Code(Get("pattern") ?? "") + (Get("path") is { } p ? $" in `{p}`" : "");
            case "Skill":
                return Code(Get("skill") ?? Get("command") ?? "") + (Get("args") is { Length: > 0 } a ? " " + Inline(Preview(a, 120)) : "");
            case "Task" or "Agent":
                return Inline(Get("description") ?? Preview(Get("prompt") ?? "", 120));
            case "AskUserQuestion" when (input?["questions"]) is JsonArray qs:
                var sb = new StringBuilder();
                foreach (var q in qs.OfType<JsonObject>())
                {
                    sb.Append("\n  - Question: ").Append(Inline(Str(q, "question") ?? ""));
                    if (q["options"] is JsonArray opts)
                        sb.Append(" Options: ").Append(string.Join(" / ", opts.OfType<JsonObject>().Select(x => Inline(Str(x, "label") ?? ""))));
                }
                return sb.ToString();
            case "TodoWrite" when (input?["todos"]) is JsonArray todos:
                return $"{todos.Count} item(s)";
            case "WebFetch":
                return Code(Get("url") ?? "");
            default:
                return Code(Preview(input?.ToJsonString() ?? "", 160));
        }
    }

    private static string KindName(EntryKind k) => System.Text.Json.JsonNamingPolicy.KebabCaseLower.ConvertName(k.ToString());
    private static string Normalise(string s) => Whitespace().Replace(s, " ").Trim().ToLowerInvariant();
    private static string Preview(string s, int max) { s = Whitespace().Replace(s, " ").Trim(); return s.Length <= max ? s : s[..max] + "…"; }
    private static string Inline(string s) => s.Replace("\r", " ").Replace("\n", " ");
    private static string Code(string s)
    {
        s = Preview(s, 200);
        var fence = s.Contains('`') ? "``" : "`";
        return s.Length == 0 ? "" : $"{fence}{(fence.Length > 1 ? " " : "")}{s}{(fence.Length > 1 ? " " : "")}{fence}";
    }
    private static string Quote(string s) => string.Join("\n", s.Trim().Replace("\r\n", "\n").Split('\n').Select(l => "> " + l));
    private static string Clock(DateTimeOffset? t) => t is null ? "time unknown" : $"{t.Value.UtcDateTime:HH:mm:ss}";
    public static string Dur(double seconds) => seconds < 60 ? $"{seconds:0}s" : seconds < 3600 ? $"{seconds / 60:0.0} min" : $"{seconds / 3600:0.00} h";
    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static DateTimeOffset? Time(JsonObject o) =>
        Str(o, "timestamp") is { } s && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : null;
}
