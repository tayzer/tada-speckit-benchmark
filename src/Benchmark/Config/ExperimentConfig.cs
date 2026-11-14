using System.Text.RegularExpressions;
using Benchmark.Util;

namespace Benchmark.Config;

public sealed class ExperimentConfig
{
    public int SchemaVersion { get; set; } = 1;
    public string ExperimentId { get; set; } = "";
    public string Description { get; set; } = "";
    public ApplicationConfig Application { get; set; } = new();
    public TaskConfig Task { get; set; } = new();
    public AgentConfig Agent { get; set; } = new();
    public List<WorkflowConfig> Workflows { get; set; } = [];
    public EvaluationConfig Evaluation { get; set; } = new();
    public PathsConfig Paths { get; set; } = new();
    public EvidenceConfig Evidence { get; set; } = new();
    public string PricingFile { get; set; } = "pricing/models.json";
    public string DecisionsFile { get; set; } = "decisions.json";

    /// <summary>Directory containing experiment.json (set at load time, not serialised meaningfully).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string ExperimentDirectory { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore] public string HarnessRoot { get; set; } = "";

    public WorkflowConfig Workflow(string id) =>
        Workflows.FirstOrDefault(w => w.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? throw new BenchmarkException($"Unknown workflow '{id}'. Configured: {string.Join(", ", Workflows.Select(w => w.Id))}");

    public string ResolveHarnessPath(string path) => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(HarnessRoot, path));
    public string ResolveExperimentPath(string path) => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(ExperimentDirectory, path));
    public string DataRoot => ResolveHarnessPath(Paths.DataRoot);
    public string PromptPath => ResolveExperimentPath(Task.PromptFile);
    public string PromptText => Fs.NormalizeNewlines(File.ReadAllText(PromptPath)).Trim();
    public string HiddenSuitePath => ResolveHarnessPath(Evaluation.HiddenSuite);

    public static ExperimentConfig Load(string experimentPathOrDir)
    {
        var path = Directory.Exists(experimentPathOrDir) ? Path.Combine(experimentPathOrDir, "experiment.json") : experimentPathOrDir;
        if (!File.Exists(path)) throw new BenchmarkException($"Experiment configuration not found: {path}");
        var config = Json.Read<ExperimentConfig>(path);
        config.ExperimentDirectory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        config.HarnessRoot = FindHarnessRoot(config.ExperimentDirectory);
        return config;
    }

    public static string FindHarnessRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Benchmark.slnx"))) return dir.FullName;
        return start;
    }
}

public sealed class ApplicationConfig
{
    public string Name { get; set; } = "";
    public string Repository { get; set; } = "";
    public string Commit { get; set; } = "";
    /// <summary>Patch files (relative to the experiment dir) applied and committed before any workflow starts.</summary>
    public List<string> CompatibilityPatches { get; set; } = [];
    public string Solution { get; set; } = "";
    public List<string> TestProjects { get; set; } = [];
    public List<ExcludedProject> ExcludedTestProjects { get; set; } = [];
    public List<string> RequiredTools { get; set; } = [];
}

public sealed class ExcludedProject
{
    public string Path { get; set; } = "";
    public string Reason { get; set; } = "";
}

public sealed class TaskConfig
{
    public string PromptFile { get; set; } = "task.md";
    /// <summary>SHA-256 of the newline-normalised, trimmed prompt. Validation fails if the file drifts.</summary>
    public string PromptSha256 { get; set; } = "";
}

public sealed class AgentConfig
{
    public string Host { get; set; } = "claude-code-cli";
    public string ClaudeExecutable { get; set; } = "claude";
    public string CliVersion { get; set; } = "";
    public string Model { get; set; } = "";
    public string Effort { get; set; } = "";
    public string? FallbackModel { get; set; }
    public string PermissionMode { get; set; } = "default";
    public List<string> AllowedTools { get; set; } = [];
    public List<string> DisallowedTools { get; set; } = [];
    /// <summary>Setting sources passed via --setting-sources. Excluding "user" keeps personal plugins/hooks out of both arms.</summary>
    public string SettingSources { get; set; } = "project,local";
    public bool StrictMcpConfig { get; set; } = true;
    /// <summary>Managed mode only: who answers permission prompts. "none" = deny and record (no bypass).</summary>
    public string PermissionPrompts { get; set; } = "none";
    public int ManagedInvocationTimeoutMinutes { get; set; } = 240;
    public Dictionary<string, string> Environment { get; set; } = new();
    /// <summary>Absolute-path roots the agent must never read (deny rules + audit). Harness root and data root are always added.</summary>
    public List<string> ExtraForbiddenRoots { get; set; } = [];
}

