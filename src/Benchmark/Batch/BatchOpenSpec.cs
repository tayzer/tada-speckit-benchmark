using System.Text.Json.Nodes;
using Benchmark.Config;
using Benchmark.Util;

namespace Benchmark.Batch;

internal sealed class BatchOpenSpec
{
    public const string Version = "2.0.0";
    private const string Package = "@fission-ai/openspec";
    private readonly string modules;
    private readonly string launcher;
    public string PackageSha256 { get; }
    public string NodeSha256 { get; }

    public BatchOpenSpec(BatchConfiguration config)
    {
        string root = config.OpenSpecPackage.Length > 0 ? config.OpenSpecPackage
            : Path.Combine(Path.GetDirectoryName(config.OpenSpecTemplate)!, "openspec-package");
        modules = Path.Combine(root, "node_modules");
        string manifest = Path.Combine(modules, Package, "package.json");
        if (!Path.IsPathFullyQualified(root) || !File.Exists(manifest))
            throw new BenchmarkException("OpenSpec CLI package is missing. Prepare it with New-CodexBenchmarkTemplates.ps1 or set openSpecPackage.");
        var package = Json.Read<JsonObject>(manifest);
        if (package["version"]?.ToString() != Version || package["bin"] is not JsonObject bin || bin["openspec"]?.ToString() != "./bin/openspec.js")
            throw new BenchmarkException($"Expected the pinned OpenSpec CLI {Version}.");

        string? node = config.Node.Length > 0 ? config.Node : Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator)
            .Where(Path.IsPathFullyQualified).Select(directory => Path.Combine(directory, "node.exe")).FirstOrDefault(File.Exists);
        if (node is null || !Path.IsPathFullyQualified(node) || !File.Exists(node))
            throw new BenchmarkException("Node.js is missing. Install Node.js 22.12 or newer or set the node executable in worker.json.");
        NodeSha256 = Fs.Sha256File(node);
        PackageSha256 = Fs.Sha256Directory(modules, includeBuildOutput: true);
        launcher = $"@echo off\r\n\"{node.Replace("%", "%%")}\" \"%~dp0node_modules\\@fission-ai\\openspec\\bin\\openspec.js\" %*\r\n";
    }

    public static string ToolPath(string workspace) => Path.Combine(workspace, ".worker-tmp", "openspec-tool");

    public void Stage(string workspace)
    {
        string tool = ToolPath(workspace);
        foreach (string file in Fs.EnumerateFilesSorted(modules, includeBuildOutput: true))
        {
            Submission.RequireNoLinks(modules, file);
            string target = Path.Combine(tool, "node_modules", Path.GetRelativePath(modules, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        Fs.WriteAtomic(Path.Combine(tool, "openspec.cmd"), launcher);
        if (!Unchanged(workspace)) throw new BenchmarkException("Staged OpenSpec CLI differs from the pinned package.");
    }

    public bool Unchanged(string workspace)
    {
        string tool = ToolPath(workspace);
        string command = Path.Combine(tool, "openspec.cmd");
        string staged = Path.Combine(tool, "node_modules");
        if (!File.Exists(command) || !Directory.Exists(staged)) return false;
        Submission.RequireNoLinks(tool, command);
        foreach (string file in Fs.EnumerateFilesSorted(staged, includeBuildOutput: true)) Submission.RequireNoLinks(tool, file);
        return File.ReadAllText(command) == launcher && Fs.Sha256Directory(staged, includeBuildOutput: true) == PackageSha256;
    }
}
