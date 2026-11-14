using System.Text.Json.Nodes;
using Benchmark.Telemetry;
using Xunit;

namespace Benchmark.Tests;

public class ConversationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);

    private static string User(int second, JsonNode content, bool meta = false, bool compact = false)
    {
        var o = new JsonObject { ["type"] = "user", ["timestamp"] = T0.AddSeconds(second).ToString("O"), ["message"] = new JsonObject { ["role"] = "user", ["content"] = content } };
        if (meta) o["isMeta"] = true;
        if (compact) o["isCompactSummary"] = true;
        return o.ToJsonString();
    }

    private static string Assistant(int second, string id, params JsonObject[] blocks) =>
        new JsonObject { ["type"] = "assistant", ["timestamp"] = T0.AddSeconds(second).ToString("O"), ["message"] = new JsonObject { ["id"] = id, ["content"] = new JsonArray(blocks) } }.ToJsonString();

    private static JsonObject Text(string t) => new() { ["type"] = "text", ["text"] = t };
    private static JsonObject Tool(string id, string name, JsonObject input) => new() { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = input };
    private static JsonArray Result(string id, string text, bool error = false) => new(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = text, ["is_error"] = error });

    private static List<string> Sample() =>
    [
        User(0, "Fix duplicate orders."),
        Assistant(5, "m1", Text("Looking at checkout.")),
        Assistant(6, "m1", Tool("a", "Bash", new JsonObject { ["command"] = "dotnet build" })),
        User(20, Result("a", "Build succeeded")),
        User(21, new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Base directory for this skill: ..." }), meta: true),
        Assistant(30, "m2", Tool("q", "AskUserQuestion", new JsonObject { ["questions"] = new JsonArray(new JsonObject { ["question"] = "Retry outcome?", ["options"] = new JsonArray(new JsonObject { ["label"] = "Original" }) }) })),
        User(90, Result("q", "User has answered your questions: \"Retry outcome?\"=\"Show the original order confirmation.\"")),
        Assistant(100, "m3", Tool("r", "Bash", new JsonObject { ["command"] = "git reset --hard" })),
        User(110, Result("r", "The user doesn't want to proceed with this tool use. The tool use was rejected.", error: true)),
        User(111, "[Request interrupted by user for tool use]"),
        new JsonObject { ["type"] = "system", ["subtype"] = "local_command", ["timestamp"] = T0.AddSeconds(115).ToString("O"), ["content"] = "<command-name>/model</command-name>\n<command-message>model</command-message>\n<command-args>claude-haiku-5-5</command-args>" }.ToJsonString(),
        User(200, "<command-message>speckit-plan</command-message>\n<command-name>/speckit-plan</command-name>\n<command-args>use EF</command-args>"),
        User(201, "<local-command-stdout>Set model</local-command-stdout>"),
        Assistant(210, "m4", Text("Plan written.")),
        User(4000, "You edited the wrong layer."),
        User(4001, "summary", compact: true),
        "not json",
    ];

    [Fact]
    public void Read_classifies_human_inputs_and_skips_meta_and_command_output()
    {
        var entries = Conversation.Read(Sample());
        var kinds = entries.Where(e => e.IsHumanInput).Select(e => e.Kind).ToList();
        Assert.Equal([EntryKind.HumanPrompt, EntryKind.QuestionAnswer, EntryKind.ToolRejection, EntryKind.Interrupt, EntryKind.SlashCommand, EntryKind.HumanPrompt], kinds);
        Assert.Contains(entries, e => e.Kind == EntryKind.LocalCommand && e.Text == "/model claude-haiku-5-5");
        Assert.Contains(entries, e => e.Kind == EntryKind.SlashCommand && e.Text == "/speckit-plan use EF");
        Assert.DoesNotContain(entries, e => e.Text.Contains("Base directory for this skill"));
        Assert.DoesNotContain(entries, e => e.Text.Contains("local-command-stdout"));
        Assert.Contains(entries, e => e.Kind == EntryKind.CompactSummary);
    }

    [Fact]
    public void Analyze_counts_inputs_and_measures_capped_waiting_time()
    {
        var answers = new Dictionary<string, string> { ["D2"] = "Show the original order   confirmation.", ["D9"] = "Something never said." };
        var s = Conversation.Analyze(Conversation.Read(Sample()), TimeSpan.FromMinutes(30), answers);
        Assert.Equal(2, s.HumanPrompts);
        Assert.Equal(1, s.SlashCommands);
        Assert.Equal(["/speckit-plan"], s.SlashCommandNames);
        Assert.Equal(1, s.AgentQuestions);
        Assert.Equal(1, s.QuestionsAnswered);
        Assert.Equal(1, s.ToolRejections);
        Assert.Equal(1, s.Interruptions);
        Assert.Equal(6, s.TotalHumanInputs);
        // Waits: prompt 0 (first), answer 90-30=60, rejection 110-100=10, interrupt 0 (agent busy),
        // slash command 200-111=89 (the local /model command does not reset the clock), last prompt 4000-210=3790 capped to 1800.
        Assert.Equal(60 + 10 + 89 + 1800, s.HumanWaitSeconds, 3);
        Assert.Equal(1, s.GapsCapped);
        Assert.Equal(["D2"], s.DecisionsMatched);
        Assert.Contains(s.Warnings, w => w.Contains("/model claude-haiku-5-5"));
        Assert.Equal([1, 2, 3, 4, 5, 6], s.Turns.Select(t => t.Number));
    }

    [Fact]
    public void Markdown_numbers_human_inputs_and_summarises_tools()
    {
        var entries = Conversation.Read(Sample());
        var s = Conversation.Analyze(entries, TimeSpan.FromMinutes(30));
        var md = Conversation.RenderMarkdown("Conversation: test", entries, s, [("agent-1", Conversation.Read([Assistant(5, "s1", Tool("x", "Grep", new JsonObject { ["pattern"] = "Order" }))]))]);
        Assert.Contains("### H1 Human, message", md);
        Assert.Contains("### H2 Human, answer to agent question", md);
        Assert.Contains("### H3 Human, rejected tool call", md);
        Assert.Contains("### H5 Human, slash command", md);
        Assert.Contains("> You edited the wrong layer.", md);
        Assert.Contains("- Tool `Bash`: `dotnet build`", md);
        Assert.Contains("Question: Retry outcome? Options: Original", md);
        Assert.Contains("_Operator ran local command `/model claude-haiku-5-5`", md);
        Assert.Contains("## Subagents", md);
        Assert.Contains("- Tool `Grep`: `Order`", md);
        Assert.DoesNotContain("Build succeeded", md); // successful tool output omitted
    }

    [Fact]
    public void Real_cli_transcript_fixture_parses()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "cli-2.1.323-transcript-resumed.jsonl");
        var entries = Conversation.Read(File.ReadLines(path));
        var s = Conversation.Analyze(entries, TimeSpan.FromMinutes(30));
        Assert.True(s.HumanPrompts >= 1);
        Assert.Contains(entries, e => e.Kind == EntryKind.AssistantText);
    }
}
