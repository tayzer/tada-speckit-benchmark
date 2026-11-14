using System.Security.Cryptography;
using Benchmark.Config;
using Benchmark.Util;

namespace Benchmark.Runs;

public enum RunStatus { Prepared, Running, Finished, Sealed }
public enum ExecutionMode { Interactive, Managed }

public sealed class AgentSettingsSnapshot
{
    public string ClaudeExecutable { get; set; } = "";
    public string? ClaudeVersionReported { get; set; }
    public string Model { get; set; } = "";
    public string Effort { get; set; } = "";
    public string? FallbackModel { get; set; }
    public string PermissionMode { get; set; } = "";
    public List<string> AllowedTools { get; set; } = [];
    public List<string> DisallowedTools { get; set; } = [];
    public string SettingSources { get; set; } = "";
    public bool StrictMcpConfig { get; set; }
    public string PermissionPrompts { get; set; } = "";
    /// <summary>Hash of the workflow-independent permission policy (allow/deny lists with paths templated).</summary>
    public string PermissionPolicySha256 { get; set; } = "";
    public List<string> CommonArgs { get; set; } = [];
    public List<string> WorkflowArgs { get; set; } = [];
    public Dictionary<string, string> Environment { get; set; } = new();
    public string RunSettingsPath { get; set; } = "";
}

public sealed class WorkflowSetupRecord
{
    public string WorkflowId { get; set; } = "";
    public WorkflowKind Kind { get; set; }
    public string Version { get; set; } = "";
    public double SetupSeconds { get; set; }
    public List<string> Steps { get; set; } = [];
    public List<string> FilesAdded { get; set; } = [];
    public bool StaticActivationPassed { get; set; }
    public List<string> ActivationFindings { get; set; } = [];
    public string? PluginSha256 { get; set; }
    public Dictionary<string, string> VersionRecords { get; set; } = new();
}

public sealed class InvocationRecord
{
    public string InvocationId { get; set; } = "";
    public ExecutionMode Mode { get; set; }
    public bool IsResume { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; }
    public int? ExitCode { get; set; }
    public bool TimedOut { get; set; }
    public bool Cancelled { get; set; }
    public string? LaunchError { get; set; }
    public string? MessageSha256 { get; set; }
    public string? MessageKind { get; set; }
    public string? StdoutFile { get; set; }
    public string? StderrFile { get; set; }
    public List<string> Arguments { get; set; } = [];
    public double DurationSeconds => (EndedAt - StartedAt).TotalSeconds;
}

public sealed class RunRecord
{
    public string RunId { get; set; } = "";
    public string ExperimentId { get; set; } = "";
    public string WorkflowId { get; set; } = "";
    public bool Synthetic { get; set; }
    public RunStatus Status { get; set; }
    public ExecutionMode? Mode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string ApplicationCommit { get; set; } = "";
    public string BaseCommit { get; set; } = "";
    public string SetupCommit { get; set; } = "";
    public string? FinalCommit { get; set; }
    public string? FinalBranch { get; set; }
    public string WorkspacePath { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string PromptSha256 { get; set; } = "";
    public string InitialMessage { get; set; } = "";
    public string InitialMessageSha256 { get; set; } = "";
    public string HarnessCommit { get; set; } = "";
    public string ConfigSha256 { get; set; } = "";
    public AgentSettingsSnapshot Agent { get; set; } = new();
    public WorkflowSetupRecord Setup { get; set; } = new();
    public EnvironmentSnapshot Environment { get; set; } = new();
    public List<InvocationRecord> Invocations { get; set; } = [];
    public DateTimeOffset? FinishedAt { get; set; }
    public List<string> Deviations { get; set; } = [];
    public List<string> ForbiddenRoots { get; set; } = [];
}

public sealed class EnvironmentSnapshot
{
    public string Os { get; set; } = "";
    public string DotnetSdk { get; set; } = "";
    public string Git { get; set; } = "";
    public string? Uv { get; set; }
    public string? PowerShell7 { get; set; }
    public string? LocalDb { get; set; }
    public string Machine { get; set; } = "";
    public bool AnthropicApiKeyPresent { get; set; }
    public bool UserSettingsExcluded { get; set; }
    public string NugetPackagesPath { get; set; } = "";
}

public sealed class SealManifest
{
    public string RunId { get; set; } = "";
    public DateTimeOffset SealedAt { get; set; }
    public Dictionary<string, string> Files { get; set; } = new();
}

/// <summary>
/// Evidence store. A run directory is append-only while running and becomes immutable once sealed:
/// a manifest of SHA-256 hashes is written and any later write through this API is refused.
/// Evaluations are stored separately so sealed run evidence never changes.
/// </summary>
public sealed class RunStore(string dataRoot)
{
    public string DataRoot { get; } = Path.GetFullPath(dataRoot);
    public string RunsRoot => Path.Combine(DataRoot, "runs");
    public string WorkspacesRoot => Path.Combine(DataRoot, "workspaces");
    public string EvaluationsRoot => Path.Combine(DataRoot, "evaluations");
    public string BaseRoot => Path.Combine(DataRoot, "base");
    public string ReportsRoot => Path.Combine(DataRoot, "reports");
    public string PluginsRoot => Path.Combine(DataRoot, "plugins");

