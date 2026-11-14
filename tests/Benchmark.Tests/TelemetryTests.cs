using System.Text.Json.Nodes;
using Benchmark.Config;
using Benchmark.DryRun;
using Benchmark.Telemetry;
using Xunit;

namespace Benchmark.Tests;

/// <summary>Structured Claude Code event parsing and token accounting.</summary>
public sealed class StreamParsingTests
{
    [Fact]
    public void Parses_real_cli_init_event()
    {
        var s = ClaudeEventParser.ParseStreamJson(File.ReadLines(TestSupport.Fixture("cli-2.1.323-print-first.stream.jsonl")));
        var init = Assert.Single(s.Inits);
        Assert.Equal("claude-haiku-5-5", init.Model);
        Assert.Equal("2.1.323", init.ClaudeCodeVersion);
        Assert.Equal("default", init.PermissionMode);
        Assert.Contains("Skill", init.Tools);
        Assert.DoesNotContain(init.Plugins, p => p.Contains("caveman", StringComparison.Ordinal)); // user plugins excluded by --setting-sources
        Assert.Equal(0, s.MalformedLines);
        Assert.Single(s.Results);
    }

    [Fact]
    public void Counts_malformed_lines_without_failing()
    {
        var lines = new StreamJsonBuilder("s1", "claude-opus-5-5").Init().Assistant("m1", 1, 2, 3, 4).Garbage().Garbage("[1,2").Result().Lines;
        var s = ClaudeEventParser.ParseStreamJson(lines);
        Assert.Equal(2, s.MalformedLines);
        Assert.Single(s.Messages);
    }

    [Fact]
    public void Extracts_tool_uses_once_per_id_and_marks_subagent_calls()
    {
        var b = new StreamJsonBuilder("s1", "claude-opus-5-5").Init()
            .Assistant("m1", 1, 1, 0, 0, blocks: 3, toolName: "Bash", toolInput: new JsonObject { ["command"] = "dotnet test" })
            .Assistant("m2", 1, 1, 0, 0, toolName: "Read", toolInput: new JsonObject { ["file_path"] = "x" }, parentToolUseId: "toolu_parent")
            .Result();
        var s = b.Parse();
        Assert.Equal(2, s.ToolUses.Count);
        Assert.True(s.ToolUses.Single(t => t.Name == "Read").IsSubagent);
        Assert.Equal(2, s.DuplicateMessageLines);
    }
}

public sealed class TokenAccountingTests
{
    private static PricingTable Pricing() => PricingTable.Load(Path.Combine(TestSupport.HarnessRoot, "pricing", "models.json"));

    [Fact]
    public void Duplicate_content_block_lines_are_counted_once()
    {
        var b = new StreamJsonBuilder("s1", "claude-opus-5-5").Init()
            .Assistant("m1", 10, 100, 1000, 500, blocks: 4)
            .Assistant("m2", 20, 200, 2000, 0, blocks: 2);
        var s = b.Parse();
        var sum = UsageAggregator.SumMessages(s.Messages.Values)["claude-opus-5-5"].Tokens;
        Assert.Equal(30, sum.Input);
        Assert.Equal(300, sum.Output);
        Assert.Equal(3000, sum.CacheRead);
        Assert.Equal(500, sum.CacheCreation);
    }

    [Fact]
    public void Cache_tokens_are_separate_categories_and_not_double_counted_in_prompt_total()
    {
        var t = new TokenCounts { Input = 5, CacheRead = 100, CacheCreation = 20, Output = 7 };
        Assert.Equal(125, t.TotalPromptTokens);
        var doubled = t + t;
        Assert.Equal(250, doubled.TotalPromptTokens);
    }

