using Benchmark.Batch;
using Benchmark.Config;
using Benchmark.Evaluation;
using Benchmark.Util;
using Xunit;

namespace Benchmark.Tests;

public sealed class CodexBatchTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = TestSupport.TempDir();
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private const string Thread = """{"type":"thread.started","thread_id":"00000000-0000-0000-0000-000000000001"}""";
    private const string Start = """{"type":"turn.started"}""";
    private const string Complete = """{"type":"turn.completed","usage":{"input_tokens":100,"cached_input_tokens":60,"output_tokens":12,"reasoning_output_tokens":7}}""";
    private const string Message = """{"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"ready"}}""";
    private const string Tool = """{"type":"item.completed","item":{"id":"item_1","type":"command_execution","command":"dotnet --version"}}""";

    [Fact]
    public void CountsCompletedUsageAndItemsOnceAcrossResumes()
    {
        var first = CodexUsage.Parse([Thread, Start, Message, Message, Tool, Tool, Complete, Complete]);
        var resumed = CodexUsage.Parse([Thread, Start, Message, Tool, Complete]);
        var aggregate = CodexUsage.Combine([first, resumed]);

        Assert.True(aggregate.Complete);
        Assert.Equal(2, aggregate.Turns);
        Assert.Equal(2, aggregate.Messages);
        Assert.Equal(2, aggregate.ToolCalls);
        Assert.Equal(200, aggregate.InputTokens);
        Assert.Equal(120, aggregate.CachedInputTokens);
        Assert.Equal(80, aggregate.UncachedInputTokens);
        Assert.Equal(24, aggregate.OutputTokens);
        Assert.Equal(14, aggregate.ReasoningTokens);
        Assert.Null(aggregate.CacheWriteTokens);
    }

    [Theory]
    [InlineData("""{"type":"turn.failed","error":{"message":"failure"}}""")]
    [InlineData("""{"type":"turn.completed","usage":""")]
    [InlineData("""{"type":"turn.completed","usage":{"input_tokens":1,"cached_input_tokens":2,"output_tokens":3}}""")]
    [InlineData("")]
    public void RetainsPreviousUsageForFailedOrTruncatedTurn(string incomplete)
    {
        var usage = CodexUsage.Parse([Thread, Start, Complete, Start, Message, incomplete]);
        Assert.False(usage.Complete);
        Assert.Equal(2, usage.Turns);
        Assert.Equal(1, usage.CompletedTurnsWithUsage);
        Assert.Equal(100, usage.InputTokens);
    }

    [Fact]
    public void ReportsTheProviderErrorFromAFailedTurn()
    {
        const string rejected = """{"type":"turn.failed","error":{"message":"{\"error\":{\"message\":\"Model not available for this account.\"}}"}}""";
        var usage = CodexUsage.Parse([Thread, Start, rejected]);
        Assert.Equal("Model not available for this account.", usage.FailureMessage);
        Assert.False(usage.Complete);
    }

    [Fact]
    public void RequiresTheSelectedModelToSupportTheBatchEffort()
    {
        const string catalog = """[{"model":"available","efforts":["low","medium"]},{"model":"low-only","efforts":["low"]}]""";
        var result = new ProcessResult(0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false, false, catalog, "", null);
        BatchWorker.RequireModel(result, "available");
        var error = Assert.Throws<BenchmarkException>(() => BatchWorker.RequireModel(result, "low-only"));
        Assert.Contains("Available models at medium effort: available.", error.Message);
        Assert.Equal("available", BatchEvidence.Create(1729, 1, synthetic: false, model: "available").Pins["model"]);
    }

    [Fact]
    public void StagesThePinnedOpenSpecExecutableAndDetectsChangesToItsBytes()
    {
        using var scratch = new TempDir();
        string module = Path.Combine(scratch.Path, "openspec-package", "node_modules", "@fission-ai", "openspec");
        string node = Path.Combine(scratch.Path, "node.exe");
        Fs.WriteAtomic(node, "placeholder for a pinned Node executable");
        Fs.WriteAtomic(Path.Combine(module, "package.json"), """{"version":"2.0.0","bin":{"openspec":"./bin/openspec.js"}}""");
        Fs.WriteAtomic(Path.Combine(module, "bin", "openspec.js"), "// Pinned OpenSpec executable bytes");
        var tool = new BatchOpenSpec(new BatchConfiguration { OpenSpecTemplate = Path.Combine(scratch.Path, "openspec"), Node = node });
        string app = Path.Combine(scratch.Path, "app");
        tool.Stage(app);
        Assert.True(tool.Unchanged(app));
        Fs.WriteAtomic(Path.Combine(BatchOpenSpec.ToolPath(app), "node_modules", "@fission-ai", "openspec", "bin", "openspec.js"), "// changed");
        Assert.False(tool.Unchanged(app));
    }

    [Fact]
    public async Task SealsTimedOutAttemptAndRunsEveryLaterSlot()
    {
        using var dir = new TempDir();
        var batch = BatchEvidence.Create(1729, 1, synthetic: true);
        Json.Write(Path.Combine(dir.Path, "batch.json"), batch);
        var visited = new List<string>();
        await CodexBatch.ScheduleAsync(dir.Path, batch, (slot, attempt, _) =>
        {
            visited.Add(slot.Id);
            attempt.Status = visited.Count == 1 ? "timed-out" : "pass";
            return Task.CompletedTask;
        }, _ => { }, TestContext.Current.CancellationToken);

        Assert.Equal(batch.Slots.Select(s => s.Id), visited);
        var markdown = BatchEvidence.Report(dir.Path);
        var result = Json.Read<BatchComparison>(Path.ChangeExtension(markdown, ".json"));
        Assert.Equal(8, result.Attempts.Count);
        Assert.Equal("timed-out", result.Attempts[0].Status);
        Assert.All(result.Attempts.Skip(1), a => Assert.Equal("pass", a.Status));
        Assert.Contains("No live model calls", File.ReadAllText(markdown));

        var first = BatchEvidence.AttemptPath(dir.Path, batch.Slots[0]);
        File.AppendAllText(Path.Combine(first, "attempt.json"), "tamper");
        BatchEvidence.Report(dir.Path);
        result = Json.Read<BatchComparison>(Path.ChangeExtension(markdown, ".json"));
        Assert.Equal("evidence-invalid", result.Attempts[0].Status);
    }

    [Fact]
    public async Task RetainsCancelledAttemptAndScheduledDenominator()
    {
        using var dir = new TempDir();
        using var cancellation = new CancellationTokenSource();
        var batch = BatchEvidence.Create(1729, 1, synthetic: true);
        Json.Write(Path.Combine(dir.Path, "batch.json"), batch);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CodexBatch.ScheduleAsync(dir.Path, batch,
            (_, attempt, token) =>
            {
                attempt.Status = "coding";
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }, _ => { }, cancellation.Token));
        batch.Cancelled = true;
        Json.Write(Path.Combine(dir.Path, "batch.json"), batch);
        string markdown = BatchEvidence.Report(dir.Path);
        var comparison = Json.Read<BatchComparison>(Path.ChangeExtension(markdown, ".json"));
        Assert.True(comparison.Batch.Cancelled);
        Assert.Equal("cancelled", comparison.Attempts[0].Status);
        Assert.Equal(7, comparison.Attempts.Count(attempt => attempt.Status == "not-run"));
    }

    [Fact]
    public void SeedProducesRepeatableOrderAndFullDenominators()
    {
        var a = BatchEvidence.Create(93, 5, synthetic: true);
        var b = BatchEvidence.Create(93, 5, synthetic: true);
        Assert.Equal(a.Slots, b.Slots);
        Assert.Equal(40, a.Slots.Count);
        Assert.All(a.Slots.GroupBy(s => (s.Task, s.Workflow)), group => Assert.Equal(5, group.Count()));
    }

    [Fact]
    public void HandoffScheduleKeepsBothTicketsTogetherAndRotatesWorkflowOrder()
    {
        var batch = BatchEvidence.Create(93, 3, synthetic: true, suite: HandoffFixture.Suite);
        Assert.Equal(24, batch.Slots.Count);
        var campaigns = batch.Slots.Chunk(2).ToArray();
        Assert.Equal(12, campaigns.Length);
        Assert.All(campaigns, campaign => Assert.Equal(campaign[0], CodexBatch.HandoffPredecessor(campaign[1])));
        foreach (string workflow in InventoryFixture.Workflows)
        {
            var positions = campaigns.Chunk(4).Select(repeat => Array.FindIndex(repeat, campaign => campaign[0].Workflow == workflow));
            Assert.Equal(3, positions.Distinct().Count());
        }
    }

    [Fact]
    public async Task HandoffResumeCopiesSealedPrefixAndUsesRetainedPredecessor()
    {
        using var dir = new TempDir();
        string source = Path.Combine(dir.Path, "source");
        string destination = Path.Combine(dir.Path, "continuation");
        var batch = BatchEvidence.Create(1729, 1, synthetic: false, suite: HandoffFixture.Suite);
        batch.Pins["handoff-runner-sha256"] = "source-runner";
        Json.Write(Path.Combine(source, "batch.json"), batch);
        await CodexBatch.ScheduleAsync(source, batch with { Slots = [batch.Slots[0]] }, (_, attempt, _) =>
        {
            attempt.Status = "pass";
            return Task.CompletedTask;
        }, _ => { }, TestContext.Current.CancellationToken);
        string partial = Path.Combine(BatchEvidence.AttemptPath(source, batch.Slots[1]), "coding", "01-implement.out");
        Fs.WriteAtomic(partial, "interrupted transcript");
        string seal = Fs.Sha256File(Path.Combine(BatchEvidence.AttemptPath(source, batch.Slots[0]), "seal.json"));

        var resume = new HandoffBatchResume(source);
        resume.Batch.Pins["handoff-runner-sha256"] = "continuation-runner";
        resume.VerifyPins();
        var remaining = resume.RetainCompleted(destination, _ => { });
        Assert.Equal(batch.Slots.Skip(1), remaining);
        Assert.Equal(seal, Fs.Sha256File(Path.Combine(BatchEvidence.AttemptPath(destination, batch.Slots[0]), "seal.json")));
        Assert.Equal(batch.Slots[1].Id, resume.Batch.Resume!.InterruptedSlot);
        Assert.False(Directory.Exists(BatchEvidence.AttemptPath(destination, batch.Slots[1])));
        Assert.Equal("interrupted transcript", File.ReadAllText(partial));

        var dispatched = new List<BatchSlot>();
        await CodexBatch.ScheduleAsync(destination, resume.Batch with { Slots = remaining }, (slot, attempt, _) =>
        {
            dispatched.Add(slot);
            attempt.Status = "pass";
            return Task.CompletedTask;
        }, _ => { }, TestContext.Current.CancellationToken);
        Json.Write(Path.Combine(destination, "batch.json"), resume.Batch);
        var markdown = BatchEvidence.Report(destination);
        var comparison = Json.Read<BatchComparison>(Path.ChangeExtension(markdown, ".json"));
        Assert.Equal(remaining, dispatched);
        Assert.Equal(batch.Id, comparison.Batch.OriginBatchId);
        Assert.Equal([batch.Slots[0].Id], comparison.Batch.Resume!.RetainedSlots);
        Assert.Equal(batch.Slots, comparison.Attempts.Select(attempt => attempt.Slot));
        Assert.All(comparison.Campaigns!, campaign => Assert.True(campaign.Passed));
        Assert.Contains("incomplete evidence is excluded", File.ReadAllText(markdown));

        resume.Batch.Pins["model"] = "wrong-model";
        Assert.Throws<BenchmarkException>(resume.VerifyPins);
        File.AppendAllText(Path.Combine(BatchEvidence.AttemptPath(source, batch.Slots[0]), "attempt.json"), "tamper");
        var tampered = new HandoffBatchResume(source);
        Assert.Throws<BenchmarkException>(() => tampered.RetainCompleted(Path.Combine(dir.Path, "rejected"), _ => { }));
    }

    [Fact]
    public async Task HandoffSealsTheBlockedSecondTicketAndContinuesOtherCampaigns()
    {
        using var dir = new TempDir();
        var batch = BatchEvidence.Create(1729, 1, synthetic: true, suite: HandoffFixture.Suite);
        Json.Write(Path.Combine(dir.Path, "batch.json"), batch);
        var visited = new List<BatchSlot>();
        await CodexBatch.ScheduleAsync(dir.Path, batch, (slot, attempt, _) =>
        {
            visited.Add(slot);
            attempt.Status = slot == batch.Slots[0] ? "timed-out" : "pass";
            return Task.CompletedTask;
        }, _ => { }, TestContext.Current.CancellationToken);
        Assert.Equal(7, visited.Count);
        var markdown = BatchEvidence.Report(dir.Path);
        var comparison = Json.Read<BatchComparison>(Path.ChangeExtension(markdown, ".json"));
        Assert.Equal(8, comparison.Attempts.Count);
        Assert.Equal("blocked-by-ticket-1", comparison.Attempts[1].Status);
        Assert.Equal(4, comparison.Campaigns!.Count);
        Assert.Equal(3, comparison.Campaigns.Count(campaign => campaign.Passed));
    }

    [Fact]
    public void FailsClosedOnForeignAndMissingSkills()
    {
        using var dir = new TempDir();
        CodexBatch.ValidateSkills(dir.Path, "plain");
        Assert.Throws<BenchmarkException>(() => CodexBatch.ValidateSkills(dir.Path, "speckit"));
        Fs.WriteAtomic(Path.Combine(dir.Path, ".agents", "skills", "tada", "SKILL.md"), "fixture");
        CodexBatch.ValidateSkills(dir.Path, "tada");
        Assert.Throws<BenchmarkException>(() => CodexBatch.ValidateSkills(dir.Path, "plain"));
    }

    [Theory]
    [InlineData(1, "", "Worker can read a forbidden file.")]
    [InlineData(1, "", "Loopback HTTP response failed.")]
    [InlineData(0, "not BENCHMARK_PREFLIGHT_OK", "")]
    public void RejectsIsolationAndHttpPreflightFailures(int exit, string output, string error)
    {
        var result = new ProcessResult(exit, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false, false, output, error, null);
        Assert.Throws<BenchmarkException>(() => BatchWorker.RequireProbe(result, "Worker"));
    }

    [Fact]
    public void RejectsChangedApprovalBytes()
    {
        using var dir = new TempDir();
        var receipts = new List<FixtureApproval>();
        foreach (string task in InventoryFixture.Tasks)
        {
            string app = Path.Combine(dir.Path, task);
            foreach (string file in new[] { "tada.json", "NuGet.Config", "global.json", "Directory.Build.props",
                ".config/dotnet-tools.json", "src/Inventory/Inventory.csproj", "src/Inventory/packages.lock.json",
                "Acceptance/test.txt", "features/task.feature", ".tada/baselines/inventory.json", ".tada/snapshots/file" })
                Fs.WriteAtomic(Path.Combine(app, file), "isolated test fixture");
            string review = Guid.NewGuid().ToString("N");
            string id = Guid.NewGuid().ToString("N");
            string baseline = "sha256:" + new string('0', 64);
            Json.Write(Path.Combine(app, ".tada/reviews", review + ".json"), new { id = review, baseline });
            Json.Write(Path.Combine(app, ".tada/approvals", id + ".json"), new { id, baseline, review, by = "Fixture test owner" });
            receipts.Add(new FixtureApproval(task, baseline, review, id, "Fixture test owner", BatchFixtures.OracleHash(app)));
        }
        Json.Write(Path.Combine(dir.Path, "approvals.json"), receipts);
        Assert.Equal(2, BatchFixtures.ReadApprovals(dir.Path).Count);
        string receiptPath = Path.Combine(dir.Path, "approvals.json");
        string receiptHash = Fs.Sha256File(receiptPath);
        Assert.Equal(2, BatchFixtures.Register(dir.Path).Count);
        Assert.Equal(receiptHash, Fs.Sha256File(receiptPath));
        Fs.WriteAtomic(Path.Combine(dir.Path, "cancel", "features", "task.feature"), "Changed");
        Assert.Throws<BenchmarkException>(() => BatchFixtures.ReadApprovals(dir.Path));
        Assert.Throws<BenchmarkException>(() => BatchFixtures.Register(dir.Path));
    }

    [Fact]
    public void MutationRequiresStablePassOnBothGoodControlsAndAnAssertionFailure()
    {
        static TestProjectResult Result(bool fault, bool broken = false) => new()
        {
            Tests =
            [
                new TestCaseResult { Id = "Baseline.Case", Outcome = TestOutcome.Pass },
                new TestCaseResult { Id = "Added.Case", Outcome = fault ? TestOutcome.Fail : TestOutcome.Pass,
                    Message = fault ? (broken ? "Host refused connection" : "Assert.Equal() Failure") : null }
            ]
        };
        var matrix = new Dictionary<string, List<TestProjectResult>>
        {
            ["reference"] = [Result(false), Result(false)],
            ["submission"] = [Result(false), Result(false)],
            ["original"] = [Result(true), Result(true)],
            ["fault-a"] = [Result(true), Result(false)],
            ["fault-b"] = [Result(true, broken: true), Result(true, broken: true)]
        };
        var score = BatchScoring.Score(matrix, ["Baseline.Case"]);
        Assert.Equal(1, score.AddedCases);
        Assert.Equal(1, score.Flaky);
        Assert.Equal(1, score.FixtureErrors);
        Assert.Equal(["detected", "indeterminate", "indeterminate"], score.Variants.Select(v => v.Status));
    }
}
