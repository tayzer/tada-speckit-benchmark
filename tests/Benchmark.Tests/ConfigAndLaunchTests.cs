using Benchmark.Claude;
using Benchmark.Config;
using Benchmark.Util;
using Benchmark.Workspace;
using Xunit;

namespace Benchmark.Tests;

public sealed class ConfigValidationTests
{
    [Fact]
    public void Shipped_experiment_configuration_is_valid()
    {
        var errors = ConfigValidator.Validate(TestSupport.RealConfig());
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("f21d623", "full 40-character")]
    [InlineData("main", "full 40-character")]
    public void Commit_must_be_a_full_sha(string commit, string expected)
    {
        var c = TestSupport.RealConfig();
        c.Application.Commit = commit;
        Assert.Contains(ConfigValidator.Validate(c), e => e.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Model_alias_bypass_mode_and_bad_effort_are_rejected()
    {
        var c = TestSupport.RealConfig();
        c.Agent.Model = "opus";
        c.Agent.PermissionMode = "bypassPermissions";
        c.Agent.Effort = "extreme";
        var errors = ConfigValidator.Validate(c);
        Assert.Contains(errors, e => e.Contains("full model identifier", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("bypassPermissions", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("agent.effort", StringComparison.Ordinal));
    }

    [Fact]
    public void Template_must_deliver_prompt_verbatim_and_cross_contamination_markers_required()
    {
        var c = TestSupport.RealConfig();
        c.Workflow("speckit").InitialMessageTemplate = "/speckit-specify fix checkout";
        c.Workflow("speckit").ForbiddenMarkers.Clear();
        var errors = ConfigValidator.Validate(c);
        Assert.Contains(errors, e => e.Contains("{prompt}", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("cross-contamination", StringComparison.Ordinal));
    }

    [Fact]
    public void Prompt_drift_is_detected()
    {
        var c = TestSupport.RealConfig();
        c.Task.PromptSha256 = new string('0', 64);
        Assert.Contains(ConfigValidator.Validate(c), e => e.Contains("Prompt hash mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void Data_root_inside_harness_is_rejected()
    {
        var c = TestSupport.RealConfig();
        c.Paths.DataRoot = "./data";
        Assert.Contains(ConfigValidator.Validate(c), e => e.Contains("outside the harness", StringComparison.Ordinal));
    }
}

public sealed class ClaudeLaunchTests
{
    [Fact]
    public void Common_args_pin_model_effort_and_exclude_bypass_flags()
    {
        var c = TestSupport.RealConfig();
        var args = ClaudeLaunch.CommonArgs(c.Agent, @"C:\x\settings.json");
        Assert.Equal(c.Agent.Model, args[args.IndexOf("--model") + 1]);
        Assert.Equal(c.Agent.Effort, args[args.IndexOf("--effort") + 1]);
        Assert.Equal("project,local", args[args.IndexOf("--setting-sources") + 1]);
        Assert.DoesNotContain("--bare", args);
        Assert.DoesNotContain(args, a => a.Contains("dangerously", StringComparison.Ordinal));
        ClaudeLaunch.AssertNoForbidden(args);
        Assert.Throws<BenchmarkException>(() => ClaudeLaunch.AssertNoForbidden(["--bare"]));
        Assert.Throws<BenchmarkException>(() => ClaudeLaunch.AssertNoForbidden(["--permission-mode", "bypassPermissions"]));
    }

    [Fact]
    public void Only_tada_gets_plugin_dir()
    {
        var c = TestSupport.RealConfig();
        Assert.Empty(ClaudeLaunch.WorkflowArgs(c.Workflow("speckit"), @"C:\p\tada"));
        Assert.Equal(["--plugin-dir", @"C:\p\tada"], ClaudeLaunch.WorkflowArgs(c.Workflow("tada"), @"C:\p\tada"));
    }

    [Fact]
    public void Permission_paths_use_claude_posix_form()
    {
        Assert.Equal("//d/Dev/Benchmark/**", ClaudeLaunch.PermissionPath(@"D:\Dev\Benchmark"));
        Assert.Equal("//c/Users/x/y/**", ClaudeLaunch.PermissionPath(@"C:\Users\x\y\"));
    }

    [Fact]
    public void Project_dir_encoding_matches_observed_claude_layout()
    {
        Assert.Equal("D--Dev-Benchmark", ClaudeLaunch.ProjectDirName(@"D:\Dev\Benchmark"));
        Assert.Equal("D--Dev-BenchmarkData-workspaces-20261113-101010-tada-abc123-app", ClaudeLaunch.ProjectDirName(@"D:\Dev\BenchmarkData\workspaces\20261113-101010-tada-abc123\app"));
    }

    [Fact]
    public void Permission_policy_hash_is_equal_across_arms_with_different_paths()
    {
        var c = TestSupport.RealConfig();
        var (s1, h1) = ClaudeLaunch.BuildRunSettings(c.Agent, [@"D:\H", @"D:\Data\runs", @"D:\Data\workspaces\run-a"], @"D:\Data\workspaces\run-b\app");
        var (_, h2) = ClaudeLaunch.BuildRunSettings(c.Agent, [@"D:\H", @"D:\Data\runs", @"D:\Data\workspaces\run-b", @"D:\Data\workspaces\run-c"], @"D:\Data\workspaces\run-a\app");
        Assert.Equal(h1, h2);
        var deny = s1["permissions"]!["deny"]!.AsArray().Select(x => x!.ToString()).ToList();
        Assert.Contains("Read(//d/H/**)", deny);
        Assert.Contains("WebFetch", deny);
        c.Agent.AllowedTools.Add("Bash(curl *)");
        var (_, h3) = ClaudeLaunch.BuildRunSettings(c.Agent, [@"D:\H"], @"D:\Data\workspaces\run-a\app");
        Assert.NotEqual(h1, h3);
    }

    [Fact]
    public void Permission_policy_hash_ignores_sibling_workspaces_when_harness_root_is_a_path_prefix()
    {
        // Real layout: harness D:\Dev\Benchmark is a string prefix of data root D:\Dev\BenchmarkData.
        var c = TestSupport.RealConfig();
        var (_, h1) = ClaudeLaunch.BuildRunSettings(c.Agent,
            [@"D:\Dev\Benchmark", @"D:\Dev\BenchmarkData\runs", @"D:\Dev\BenchmarkData\workspaces\old-1"],
            @"D:\Dev\BenchmarkData\workspaces\speckit-1\app");
        var (_, h2) = ClaudeLaunch.BuildRunSettings(c.Agent,
            [@"D:\Dev\Benchmark", @"D:\Dev\BenchmarkData\runs", @"D:\Dev\BenchmarkData\workspaces\old-1", @"D:\Dev\BenchmarkData\workspaces\speckit-1"],
            @"D:\Dev\BenchmarkData\workspaces\tada-1\app");
        Assert.Equal(h1, h2);
    }

    [Fact]
    public void Agent_environment_clears_effort_override_and_isolates_nuget()
    {
        var env = ClaudeLaunch.AgentEnvironment(new AgentConfig(), @"D:\Data\workspaces\r\nuget");
        Assert.True(env.ContainsKey("CLAUDE_CODE_EFFORT_LEVEL"));
        Assert.Null(env["CLAUDE_CODE_EFFORT_LEVEL"]);
        Assert.Equal(@"D:\Data\workspaces\r\nuget", env["NUGET_PACKAGES"]);
    }
}

public sealed class RedactorTests
{
    [Fact]
    public void Redacts_credentials_but_keeps_json_shape()
    {
        var input = """{"a":"sk-ant-api03-ABCDEFGHIJKLMNOP","h":"Authorization: Bearer abcdefghijklmnopqrstuvwxyz012345","accountUuid":"1234-5678","conn":"Server=x;Password=hunter22;"}""";
        var output = Redactor.Redact(input);
        Assert.DoesNotContain("sk-ant-api03", output, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz012345", output, StringComparison.Ordinal);
        Assert.DoesNotContain("1234-5678", output, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter22", output, StringComparison.Ordinal);
        Assert.NotNull(System.Text.Json.Nodes.JsonNode.Parse(output));
    }

    [Fact]
    public void Leaves_ordinary_code_alone()
    {
        const string code = "var total = order.Total(); // Pass@word1 is the demo password in tests";
        Assert.Equal(code, Redactor.Redact(code));
    }
}

public sealed class ProcessRunnerTests
{
    private static ProcessSpec Pwsh(string script, string dir) =>
        new("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script], dir);

    [Fact]
    public async Task Captures_stdout_and_stderr_separately_with_exit_code()
    {
        var dir = TestSupport.TempDir();
        var r = await ProcessRunner.RunAsync(Pwsh("[Console]::Out.WriteLine('out-line'); [Console]::Error.WriteLine('err-line'); exit 3", dir) with
        {
            StdoutPath = Path.Combine(dir, "o.txt"), StderrPath = Path.Combine(dir, "e.txt"),
        });
        Assert.Equal(3, r.ExitCode);
        Assert.Contains("out-line", File.ReadAllText(Path.Combine(dir, "o.txt")), StringComparison.Ordinal);
        Assert.DoesNotContain("err-line", File.ReadAllText(Path.Combine(dir, "o.txt")), StringComparison.Ordinal);
        Assert.Contains("err-line", File.ReadAllText(Path.Combine(dir, "e.txt")), StringComparison.Ordinal);
        Assert.False(r.Succeeded);
    }

    [Fact]
    public async Task Timeout_kills_process_and_is_reported()
    {
        var r = await ProcessRunner.RunAsync(Pwsh("Start-Sleep -Seconds 30", TestSupport.TempDir()) with { Timeout = TimeSpan.FromSeconds(2) });
        Assert.True(r.TimedOut);
        Assert.False(r.Cancelled);
        Assert.True(r.DurationSeconds < 25);
    }

    [Fact]
    public async Task Cancellation_is_distinguished_from_timeout()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var r = await ProcessRunner.RunAsync(Pwsh("Start-Sleep -Seconds 30", TestSupport.TempDir()), cts.Token);
        Assert.True(r.Cancelled);
        Assert.False(r.TimedOut);
    }

    [Fact]
    public async Task Launch_failure_is_reported_not_thrown()
    {
        var r = await ProcessRunner.RunAsync(new ProcessSpec("definitely-not-a-real-exe-xyz", [], TestSupport.TempDir()));
        Assert.NotNull(r.LaunchError);
        Assert.Null(r.ExitCode);
    }

    [Fact]
    public async Task Stdin_is_delivered()
    {
        var r = await ProcessRunner.RunAsync(Pwsh("$input | ForEach-Object { 'got:' + $_ }", TestSupport.TempDir()) with { StdinText = "hello\n" });
        Assert.Contains("got:hello", r.Stdout, StringComparison.Ordinal);
    }
}

public sealed class GitSnapshotTests
{
    [Fact]
    public async Task Snapshot_captures_untracked_and_modified_files_without_touching_head_or_index()
    {
        var repo = TestSupport.TempDir("bench-git");
        await Git.RunAsync(repo, "init", "-q");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "one\n");
        File.WriteAllText(Path.Combine(repo, ".gitignore"), "bin/\n");
        await Git.RunAsync(repo, "add", "-A");
        await Git.CommitAllAsync(repo, "base");
        var head = await Git.HeadAsync(repo);

        File.WriteAllText(Path.Combine(repo, "a.txt"), "two\n");
        Directory.CreateDirectory(Path.Combine(repo, "tests", "X"));
        File.WriteAllText(Path.Combine(repo, "tests", "X", "NewTests.cs"), "class T {}\n");
        Directory.CreateDirectory(Path.Combine(repo, "bin"));
        File.WriteAllText(Path.Combine(repo, "bin", "junk.dll"), "x");

        var snap = await Git.SnapshotWorkingTreeAsync(repo, head, "final");
        Assert.Equal(head, await Git.HeadAsync(repo));
        Assert.Equal("?? tests/", (await Git.RunAsync(repo, "status", "--porcelain")).Split('\n').Single(l => l.StartsWith("??", StringComparison.Ordinal)).Trim());
        var changes = await Runs.RunFinisher.ChangesAsync(repo, head, snap);
        var listing = string.Join(";", changes.Files.Select(f => $"{f.Status}:{f.Path}:{f.IsTest}"));
        Assert.True(changes.Files.Count == 2, listing);
        Assert.Contains(changes.Files, f => f.Path == "a.txt" && f.Status == "M" && !f.IsTest);
        Assert.Contains(changes.Files, f => f.Path == "tests/X/NewTests.cs" && f.Status == "A" && f.IsTest);
        Assert.DoesNotContain(changes.Files, f => f.Path.StartsWith("bin/", StringComparison.Ordinal));
    }
}

public sealed class ComparisonPinTests
{
    [Fact]
    public void Default_comparison_excludes_runs_made_with_an_older_workflow_pin()
    {
        var c = TestSupport.RealConfig();
        var tada = c.Workflows.Single(w => w.Id == "tada");
        Benchmark.Runs.RunRecord Run(string version, string? hash) => new()
        {
            WorkflowId = "tada",
            Setup = new Benchmark.Runs.WorkflowSetupRecord { WorkflowId = "tada", Version = version, PluginSha256 = hash },
        };

        Assert.True(Benchmark.Cli.SimpleRun.MatchesCurrentPin(c, Run(tada.Version, tada.PluginSha256)));
        Assert.False(Benchmark.Cli.SimpleRun.MatchesCurrentPin(c, Run(
            "42f1429b0458c5c877b3bf5b093d7f8a10b760a3 (plugin 0.1.0-alpha.13, unreleased source build)",
            "b2199946ff44fc810611c466c09a65ede9d4fc3add4e486554e8abf92d6dd50d")));
        Assert.False(Benchmark.Cli.SimpleRun.MatchesCurrentPin(c, Run(tada.Version, "b2199946ff44fc810611c466c09a65ede9d4fc3add4e486554e8abf92d6dd50d")));
    }
}
