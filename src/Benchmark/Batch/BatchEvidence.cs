using System.Globalization;
using System.Text;
using Benchmark.Config;
using Benchmark.Evaluation;
using Benchmark.Util;

namespace Benchmark.Batch;

internal sealed record BatchSlot(string Id, string Task, string Workflow, int Repeat);
internal sealed record CodexStage(string Name, string Prompt, string OutputFile, int? ExitCode, double Seconds, bool TimedOut, CodexUsage Usage);
internal sealed record FixtureApproval(string Task, string Baseline, string ReviewId, string ApprovalId, string ApprovedBy, string OracleSha256);
internal sealed record VariantScore(string Variant, string Status, List<string> DetectedBy);
internal sealed record TestQuality(int AddedCases, int PassingOnSubmission, int PassingOnReference, int Flaky, int FixtureErrors, List<VariantScore> Variants);
internal sealed record Advisory(string Outcome, string? Baseline, string? RunId, string? Detail);
internal sealed record HandoffEvidence(string PreviousSubmissionSha256, string PreviousAssemblySha256, string DatabaseSha256,
    string? ArchiveStatus, List<string> ArtifactReads);
internal sealed record BatchResumeEvidence(string OriginRunnerSha256)
{
    public List<string> RetainedSlots { get; init; } = [];
    public string? InterruptedSlot { get; set; }
}

internal sealed class BatchAttempt
{
    public required BatchSlot Slot { get; init; }
    public string Status { get; set; } = "pending";
    public string? Detail { get; set; }
    public string? BaseCommit { get; set; }
    public string? SubmissionSha256 { get; set; }
    public List<CodexStage> Stages { get; set; } = [];
    public CodexUsage Usage { get; set; } = new();
    public double CodingSeconds { get; set; }
    public double? ElapsedSeconds { get; set; }
    public List<TestCaseResult> Checks { get; set; } = [];
    public List<TestCaseResult> OriginalTests { get; set; } = [];
    public TestQuality? TestQuality { get; set; }
    public Advisory? Tada { get; set; }
    public string? Review { get; set; }
    public CodexUsage? ReviewUsage { get; set; }
    public List<string> Integrity { get; set; } = [];
    public bool Reevaluated { get; set; }
    public bool ReranCoding { get; set; }
    public string? PromotedApiSha256 { get; set; }
    public HandoffEvidence? Handoff { get; set; }
    public TestQuality? FollowUpTestQuality { get; set; }

    internal bool HasCodingOutput() => Usage.CompletedTurnsWithUsage > 0 || Usage.Messages > 0 || Usage.ToolCalls > 0;
}

internal sealed record BatchManifest
{
    public required string Id { get; init; }
    public string Suite { get; init; } = "codex-sdd";
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public required int Seed { get; init; }
    public required int Repeats { get; init; }
    public required bool Synthetic { get; init; }
    public bool PreflightOnly { get; init; }
    public required List<BatchSlot> Slots { get; init; }
    public Dictionary<string, string> Pins { get; set; } = [];
    public string? OriginBatchId { get; init; }
    public string? OriginManifestSha256 { get; init; }
    public BatchResumeEvidence? Resume { get; init; }
    public string? PreflightFailure { get; set; }
    public bool Cancelled { get; set; }
}

internal sealed record CampaignOutcome(string Id, string Workflow, int Repeat, string Ticket1, string Ticket2, bool Passed,
    double CodingSeconds, double ElapsedSeconds, CodexUsage Usage);
internal sealed record BatchComparison(BatchManifest Batch, List<BatchAttempt> Attempts, List<CampaignOutcome>? Campaigns = null);

