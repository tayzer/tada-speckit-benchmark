using System.Text.Json.Nodes;
using Benchmark.Util;

namespace Benchmark.Telemetry;

public sealed class InitRecord
{
    public string? SessionId { get; set; }
    public string? Model { get; set; }
    public string? ClaudeCodeVersion { get; set; }
    public string? PermissionMode { get; set; }
    public string? Cwd { get; set; }
    public string? ApiKeySource { get; set; }
    public string? Effort { get; set; }
    public List<string> Tools { get; set; } = [];
    public List<string> Skills { get; set; } = [];
    public List<string> SlashCommands { get; set; } = [];
    public List<string> Agents { get; set; } = [];
    public List<string> Plugins { get; set; } = [];
    public List<string> PluginPaths { get; set; } = [];
    public List<string> McpServers { get; set; } = [];
}

public sealed class ApiMessage
{
    public string Id { get; set; } = "";
    public string? Model { get; set; }
    public TokenCounts Usage { get; set; } = TokenCounts.Zero;
    public bool IsSubagent { get; set; }
    public string? AgentId { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public string? Effort { get; set; }
    public int DuplicateLines { get; set; }
}

public sealed class ToolUseRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public JsonNode? Input { get; set; }
    public bool IsSubagent { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public string InputText => Input?.ToJsonString() ?? "";
}

public sealed class ResultRecord
{
    public string? SessionId { get; set; }
    public string? Subtype { get; set; }
    public bool IsError { get; set; }
    public decimal? TotalCostUsd { get; set; }
    /// <summary>Cumulative for the whole session (including earlier resumed invocations and subagents).</summary>
    public Dictionary<string, ModelUsage> ModelUsage { get; set; } = new();
    /// <summary>Per-invocation delta when the CLI reports it.</summary>
    public Dictionary<string, ModelUsage>? ModelUsageSinceLastResult { get; set; }
    public TokenCounts? MainLoopUsage { get; set; }
    public int? NumTurns { get; set; }
    public double? DurationMs { get; set; }
    public double? DurationApiMs { get; set; }
    public int PermissionDenials { get; set; }
    public List<string> PermissionDenialTools { get; set; } = [];
    public DateTimeOffset? Timestamp { get; set; }
}

/// <summary>Cumulative cost snapshot written into interactive transcripts ("cost-state" lines).</summary>
public sealed class CostStateRecord
{
    public decimal? TotalCostUsd { get; set; }
    public Dictionary<string, ModelUsage> ModelUsage { get; set; } = new();
    public double? TotalApiDurationMs { get; set; }
    public double? TotalToolDurationMs { get; set; }
    public double? TotalDurationMs { get; set; }
    public long? LinesAdded { get; set; }
    public long? LinesRemoved { get; set; }
}

public sealed class HumanMessage
{
    public DateTimeOffset? Timestamp { get; set; }
    public string Text { get; set; } = "";
    public bool IsSlashCommand => Text.TrimStart().StartsWith('/');
}

/// <summary>Everything extracted from one stream-json capture or one transcript file set.</summary>
public sealed class ParsedSession
{
    public string SourceKind { get; set; } = ""; // "stream-json" | "transcript"
    public List<InitRecord> Inits { get; set; } = [];
    public Dictionary<string, ApiMessage> Messages { get; set; } = new(StringComparer.Ordinal);
    public List<ToolUseRecord> ToolUses { get; set; } = [];
    public List<ResultRecord> Results { get; set; } = [];
    public List<CostStateRecord> CostStates { get; set; } = [];
    public List<HumanMessage> HumanMessages { get; set; } = [];
    public List<string> SystemEvents { get; set; } = [];
    public HashSet<string> SessionIds { get; set; } = new(StringComparer.Ordinal);
    public int TotalLines { get; set; }
    public int MalformedLines { get; set; }
    public int DuplicateMessageLines { get; set; }
    public DateTimeOffset? FirstTimestamp { get; set; }
    public DateTimeOffset? LastTimestamp { get; set; }
}

public static class ClaudeEventParser
{
    public static ParsedSession ParseStreamJson(IEnumerable<string> lines)
    {
        var session = new ParsedSession { SourceKind = "stream-json" };
        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            session.TotalLines++;
            var node = Json.TryParseNode(raw);
            if (node is not JsonObject obj) { session.MalformedLines++; continue; }
            var type = Str(obj, "type");
            if (Str(obj, "session_id") is { } sid) session.SessionIds.Add(sid);
            Touch(session, Time(obj, "timestamp"));
            switch (type)
            {
                case "system" when Str(obj, "subtype") == "init":
                    session.Inits.Add(ParseInit(obj));
                    break;
                case "system":
                    session.SystemEvents.Add(Str(obj, "subtype") ?? "unknown");
                    break;
                case "assistant":
                    AddAssistant(session, obj, obj["message"] as JsonObject,
                        isSubagent: obj["parent_tool_use_id"] is JsonValue, agentId: Str(obj, "agent_id"), effort: null);
                    break;
                case "user":
                    AddUser(session, obj, isSubagent: obj["parent_tool_use_id"] is JsonValue);
                    break;
                case "result":
                    session.Results.Add(ParseResult(obj));
                    break;
            }
        }
        return session;
    }

