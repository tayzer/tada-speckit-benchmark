using System.Diagnostics;
using System.Text;

namespace Benchmark.Util;

public sealed record ProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory)
{
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();
    public TimeSpan? Timeout { get; init; }
    /// <summary>When set, stdout is streamed to this file byte-for-byte as it arrives (raw evidence).</summary>
    public string? StdoutPath { get; init; }
    public string? StderrPath { get; init; }
    /// <summary>Optional per-line observer for live progress (does not alter captured output).</summary>
    public Action<string>? OnStdoutLine { get; init; }
    public string? StdinText { get; init; }
}

public sealed record ProcessResult(
    int? ExitCode,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    bool TimedOut,
    bool Cancelled,
    string Stdout,
    string Stderr,
    string? LaunchError)
{
    public double DurationSeconds => (EndedAt - StartedAt).TotalSeconds;
    public bool Succeeded => ExitCode == 0 && !TimedOut && !Cancelled && LaunchError is null;
}

public static class ProcessRunner
{
    private const int MaxInMemoryChars = 4 * 1024 * 1024;

    public static async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default)
    {
        var psi = CreateStartInfo(spec, redirect: true);
        var started = DateTimeOffset.UtcNow;
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        StreamWriter? stdoutFile = null, stderrFile = null;
        try
        {
            if (spec.StdoutPath is not null) { Directory.CreateDirectory(Path.GetDirectoryName(spec.StdoutPath)!); stdoutFile = new StreamWriter(spec.StdoutPath, false, new UTF8Encoding(false)) { AutoFlush = true }; }
            if (spec.StderrPath is not null) { Directory.CreateDirectory(Path.GetDirectoryName(spec.StderrPath)!); stderrFile = new StreamWriter(spec.StderrPath, false, new UTF8Encoding(false)) { AutoFlush = true }; }

            var stdoutLock = new object();
            var stderrLock = new object();
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (stdoutLock)
                {
                    stdoutFile?.Write(e.Data + "\n");
                    // "\n" (not AppendLine/CRLF) so callers can split on '\n' without stray '\r'.
                    if (stdout.Length < MaxInMemoryChars) stdout.Append(e.Data).Append('\n');
                }
                spec.OnStdoutLine?.Invoke(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (stderrLock)
                {
                    stderrFile?.Write(e.Data + "\n");
                    if (stderr.Length < MaxInMemoryChars) stderr.Append(e.Data).Append('\n');
                }
            };

            try
            {
                if (!process.Start()) return new ProcessResult(null, started, DateTimeOffset.UtcNow, false, false, "", "", "Process.Start returned false");
            }
            catch (Exception ex)
            {
                return new ProcessResult(null, started, DateTimeOffset.UtcNow, false, false, "", "", ex.Message);
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (spec.StdinText is not null) await process.StandardInput.WriteAsync(spec.StdinText);
            process.StandardInput.Close();

            using var timeoutCts = spec.Timeout is { } t ? new CancellationTokenSource(t) : new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            bool timedOut = false, cancelled = false;
            try
            {
                await process.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                timedOut = timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
                cancelled = cancellationToken.IsCancellationRequested;
                TryKill(process);
                try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30)); } catch { /* best effort */ }
            }
            // Drain async readers.
            try { process.WaitForExit(); } catch { /* already exited */ }

            int? exit = null;
            try { exit = process.ExitCode; } catch { /* not available after kill on some platforms */ }
            return new ProcessResult(exit, started, DateTimeOffset.UtcNow, timedOut, cancelled, stdout.ToString(), stderr.ToString(), null);
        }
        finally
        {
            stdoutFile?.Dispose();
            stderrFile?.Dispose();
        }
    }

    /// <summary>Runs a process attached to the current console (interactive Claude Code sessions).</summary>
    public static async Task<ProcessResult> RunInteractiveAsync(ProcessSpec spec, CancellationToken cancellationToken = default)
    {
        var psi = CreateStartInfo(spec, redirect: false);
        var started = DateTimeOffset.UtcNow;
        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start()) return new ProcessResult(null, started, DateTimeOffset.UtcNow, false, false, "", "", "Process.Start returned false");
        }
        catch (Exception ex)
        {
            return new ProcessResult(null, started, DateTimeOffset.UtcNow, false, false, "", "", ex.Message);
        }
        var cancelled = false;
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException) { cancelled = true; TryKill(process); process.WaitForExit(); }
        return new ProcessResult(process.ExitCode, started, DateTimeOffset.UtcNow, false, cancelled, "", "", null);
    }

    public static ProcessStartInfo CreateStartInfo(ProcessSpec spec, bool redirect)
    {
        var psi = new ProcessStartInfo
        {
            FileName = spec.FileName,
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = redirect,
            RedirectStandardError = redirect,
            RedirectStandardInput = redirect,
        };
        if (redirect)
        {
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
        }
        foreach (var a in spec.Arguments) psi.ArgumentList.Add(a);
        foreach (var (k, v) in spec.Environment)
        {
            if (v is null) psi.Environment.Remove(k);
            else psi.Environment[k] = v;
        }
        return psi;
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* best effort */ }
    }

    /// <summary>Resolves an executable on PATH, preferring .exe then .cmd (npm shims) on Windows.</summary>
    public static string? ResolveOnPath(string name)
    {
        if (Path.IsPathRooted(name)) return File.Exists(name) ? name : null;
        var exts = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat", "" } : new[] { "" };
        foreach (var dir in (System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in exts)
            {
                var candidate = Path.Combine(dir.Trim('"'), name.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ? name : name + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    /// <summary>.cmd/.bat shims must be launched through cmd.exe; returns (fileName, prefixArgs).</summary>
    public static (string FileName, List<string> PrefixArgs) LaunchTarget(string resolvedPath)
    {
        var ext = Path.GetExtension(resolvedPath);
        if (ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase))
            return ("cmd.exe", ["/d", "/s", "/c", resolvedPath]);
        return (resolvedPath, []);
    }
}
