using System.Text.Json.Nodes;
using Benchmark.Telemetry;

namespace Benchmark.DryRun;

/// <summary>
/// Generates SYNTHETIC Claude Code stream-json lines with the same shape as real CLI 2.1.323 output
/// (system/init, assistant with per-response usage repeated per content block, user tool results, result
/// with cumulative modelUsage and model_usage_since_last_result). Used by tests and the dry run only.
/// </summary>
public sealed class StreamJsonBuilder(string sessionId, string model)
{
    private readonly List<string> _lines = [];
    private readonly Dictionary<string, (long In, long Out, long Cr, long Cc, decimal Cost)> _sinceLast = new();
    private int _turns;
    private DateTimeOffset _clock = new(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);
    public string SessionId { get; } = sessionId;
    public string Model { get; } = model;

    public StreamJsonBuilder Init(IEnumerable<string>? plugins = null, IEnumerable<string>? skills = null, string version = "2.1.323", string? initModel = null)
    {
        var pluginArr = new JsonArray((plugins ?? []).Select(p => (JsonNode)new JsonObject { ["name"] = p, ["path"] = $"C:/synthetic/plugins/{p}", ["source"] = $"{p}@synthetic" }).ToArray());
        var skillList = (skills ?? []).ToList();
        _lines.Add(new JsonObject
        {
            ["type"] = "system", ["subtype"] = "init", ["session_id"] = SessionId, ["cwd"] = "C:/synthetic/workspace",
            ["model"] = initModel ?? Model, ["permissionMode"] = "acceptEdits", ["claude_code_version"] = version, ["apiKeySource"] = "none",
            ["tools"] = new JsonArray("Bash", "Edit", "Read", "Skill"), ["mcp_servers"] = new JsonArray(),
            ["slash_commands"] = new JsonArray(skillList.Select(s => (JsonNode)s).ToArray()),
            ["skills"] = new JsonArray(skillList.Select(s => (JsonNode)s).ToArray()),
            ["agents"] = new JsonArray("general-purpose", "Explore"), ["plugins"] = pluginArr,
            ["uuid"] = Guid.NewGuid().ToString(),
        }.ToJsonString());
        return this;
    }

    /// <summary>One API response. <paramref name="blocks"/> content blocks are emitted as separate lines sharing the id and usage.</summary>
    public StreamJsonBuilder Assistant(string messageId, long input, long output, long cacheRead, long cacheCreation, int blocks = 1,
        string? toolName = null, JsonObject? toolInput = null, string? parentToolUseId = null, string? modelOverride = null, string? text = "Working on it.")
    {
        var m = modelOverride ?? Model;
        for (var b = 0; b < blocks; b++)
        {
            var content = new JsonArray();
            if (b == blocks - 1 && toolName is not null)
                content.Add(new JsonObject { ["type"] = "tool_use", ["id"] = $"toolu_{messageId}", ["name"] = toolName, ["input"] = toolInput?.DeepClone() ?? new JsonObject() });
            else content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
            _lines.Add(new JsonObject
            {
                ["type"] = "assistant",
                ["message"] = new JsonObject
                {
                    ["id"] = messageId, ["model"] = m, ["role"] = "assistant", ["content"] = content,
                    ["usage"] = new JsonObject
                    {
                        ["input_tokens"] = input, ["output_tokens"] = output,
                        ["cache_read_input_tokens"] = cacheRead, ["cache_creation_input_tokens"] = cacheCreation,
                        ["cache_creation"] = new JsonObject { ["ephemeral_5m_input_tokens"] = 0, ["ephemeral_1h_input_tokens"] = cacheCreation },
                    },
                },
                ["parent_tool_use_id"] = parentToolUseId, ["session_id"] = SessionId, ["uuid"] = Guid.NewGuid().ToString(),
                ["timestamp"] = (_clock = _clock.AddSeconds(7)).ToString("O"),
            }.ToJsonString());
        }
        var cur = _sinceLast.GetValueOrDefault(m);
        _sinceLast[m] = (cur.In + input, cur.Out + output, cur.Cr + cacheRead, cur.Cc + cacheCreation, cur.Cost + Cost(input, output, cacheRead, cacheCreation));
        if (parentToolUseId is null) _turns++;
        return this;
    }

    public StreamJsonBuilder ToolResult(string toolUseId, string content = "ok")
    {
        _lines.Add(new JsonObject
        {
            ["type"] = "user", ["session_id"] = SessionId, ["parent_tool_use_id"] = null,
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = toolUseId, ["content"] = content }) },
        }.ToJsonString());
        return this;
    }

    public StreamJsonBuilder Garbage(string text = "{not json") { _lines.Add(text); return this; }

    /// <summary>Emits a result. <paramref name="priorCumulative"/> is the session's cumulative usage before this invocation (resume semantics).</summary>
    public StreamJsonBuilder Result(Dictionary<string, (long In, long Out, long Cr, long Cc, decimal Cost)>? priorCumulative = null,
        bool includeSinceLast = true, string subtype = "success", bool isError = false, int permissionDenials = 0)
    {
        var cumulative = new Dictionary<string, (long In, long Out, long Cr, long Cc, decimal Cost)>(priorCumulative ?? new());
        foreach (var (m, v) in _sinceLast)
        {
            var c = cumulative.GetValueOrDefault(m);
            cumulative[m] = (c.In + v.In, c.Out + v.Out, c.Cr + v.Cr, c.Cc + v.Cc, c.Cost + v.Cost);
        }
        var obj = new JsonObject
        {
            ["type"] = "result", ["subtype"] = subtype, ["is_error"] = isError, ["session_id"] = SessionId,
            ["num_turns"] = _turns, ["duration_ms"] = 60000 + _turns * 1000, ["duration_api_ms"] = 45000 + _turns * 800,
            ["total_cost_usd"] = cumulative.Values.Sum(v => v.Cost),
            ["modelUsage"] = UsageMap(cumulative),
            ["permission_denials"] = new JsonArray(Enumerable.Range(0, permissionDenials).Select(i => (JsonNode)new JsonObject { ["tool_name"] = "Bash", ["tool_use_id"] = $"denied{i}" }).ToArray()),
            ["uuid"] = Guid.NewGuid().ToString(),
        };
        if (includeSinceLast) obj["model_usage_since_last_result"] = UsageMap(_sinceLast);
        _lines.Add(obj.ToJsonString());
        LastCumulative = cumulative;
        return this;
    }

    public Dictionary<string, (long In, long Out, long Cr, long Cc, decimal Cost)> LastCumulative { get; private set; } = new();

    public List<string> Lines => _lines;

    public ParsedSession Parse() => ClaudeEventParser.ParseStreamJson(_lines);

    public static decimal Cost(long input, long output, long cacheRead, long cacheCreation) =>
        (input * 4m + output * 20m + cacheRead * 0.2m + cacheCreation * 8m) / 1_000_000m;

    private static JsonObject UsageMap(Dictionary<string, (long In, long Out, long Cr, long Cc, decimal Cost)> map)
    {
        var o = new JsonObject();
        foreach (var (m, v) in map)
            o[m] = new JsonObject
            {
                ["inputTokens"] = v.In, ["outputTokens"] = v.Out, ["cacheReadInputTokens"] = v.Cr, ["cacheCreationInputTokens"] = v.Cc,
                ["cacheCreationEphemeral5mInputTokens"] = 0, ["cacheCreationEphemeral1hInputTokens"] = v.Cc,
                ["webSearchRequests"] = 0, ["costUSD"] = v.Cost, ["contextWindow"] = 1000000,
            };
        return o;
    }
}