internal static class BatchEvidence
{
    public static BatchManifest Create(int seed, int repeats, bool synthetic, bool preflightOnly = false, string model = BatchWorker.DefaultModel,
        string suite = "codex-sdd")
    {
        if (repeats is < 1 or > 50) throw new BenchmarkException("--repeats must be between 1 and 50.");
        var ordered = (from repeat in Enumerable.Range(1, repeats)
                       from task in InventoryFixture.Tasks
                       from workflow in InventoryFixture.Workflows
                       select new BatchSlot($"{task}-{workflow}-{repeat:00}", task, workflow, repeat)).ToArray();
        var rng = new Random(seed);
        rng.Shuffle(ordered.AsSpan());
        if (suite == HandoffFixture.Suite)
        {
            string[] rotation = [.. InventoryFixture.Workflows];
            new Random(seed).Shuffle(rotation.AsSpan());
            ordered = (from repeat in Enumerable.Range(1, repeats)
                       from index in Enumerable.Range(0, rotation.Length)
                       let workflow = rotation[(index + repeat - 1) % rotation.Length]
                       from ticket in HandoffFixture.Tickets
                       select new BatchSlot($"{ticket}-{workflow}-{repeat:00}", ticket, workflow, repeat)).ToArray();
        }
        var manifest = new BatchManifest
        {
            Id = NewId(suite),
            Suite = suite,
            Seed = seed,
            Repeats = repeats,
            Synthetic = synthetic,
            PreflightOnly = preflightOnly,
            Slots = preflightOnly ? [] : ordered.ToList(),
            Pins = new()
            {
                ["codex"] = BatchWorker.CodexVersion, ["sdk"] = BatchWorker.SdkVersion,
                ["model"] = model, ["effort"] = BatchWorker.Effort,
                ["tada"] = "0.1.0-alpha.13.107", ["speckit"] = "1.4.0", ["openspec"] = "2.0.0",
                ["sandbox"] = "elevated workspace-write, offline, approval never"
            }
        };
        if (suite == HandoffFixture.Suite)
            foreach (string ticket in HandoffFixture.Tickets)
                manifest.Pins[ticket + "-hidden-checks"] = HandoffChecks.Denominator(ticket).ToString(CultureInfo.InvariantCulture);
        return manifest;
    }

    public static string NewId(string suite = "codex-sdd") => $"{suite}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}";

    public static string AttemptPath(string root, BatchSlot slot) => Path.Combine(root, "attempts", slot.Id);

    public static void Save(string root, BatchAttempt attempt) => Json.Write(Path.Combine(AttemptPath(root, attempt.Slot), "attempt.json"), attempt);

    internal static string CodexFailure(CodexStage stage, string? failure)
    {
        string detail = $"Codex stage {stage.Name} exited {stage.ExitCode}.";
        return string.IsNullOrWhiteSpace(failure) ? detail : $"{detail} {failure.Trim()}";
    }

    public static void Seal(string folder)
    {
        var seal = Path.Combine(folder, "seal.json");
        if (File.Exists(seal)) throw new BenchmarkException("Evidence is already sealed.");
        var entries = Fs.EnumerateFilesSorted(folder).ToDictionary(file => Fs.RelativeUnix(folder, file), Fs.Sha256File);
        Json.Write(seal, entries);
    }

    public static void Verify(string folder)
    {
        var expected = Json.Read<Dictionary<string, string>>(Path.Combine(folder, "seal.json"));
        var observed = Fs.EnumerateFilesSorted(folder)
            .Where(file => Fs.RelativeUnix(folder, file) != "seal.json")
            .ToDictionary(file => Fs.RelativeUnix(folder, file), Fs.Sha256File);
        if (expected.Count != observed.Count || expected.Any(pair => observed.GetValueOrDefault(pair.Key) != pair.Value))
            throw new BenchmarkException("Sealed evidence differs from its manifest.");
    }

    public static BatchManifest ReadManifest(string root, bool requireSeal = false)
    {
        string manifestPath = Path.Combine(root, "batch.json");
        if (!File.Exists(manifestPath)) throw new BenchmarkException("Batch manifest was not found.");
        string sealPath = Path.Combine(root, "batch.sha256");
        if (requireSeal && !File.Exists(sealPath)) throw new BenchmarkException("The batch has not sealed its manifest.");
        if (File.Exists(sealPath) && Fs.Sha256File(manifestPath) != File.ReadAllText(sealPath).Trim())
            throw new BenchmarkException("The batch manifest differs from its seal.");
        return Json.Read<BatchManifest>(manifestPath);
    }

    public static BatchAttempt ReadAttempt(string root, BatchSlot slot)
    {
        string path = AttemptPath(root, slot);
        Verify(path);
        Verify(Path.Combine(path, "coding"));
        var attempt = Json.Read<BatchAttempt>(Path.Combine(path, "attempt.json"));
        if (attempt.Slot != slot) throw new BenchmarkException("Sealed attempt belongs to another batch slot.");
        return attempt;
    }