public enum WorkflowKind { Speckit, Tada, None }

public sealed class WorkflowConfig
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public WorkflowKind Kind { get; set; }
    /// <summary>Exact pinned version string or git SHA; recorded in every run.</summary>
    public string Version { get; set; } = "";
    /// <summary>
    /// How the identical task text is delivered as the first message. "{prompt}" means verbatim.
    /// A workflow-specific wrapper (e.g. "/speckit-specify {prompt}") is a recorded, owner-approved deviation.
    /// </summary>
    public string InitialMessageTemplate { get; set; } = "{prompt}";
    public List<string> SetupCommand { get; set; } = [];
    /// <summary>TADA: plugin directory (extracted standalone ZIP) passed via --plugin-dir.</summary>
    public string? PluginPath { get; set; }
    public string? PluginSha256 { get; set; }
    public ActivationMarkers ExpectedMarkers { get; set; } = new();
    public List<string> ForbiddenMarkers { get; set; } = [];
    /// <summary>Files the setup must create inside the workspace (static activation check).</summary>
    public List<string> ExpectedFiles { get; set; } = [];
    public List<string> RequiredTools { get; set; } = [];
    public List<string> ArtifactGlobs { get; set; } = [];
}

public sealed class ActivationMarkers
{
    public List<string> Plugins { get; set; } = [];
    public List<string> Skills { get; set; } = [];
}

public sealed class EvaluationConfig
{
    public string HiddenSuite { get; set; } = "evaluator/CheckoutRetry.HiddenTests";
    /// <summary>"draft" until the benchmark owner approves the acceptance criteria.</summary>
    public string Status { get; set; } = "draft";
    public string? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public string? ApprovedSuiteSha256 { get; set; }
    public int TestTimeoutMinutes { get; set; } = 30;
    public string HiddenSuiteDestination { get; set; } = "tests/__HiddenEvaluation";
    public List<string> Controls { get; set; } = [];
}

public sealed class PathsConfig
{
    /// <summary>Evidence + workspaces root, outside the harness repository. Relative paths resolve from the harness root.</summary>
    public string DataRoot { get; set; } = "../BenchmarkData";
}

public sealed class EvidenceConfig
{
    public bool RedactSecrets { get; set; } = true;
    /// <summary>Agent-idle gaps before a human input longer than this are capped when estimating human time.</summary>
    public double HumanIdleCapMinutes { get; set; } = 30;
    public string Retention { get; set; } = "Kept locally until the operator deletes it. Never uploaded by the harness.";
}

public sealed class BenchmarkException(string message) : Exception(message);

