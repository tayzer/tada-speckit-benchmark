using System.Text.Json;
using Benchmark.Util;

namespace Benchmark.Batch;

internal sealed class CodexUsage
{
    public int Turns { get; set; }
    public int Messages { get; set; }
    public int ToolCalls { get; set; }
    public long InputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long? CacheWriteTokens { get; set; }
    public long? ReasoningTokens { get; set; }
    public int CompletedTurnsWithUsage { get; set; }
    public bool Complete { get; set; }
    public long UncachedInputTokens => InputTokens - CachedInputTokens;
    public string? ThreadId { get; set; }
    public string? FinalMessage { get; set; }
    public string? FailureMessage { get; set; }

    public static CodexUsage Combine(IEnumerable<CodexUsage> stages)
    {
        var all = stages.ToList();
        return new CodexUsage
        {
            Turns = all.Sum(x => x.Turns),
            Messages = all.Sum(x => x.Messages),
            ToolCalls = all.Sum(x => x.ToolCalls),
            InputTokens = all.Sum(x => x.InputTokens),
            CachedInputTokens = all.Sum(x => x.CachedInputTokens),
            OutputTokens = all.Sum(x => x.OutputTokens),
            CacheWriteTokens = SumOptional(all.Select(x => x.CacheWriteTokens)),
            ReasoningTokens = SumOptional(all.Select(x => x.ReasoningTokens)),
            CompletedTurnsWithUsage = all.Sum(x => x.CompletedTurnsWithUsage),
            Complete = all.Count > 0 && all.All(x => x.Complete),
            ThreadId = all.FirstOrDefault()?.ThreadId,
            FinalMessage = all.LastOrDefault()?.FinalMessage
        };
    }

    private static long? SumOptional(IEnumerable<long?> values)
    {
        var all = values.ToList();
        return all.Count > 0 && all.All(value => value.HasValue) ? all.Sum() : null;
    }

    // A resume is a new invocation. Only items within a single invocation share an ID namespace.
    public static CodexUsage Parse(IEnumerable<string> lines)
    {
        var usage = new CodexUsage();
        var itemIds = new HashSet<string>(StringComparer.Ordinal);
        bool pending = false;
        bool invalid = false;
        var turns = new List<CodexUsage>();
        foreach (var line in lines.Where(line => !string.IsNullOrWhiteSpace(line)))
        {
            if (Json.TryParseNode(line) is not System.Text.Json.Nodes.JsonObject node ||
                node["type"] is not System.Text.Json.Nodes.JsonValue type || !type.TryGetValue<string>(out var kind))
            {
                invalid = true;
                continue;
            }
            switch (kind)
            {
                case "thread.started":
                    if (node["thread_id"]?.ToString() is { } id && Guid.TryParse(id, out _))
                    {
                        if (usage.ThreadId is not null && usage.ThreadId != id) invalid = true;
                        usage.ThreadId = id;
                    }
                    else invalid = true;
                    break;
                case "turn.started":
                    if (pending) invalid = true;
                    else usage.Turns++;
                    pending = true;
                    break;
                case "turn.failed":
                    invalid = true;
                    pending = false;
                    if (node["error"] is System.Text.Json.Nodes.JsonObject error && error["message"]?.ToString() is { } message)
                    {
                        usage.FailureMessage = Json.TryParseNode(message) is System.Text.Json.Nodes.JsonObject payload &&
                            payload["error"] is System.Text.Json.Nodes.JsonObject providerError
                            ? providerError["message"]?.ToString() ?? message
                            : message;
                    }
                    break;
                case "error":
                    // Codex can emit transient retry errors. A terminal failure is represented by turn.failed or exit status.
                    break;
                case "item.completed" when node["item"] is System.Text.Json.Nodes.JsonObject item:
                    if (item["id"]?.ToString() is not { Length: > 0 } itemId || !itemIds.Add(itemId)) break;
                    if (item["type"]?.ToString() == "agent_message")
                    {
                        usage.Messages++;
                        usage.FinalMessage = item["text"]?.ToString();
                    }
                    else if (item["type"]?.ToString() is "command_execution" or "file_change" or "mcp_tool_call"
                        or "web_search" or "collab_tool_call" or "dynamic_tool_call") usage.ToolCalls++;
                    break;
                case "turn.completed" when pending:
                    pending = false;
                    if (node["usage"] is not System.Text.Json.Nodes.JsonObject tokens ||
                        Count(tokens["input_tokens"]) is not long input ||
                        Count(tokens["cached_input_tokens"]) is not long cached || cached > input ||
                        Count(tokens["output_tokens"]) is not long output)
                    {
                        invalid = true;
                        break;
                    }
                    turns.Add(new CodexUsage
                    {
                        InputTokens = input,
                        CachedInputTokens = cached,
                        OutputTokens = output,
                        CacheWriteTokens = Count(tokens["cache_write_input_tokens"] ?? tokens["cache_creation_input_tokens"]),
                        ReasoningTokens = Count(tokens["reasoning_output_tokens"])
                    });
                    break;
            }
        }
        usage.InputTokens = turns.Sum(x => x.InputTokens);
        usage.CachedInputTokens = turns.Sum(x => x.CachedInputTokens);
        usage.OutputTokens = turns.Sum(x => x.OutputTokens);
        usage.CacheWriteTokens = SumOptional(turns.Select(x => x.CacheWriteTokens));
        usage.ReasoningTokens = SumOptional(turns.Select(x => x.ReasoningTokens));
        usage.CompletedTurnsWithUsage = turns.Count;
        usage.Complete = !invalid && !pending && usage.ThreadId is not null && usage.Turns > 0 && turns.Count == usage.Turns;
        return usage;
    }

    private static long? Count(System.Text.Json.Nodes.JsonNode? value) =>
        value is System.Text.Json.Nodes.JsonValue number && number.TryGetValue<long>(out var count) && count >= 0 ? count : null;
}