    [Fact]
    public void Estimated_cost_matches_cli_reported_cost_for_real_capture()
    {
        var parsed = ClaudeEventParser.ParseStreamJson(File.ReadLines(TestSupport.Fixture("cli-2.1.323-print-first.stream.jsonl")));
        var summary = UsageAggregator.FromInvocations([new InvocationCapture { InvocationId = "001", ProcessSucceeded = true, Parsed = parsed }], "claude-haiku-5-5", Pricing());
        Assert.Equal(0.00157153m, summary.CliReportedCostUsd);
        Assert.Equal(summary.CliReportedCostUsd, summary.EstimatedCostUsd); // pricing table reproduces the CLI's figure exactly
        Assert.Equal(7006, summary.Totals.CacheCreation);
        Assert.Equal(16813, summary.Totals.CacheRead);
        Assert.True(summary.Complete);
    }

    [Fact]
    public void Unpriced_models_are_reported_not_silently_zeroed()
    {
        var b = new StreamJsonBuilder("s1", "claude-unknown-9").Init().Assistant("m1", 10, 10, 0, 0).Result();
        var summary = UsageAggregator.FromInvocations([new InvocationCapture { InvocationId = "001", ProcessSucceeded = true, Parsed = b.Parse() }], "claude-unknown-9", Pricing());
        Assert.Contains("claude-unknown-9", summary.UnpricedModels);
        Assert.Null(summary.EstimatedCostUsd);
    }
}

public sealed class SessionContinuationTests
{
    [Fact]
    public void Resumed_session_uses_final_cumulative_not_sum_of_results_real_capture()
    {
        var first = ClaudeEventParser.ParseStreamJson(File.ReadLines(TestSupport.Fixture("cli-2.1.323-print-first.stream.jsonl")));
        var second = ClaudeEventParser.ParseStreamJson(File.ReadLines(TestSupport.Fixture("cli-2.1.323-print-resumed.stream.jsonl")));
        var t0 = DateTimeOffset.UtcNow;
        var summary = UsageAggregator.FromInvocations(
        [
            new InvocationCapture { InvocationId = "001", StartedAt = t0, ProcessSucceeded = true, Parsed = first },
            new InvocationCapture { InvocationId = "002", StartedAt = t0.AddMinutes(1), IsResume = true, ProcessSucceeded = true, Parsed = second },
        ], "claude-haiku-5-5", null);

        // CLI reports 0.00157153 then cumulative 0.00292885 — summing results would give 0.00450038.
        Assert.Equal(0.00292885m, summary.CliReportedCostUsd);
        Assert.Equal(4, summary.Totals.Input);
        Assert.Equal(9, summary.Totals.Output);
        Assert.Equal(16813 + 20102, summary.Totals.CacheRead);
        Assert.Equal(0.00135732m, summary.Invocations[1].CliCostUsd);
        Assert.Equal(UsageSource.ReportedDelta, summary.Invocations[1].Source);
        Assert.Single(summary.SessionIds);
        Assert.DoesNotContain(summary.Warnings, w => w.Contains("reconcile", StringComparison.Ordinal));
    }

    [Fact]
    public void Cumulative_difference_used_when_since_last_result_absent()
    {
        var b1 = new StreamJsonBuilder("s1", "claude-opus-5-5").Init().Assistant("a", 10, 100, 1000, 100).Result(includeSinceLast: false);
        var b2 = new StreamJsonBuilder("s1", "claude-opus-5-5").Init().Assistant("b", 20, 200, 2000, 200).Result(priorCumulative: b1.LastCumulative, includeSinceLast: false);
        var summary = UsageAggregator.FromInvocations(
        [
            new InvocationCapture { InvocationId = "1", StartedAt = DateTimeOffset.UnixEpoch, ProcessSucceeded = true, Parsed = b1.Parse() },
            new InvocationCapture { InvocationId = "2", StartedAt = DateTimeOffset.UnixEpoch.AddSeconds(1), IsResume = true, ProcessSucceeded = true, Parsed = b2.Parse() },
        ], "claude-opus-5-5", null);
        Assert.Equal(UsageSource.CumulativeDifference, summary.Invocations[1].Source);
        Assert.Equal(20, summary.Invocations[1].ByModel["claude-opus-5-5"].Tokens.Input);
        Assert.Equal(30, summary.Totals.Input);
        Assert.Equal(300, summary.Totals.Output);
        Assert.Equal(StreamJsonBuilder.Cost(30, 300, 3000, 300), summary.CliReportedCostUsd);
    }