    /// <summary>
    /// Parses Claude Code's on-disk session transcript(s). The format is internal and undocumented; every
    /// field is read defensively and anything unrecognised is ignored. Subagent transcripts are passed as
    /// additional files and their messages are marked as subagent usage.
    /// </summary>
    public static ParsedSession ParseTranscripts(string mainTranscript, IEnumerable<string> subagentTranscripts)
    {
        var session = new ParsedSession { SourceKind = "transcript" };
        ParseTranscriptLines(session, File.ReadLines(mainTranscript), forceSubagent: false);
        foreach (var file in subagentTranscripts.OrderBy(f => f, StringComparer.Ordinal))
            ParseTranscriptLines(session, File.ReadLines(file), forceSubagent: true);
        return session;
    }

    public static void ParseTranscriptLines(ParsedSession session, IEnumerable<string> lines, bool forceSubagent)
    {
        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            session.TotalLines++;
            var node = Json.TryParseNode(raw);
            if (node is not JsonObject obj) { session.MalformedLines++; continue; }
            var type = Str(obj, "type");
            if (Str(obj, "sessionId") is { } sid && !forceSubagent) session.SessionIds.Add(sid);
            Touch(session, Time(obj, "timestamp"));
            var sidechain = forceSubagent || obj["isSidechain"]?.GetValueKind() == System.Text.Json.JsonValueKind.True;
            switch (type)
            {
                case "assistant":
                    AddAssistant(session, obj, obj["message"] as JsonObject, sidechain, Str(obj, "agentId"), Str(obj, "effort"));
                    break;
                case "user":
                    AddUser(session, obj, sidechain);
                    break;
                case "cost-state" when !forceSubagent:
                    session.CostStates.Add(new CostStateRecord
                    {
                        TotalCostUsd = Dec(obj, "totalCostUSD"),
                        ModelUsage = ParseModelUsageMap(obj["modelUsage"] as JsonObject),
                        TotalApiDurationMs = Dbl(obj, "totalAPIDuration"),
                        TotalToolDurationMs = Dbl(obj, "totalToolDuration"),
                        TotalDurationMs = Dbl(obj, "totalDuration"),
                        LinesAdded = Lng(obj, "totalLinesAdded"),
                        LinesRemoved = Lng(obj, "totalLinesRemoved"),
                    });
                    break;
                case "system":
                    session.SystemEvents.Add(Str(obj, "subtype") ?? "system");
                    break;
            }
        }
    }

    private static void AddAssistant(ParsedSession session, JsonObject envelope, JsonObject? message, bool isSubagent, string? agentId, string? effort)
    {
        if (message is null) return;
        var ts = Time(envelope, "timestamp");
        var id = Str(message, "id") ?? Str(envelope, "uuid") ?? Guid.NewGuid().ToString();
        var usage = ParseMessageUsage(message["usage"] as JsonObject);
        if (session.Messages.TryGetValue(id, out var existing))
        {
            // Same API response split across several lines (one per content block): identical usage.
            // Keep the first snapshot, but take the largest output count in case early snapshots are placeholders.
            existing.DuplicateLines++;
            session.DuplicateMessageLines++;
            if (usage.Output > existing.Usage.Output) existing.Usage = existing.Usage with { Output = usage.Output };
        }
        else
        {
            session.Messages[id] = new ApiMessage
            {
                Id = id, Model = Str(message, "model"), Usage = usage, IsSubagent = isSubagent,
                AgentId = agentId, Timestamp = ts, Effort = effort,
            };
        }
        if (message["content"] is JsonArray content)
        {
            foreach (var block in content.OfType<JsonObject>().Where(b => Str(b, "type") == "tool_use"))
            {
                var toolId = Str(block, "id") ?? "";
                if (session.ToolUses.Any(t => t.Id == toolId && toolId != "")) continue;
                session.ToolUses.Add(new ToolUseRecord
                {
                    Id = toolId, Name = Str(block, "name") ?? "", Input = block["input"]?.DeepClone(),
                    IsSubagent = isSubagent, Timestamp = ts,
                });
            }
        }
    }

    private static void AddUser(ParsedSession session, JsonObject envelope, bool isSubagent)
    {
        if (isSubagent) return;
        if (envelope["isMeta"]?.GetValueKind() == System.Text.Json.JsonValueKind.True) return;
        if (envelope["message"] is not JsonObject message) return;
        string? text = null;
        switch (message["content"])
        {
            case JsonValue v when v.TryGetValue<string>(out var s):
                text = s;
                break;
            case JsonArray arr:
                if (arr.OfType<JsonObject>().Any(b => Str(b, "type") == "tool_result")) return;
                text = string.Join("\n", arr.OfType<JsonObject>().Where(b => Str(b, "type") == "text").Select(b => Str(b, "text")));
                break;
        }
        if (string.IsNullOrWhiteSpace(text)) return;
        // Claude Code wraps local command output and system reminders in tags; those are not human input.
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith("<local-command", StringComparison.Ordinal) || trimmed.StartsWith("<system-reminder", StringComparison.Ordinal)) return;
        session.HumanMessages.Add(new HumanMessage { Timestamp = Time(envelope, "timestamp"), Text = text });
    }

    public static InitRecord ParseInit(JsonObject obj) => new()
    {
        SessionId = Str(obj, "session_id"),
        Model = Str(obj, "model"),
        ClaudeCodeVersion = Str(obj, "claude_code_version"),
        PermissionMode = Str(obj, "permissionMode"),
        Cwd = Str(obj, "cwd"),
        ApiKeySource = Str(obj, "apiKeySource"),
        Effort = Str(obj, "effort"),
        Tools = Strings(obj["tools"]),
        Skills = Strings(obj["skills"]),
        SlashCommands = Strings(obj["slash_commands"]),
        Agents = Strings(obj["agents"]),
        Plugins = (obj["plugins"] as JsonArray)?.Select(p => p is JsonObject o ? Str(o, "name") ?? "" : p?.ToString() ?? "").ToList() ?? [],
        PluginPaths = (obj["plugins"] as JsonArray)?.OfType<JsonObject>().Select(o => Str(o, "path") ?? "").ToList() ?? [],
        McpServers = (obj["mcp_servers"] as JsonArray)?.OfType<JsonObject>().Select(o => Str(o, "name") ?? "").ToList() ?? [],
    };

    public static ResultRecord ParseResult(JsonObject obj)
    {
        var denials = obj["permission_denials"] as JsonArray;
        return new ResultRecord
        {
            SessionId = Str(obj, "session_id"),
            Subtype = Str(obj, "subtype"),
            IsError = obj["is_error"]?.GetValueKind() == System.Text.Json.JsonValueKind.True,
            TotalCostUsd = Dec(obj, "total_cost_usd"),
            ModelUsage = ParseModelUsageMap(obj["modelUsage"] as JsonObject),
            ModelUsageSinceLastResult = obj["model_usage_since_last_result"] is JsonObject since ? ParseModelUsageMap(since) : null,
            MainLoopUsage = obj["usage"] is JsonObject u ? ParseMessageUsage(u) : null,
            NumTurns = (int?)Lng(obj, "num_turns"),
            DurationMs = Dbl(obj, "duration_ms"),
            DurationApiMs = Dbl(obj, "duration_api_ms"),
            PermissionDenials = denials?.Count ?? 0,
            PermissionDenialTools = denials?.OfType<JsonObject>().Select(d => Str(d, "tool_name") ?? "?").ToList() ?? [],
            Timestamp = Time(obj, "timestamp"),
        };
    }

    public static TokenCounts ParseMessageUsage(JsonObject? u)
    {
        if (u is null) return TokenCounts.Zero;
        var cc = u["cache_creation"] as JsonObject;
        return new TokenCounts
        {
            Input = Lng(u, "input_tokens") ?? 0,
            Output = Lng(u, "output_tokens") ?? 0,
            CacheRead = Lng(u, "cache_read_input_tokens") ?? 0,
            CacheCreation = Lng(u, "cache_creation_input_tokens") ?? 0,
            CacheCreation5m = cc is null ? null : Lng(cc, "ephemeral_5m_input_tokens"),
            CacheCreation1h = cc is null ? null : Lng(cc, "ephemeral_1h_input_tokens"),
            Thinking = (u["output_tokens_details"] as JsonObject) is { } d ? Lng(d, "thinking_tokens") : null,
            WebSearchRequests = (u["server_tool_use"] as JsonObject) is { } s ? Lng(s, "web_search_requests") ?? 0 : 0,
        };
    }

    public static Dictionary<string, ModelUsage> ParseModelUsageMap(JsonObject? map)
    {
        var result = new Dictionary<string, ModelUsage>(StringComparer.Ordinal);
        if (map is null) return result;
        foreach (var (model, value) in map)
        {
            if (value is not JsonObject m) continue;
            result[model] = new ModelUsage(model, new TokenCounts
            {
                Input = Lng(m, "inputTokens") ?? 0,
                Output = Lng(m, "outputTokens") ?? 0,
                CacheRead = Lng(m, "cacheReadInputTokens") ?? 0,
                CacheCreation = Lng(m, "cacheCreationInputTokens") ?? 0,
                CacheCreation5m = Lng(m, "cacheCreationEphemeral5mInputTokens"),
                CacheCreation1h = Lng(m, "cacheCreationEphemeral1hInputTokens"),
                Thinking = Lng(m, "thinkingTokens"),
                WebSearchRequests = Lng(m, "webSearchRequests") ?? 0,
            }, Dec(m, "costUSD"));
        }
        return result;
    }

    private static void Touch(ParsedSession s, DateTimeOffset? ts)
    {
        if (ts is null) return;
        if (s.FirstTimestamp is null || ts < s.FirstTimestamp) s.FirstTimestamp = ts;
        if (s.LastTimestamp is null || ts > s.LastTimestamp) s.LastTimestamp = ts;
    }

    internal static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    internal static long? Lng(JsonObject o, string key)
    {
        if (o[key] is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<double>(out var d)) return (long)d;
        return null;
    }

    internal static double? Dbl(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    internal static decimal? Dec(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<decimal>(out var d) ? d : null;

    private static DateTimeOffset? Time(JsonObject o, string key) =>
        Str(o, key) is { } s && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    private static List<string> Strings(JsonNode? node) =>
        node is JsonArray arr ? arr.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToJsonString() ?? "").ToList() : [];
}
