using Benchmark.Config;
using Benchmark.Util;

namespace Benchmark.Batch;

internal static class Submission
{
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".worker-tmp", ".vs", "bin", "obj", "TestResults"
    };

    public static IEnumerable<string> Files(string root, bool retainEvidence = false)
    {
        RequireNoLinks(root, root);
        var pending = new Stack<string>();
        pending.Push(root);
        long bytes = 0;
        int count = 0;
        while (pending.TryPop(out var directory))
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if (Ignored.Contains(Path.GetFileName(entry))) continue;
            string relative = Fs.RelativeUnix(root, entry);
            if (!retainEvidence && (relative is ".tada/runs" or ".tada/scratch" ||
                relative.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase) ||
                relative.Contains(".sqlite-", StringComparison.OrdinalIgnoreCase))) continue;
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new BenchmarkException($"Submission contains a linked path: {relative}");
            if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            else
            {
                bytes += new FileInfo(entry).Length;
                if (++count > 12_000 || bytes > 512L * 1024 * 1024)
                    throw new BenchmarkException("Submission exceeds the evidence capture limit.");
                yield return entry;
            }
        }
    }

    public static void Copy(string source, string destination, bool retainEvidence = false)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Files(source, retainEvidence))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    public static void RequireNoLinks(string root, string path)
    {
        if (!Fs.IsUnder(path, root)) throw new BenchmarkException("Evidence path points outside the workspace.");
        for (string? current = path; current is not null && Fs.IsUnder(current, root); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new BenchmarkException("Evidence path contains a reparse point.");
    }
}
