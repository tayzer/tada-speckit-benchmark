using Benchmark.Claude;
using Benchmark.Config;
using Benchmark.Runs;
using Benchmark.Util;

namespace Benchmark.Workspace;

public static class WorkspaceManager
{
    public static string WorkspaceRunRoot(RunStore store, string runId) => Path.Combine(store.WorkspacesRoot, runId);
    public static string AppDir(RunStore store, string runId) => Path.Combine(WorkspaceRunRoot(store, runId), "app");
    public static string NugetDir(RunStore store, string runId) => Path.Combine(WorkspaceRunRoot(store, runId), "nuget");

    /// <summary>
    /// Absolute roots the agent must not touch: the harness repository (holds the hidden evaluator), evidence
    /// stores, the frozen base and every *other* workspace. Plain tokens catch references to the hidden suite.
    /// </summary>
    public static List<string> ForbiddenRoots(ExperimentConfig c, RunStore store, string runId)
    {
        var roots = new List<string> { c.HarnessRoot, store.RunsRoot, store.EvaluationsRoot, store.BaseRoot, store.ReportsRoot };
        if (Directory.Exists(store.WorkspacesRoot))
            roots.AddRange(Directory.GetDirectories(store.WorkspacesRoot).Where(d => !Path.GetFileName(d).Equals(runId, StringComparison.OrdinalIgnoreCase)));
        roots.AddRange(c.Agent.ExtraForbiddenRoots.Select(c.ResolveHarnessPath));
        roots.Add(c.HiddenSuitePath);
        roots.Add(Path.GetFileName(c.HiddenSuitePath)); // token
        roots.Add(Path.GetFileName(c.Evaluation.HiddenSuiteDestination)); // token
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The pinned parent run for a continuation experiment (null otherwise), validated.</summary>
    public static RunRecord? ContinuationParent(ExperimentConfig c, RunStore store, string workflowId)
    {
        if (c.Continuation is not { } k) return null;
        if (!k.ParentRuns.TryGetValue(workflowId, out var parentId)) throw new BenchmarkException($"continuation.parentRuns has no parent for '{workflowId}'.");
        var parent = store.Load(parentId);
        if (parent.WorkflowId != workflowId) throw new BenchmarkException($"Parent run {parentId} is a {parent.WorkflowId} run, not {workflowId}.");
        if (parent.ExperimentId != k.ParentExperiment) throw new BenchmarkException($"Parent run {parentId} belongs to experiment {parent.ExperimentId}, not {k.ParentExperiment}.");
        if (!store.IsSealed(parentId) || parent.FinalCommit is null) throw new BenchmarkException($"Parent run {parentId} is not sealed.");
        if (!Directory.Exists(parent.WorkspacePath)) throw new BenchmarkException($"Parent workspace {parent.WorkspacePath} no longer exists; a continuation needs it.");
        return parent;
    }

    public static async Task<RunRecord> PrepareRunAsync(ExperimentConfig c, string workflowId, RunStore store, Action<string> log)
    {
        var w = c.Workflow(workflowId);
        var errors = ConfigValidator.Validate(c);
        if (errors.Count > 0) throw new BenchmarkException("Configuration invalid:\n  " + string.Join("\n  ", errors));
        var baseRecord = BasePreparer.Load(store, c);
        if (!baseRecord.BuildSucceeded) throw new BenchmarkException("Baseline does not build; fix compatibility patches before preparing runs.");
        var parent = ContinuationParent(c, store, w.Id);

        var now = DateTimeOffset.UtcNow;
        var runId = RunStore.NewRunId(w.Id, now);
        var wsRoot = WorkspaceRunRoot(store, runId);
        if (Directory.Exists(wsRoot)) throw new BenchmarkException($"Workspace {wsRoot} already exists.");
        Directory.CreateDirectory(wsRoot);
        var app = AppDir(store, runId);
        var nuget = NugetDir(store, runId);
        var env = ClaudeLaunch.AgentEnvironment(c.Agent, nuget);

        var record = new RunRecord
        {
            RunId = runId,
            ExperimentId = c.ExperimentId,
            WorkflowId = w.Id,
            Status = RunStatus.Prepared,
            CreatedAt = now,
            ApplicationCommit = c.Application.Commit,
            BaseCommit = baseRecord.BaseCommit,
            WorkspacePath = app,
            SessionId = Guid.NewGuid().ToString(),
            PromptSha256 = Fs.Sha256Text(c.PromptText),
            HarnessCommit = await HarnessCommitAsync(c.HarnessRoot),
            ConfigSha256 = Fs.Sha256File(Path.Combine(c.ExperimentDirectory, "experiment.json")),
            ParentRunId = parent?.RunId,
            ParentFinalCommit = parent?.FinalCommit,
        };
        store.Create(record);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        log($"Creating workspace {app}");
        var start = parent?.FinalCommit ?? baseRecord.BaseCommit;
        await Git.RunAsync(wsRoot, "clone", "--quiet", "--no-hardlinks", "--no-checkout", parent?.WorkspacePath ?? baseRecord.RepositoryPath, "app");
        await Git.RunAsync(app, "checkout", "--quiet", "--detach", start);
        await Git.RunAsync(app, "remote", "remove", "origin"); // no path back to the shared base or the parent workspace
        if (await Git.HeadAsync(app) != start) throw new BenchmarkException(parent is null ? "Workspace HEAD does not match the frozen base commit." : $"Workspace HEAD does not match parent run {parent.RunId}'s final commit.");
        if (!await Git.IsCleanAsync(app)) throw new BenchmarkException("Workspace is not clean after checkout.");

        log("Restoring dependencies into the per-run NuGet cache ...");
        var restore = await ProcessRunner.RunAsync(new ProcessSpec("dotnet", ["restore", c.Application.Solution], app)
        {
            Environment = env, Timeout = TimeSpan.FromMinutes(30),
            StdoutPath = store.EvidencePath(runId, "setup", "restore.stdout.txt"),
            StderrPath = store.EvidencePath(runId, "setup", "restore.stderr.txt"),
        });
        if (!restore.Succeeded) throw new BenchmarkException("dotnet restore failed in the workspace; see setup/restore.*.txt");
        var environmentSetupSeconds = sw.Elapsed.TotalSeconds;

        var (setup, pluginPath) = await WorkflowSetup.ApplyAsync(c, w, store, runId, app, env, log, parent?.RunId);
        var added = await Git.RunAsync(app, "status", "--porcelain", "--untracked-files=all");
        setup.FilesAdded = added.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Length > 3 ? l[3..].Trim() : l).ToList();
        await Git.RunAsync(app, "add", "-A");
        await Git.CommitAllAsync(app, parent is null ? $"benchmark: workflow setup {w.Id} {w.Version}" : $"benchmark: continuation of {parent.RunId} ({w.Id} {w.Version})");
        record.SetupCommit = await Git.HeadAsync(app);
        setup.ActivationFindings = WorkflowSetup.StaticChecks(w, app, pluginPath);
        setup.StaticActivationPassed = setup.ActivationFindings.Count == 0;
        setup.Steps.Insert(0, $"Environment setup (clone + restore) {environmentSetupSeconds:0.0}s");
        record.Setup = setup;
        if (!setup.StaticActivationPassed) record.Deviations.Add("Static activation check failed: " + string.Join("; ", setup.ActivationFindings));

        record.ForbiddenRoots = ForbiddenRoots(c, store, runId);
        var settingsPath = store.EvidencePath(runId, "claude-settings.json");
        var (settings, policyHash) = ClaudeLaunch.BuildRunSettings(c.Agent, record.ForbiddenRoots, app);
        Fs.WriteAtomic(settingsPath, settings.ToJsonString(Json.Options));

        var common = ClaudeLaunch.CommonArgs(c.Agent, settingsPath);
        var workflowArgs = ClaudeLaunch.WorkflowArgs(w, pluginPath);
        ClaudeLaunch.AssertNoForbidden(common.Concat(workflowArgs));
        record.Agent = new AgentSettingsSnapshot
        {
            ClaudeExecutable = c.Agent.ClaudeExecutable,
            ClaudeVersionReported = await EnvironmentProbe.VersionAsync(c.Agent.ClaudeExecutable, app, "--version"),
            Model = c.Agent.Model,
            Effort = c.Agent.Effort,
            FallbackModel = c.Agent.FallbackModel,
            PermissionMode = c.Agent.PermissionMode,
            AllowedTools = c.Agent.AllowedTools,
            DisallowedTools = c.Agent.DisallowedTools,
            SettingSources = c.Agent.SettingSources,
            StrictMcpConfig = c.Agent.StrictMcpConfig,
            PermissionPrompts = c.Agent.PermissionPrompts,
            PermissionPolicySha256 = policyHash,
            CommonArgs = common,
            WorkflowArgs = workflowArgs,
            Environment = env.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Key == "NUGET_PACKAGES" ? "<per-run>" : kv.Value!),
            RunSettingsPath = settingsPath,
        };
        record.Environment = await EnvironmentProbe.CaptureAsync(app, c.Agent.SettingSources, nuget);
        if (record.Agent.ClaudeVersionReported is { } v && !v.StartsWith(c.Agent.CliVersion, StringComparison.Ordinal))
            record.Deviations.Add($"Claude Code CLI version '{v}' differs from pinned {c.Agent.CliVersion}.");
        if (record.Environment.AnthropicApiKeyPresent)
            record.Deviations.Add("ANTHROPIC_API_KEY is set: authentication/billing source may differ from a subscription login. Ensure both arms use the same source.");