    public string RunDir(string runId) => Path.Combine(RunsRoot, runId);
    public string RunFile(string runId) => Path.Combine(RunDir(runId), "run.json");
    public string SealFile(string runId) => Path.Combine(RunDir(runId), "SEALED.json");

    public static string NewRunId(string workflowId, DateTimeOffset now) =>
        $"{now.UtcDateTime:yyyyMMdd-HHmmss}-{workflowId}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3))}";

    public void EnsureLayout()
    {
        foreach (var d in new[] { RunsRoot, WorkspacesRoot, EvaluationsRoot, BaseRoot, ReportsRoot, PluginsRoot }) Directory.CreateDirectory(d);
    }

    public RunRecord Create(RunRecord record)
    {
        var dir = RunDir(record.RunId);
        if (Directory.Exists(dir)) throw new BenchmarkException($"Run directory already exists: {dir}. Runs are never overwritten.");
        Directory.CreateDirectory(dir);
        Json.Write(RunFile(record.RunId), record);
        return record;
    }

    public RunRecord Load(string runId)
    {
        var file = RunFile(runId);
        if (!File.Exists(file)) throw new BenchmarkException($"Run '{runId}' not found under {RunsRoot}.");
        return Json.Read<RunRecord>(file);
    }

    public IEnumerable<RunRecord> All() =>
        Directory.Exists(RunsRoot)
            ? Directory.GetDirectories(RunsRoot).Where(d => File.Exists(Path.Combine(d, "run.json"))).Select(d => Json.Read<RunRecord>(Path.Combine(d, "run.json"))).OrderBy(r => r.CreatedAt)
            : [];

    public bool IsSealed(string runId) => File.Exists(SealFile(runId));

    public void Save(RunRecord record)
    {
        EnsureWritable(record.RunId);
        Json.Write(RunFile(record.RunId), record);
    }

    public string EvidencePath(string runId, params string[] parts) => Path.Combine([RunDir(runId), .. parts]);

    public void WriteEvidence(string runId, string relativePath, string content)
    {
        EnsureWritable(runId);
        Fs.WriteAtomic(EvidencePath(runId, relativePath), content);
    }

    public void EnsureWritable(string runId)
    {
        if (IsSealed(runId)) throw new BenchmarkException($"Run '{runId}' is sealed; its evidence is immutable. Create a new run instead.");
    }

    public SealManifest Seal(RunRecord record)
    {
        EnsureWritable(record.RunId);
        record.Status = RunStatus.Sealed;
        Json.Write(RunFile(record.RunId), record);
        var dir = RunDir(record.RunId);
        var manifest = new SealManifest { RunId = record.RunId, SealedAt = DateTimeOffset.UtcNow };
        foreach (var file in Fs.EnumerateFilesSorted(dir))
            manifest.Files[Fs.RelativeUnix(dir, file)] = Fs.Sha256File(file);
        Json.Write(SealFile(record.RunId), manifest);
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
        return manifest;
    }

    /// <summary>Re-hashes sealed evidence; returns a list of problems (empty = intact).</summary>
    public List<string> Verify(string runId)
    {
        var problems = new List<string>();
        if (!IsSealed(runId)) { problems.Add("Run is not sealed."); return problems; }
        var dir = RunDir(runId);
        var manifest = Json.Read<SealManifest>(SealFile(runId));
        foreach (var (rel, hash) in manifest.Files)
        {
            var path = Path.Combine(dir, rel);
            if (!File.Exists(path)) problems.Add($"Missing: {rel}");
            else if (!Fs.Sha256File(path).Equals(hash, StringComparison.OrdinalIgnoreCase)) problems.Add($"Modified: {rel}");
        }
        foreach (var file in Fs.EnumerateFilesSorted(dir))
        {
            var rel = Fs.RelativeUnix(dir, file);
            if (rel != "SEALED.json" && !manifest.Files.ContainsKey(rel)) problems.Add($"Added after sealing: {rel}");
        }
        return problems;
    }
}
