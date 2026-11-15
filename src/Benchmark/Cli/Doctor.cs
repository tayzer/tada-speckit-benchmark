using Benchmark.Claude;
using Benchmark.Config;
using Benchmark.Runs;
using Benchmark.Telemetry;
using Benchmark.Util;
using Benchmark.Workspace;

namespace Benchmark.Cli;

public static class Doctor
{
    public static async Task<int> RunAsync(ExperimentConfig c, RunStore store)
    {
        var failures = 0;
        void Report(bool ok, string what, string detail, bool warnOnly = false)
        {
            var tag = ok ? "OK  " : warnOnly ? "WARN" : "FAIL";
            if (!ok && !warnOnly) failures++;
            Console.WriteLine($"[{tag}] {what}: {detail}");
        }

        var errors = ConfigValidator.Validate(c);
        Report(errors.Count == 0, "configuration", errors.Count == 0 ? "valid" : string.Join("; ", errors));

        var cwd = c.HarnessRoot;
        var dotnet = await EnvironmentProbe.VersionAsync("dotnet", cwd, "--version");
        Report(dotnet?.StartsWith("10.", StringComparison.Ordinal) == true, ".NET SDK", dotnet ?? "missing");
        var git = await EnvironmentProbe.VersionAsync("git", cwd, "--version");
        Report(git is not null, "git", git ?? "missing");
        var claude = await EnvironmentProbe.VersionAsync(c.Agent.ClaudeExecutable, cwd, "--version");
        Report(claude?.StartsWith(c.Agent.CliVersion, StringComparison.Ordinal) == true, "Claude Code CLI", $"{claude ?? "missing"} (pinned {c.Agent.CliVersion})");

        foreach (var w in c.Workflows)
        {
            foreach (var tool in w.RequiredTools)
            {
                var found = ProcessRunner.ResolveOnPath(tool);
                Report(found is not null, $"{w.Id} requires '{tool}'", found ?? "NOT FOUND on PATH");
            }
            if (w.Kind == WorkflowKind.Tada)
            {
                var p = w.PluginPath is null ? null : c.ResolveHarnessPath(w.PluginPath);
                if (p is null || !Directory.Exists(p)) Report(false, "TADA plugin", $"not built/available at {p}. See scripts/Get-TadaRelease.ps1.");
                else
                {
                    var hash = Fs.Sha256Directory(p);
                    Report(hash.Equals(w.PluginSha256, StringComparison.OrdinalIgnoreCase), "TADA plugin hash", $"{hash} (pinned {w.PluginSha256 ?? "unset"})");
                    Report(File.Exists(Path.Combine(p, ".claude-plugin", "plugin.json")), "TADA Claude manifest", Path.Combine(p, ".claude-plugin", "plugin.json"));
                }
            }
        }
        foreach (var tool in c.Application.RequiredTools)
        {
            var found = ProcessRunner.ResolveOnPath(tool);
            Report(found is not null, $"application requires '{tool}'", found ?? "NOT FOUND");
        }
        var localDb = await EnvironmentProbe.VersionAsync("sqllocaldb", cwd, "info");
        Report(localDb is not null, "SQL Server LocalDB (hidden suite: relational concurrency tests)", localDb ?? "missing — concurrency tests will be INDETERMINATE", warnOnly: true);

        Report(!Fs.IsUnder(c.DataRoot, c.HarnessRoot), "data root outside harness repo", c.DataRoot);
        Report(!c.Agent.SettingSources.Contains("user"), "user-level Claude settings excluded", $"--setting-sources {c.Agent.SettingSources} (user plugins/hooks/skills/MCP stay out of both arms)", warnOnly: true);
        var userSettings = Path.Combine(ClaudeLaunch.ClaudeConfigDir(), "settings.json");
        if (File.Exists(userSettings) && c.Agent.SettingSources.Contains("user"))
            Report(false, "user settings", "user-level settings (plugins, hooks, model overrides) would load into both workflows", warnOnly: true);
        Report(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")), "authentication source", "ANTHROPIC_API_KEY " + (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")) ? "not set (subscription/OAuth login used)" : "SET — API billing; keep identical for both arms"), warnOnly: true);
        Report(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLAUDE_CODE_EFFORT_LEVEL")), "CLAUDE_CODE_EFFORT_LEVEL", "unset in harness shell (it would override --effort; the harness also clears it for agents)", warnOnly: true);

        var suiteHash = Fs.Sha256Directory(c.HiddenSuitePath);
        var approved = c.Evaluation.Status == "approved" && suiteHash.Equals(c.Evaluation.ApprovedSuiteSha256, StringComparison.OrdinalIgnoreCase);
        Report(approved, "hidden acceptance suite approved", $"status={c.Evaluation.Status}, sha256={suiteHash}", warnOnly: true);
        var decisions = Human.DecisionRegister.Load(c.ResolveExperimentPath(c.DecisionsFile));
        var undecided = decisions.Decisions.Where(d => d.Status != "approved").Select(d => d.Id).ToList();
        Report(undecided.Count == 0, "business decisions approved", undecided.Count == 0 ? "all approved" : "awaiting owner: " + string.Join(", ", undecided), warnOnly: true);

        try
        {
            var b = BasePreparer.Load(store, c);
            Report(b.BuildSucceeded, "frozen base", $"{b.BaseCommit} (known issues: {b.KnownIssues.Count})");
        }
        catch (BenchmarkException ex) { Report(false, "frozen base", ex.Message, warnOnly: true); }

        Console.WriteLine(failures == 0 ? "doctor: no blocking problems." : $"doctor: {failures} blocking problem(s).");
        return failures == 0 ? 0 : 1;
    }
}