    public static string Report(string root)
    {
        var manifest = ReadManifest(root);
        var attempts = new List<BatchAttempt>();
        foreach (var slot in manifest.Slots)
        {
            string path = AttemptPath(root, slot);
            if (!File.Exists(Path.Combine(path, "seal.json")))
            {
                attempts.Add(new BatchAttempt { Slot = slot, Status = manifest.PreflightFailure is null ? "not-run" : "preflight-blocked" });
                continue;
            }
            try
            {
                var attempt = ReadAttempt(root, slot);
                if (attempt.Status == "blocked" && attempt.Stages.LastOrDefault() is { } stage &&
                    attempt.Detail == CodexFailure(stage, null) && Path.GetFileName(stage.OutputFile) == stage.OutputFile)
                {
                    string transcript = Path.Combine(path, "coding", stage.OutputFile);
                    if (File.Exists(transcript)) attempt.Detail = CodexFailure(stage, CodexUsage.Parse(File.ReadLines(transcript)).FailureMessage);
                }
                attempts.Add(attempt);
            }
            catch (Exception error) when (error is BenchmarkException or IOException or System.Text.Json.JsonException)
            {
                attempts.Add(new BatchAttempt { Slot = slot, Status = "evidence-invalid", Detail = error.Message });
            }
        }
        var report = new BatchComparison(manifest, attempts, manifest.Suite == HandoffFixture.Suite
            ? attempts.GroupBy(a => (a.Slot.Workflow, a.Slot.Repeat)).Select(group =>
            {
                var first = group.Single(a => a.Slot.Task == HandoffFixture.First);
                var second = group.Single(a => a.Slot.Task == HandoffFixture.Second);
                return new CampaignOutcome($"{group.Key.Workflow}-{group.Key.Repeat:00}", group.Key.Workflow, group.Key.Repeat,
                    first.Status, second.Status, first.Status == "pass" && second.Status == "pass",
                    group.Sum(a => a.CodingSeconds), group.Sum(a => a.ElapsedSeconds ?? 0), CodexUsage.Combine(group.Select(a => a.Usage)));
            }).ToList() : null);
        string directory = Path.Combine(root, "report");
        Json.Write(Path.Combine(directory, "comparison.json"), report);
        string markdown = Path.Combine(directory, "comparison.md");
        Fs.WriteAtomic(markdown, Render(report));
        return markdown;
    }

