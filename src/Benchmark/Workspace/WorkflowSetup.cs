using System.Text.Json.Nodes;
using Benchmark.Config;
using Benchmark.Runs;
using Benchmark.Telemetry;
using Benchmark.Util;

namespace Benchmark.Workspace;

/// <summary>Installs/activates one workflow into an isolated workspace using its documented mechanism.</summary>
public static class WorkflowSetup
{
    public static async Task<(WorkflowSetupRecord Record, string? InstalledPluginPath)> ApplyAsync(
        ExperimentConfig c, WorkflowConfig w, RunStore store, string runId, string workspace, IReadOnlyDictionary<string, string?> env, Action<string> log)
    {
        var record = new WorkflowSetupRecord { WorkflowId = w.Id, Kind = w.Kind, Version = w.Version };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? pluginPath = null;
        switch (w.Kind)
        {
            case WorkflowKind.Speckit:
                {
                    var resolved = ProcessRunner.ResolveOnPath(w.SetupCommand[0]) ?? throw new BenchmarkException($"Setup executable '{w.SetupCommand[0]}' not found on PATH.");
                    var (file, prefix) = ProcessRunner.LaunchTarget(resolved);
                    log($"Spec Kit setup: {string.Join(' ', w.SetupCommand)}");
                    var r = await ProcessRunner.RunAsync(new ProcessSpec(file, [.. prefix, .. w.SetupCommand.Skip(1)], workspace)
                    {
                        Environment = env,
                        Timeout = TimeSpan.FromMinutes(15),
                        StdoutPath = store.EvidencePath(runId, "setup", "workflow-setup.stdout.txt"),
                        StderrPath = store.EvidencePath(runId, "setup", "workflow-setup.stderr.txt"),
                    });
                    record.Steps.Add($"{string.Join(' ', w.SetupCommand)} -> exit {r.ExitCode} in {r.DurationSeconds:0.0}s");
                    if (!r.Succeeded) throw new BenchmarkException($"Spec Kit setup failed (exit {r.ExitCode}). See setup/workflow-setup.stderr.txt. {r.LaunchError}");
                    foreach (var f in new[] { ".specify/init-options.json", ".specify/integration.json", ".specify/integrations/claude.manifest.json" })
                    {
                        var p = Path.Combine(workspace, f);
                        if (File.Exists(p)) record.VersionRecords[f] = File.ReadAllText(p).Trim();
                    }
                    break;
                }
            case WorkflowKind.Tada:
                {
                    var source = c.ResolveHarnessPath(w.PluginPath!);
                    if (!Directory.Exists(source)) throw new BenchmarkException($"TADA plugin directory not found: {source}. Build it from the pinned commit (scripts/Build-TadaPlugin.ps1) and set pluginPath.");
                    var hash = Fs.Sha256Directory(source);
                    record.PluginSha256 = hash;
                    if (!hash.Equals(w.PluginSha256, StringComparison.OrdinalIgnoreCase))
                        throw new BenchmarkException($"TADA plugin hash {hash} does not match pinned pluginSha256 {w.PluginSha256}.");
                    var manifest = Path.Combine(source, ".claude-plugin", "plugin.json");
                    if (!File.Exists(manifest)) throw new BenchmarkException("TADA plugin has no .claude-plugin/plugin.json; it cannot be activated in Claude Code.");
                    var manifestJson = JsonNode.Parse(File.ReadAllText(manifest))!;
                    record.VersionRecords[".claude-plugin/plugin.json"] = manifestJson.ToJsonString();
                    if (manifestJson["name"]?.ToString() != "tada") throw new BenchmarkException("Plugin manifest name is not 'tada'.");
                    // Shared, read-only installed copy outside every workspace (activation is per-session via --plugin-dir).
                    pluginPath = Path.Combine(store.PluginsRoot, $"tada-{hash[..12]}", "tada");
                    if (!Directory.Exists(pluginPath))
                    {
                        Fs.CopyDirectory(source, pluginPath);
                        if (!Fs.Sha256Directory(pluginPath).Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new BenchmarkException("Installed plugin copy hash mismatch.");
                    }
                    record.Steps.Add($"Verified plugin {source} sha256={hash}; installed copy {pluginPath}; activation via --plugin-dir");
                    break;
                }
            case WorkflowKind.None:
                record.Steps.Add("No workflow installed (control arm).");
                break;
        }
        record.SetupSeconds = sw.Elapsed.TotalSeconds;
        return (record, pluginPath);
    }

    /// <summary>File-system activation checks that need no model call.</summary>
    public static List<string> StaticChecks(WorkflowConfig w, string workspace, string? pluginPath)
    {
        var findings = new List<string>();
        foreach (var f in w.ExpectedFiles)
            if (!File.Exists(Path.Combine(workspace, f)) && !Directory.Exists(Path.Combine(workspace, f)))
                findings.Add($"MISSING expected file: {f}");
        var claudeDir = Path.Combine(workspace, ".claude");
        var candidates = new List<string>();
        if (Directory.Exists(claudeDir)) candidates.AddRange(Directory.EnumerateFileSystemEntries(claudeDir, "*", SearchOption.AllDirectories).Select(p => Fs.RelativeUnix(workspace, p)));
        candidates.AddRange(Directory.EnumerateFileSystemEntries(workspace).Select(p => Fs.RelativeUnix(workspace, p)));
        foreach (var marker in w.ForbiddenMarkers)
            foreach (var hit in candidates.Where(p => p.Contains(marker, StringComparison.OrdinalIgnoreCase)))
                findings.Add($"FORBIDDEN marker '{marker}' present in workspace: {hit}");
        if (w.Kind == WorkflowKind.Tada && (pluginPath is null || !Directory.Exists(pluginPath))) findings.Add("MISSING TADA plugin directory.");
        return findings;
    }

    /// <summary>Checks the Claude Code init event for expected and forbidden workflow components.</summary>
    public static List<string> DynamicChecks(WorkflowConfig w, InitRecord init)
    {
        var findings = new List<string>();
        foreach (var p in w.ExpectedMarkers.Plugins.Where(p => !init.Plugins.Contains(p, StringComparer.OrdinalIgnoreCase)))
            findings.Add($"MISSING plugin '{p}' in init event (loaded: {string.Join(", ", init.Plugins)})");
        var available = init.Skills.Concat(init.SlashCommands).ToList();
        foreach (var s in w.ExpectedMarkers.Skills.Where(s => !available.Contains(s, StringComparer.OrdinalIgnoreCase)))
            findings.Add($"MISSING skill '{s}' in init event");
        var everything = init.Plugins.Concat(available).Concat(init.Agents).Concat(init.PluginPaths).ToList();
        foreach (var marker in w.ForbiddenMarkers)
            foreach (var hit in everything.Where(x => x.Contains(marker, StringComparison.OrdinalIgnoreCase)).Distinct())
                findings.Add($"FORBIDDEN component '{hit}' loaded (marker '{marker}')");
        return findings;
    }
}