        record.InitialMessage = w.InitialMessageTemplate.Replace("{prompt}", c.PromptText, StringComparison.Ordinal);
        record.InitialMessageSha256 = Fs.Sha256Text(record.InitialMessage);
        if (w.InitialMessageTemplate != "{prompt}")
            record.Deviations.Add($"Initial message wraps the task in a workflow-specific template '{w.InitialMessageTemplate}' (must be owner-approved).");

        WriteLaunchScript(store, record, c.Agent.ClaudeExecutable);
        store.Save(record);
        log($"Prepared run {runId} (setup commit {record.SetupCommit[..12]}).");
        return record;
    }

    /// <summary>A transparent record of the exact interactive launch (the harness launches it via `benchmark start`).</summary>
    private static void WriteLaunchScript(RunStore store, RunRecord r, string claude)
    {
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        var args = r.Agent.CommonArgs.Concat(r.Agent.WorkflowArgs).Concat(ClaudeLaunch.SessionArgs(r.SessionId, false)).Select(Q);
        var lines = new List<string>
        {
            "# Generated for transparency. Prefer `benchmark start --run " + r.RunId + "`, which records timing and evidence.",
            "$ErrorActionPreference = 'Stop'",
        };
        foreach (var (k, v) in ClaudeLaunch.AgentEnvironment(new AgentConfig(), Path.Combine(Path.GetDirectoryName(r.WorkspacePath)!, "nuget")))
            lines.Add(v is null ? $"Remove-Item Env:{k} -ErrorAction SilentlyContinue" : $"$env:{k} = {Q(v)}");
        lines.Add($"Set-Location {Q(r.WorkspacePath)}");
        lines.Add($"& {Q(claude)} {string.Join(' ', args)} {Q(r.InitialMessage)}");
        store.WriteEvidence(r.RunId, "launch-interactive.ps1", string.Join("\r\n", lines) + "\r\n");
    }

    public static async Task<string> HarnessCommitAsync(string harnessRoot)
    {
        try
        {
            var head = await Git.HeadAsync(harnessRoot);
            var dirty = !await Git.IsCleanAsync(harnessRoot);
            return dirty ? head + "+dirty" : head;
        }
        catch (BenchmarkException) { return "unversioned"; }
    }
}
