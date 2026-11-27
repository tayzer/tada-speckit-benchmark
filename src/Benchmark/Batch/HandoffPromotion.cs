using System.Text.Json.Nodes;
using Benchmark.Config;
using Benchmark.Util;

namespace Benchmark.Batch;

internal sealed record HandoffPromotion(string PreviousSubmission, string PreviousBuild, string Commit, string Api, string Database,
    string Reservation, Dictionary<string, string> Artifacts, HandoffEvidence Evidence)
{
    public static async Task<HandoffPromotion> CreateAsync(BatchWorker worker, string app, string coding,
        string evidence, BatchAttempt previous, (string Path, string Commit) pinned, CancellationToken ct)
    {
        string prior = BatchEvidence.AttemptPath(evidence, previous.Slot);
        string submission = Path.Combine(prior, "coding", "submission");
        string build = Path.Combine(prior, "evaluation", "promoted-api");
        if (previous.PromotedApiSha256 is null || Fs.Sha256Directory(build, includeBuildOutput: true) != previous.PromotedApiSha256 ||
            previous.SubmissionSha256 is null || Fs.Sha256Directory(submission) != previous.SubmissionSha256)
            throw new BenchmarkException("Ticket 1 promotion evidence changed after sealing.");
        Submission.Copy(submission, app);
        string nativeRuns = Path.Combine(prior, "coding", "agent-native-runs");
        if (previous.Slot.Workflow == "tada" && Directory.Exists(nativeRuns))
            Submission.Copy(nativeRuns, Path.Combine(app, ".tada", "runs"), retainEvidence: true);
        await Git.RunAsync(app, "init", "--quiet");
        await Git.RunAsync(app, "fetch", "--quiet", "--no-tags", "--", pinned.Path, pinned.Commit);
        await Git.RunAsync(app, "update-ref", "HEAD", pinned.Commit);
        string commit = await CodexBatch.InitializeGitAsync(app, "Completed ticket 1");
        if (previous.Slot.Workflow == "openspec") worker.OpenSpec!.Stage(app);
        await worker.PrepareMetadataAsync(app);
        string? archive = previous.Slot.Workflow == "openspec"
            ? await ArchiveAsync(worker, app, Path.Combine(coding, "openspec-archive"), ct) : null;
        if (archive == "archived") commit = await CodexBatch.InitializeGitAsync(app, "Archive completed workflow change");
        string api = HandoffFixture.StageApiBundle(build, app, "previous-release");
        string database = Path.Combine(app, ".worker-tmp", "handoff.sqlite");
        string reservation = await worker.WithApiAsync(app, api, Path.Combine(coding, "handoff-seed"),
            ct, HandoffChecks.SeedShipment, database);
        string digest = DatabaseDigest(app, database);
        string retained = Path.Combine(coding, "handoff-database");
        Directory.CreateDirectory(retained);
        foreach (string file in new[] { database, database + "-wal" }.Where(File.Exists))
            File.Copy(file, Path.Combine(retained, Path.GetFileName(file)));
        return new HandoffPromotion(submission, build, commit, api, database, reservation, WorkflowArtifacts(app, previous.Slot.Workflow),
            new HandoffEvidence(previous.SubmissionSha256, previous.PromotedApiSha256, digest, archive, []));
    }

    public bool InputsUnchanged(string app) =>
        Fs.Sha256Directory(Path.GetDirectoryName(Api)!, includeBuildOutput: true) == Evidence.PreviousAssemblySha256 &&
        DatabaseDigest(app, Database) == Evidence.DatabaseSha256;

    public static async Task ValidateArchiveAsync(BatchWorker worker, string probe, string logs, CancellationToken ct)
    {
        string change = Path.Combine(probe, "openspec", "changes", "handoff-preflight");
        Fs.WriteAtomic(Path.Combine(change, "proposal.md"), "# Why\n\nVerify archival under the configured worker.\n\n## What Changes\n\n- Archive a disposable readiness scenario.\n\n## Impact\n\n- None outside this probe.\n");
        Fs.WriteAtomic(Path.Combine(change, "tasks.md"), "## 1. Readiness\n\n- [x] 1.1 Run this diagnostic.\n");
        Fs.WriteAtomic(Path.Combine(change, "specs", "probe", "spec.md"), """
            ## ADDED Requirements

            ### Requirement: Verify the archive path
            The preflight MUST retain a canonical spec.

            #### Scenario: Archive the completed probe
            - **WHEN** the probe has completed
            - **THEN** its requirement is retained
            """);
        if (await ArchiveAsync(worker, probe, logs, ct) != "archived" || !File.Exists(Path.Combine(probe, "openspec", "specs", "probe", "spec.md")))
            throw new BenchmarkException("The pinned OpenSpec CLI could not promote the canonical spec under the worker sandbox.");
    }

    private static string DatabaseDigest(string workspace, string database)
    {
        var files = new[] { database, database + "-wal" }.Where(File.Exists).ToList();
        if (files.Count == 0 || files[0] != database) throw new BenchmarkException("The handoff database is missing.");
        foreach (string file in files) Submission.RequireNoLinks(workspace, file);
        return Fs.Sha256Text(string.Join("\n", files.Select(file => Path.GetFileName(file) + ":" + Fs.Sha256File(file))));
    }

    private static Dictionary<string, string> WorkflowArtifacts(string app, string workflow)
    {
        bool IsArtifact(string path) => workflow switch
        {
            "speckit" => path.StartsWith("specs/", StringComparison.Ordinal),
            "openspec" => path.StartsWith("openspec/", StringComparison.Ordinal),
            "tada" => path == ".tada/context.md" || path.StartsWith("features/", StringComparison.Ordinal),
            _ => path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && path is not ("TASK.md" or "CONTRACT.md")
        };
        return Submission.Files(app).Where(file => IsArtifact(Fs.RelativeUnix(app, file)))
            .ToDictionary(file => Fs.RelativeUnix(app, file), Fs.Sha256File, StringComparer.OrdinalIgnoreCase);
    }

    public List<string> ObservedArtifactReads(string coding)
    {
        var reads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string transcript in Directory.EnumerateFiles(coding, "??-*.out"))
        foreach (string line in File.ReadLines(transcript))
        {
            if (Json.TryParseNode(line) is not JsonObject item || item["type"]?.ToString() != "item.completed" ||
                item["item"] is not JsonObject command || command["type"]?.ToString() != "command_execution")
                continue;
            string pointer = (command["command"]?.ToString() ?? "").Replace('\\', '/');
            if (!System.Text.RegularExpressions.Regex.IsMatch(pointer, @"\b(Get-Content|cat|type|rg|git show)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
            foreach (string path in Artifacts.Keys.Where(path => pointer.Contains(path, StringComparison.OrdinalIgnoreCase)))
                reads.Add(path);
        }
        return reads.Order(StringComparer.Ordinal).ToList();
    }

    private static async Task<string> ArchiveAsync(BatchWorker worker, string app, string logs, CancellationToken ct)
    {
        string changes = Path.Combine(app, "openspec", "changes");
        var named = Directory.Exists(changes) ? Directory.GetDirectories(changes).Where(directory =>
            Path.GetFileName(directory) != "archive" && File.Exists(Path.Combine(directory, "proposal.md"))).ToList() : [];
        if (named.Count != 1) return "failed-to-identify-change";
        string change = Path.GetFileName(named[0]);
        if (!System.Text.RegularExpressions.Regex.IsMatch(change, @"\A[A-Za-z0-9][A-Za-z0-9_-]*\z")) return "invalid-change-name";
        string BoundaryDigest() => Fs.Sha256Text(string.Join("\n", Submission.Files(app)
            .Select(file => (Path: Fs.RelativeUnix(app, file), Digest: Fs.Sha256File(file)))
            .Where(file => !file.Path.StartsWith("openspec/", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file.Path, StringComparer.Ordinal).Select(file => $"{file.Path}:{file.Digest}")));
        string before = BoundaryDigest();
        var result = await worker.ArchiveOpenSpecAsync(app, change, logs, ct);
        if (BoundaryDigest() != before) throw new BenchmarkException("OpenSpec archival changed files outside its workflow artifacts.");
        return result.Succeeded && !Directory.Exists(named[0]) && Directory.Exists(Path.Combine(app, "openspec", "specs"))
            ? "archived" : "archive-failed";
    }
}