    [Fact]
    public void Independent_sessions_are_summed()
    {
        var a = new StreamJsonBuilder("s1", "claude-opus-5-5").Init().Assistant("a", 10, 10, 0, 0).Result();
        var b = new StreamJsonBuilder("s2", "claude-opus-5-5").Init().Assistant("b", 5, 5, 0, 0).Result();
        var summary = UsageAggregator.FromInvocations(
        [
            new InvocationCapture { InvocationId = "1", ProcessSucceeded = true, Parsed = a.Parse() },
            new InvocationCapture { InvocationId = "2", StartedAt = DateTimeOffset.UnixEpoch.AddSeconds(5), ProcessSucceeded = true, Parsed = b.Parse() },
        ], "claude-opus-5-5", null);
        Assert.Equal(15, summary.Totals.Input);
        Assert.Equal(2, summary.SessionIds.Count);
    }

    [Fact]
    public void Transcript_cost_state_is_cumulative_across_resumes_real_capture()
    {
        var t = ClaudeEventParser.ParseTranscripts(TestSupport.Fixture("cli-2.1.323-transcript-resumed.jsonl"), []);
        Assert.Equal(2, t.CostStates.Count);
        var summary = UsageAggregator.FromTranscript(t, "claude-haiku-5-5", null);
        Assert.Equal(0.00292885m, summary.CliReportedCostUsd);
        Assert.Equal(2, summary.Invocations.Count);
        Assert.Equal(0.00157153m, summary.Invocations[0].CliCostUsd);
        Assert.Equal(0.00135732m, summary.Invocations[1].CliCostUsd);
        Assert.Contains("low", summary.ObservedEfforts);
        Assert.True(summary.Complete);
        Assert.Equal(2, t.HumanMessages.Count);
    }
}

public sealed class SubagentUsageTests
{
    [Fact]
    public void Subagent_usage_is_attributed_but_not_added_to_totals()
    {
        var b = new StreamJsonBuilder("s1", "claude-opus-5-5").Init()
            .Assistant("main1", 10, 100, 1000, 0)
            .Assistant("sub1", 3, 30, 300, 0, parentToolUseId: "toolu_x", modelOverride: "claude-haiku-5-5")
            .Assistant("main2", 10, 100, 1000, 0)
            .Result();
        var summary = UsageAggregator.FromInvocations([new InvocationCapture { InvocationId = "1", ProcessSucceeded = true, Parsed = b.Parse() }], "claude-opus-5-5", null);
        Assert.Equal(23, summary.Totals.Input); // result.modelUsage already includes the subagent
        Assert.Equal(3, summary.Subagents.Tokens.Input);
        Assert.Equal(1, summary.Subagents.ApiCalls);
        Assert.Equal(new[] { "claude-opus-5-5" }, summary.MainThreadModels);
        Assert.Contains(summary.Warnings, w => w.Contains("outside the main thread", StringComparison.Ordinal));
        Assert.DoesNotContain(summary.Warnings, w => w.StartsWith("MODEL_MISMATCH", StringComparison.Ordinal));
    }

    [Fact]
    public void Main_thread_model_switch_is_flagged_as_incomparable()
    {
        var b = new StreamJsonBuilder("s1", "claude-opus-5-5").Init()
            .Assistant("m1", 1, 1, 0, 0)
            .Assistant("m2", 1, 1, 0, 0, modelOverride: "claude-sonnet-5-5")
            .Result();
        var summary = UsageAggregator.FromInvocations([new InvocationCapture { InvocationId = "1", ProcessSucceeded = true, Parsed = b.Parse() }], "claude-opus-5-5", null);
        Assert.Contains(summary.Warnings, w => w.StartsWith("MODEL_MISMATCH", StringComparison.Ordinal) && w.Contains("claude-sonnet-5-5", StringComparison.Ordinal));
    }
}