public static partial class ConfigValidator
{
    [GeneratedRegex("^[0-9a-f]{40}$")] private static partial Regex Sha1();
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,40}$")] private static partial Regex Id();

    private static readonly string[] Efforts = ["low", "medium", "high", "xhigh", "max"];
    private static readonly string[] Modes = ["default", "manual", "acceptEdits", "plan", "auto", "dontAsk"];
    private static readonly string[] ForbiddenFlags = ["--dangerously-skip-permissions", "--allow-dangerously-skip-permissions", "--bare", "bypassPermissions"];

    public static List<string> Validate(ExperimentConfig c, bool checkFiles = true)
    {
        var errors = new List<string>();
        if (c.SchemaVersion != 1) errors.Add($"Unsupported schemaVersion {c.SchemaVersion}.");
        if (!Id().IsMatch(c.ExperimentId)) errors.Add("experimentId must be lowercase kebab-case.");
        if (string.IsNullOrWhiteSpace(c.Application.Repository)) errors.Add("application.repository is required.");
        if (!Sha1().IsMatch(c.Application.Commit)) errors.Add("application.commit must be a full 40-character lowercase git SHA (branches/tags are not reproducible).");
        if (string.IsNullOrWhiteSpace(c.Application.Solution)) errors.Add("application.solution is required.");
        if (c.Application.TestProjects.Count == 0) errors.Add("application.testProjects must list the original test projects.");

        if (c.Agent.Host != "claude-code-cli") errors.Add("agent.host must be 'claude-code-cli' (the only supported host).");
        if (string.IsNullOrWhiteSpace(c.Agent.Model)) errors.Add("agent.model is required.");
        else if (!c.Agent.Model.StartsWith("claude-", StringComparison.Ordinal))
            errors.Add("agent.model must be a full model identifier (e.g. claude-opus-5-5), not an alias, so both arms resolve identically.");
        if (!Efforts.Contains(c.Agent.Effort)) errors.Add($"agent.effort must be one of {string.Join("|", Efforts)}.");
        if (!Modes.Contains(c.Agent.PermissionMode)) errors.Add($"agent.permissionMode '{c.Agent.PermissionMode}' is not allowed (bypassPermissions is never allowed).");
        if (string.IsNullOrWhiteSpace(c.Agent.CliVersion)) errors.Add("agent.cliVersion must pin the Claude Code CLI version.");
        if (c.Agent.PermissionPrompts is not ("none" or "host")) errors.Add("agent.permissionPrompts must be 'none' or 'host'.");
        foreach (var rule in c.Agent.AllowedTools.Concat(c.Agent.DisallowedTools))
            if (ForbiddenFlags.Any(f => rule.Contains(f, StringComparison.OrdinalIgnoreCase))) errors.Add($"Tool rule '{rule}' contains a forbidden bypass flag.");
        if (c.Agent.SettingSources.Split(',').Any(s => s.Trim() is not ("user" or "project" or "local")))
            errors.Add("agent.settingSources may only contain user, project, local.");

        if (c.Workflows.Count < 2) errors.Add("At least two workflows are required for a comparison.");
        foreach (var dup in c.Workflows.GroupBy(w => w.Id).Where(g => g.Count() > 1)) errors.Add($"Duplicate workflow id '{dup.Key}'.");
        foreach (var w in c.Workflows)
        {
            if (!Id().IsMatch(w.Id)) errors.Add($"Workflow id '{w.Id}' must be lowercase kebab-case.");
            if (string.IsNullOrWhiteSpace(w.Version)) errors.Add($"Workflow '{w.Id}' must pin an exact version or git SHA.");
            if (!w.InitialMessageTemplate.Contains("{prompt}", StringComparison.Ordinal)) errors.Add($"Workflow '{w.Id}' initialMessageTemplate must contain {{prompt}} so the task text is delivered verbatim.");
            if (w.Kind == WorkflowKind.Tada && string.IsNullOrWhiteSpace(w.PluginPath)) errors.Add($"Workflow '{w.Id}' (tada) requires pluginPath.");
            if (w.Kind == WorkflowKind.Speckit && w.SetupCommand.Count == 0) errors.Add($"Workflow '{w.Id}' (speckit) requires setupCommand.");
        }
        foreach (var a in c.Workflows)
            foreach (var b in c.Workflows.Where(b => b != a))
                foreach (var marker in a.ExpectedMarkers.Plugins.Concat(a.ExpectedMarkers.Skills))
                    if (!b.ForbiddenMarkers.Any(f => marker.Contains(f, StringComparison.OrdinalIgnoreCase)))
                        errors.Add($"Workflow '{b.Id}' must list a forbiddenMarker covering '{marker}' from '{a.Id}' (cross-contamination check).");

        if (c.Evaluation.Status is not ("draft" or "approved")) errors.Add("evaluation.status must be 'draft' or 'approved'.");
        if (c.Evaluation.Status == "approved" && (string.IsNullOrWhiteSpace(c.Evaluation.ApprovedBy) || string.IsNullOrWhiteSpace(c.Evaluation.ApprovedSuiteSha256)))
            errors.Add("An approved evaluation requires approvedBy and approvedSuiteSha256.");

        if (checkFiles)
        {
            if (!File.Exists(c.PromptPath)) errors.Add($"Prompt file not found: {c.PromptPath}");
            else
            {
                var actual = Fs.Sha256Text(c.PromptText);
                if (!actual.Equals(c.Task.PromptSha256, StringComparison.OrdinalIgnoreCase))
                    errors.Add($"Prompt hash mismatch: config {c.Task.PromptSha256}, file {actual}. The task text must not drift.");
            }
            foreach (var p in c.Application.CompatibilityPatches)
                if (!File.Exists(c.ResolveExperimentPath(p))) errors.Add($"Compatibility patch not found: {p}");
            if (!Directory.Exists(c.HiddenSuitePath)) errors.Add($"Hidden suite not found: {c.HiddenSuitePath}");
            if (Fs.IsUnder(c.DataRoot, c.HarnessRoot)) errors.Add("paths.dataRoot must be outside the harness repository so agents cannot reach the evaluator via relative paths.");
        }
        return errors;
    }
}