    private static string Render(BatchComparison report)
    {
        var b = report.Batch;
        var text = new StringBuilder("# Codex specification workflow benchmark\n\n");
        text.AppendLine(b.PreflightOnly ? "**Preflight only. Zero model calls.**"
            : b.Synthetic ? "**Synthetic demonstration. No live model calls.**"
            : b.Repeats == 1 ? (report.Campaigns is null ? "**Pilot demonstration. One attempt per task and workflow.**"
                : "**Handoff smoke. One campaign per workflow.**")
            : "**Repeated comparison. Descriptive statistics.**");
        text.AppendLine().AppendLine($"Batch `{b.Id}` · seed {b.Seed} · {report.Attempts.Count} scheduled attempts").AppendLine();
        if (b.Resume is { } resume)
        {
            text.AppendLine($"Continued `{Cell(b.OriginBatchId ?? "")}` with {resume.RetainedSlots.Count} verified, retained ticket results. Unfinished slots use fresh sessions and workspaces.");
            text.AppendLine($"Source manifest SHA-256 `{Cell(b.OriginManifestSha256 ?? "")}`. Source runner SHA-256 `{Cell(resume.OriginRunnerSha256)}`.");
            if (resume.InterruptedSlot is { } interrupted)
                text.AppendLine($"The interrupted execution of `{Cell(interrupted)}` remains in the source batch. Its incomplete evidence is excluded from ticket outcomes and usage totals.");
            text.AppendLine();
        }
        else if (b.OriginBatchId is not null)
            text.AppendLine($"Recovery for `{Cell(b.OriginBatchId)}`. Each OpenSpec retry used a fresh workspace and Codex session.").AppendLine();
        if (b.PreflightFailure is not null) text.AppendLine($"Preflight blocked. {Cell(b.PreflightFailure)}").AppendLine();
        if (!b.Synthetic && !b.PreflightOnly && !b.Cancelled && b.PreflightFailure is null &&
            report.Attempts.Count > 0 && report.Attempts.All(attempt => !attempt.HasCodingOutput()))
        {
            string reason = report.Attempts.All(attempt => attempt.Stages.Count == 0)
                ? "No coding session started." : "No model returned coding output.";
            text.AppendLine($"**{reason} This batch produced no workflow comparison.**").AppendLine();
        }
        if (b.Cancelled) text.AppendLine("Cancelled. Completed evidence is retained.").AppendLine();

        text.AppendLine("## Pins").AppendLine();
        foreach (var pin in b.Pins) text.AppendLine($"- {pin.Key} `{Cell(pin.Value)}`");
        if (report.Campaigns is { } campaigns)
        {
            text.AppendLine().AppendLine("## Handoff campaigns").AppendLine();
            text.AppendLine("| Workflow | Both tickets passed / scheduled | Ticket 2 blocked by Ticket 1 | Complete campaign seconds, median [range] |");
            text.AppendLine("|---|---:|---:|---:|");
            foreach (var group in campaigns.GroupBy(campaign => campaign.Workflow).OrderBy(group => group.Key))
                text.AppendLine($"| {group.Key} | {group.Count(campaign => campaign.Passed)}/{group.Count()} | {group.Count(campaign => campaign.Ticket2 == "blocked-by-ticket-1")} | {Range(group.Where(campaign => campaign.Passed).Select(campaign => campaign.ElapsedSeconds))} |");
            text.AppendLine().AppendLine("| Campaign | Ticket 1 | Ticket 2 | Coding seconds | Total seconds | Messages | Turns | Input | Cached input | Output | Complete usage |");
            text.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---:|---:|---|");
            foreach (var campaign in campaigns)
            {
                var usage = campaign.Usage;
                text.AppendLine(FormattableString.Invariant($"| {campaign.Id} | {campaign.Ticket1} | {campaign.Ticket2} | {campaign.CodingSeconds:0.0} | {campaign.ElapsedSeconds:0.0} | {usage.Messages} | {usage.Turns} | {usage.InputTokens} | {usage.CachedInputTokens} | {usage.OutputTokens} | {usage.Complete} |"));
            }
        }
        text.AppendLine().AppendLine("## Results").AppendLine();
        text.AppendLine("| Attempt | Status | Hidden pass/fail/unknown/total | Original regressions | Mutants caught/total | TADA L1 | Turns | Messages | Tool calls | Seconds | Uncached input | Cached input | Cache writes | Output |");
        text.AppendLine("|---|---|---|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var a in report.Attempts)
        {
            var u = a.Usage;
            string Tokens(long? n) => n.HasValue ? n.Value.ToString(CultureInfo.InvariantCulture) + (u.Complete ? "" : "*") : "—";
            int total = report.Campaigns is null ? InventoryFixture.Checks(a.Slot.Task).Count
                : int.Parse(b.Pins[a.Slot.Task + "-hidden-checks"], CultureInfo.InvariantCulture);
            string hidden = $"{a.Checks.Count(x => x.Outcome == TestOutcome.Pass)}/{a.Checks.Count(x => x.Outcome == TestOutcome.Fail)}/{total - a.Checks.Count(x => x.Outcome is TestOutcome.Pass or TestOutcome.Fail)}/{total}";
            string mutations = a.TestQuality is { } quality ? $"{quality.Variants.Count(x => x.Status == "detected")}/{quality.Variants.Count}" : "—";
            string original = a.OriginalTests.Count > 0
                ? $"{a.OriginalTests.Count(x => x.Outcome == TestOutcome.Fail)}/{a.OriginalTests.Count}"
                : "—";
            bool hasTelemetry = a.Stages.Count > 0;
            string Count(int value) => hasTelemetry ? value.ToString(CultureInfo.InvariantCulture) : "—";
            text.AppendLine($"| `{a.Slot.Id}` | {a.Status} | {hidden} | {original} | {mutations} | {a.Tada?.Outcome ?? "—"} | {Count(u.Turns)} | {Count(u.Messages)} | {Count(u.ToolCalls)} | {(hasTelemetry ? FormattableString.Invariant($"{a.CodingSeconds:0.0}") : "—")} | {Tokens(u.CompletedTurnsWithUsage > 0 ? u.UncachedInputTokens : null)} | {Tokens(u.CompletedTurnsWithUsage > 0 ? u.CachedInputTokens : null)} | {Tokens(u.CacheWriteTokens)} | {Tokens(u.CompletedTurnsWithUsage > 0 ? u.OutputTokens : null)} |");
        }
        text.AppendLine().AppendLine("\\* Partial token totals. Cache reads are included in the CLI input count and shown separately here. Reviewer tokens are tracked separately.").AppendLine();

        text.AppendLine("## Per-task summary").AppendLine();
        text.AppendLine("| Task | Workflow | Successful / scheduled | Successful coding seconds, median [range] | Successful input tokens | Successful output tokens |");
        text.AppendLine("|---|---|---:|---:|---:|---:|");
        foreach (var group in report.Attempts.GroupBy(a => (a.Slot.Task, a.Slot.Workflow)).OrderBy(g => g.Key.Task).ThenBy(g => g.Key.Workflow))
        {
            var passed = group.Where(x => x.Status == "pass").ToList();
            var measured = passed.Where(x => x.Usage.Complete).ToList();
            text.AppendLine($"| {group.Key.Task} | {group.Key.Workflow} | {passed.Count}/{group.Count()} | {Range(passed.Select(x => x.CodingSeconds))} | {Range(measured.Select(x => (double)x.Usage.InputTokens))} | {Range(measured.Select(x => (double)x.Usage.OutputTokens))} |");
        }
        text.AppendLine().AppendLine("## Integrity and test evidence").AppendLine();
        foreach (var attempt in report.Attempts)
        {
            text.AppendLine($"### {attempt.Slot.Id}").AppendLine();
            if (attempt.Reevaluated) text.AppendLine("- Re-evaluated the sealed submission after a Windows sandbox setup failure.");
            if (attempt.ReranCoding) text.AppendLine("- Retried from the pinned task baseline after the original worker lacked the OpenSpec CLI.");
            if (attempt.Detail is not null) text.AppendLine($"- {Cell(attempt.Detail)}");
            foreach (string finding in attempt.Integrity) text.AppendLine($"- {Cell(finding)}");
            foreach (var result in attempt.Checks.Where(x => x.Outcome != TestOutcome.Pass))
                text.AppendLine($"- `{result.Id}` {result.Outcome} ({result.Cause}).");
            if (attempt.TestQuality is { } quality)
                text.AppendLine($"- Added cases {quality.AddedCases}. Passing on submission {quality.PassingOnSubmission}, passing on reference {quality.PassingOnReference}, flaky {quality.Flaky}, fixture errors {quality.FixtureErrors}. Faults {string.Join(", ", quality.Variants.Select(v => $"{v.Variant} {v.Status}"))}.");
            if (attempt.FollowUpTestQuality is { } followUp)
                text.AppendLine($"- Test cases in files added or edited after handoff detected {followUp.Variants.Count(v => v.Status == "detected")}/{followUp.Variants.Count} scored faults across {followUp.AddedCases} cases.");
            if (attempt.Handoff is { } handoff)
            {
                text.AppendLine($"- Handoff retained the preceding submission and database seed digests. Archive {Cell(handoff.ArchiveStatus ?? "not applicable")}.");
                foreach (string artifact in handoff.ArtifactReads)
                    text.AppendLine($"- Handoff reference observed in tool trace. {Cell(artifact)}");
            }
            if (attempt.ReviewUsage is { } review)
                text.AppendLine($"- Blind test review used {review.InputTokens} input and {review.OutputTokens} output tokens{(review.Complete ? "" : " (partial)")}. Findings are in the local JSON report.");
            if (attempt.Tada?.RunId is { } run) text.AppendLine($"- TADA retained run `{run}`.");
            text.AppendLine();
        }
        return text.ToString().Replace("\r\n", "\n");
    }

    private static string Range(IEnumerable<double> values)
    {
        var v = values.Order().ToArray();
        if (v.Length == 0) return "—";
        double median = (v[(v.Length - 1) / 2] + v[v.Length / 2]) / 2;
        return FormattableString.Invariant($"{median:0.##} [{v[0]:0.##}–{v[^1]:0.##}]");
    }

    private static string Cell(string value) => value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}