/// <summary>
/// Optional low-cost check that Claude Code actually loads the intended workflow components in the run's workspace.
/// Uses the run's exact flags except the model/effort (overridden to a small model: plugin/skill discovery is model-independent)
/// and --no-session-persistence, so no transcript or memory is created in the workspace's project directory.
/// </summary>
public static class ActivationProbe
{
    public const string ProbeModel = "claude-haiku-5-5";

    public static async Task<bool> RunAsync(ExperimentConfig c, RunStore store, RunRecord run, Action<string> log)
    {
        store.EnsureWritable(run.RunId);
        if (run.Invocations.Count > 0) throw new BenchmarkException("Activation must be verified before the session starts.");
        var w = c.Workflow(run.WorkflowId);
        var args = new List<string>();
        var common = run.Agent.CommonArgs.ToList();
        var mi = common.IndexOf("--model"); if (mi >= 0) common[mi + 1] = ProbeModel;
        var ei = common.IndexOf("--effort"); if (ei >= 0) common[ei + 1] = "low";
        args.AddRange(common);
        args.AddRange(run.Agent.WorkflowArgs);
        args.AddRange(["-p", "--output-format", "stream-json", "--verbose", "--no-session-persistence", "--permission-prompts", "none", "--tools", ""]);
        ClaudeLaunch.AssertNoForbidden(args);
        var resolved = ProcessRunner.ResolveOnPath(c.Agent.ClaudeExecutable) ?? throw new BenchmarkException("claude not found");
        var (file, prefix) = ProcessRunner.LaunchTarget(resolved);
        log($"Activation probe ({ProbeModel}, tools disabled) in {run.WorkspacePath} ...");
        var outPath = store.EvidencePath(run.RunId, "activation-probe.stdout.jsonl");
        var r = await ProcessRunner.RunAsync(new ProcessSpec(file, [.. prefix, .. args], run.WorkspacePath)
        {
            Environment = ClaudeLaunch.AgentEnvironment(c.Agent, WorkspaceManager.NugetDir(store, run.RunId)),
            Timeout = TimeSpan.FromMinutes(5),
            StdinText = "Reply with the single word OK.",
            StdoutPath = outPath,
        });
        var parsed = ClaudeEventParser.ParseStreamJson(File.Exists(outPath) ? File.ReadLines(outPath) : []);
        var init = parsed.Inits.FirstOrDefault();
        if (init is null) { log($"Probe produced no init event (exit {r.ExitCode}). {r.Stderr}"); return false; }
        var findings = WorkflowSetup.DynamicChecks(w, init);
        Redactor.RedactFile(outPath, outPath, c.Evidence.RedactSecrets);
        store.WriteEvidence(run.RunId, "activation-probe.json", Json.Serialize(new
        {
            probeModel = ProbeModel,
            claudeCodeVersion = init.ClaudeCodeVersion,
            plugins = init.Plugins,
            skills = init.Skills,
            slashCommands = init.SlashCommands,
            mcpServers = init.McpServers,
            findings,
            cliCostUsd = parsed.Results.LastOrDefault()?.TotalCostUsd,
        }));
        foreach (var f in findings) log("  " + f);
        log(findings.Count == 0 ? "Activation verified: expected components present, no forbidden components." : "Activation probe FAILED.");
        if (findings.Count > 0) { run.Deviations.Add("Activation probe failed: " + string.Join("; ", findings)); store.Save(run); }
        return findings.Count == 0;
    }
}
