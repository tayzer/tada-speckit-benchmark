using Benchmark.Runs;
using Benchmark.Util;

namespace Benchmark.Workspace;

public static class EnvironmentProbe
{
    public static async Task<string?> VersionAsync(string exe, string workingDirectory, params string[] args)
    {
        var resolved = ProcessRunner.ResolveOnPath(exe);
        if (resolved is null) return null;
        var (file, prefix) = ProcessRunner.LaunchTarget(resolved);
        var r = await ProcessRunner.RunAsync(new ProcessSpec(file, [.. prefix, .. args], workingDirectory) { Timeout = TimeSpan.FromSeconds(60) });
        if (!r.Succeeded) return null;
        var text = (r.Stdout + r.Stderr).Trim();
        return text.Split('\n').FirstOrDefault()?.Trim();
    }

    public static async Task<EnvironmentSnapshot> CaptureAsync(string appDirectory, string settingSources, string nugetPackages)
    {
        return new EnvironmentSnapshot
        {
            Os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            Machine = Environment.MachineName.Length > 0 ? Fs.Sha256Text(Environment.MachineName)[..12] : "",
            DotnetSdk = await VersionAsync("dotnet", appDirectory, "--version") ?? "missing",
            Git = await VersionAsync("git", appDirectory, "--version") ?? "missing",
            Uv = await VersionAsync("uv", appDirectory, "--version"),
            PowerShell7 = await VersionAsync("pwsh", appDirectory, "-NoProfile", "-Command", "$PSVersionTable.PSVersion.ToString()"),
            LocalDb = await VersionAsync("sqllocaldb", appDirectory, "info") is { } l ? l : null,
            AnthropicApiKeyPresent = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")),
            UserSettingsExcluded = !settingSources.Split(',').Select(s => s.Trim()).Contains("user"),
            NugetPackagesPath = nugetPackages,
        };
    }
}
