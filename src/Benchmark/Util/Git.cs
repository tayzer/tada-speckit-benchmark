using Benchmark.Config;

namespace Benchmark.Util;

public static class Git
{
    public static async Task<string> RunAsync(string workingDirectory, params string[] args) =>
        await RunAsync(workingDirectory, null, args);

    public static async Task<string> RunAsync(string workingDirectory, IReadOnlyDictionary<string, string?>? env, params string[] args)
    {
        var all = new List<string> { "-c", "core.autocrlf=false", "-c", "core.longpaths=true", "-c", "advice.detachedHead=false" };
        all.AddRange(args);
        var result = await ProcessRunner.RunAsync(new ProcessSpec("git", all, workingDirectory)
        {
            Environment = env ?? new Dictionary<string, string?>(),
            Timeout = TimeSpan.FromMinutes(20),
        });
        if (!result.Succeeded)
            throw new BenchmarkException($"git {string.Join(' ', args)} failed in {workingDirectory} (exit {result.ExitCode}): {result.Stderr.Trim()}{result.LaunchError}");
        return result.Stdout.Trim();
    }

    public static Task<string> HeadAsync(string repo) => RunAsync(repo, "rev-parse", "HEAD");

    public static async Task<bool> IsCleanAsync(string repo) => (await RunAsync(repo, "status", "--porcelain", "--untracked-files=all")).Length == 0;

    /// <summary>Commits as the harness identity without touching global git config.</summary>
    public static Task<string> CommitAllAsync(string repo, string message) =>
        RunAsync(repo, "-c", "user.name=benchmark-harness", "-c", "user.email=harness@benchmark.invalid", "-c", "commit.gpgsign=false",
            "commit", "--allow-empty", "--no-verify", "-q", "-m", message);

    /// <summary>
    /// Snapshots the working tree (tracked + untracked, honouring .gitignore) into a commit object using a
    /// temporary index, without changing HEAD, the real index, branches or any file in the workspace.
    /// </summary>
    public static async Task<string> SnapshotWorkingTreeAsync(string repo, string parentCommit, string message)
    {
        var tempIndex = Path.Combine(Path.GetTempPath(), $"bench-index-{Guid.NewGuid():N}");
        try
        {
            var env = new Dictionary<string, string?> { ["GIT_INDEX_FILE"] = tempIndex };
            await RunAsync(repo, env, "read-tree", parentCommit);
            await RunAsync(repo, env, "add", "-A", "--", ".");
            var tree = await RunAsync(repo, env, "write-tree");
            var commitEnv = new Dictionary<string, string?>
            {
                ["GIT_AUTHOR_NAME"] = "benchmark-harness", ["GIT_AUTHOR_EMAIL"] = "harness@benchmark.invalid",
                ["GIT_COMMITTER_NAME"] = "benchmark-harness", ["GIT_COMMITTER_EMAIL"] = "harness@benchmark.invalid",
            };
            return await RunAsync(repo, commitEnv, "commit-tree", tree, "-p", parentCommit, "-m", message);
        }
        finally
        {
            try { File.Delete(tempIndex); } catch { /* ignore */ }
        }
    }
}
