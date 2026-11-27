using Benchmark.Config;
using Benchmark.Util;

namespace Benchmark.Batch;

internal sealed class HandoffBatchResume
{
    private readonly string source;
    private readonly BatchManifest original;
    public BatchManifest Batch { get; }

    public HandoffBatchResume(string source)
    {
        this.source = source;
        string manifest = Path.Combine(source, "batch.json");
        original = BatchEvidence.ReadManifest(source);
        if (original.Suite != HandoffFixture.Suite || original.Synthetic || original.PreflightOnly || original.PreflightFailure is not null ||
            !original.Pins.TryGetValue("handoff-runner-sha256", out string? runner) || !original.Pins.TryGetValue("model", out string? model))
            throw new BenchmarkException("Resume requires a live handoff batch that completed preflight.");

        var fresh = BatchEvidence.Create(original.Seed, original.Repeats, synthetic: false, model: model, suite: original.Suite);
        if (!fresh.Slots.SequenceEqual(original.Slots)) throw new BenchmarkException("The source batch schedule differs from its seed and repeats.");
        Batch = fresh with
        {
            OriginBatchId = original.Id,
            OriginManifestSha256 = Fs.Sha256File(manifest),
            Resume = new(runner)
        };
    }

    public List<BatchSlot> RetainCompleted(string evidence, Action<string> log)
    {
        if (Fs.Sha256File(Path.Combine(source, "batch.json")) != Batch.OriginManifestSha256)
            throw new BenchmarkException("The source manifest changed while preparing recovery.");
        var resume = Batch.Resume!;
        var completed = new List<BatchSlot>();
        bool unfinished = false;
        foreach (var slot in Batch.Slots)
        {
            string path = BatchEvidence.AttemptPath(source, slot);
            var attempt = File.Exists(Path.Combine(path, "seal.json")) ? BatchEvidence.ReadAttempt(source, slot) : null;
            if (attempt is null || attempt.Status == "cancelled")
            {
                if (Directory.Exists(path))
                {
                    if (unfinished) throw new BenchmarkException("The source contains more than one interrupted ticket or an unexpected ticket directory.");
                    resume.InterruptedSlot = slot.Id;
                }
                unfinished = true;
                continue;
            }
            if (unfinished) throw new BenchmarkException("The source contains a sealed result after an unfinished ticket.");
            completed.Add(slot);
        }
        if (!unfinished) throw new BenchmarkException("All scheduled tickets are already sealed. Use batch-report.");

        foreach (var slot in completed)
        {
            Fs.CopyDirectory(BatchEvidence.AttemptPath(source, slot), BatchEvidence.AttemptPath(evidence, slot));
            BatchEvidence.ReadAttempt(evidence, slot);
            resume.RetainedSlots.Add(slot.Id);
        }
        log($"Retained {completed.Count} sealed tickets from {original.Id}. Remaining tickets: {Batch.Slots.Count - completed.Count}.");
        return Batch.Slots.Skip(completed.Count).ToList();
    }

    public void VerifyPins()
    {
        // Resume changes controller dispatch and reporting. Preserve both executable hashes and compare every other pin.
        var changed = original.Pins.Keys.Union(Batch.Pins.Keys)
            .Where(pin => pin != "handoff-runner-sha256" && original.Pins.GetValueOrDefault(pin) != Batch.Pins.GetValueOrDefault(pin))
            .Order(StringComparer.Ordinal).ToList();
        if (changed.Count != 0)
            throw new BenchmarkException($"Resumption inputs differ from the original pins: {string.Join(", ", changed)}.");
    }
}