public sealed class MissingTelemetryTests
{
    [Fact]
    public void Missing_result_marks_incomplete_and_falls_back_to_message_sums()
    {
        var b = new StreamJsonBuilder("s1", "claude-opus-5-5").Init().Assistant("m1", 10, 50, 100, 0, blocks: 2);
        var summary = UsageAggregator.FromInvocations([new InvocationCapture { InvocationId = "1", ProcessSucceeded = false, Parsed = b.Parse() }], "claude-opus-5-5", null);
        Assert.False(summary.Complete);
        Assert.Null(summary.CliReportedCostUsd);
        Assert.Equal(10, summary.Totals.Input);
        Assert.Equal(UsageSource.MessageSum, summary.Invocations[0].Source);
        Assert.Contains(summary.Warnings, w => w.Contains("No result event", StringComparison.Ordinal));
    }

    [Fact]
    public void Crash_after_successful_invocation_keeps_cumulative_plus_tail()
    {
        var b1 = new StreamJsonBuilder("s1", "claude-opus-5-5").Init().Assistant("a", 10, 10, 0, 0).Result();
        var b2 = new StreamJsonBuilder("s1", "claude-opus-5-5").Init().Assistant("b", 7, 7, 0, 0);
        var summary = UsageAggregator.FromInvocations(
        [
            new InvocationCapture { InvocationId = "1", StartedAt = DateTimeOffset.UnixEpoch, ProcessSucceeded = true, Parsed = b1.Parse() },
            new InvocationCapture { InvocationId = "2", StartedAt = DateTimeOffset.UnixEpoch.AddSeconds(1), IsResume = true, ProcessSucceeded = false, Parsed = b2.Parse() },
        ], "claude-opus-5-5", null);
        Assert.Equal(17, summary.Totals.Input);
        Assert.False(summary.Complete);
        Assert.Null(summary.CliReportedCostUsd); // cost of the crashed tail is unknown
    }

    [Fact]
    public void Error_result_is_flagged()
    {
        var b = new StreamJsonBuilder("s1", "claude-opus-5-5").Init().Assistant("a", 1, 1, 0, 0).Result(subtype: "error_during_execution", isError: true);
        var summary = UsageAggregator.FromInvocations([new InvocationCapture { InvocationId = "1", ProcessSucceeded = false, Parsed = b.Parse() }], "claude-opus-5-5", null);
        Assert.Contains(summary.Warnings, w => w.Contains("is_error=true", StringComparison.Ordinal));
        Assert.False(summary.Invocations[0].Complete);
    }

    [Fact]
    public void Transcript_without_cost_state_is_incomplete()
    {
        var path = Path.Combine(TestSupport.TempDir(), "t.jsonl");
        File.WriteAllLines(path, File.ReadLines(TestSupport.Fixture("cli-2.1.323-transcript-resumed.jsonl")).Where(l => !l.Contains("\"cost-state\"", StringComparison.Ordinal)));
        var summary = UsageAggregator.FromTranscript(ClaudeEventParser.ParseTranscripts(path, []), "claude-haiku-5-5", null);
        Assert.False(summary.Complete);
        Assert.Null(summary.CliReportedCostUsd);
        Assert.Equal(4, summary.Totals.Input);
    }

    [Fact]
    public void No_invocations_is_not_complete()
    {
        var summary = UsageAggregator.FromInvocations([], "claude-opus-5-5", null);
        Assert.False(summary.Complete);
        Assert.Null(summary.CliReportedCostUsd);
    }
}
