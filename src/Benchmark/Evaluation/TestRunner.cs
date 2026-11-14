using System.Globalization;
using System.Xml.Linq;
using Benchmark.Util;

namespace Benchmark.Evaluation;

public enum TestOutcome { Pass, Fail, Indeterminate, Skipped }

public enum FailureCause
{
    None,
    /// <summary>The implementation violated an expected behaviour.</summary>
    Behaviour,
    /// <summary>The implementation under test does not build.</summary>
    ImplementationBuild,
    /// <summary>The evaluator could not run (missing LocalDB, timeout, crashed host, compile error in the hidden suite).</summary>
    Infrastructure,
    /// <summary>A hidden test could not reach the fault-injection seam it needs (declared INDETERMINATE by the test).</summary>
    SeamUnavailable,
    /// <summary>Not evaluated because an owner decision is still pending.</summary>
    PendingDecision,
}

public sealed class TestCaseResult
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public TestOutcome Outcome { get; set; }
    public FailureCause Cause { get; set; }
    public double DurationSeconds { get; set; }
    public string? Message { get; set; }
    public string? RawOutcome { get; set; }
}

public sealed class TestProjectResult
{
    public string Project { get; set; } = "";
    public int? ExitCode { get; set; }
    public double DurationSeconds { get; set; }
    public bool TimedOut { get; set; }
    public string? InfrastructureError { get; set; }
    public string? TrxFile { get; set; }
    public List<TestCaseResult> Tests { get; set; } = [];
    public int Passed => Tests.Count(t => t.Outcome == TestOutcome.Pass);
    public int Failed => Tests.Count(t => t.Outcome == TestOutcome.Fail);
    public int Indeterminate => Tests.Count(t => t.Outcome == TestOutcome.Indeterminate);
    public int Skipped => Tests.Count(t => t.Outcome == TestOutcome.Skipped);
}

public static class TestRunner
{
    public const string IndeterminatePrefix = "INDETERMINATE:";

    public static async Task<TestProjectResult> RunAsync(string repoDir, string projectRelative, string resultsDir,
        IReadOnlyDictionary<string, string?> env, TimeSpan timeout, bool noBuild, string? filter = null)
    {
        Directory.CreateDirectory(resultsDir);
        var safeName = Path.GetFileNameWithoutExtension(projectRelative);
        var trxName = $"{safeName}.trx";
        var args = new List<string> { "test", projectRelative, "--logger", $"trx;LogFileName={trxName}", "--results-directory", resultsDir };
        if (noBuild) args.Add("--no-build");
        if (filter is not null) args.AddRange(["--filter", filter]);
        var result = await ProcessRunner.RunAsync(new ProcessSpec("dotnet", args, repoDir)
        {
            Environment = env,
            Timeout = timeout,
            StdoutPath = Path.Combine(resultsDir, $"{safeName}.stdout.txt"),
            StderrPath = Path.Combine(resultsDir, $"{safeName}.stderr.txt"),
        });
        var project = new TestProjectResult
        {
            Project = projectRelative.Replace('\\', '/'),
            ExitCode = result.ExitCode,
            DurationSeconds = result.DurationSeconds,
            TimedOut = result.TimedOut,
        };
        var trx = Path.Combine(resultsDir, trxName);
        if (File.Exists(trx))
        {
            project.TrxFile = trx;
            project.Tests = TrxParser.Parse(trx);
        }
        if (result.TimedOut) project.InfrastructureError = $"Timed out after {timeout.TotalMinutes:0} minutes.";
        else if (result.LaunchError is not null) project.InfrastructureError = result.LaunchError;
        else if (project.Tests.Count == 0)
        {
            var output = result.Stdout + result.Stderr;
            project.InfrastructureError = output.Contains("error CS", StringComparison.Ordinal) || output.Contains("Build FAILED", StringComparison.OrdinalIgnoreCase)
                ? "Build failed: " + FirstLines(output, "error", 8)
                : output.Contains("No test is available", StringComparison.OrdinalIgnoreCase)
                    ? "No tests discovered by the VSTest adapter (pre-existing project configuration)."
                    : $"No TRX results produced (exit {result.ExitCode}). " + FirstLines(output, "error", 5);
        }
        return project;
    }

    public static string FirstLines(string output, string containing, int max) =>
        string.Join(" | ", output.Split('\n').Where(l => l.Contains(containing, StringComparison.OrdinalIgnoreCase)).Select(l => l.Trim()).Distinct().Take(max));
}

public static class TrxParser
{
    private static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    public static List<TestCaseResult> Parse(string trxPath) => Parse(XDocument.Load(trxPath));

    public static List<TestCaseResult> Parse(XDocument doc)
    {
        var definitions = doc.Descendants(Ns + "UnitTest").ToDictionary(
            u => (string?)u.Attribute("id") ?? "",
            u => u.Element(Ns + "TestMethod") is { } m ? $"{(string?)m.Attribute("className")}.{(string?)m.Attribute("name")}" : (string?)u.Attribute("name") ?? "");
        var results = new List<TestCaseResult>();
        foreach (var r in doc.Descendants(Ns + "UnitTestResult"))
        {
            var testId = (string?)r.Attribute("testId") ?? "";
            var name = (string?)r.Attribute("testName") ?? "";
            var raw = (string?)r.Attribute("outcome") ?? "";
            var message = r.Element(Ns + "Output")?.Element(Ns + "ErrorInfo")?.Element(Ns + "Message")?.Value?.Trim();
            var stdout = r.Element(Ns + "Output")?.Element(Ns + "StdOut")?.Value;
            var (outcome, cause) = Map(raw, message);
            results.Add(new TestCaseResult
            {
                Id = definitions.TryGetValue(testId, out var full) && full.Length > 1 ? Stable(full, name) : name,
                Name = name,
                Outcome = outcome,
                Cause = cause,
                DurationSeconds = TimeSpan.TryParse((string?)r.Attribute("duration"), CultureInfo.InvariantCulture, out var d) ? d.TotalSeconds : 0,
                Message = Truncate(message ?? (outcome == TestOutcome.Pass ? null : stdout), 2000),
                RawOutcome = raw,
            });
        }
        return results;
    }

    // Theory rows share a method; keep the display name (which includes arguments) to keep ids unique.
    private static string Stable(string fullMethod, string displayName) =>
        displayName.Contains('(') ? displayName : fullMethod;

    public static (TestOutcome, FailureCause) Map(string rawOutcome, string? message)
    {
        var indeterminate = message?.Contains(TestRunner.IndeterminatePrefix, StringComparison.Ordinal) == true;
        return rawOutcome switch
        {
            "Passed" => (TestOutcome.Pass, FailureCause.None),
            "Failed" when indeterminate => (TestOutcome.Indeterminate, FailureCause.SeamUnavailable),
            "Failed" => (TestOutcome.Fail, FailureCause.Behaviour),
            "NotExecuted" when indeterminate => (TestOutcome.Indeterminate, FailureCause.SeamUnavailable),
            "NotExecuted" when message?.Contains("PENDING-DECISION", StringComparison.Ordinal) == true => (TestOutcome.Indeterminate, FailureCause.PendingDecision),
            "NotExecuted" or "Inconclusive" => (TestOutcome.Skipped, FailureCause.None),
            _ => (TestOutcome.Indeterminate, FailureCause.Infrastructure), // Timeout, Aborted, Error, ...
        };
    }

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max] + "…";
}
