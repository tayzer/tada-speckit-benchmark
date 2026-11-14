using Benchmark.Config;
using Benchmark.Evaluation;
using Benchmark.Runs;
using Benchmark.Util;

namespace Benchmark.Workspace;

public sealed class PatchRecord
{
    public string File { get; set; } = "";
    public string Sha256 { get; set; } = "";
}

public sealed class BaseRecord
{
    public string ApplicationRepository { get; set; } = "";
    public string ApplicationCommit { get; set; } = "";
    /// <summary>Frozen starting commit for every workspace (application commit + compatibility patches).</summary>
    public string BaseCommit { get; set; } = "";
    public List<PatchRecord> CompatibilityPatches { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public double CloneSeconds { get; set; }
    public double RestoreSeconds { get; set; }
    public double BuildSeconds { get; set; }
    public double TestSeconds { get; set; }
    public bool BuildSucceeded { get; set; }
    public string BuildDiagnostics { get; set; } = "";
    public EnvironmentSnapshot Environment { get; set; } = new();
    public List<TestProjectResult> BaselineTests { get; set; } = [];
    public List<ExcludedProject> ExcludedTestProjects { get; set; } = [];
    public List<string> KnownIssues { get; set; } = [];
    public string RepositoryPath { get; set; } = "";
}

/// <summary>Clones the application at the pinned commit, applies frozen compatibility patches and records the baseline.</summary>
public static class BasePreparer
{
    public static string BaseDir(RunStore store, ExperimentConfig c) => Path.Combine(store.BaseRoot, c.Application.Commit[..12]);
    public static string BaseFile(RunStore store, ExperimentConfig c) => Path.Combine(BaseDir(store, c), "base.json");

    public static BaseRecord Load(RunStore store, ExperimentConfig c)
    {
        var file = BaseFile(store, c);
        if (!File.Exists(file)) throw new BenchmarkException("Base environment not prepared. Run `benchmark prepare-base` first.");
        var record = Json.Read<BaseRecord>(file);
        if (record.ApplicationCommit != c.Application.Commit) throw new BenchmarkException("base.json does not match application.commit.");
        var expected = c.Application.CompatibilityPatches.Select(p => Fs.Sha256File(c.ResolveExperimentPath(p))).ToList();
        if (!expected.SequenceEqual(record.CompatibilityPatches.Select(p => p.Sha256)))
            throw new BenchmarkException("Compatibility patches changed after the base was frozen. Prepare a new base (new data root) instead of mutating it.");
        return record;
    }

    public static async Task<BaseRecord> PrepareAsync(ExperimentConfig c, RunStore store, Action<string> log)
    {
        store.EnsureLayout();
        var baseDir = BaseDir(store, c);
        if (File.Exists(BaseFile(store, c)))
        {
            log($"Base already prepared and frozen: {BaseFile(store, c)} (never overwritten).");
            return Load(store, c);
        }
        if (Directory.Exists(baseDir))
            throw new BenchmarkException($"{baseDir} exists without base.json (interrupted preparation). Inspect and remove it manually; the harness never deletes it.");

        var repo = Path.Combine(baseDir, "repo");
        Directory.CreateDirectory(baseDir);
        var record = new BaseRecord
        {
            ApplicationRepository = c.Application.Repository,
            ApplicationCommit = c.Application.Commit,
            CreatedAt = DateTimeOffset.UtcNow,
            RepositoryPath = repo,
            ExcludedTestProjects = c.Application.ExcludedTestProjects,
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        log($"Cloning {c.Application.Repository} ...");
        await Git.RunAsync(baseDir, "clone", "--quiet", "--no-checkout", c.Application.Repository, "repo");
        await Git.RunAsync(repo, "checkout", "--quiet", "--detach", c.Application.Commit);
        var head = await Git.HeadAsync(repo);
        if (head != c.Application.Commit) throw new BenchmarkException($"Checked-out HEAD {head} != pinned {c.Application.Commit}.");
        record.CloneSeconds = sw.Elapsed.TotalSeconds;

        foreach (var patch in c.Application.CompatibilityPatches)
        {
            var path = c.ResolveExperimentPath(patch);
            log($"Applying compatibility patch {patch}");
            await Git.RunAsync(repo, "apply", "--whitespace=nowarn", path);
            record.CompatibilityPatches.Add(new PatchRecord { File = patch, Sha256 = Fs.Sha256File(path) });
        }
        if (record.CompatibilityPatches.Count > 0)
            await Git.CommitAllAsync(repo, "benchmark: frozen compatibility patches\n\n" + string.Join("\n", record.CompatibilityPatches.Select(p => $"{p.File} {p.Sha256}")));
        record.BaseCommit = await Git.HeadAsync(repo);
        if (!await Git.IsCleanAsync(repo)) throw new BenchmarkException("Base repository is not clean after patching.");

        var nuget = Path.Combine(baseDir, "nuget");
        var env = new Dictionary<string, string?> { ["NUGET_PACKAGES"] = nuget, ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1", ["DOTNET_NOLOGO"] = "1" };
        record.Environment = await EnvironmentProbe.CaptureAsync(repo, c.Agent.SettingSources, nuget);

        log("Restoring ...");
        var restore = await ProcessRunner.RunAsync(new ProcessSpec("dotnet", ["restore", c.Application.Solution], repo) { Environment = env, Timeout = TimeSpan.FromMinutes(30) });
        record.RestoreSeconds = restore.DurationSeconds;
        log("Building ...");
        var build = await ProcessRunner.RunAsync(new ProcessSpec("dotnet", ["build", c.Application.Solution, "--no-restore"], repo) { Environment = env, Timeout = TimeSpan.FromMinutes(30) });
        record.BuildSeconds = build.DurationSeconds;
        record.BuildSucceeded = restore.Succeeded && build.Succeeded;
        record.BuildDiagnostics = record.BuildSucceeded ? "" : TestRunner.FirstLines(restore.Stdout + build.Stdout + build.Stderr, "error", 15);
        Fs.WriteAtomic(Path.Combine(baseDir, "build.log"), restore.Stdout + restore.Stderr + build.Stdout + build.Stderr);
        if (!record.BuildSucceeded) record.KnownIssues.Add("Baseline build failed: " + record.BuildDiagnostics);

        var testStart = System.Diagnostics.Stopwatch.StartNew();
        foreach (var project in c.Application.TestProjects)
        {
            log($"Running baseline tests: {project}");
            var result = await TestRunner.RunAsync(repo, project, Path.Combine(baseDir, "baseline-tests"), env, TimeSpan.FromMinutes(c.Evaluation.TestTimeoutMinutes), noBuild: true);
            record.BaselineTests.Add(result);
            if (result.InfrastructureError is not null) record.KnownIssues.Add($"{project}: {result.InfrastructureError}");
            if (result.Failed > 0) record.KnownIssues.Add($"{project}: {result.Failed} test(s) already failing at baseline (excluded from regression counts).");
        }
        record.TestSeconds = testStart.Elapsed.TotalSeconds;
        foreach (var ex in c.Application.ExcludedTestProjects) record.KnownIssues.Add($"Excluded {ex.Path}: {ex.Reason}");

        Json.Write(BaseFile(store, c), record);
        log($"Base frozen at {record.BaseCommit}.");
        return record;
    }
}
