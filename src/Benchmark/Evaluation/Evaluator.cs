using Benchmark.Config;
using Benchmark.Runs;
using Benchmark.Util;
using Benchmark.Workspace;

namespace Benchmark.Evaluation;

public sealed class EvaluatorIdentity
{
    public string HarnessCommit { get; set; } = "";
    public string HiddenSuiteSha256 { get; set; } = "";
    public string SuiteStatus { get; set; } = "";
    public string? ApprovedBy { get; set; }
    public bool SuiteMatchesApproval { get; set; }
    public string DotnetSdk { get; set; } = "";
    public string? LocalDb { get; set; }
}

public sealed class EvaluationRecord
{
    public string EvaluationId { get; set; } = "";
    public string Target { get; set; } = ""; // run id or control:<name>
    public string? RunId { get; set; }
    public string? WorkflowId { get; set; }
    public bool Synthetic { get; set; }
    public string EvaluatedCommit { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; }
    public EvaluatorIdentity Evaluator { get; set; } = new();
    public bool ImplementationBuilds { get; set; }
    public string BuildDiagnostics { get; set; } = "";
    public List<TestProjectResult> FinalTreeTests { get; set; } = [];
    public List<TestProjectResult> OriginalTestsAgainstImplementation { get; set; } = [];
    public List<RegressionRecord> Regressions { get; set; } = [];
    public TestProjectResult? Hidden { get; set; }
    public MutationControlResult? RevertedProductionControl { get; set; }
    public List<string> Notes { get; set; } = [];
}

public sealed record RegressionRecord(string Project, string Test, string BaselineOutcome, string NowOutcome, string? Message);

/// <summary>
/// Implementation-agnostic mutation control: restore production code (src/) to the base commit while keeping the
/// workflow's final tests. If the workflow's tests still pass, they do not detect the absence of the fix.
/// </summary>
public sealed class MutationControlResult
{
    public string Description { get; set; } = "";
    public List<string> AgentTestProjects { get; set; } = [];
    public bool Detected { get; set; }
    public string Detail { get; set; } = "";
    public List<TestProjectResult> Results { get; set; } = [];
}

public static class Evaluator
{
    public static EvaluatorIdentity Identity(ExperimentConfig c, string harnessCommit, BaseRecord b)
    {
        var hash = Fs.Sha256Directory(c.HiddenSuitePath);
        return new EvaluatorIdentity
        {
            HarnessCommit = harnessCommit,
            HiddenSuiteSha256 = hash,
            SuiteStatus = c.Evaluation.Status,
            ApprovedBy = c.Evaluation.ApprovedBy,
            SuiteMatchesApproval = c.Evaluation.Status == "approved" && hash.Equals(c.Evaluation.ApprovedSuiteSha256, StringComparison.OrdinalIgnoreCase),
            DotnetSdk = b.Environment.DotnetSdk,
            LocalDb = b.Environment.LocalDb,
        };
    }

    /// <summary>Evaluates a finished run's final commit (or a control patch on the base) in a fresh, isolated clone.</summary>
    public static async Task<EvaluationRecord> EvaluateAsync(ExperimentConfig c, RunStore store, RunRecord? run, string? controlPatch,
        bool allowDraft, bool mutationControl, Action<string> log)
    {
        var baseRecord = BasePreparer.Load(store, c);
        var harnessCommit = await WorkspaceManager.HarnessCommitAsync(c.HarnessRoot);
        var identity = Identity(c, harnessCommit, baseRecord);
        if (!identity.SuiteMatchesApproval && !allowDraft)
            throw new BenchmarkException(c.Evaluation.Status == "approved"
                ? "Hidden suite bytes differ from the approved hash. Re-approve or restore the approved suite."
                : "Hidden suite is a DRAFT (acceptance criteria not approved by the benchmark owner). Pass --allow-draft for non-official evaluation.");
        if (run is not null && run.Status is not (RunStatus.Finished or RunStatus.Sealed)) throw new BenchmarkException("Run must be finished before evaluation.");

        var evalId = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{(run?.RunId ?? "control-" + Path.GetFileNameWithoutExtension(controlPatch ?? "baseline"))}";
        var evalDir = Path.Combine(store.EvaluationsRoot, evalId);
        if (Directory.Exists(evalDir)) throw new BenchmarkException($"Evaluation directory exists: {evalDir}");
        Directory.CreateDirectory(evalDir);
        var app = Path.Combine(evalDir, "app");
        var record = new EvaluationRecord
        {
            EvaluationId = evalId,
            Target = run?.RunId ?? $"control:{Path.GetFileNameWithoutExtension(controlPatch ?? "baseline")}",
            RunId = run?.RunId,
            WorkflowId = run?.WorkflowId,
            StartedAt = DateTimeOffset.UtcNow,
            Evaluator = identity,
        };
        if (!identity.SuiteMatchesApproval) record.Notes.Add("UNOFFICIAL: evaluated with a hidden suite that is not owner-approved.");

        // Fresh clone: the evaluator never runs inside the agent's workspace.
        if (run is not null)
        {
            await Git.RunAsync(evalDir, "clone", "--quiet", "--no-hardlinks", "--branch", run.FinalBranch!, "--single-branch", run.WorkspacePath, "app");
            record.EvaluatedCommit = await Git.HeadAsync(app);
            if (record.EvaluatedCommit != run.FinalCommit) throw new BenchmarkException("Evaluation clone HEAD does not match the run's final commit.");
        }
        else
        {
            await Git.RunAsync(evalDir, "clone", "--quiet", "--no-hardlinks", "--no-checkout", baseRecord.RepositoryPath, "app");
            await Git.RunAsync(app, "checkout", "--quiet", "--detach", baseRecord.BaseCommit);
            if (controlPatch is not null)
            {
                var patchPath = c.ResolveHarnessPath(controlPatch);
                await Git.RunAsync(app, "apply", "--whitespace=nowarn", patchPath);
                record.Notes.Add($"Control patch {controlPatch} sha256={Fs.Sha256File(patchPath)}");
            }
            record.EvaluatedCommit = baseRecord.BaseCommit + (controlPatch is null ? "" : "+control");
        }

        var env = new Dictionary<string, string?>
        {
            ["NUGET_PACKAGES"] = Path.Combine(evalDir, "nuget"),
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1",
        };
        var timeout = TimeSpan.FromMinutes(c.Evaluation.TestTimeoutMinutes);

        log("Building implementation ...");
        var build = await ProcessRunner.RunAsync(new ProcessSpec("dotnet", ["build", c.Application.Solution], app)
        {
            Environment = env, Timeout = timeout,
            StdoutPath = Path.Combine(evalDir, "build.stdout.txt"), StderrPath = Path.Combine(evalDir, "build.stderr.txt"),
        });
        record.ImplementationBuilds = build.Succeeded;
        if (!build.Succeeded) record.BuildDiagnostics = TestRunner.FirstLines(build.Stdout + build.Stderr, "error", 15);

        // (a) The application's tests as they exist in the final tree (may include workflow-added tests).
        var finalProjects = record.ImplementationBuilds ? DiscoverTestProjects(app, c) : [];
        foreach (var project in finalProjects)
        {
            log($"Final-tree tests: {project}");
            record.FinalTreeTests.Add(await TestRunner.RunAsync(app, project, Path.Combine(evalDir, "results", "final-tree"), env, timeout, noBuild: true));
        }

        // (b) Original test suite (from the base commit) against the implementation -> regressions.
        var originalDir = Path.Combine(evalDir, "original-tests");
        await Git.RunAsync(evalDir, "clone", "--quiet", "--no-hardlinks", "app", "original-tests");
        await Git.RunAsync(originalDir, "checkout", "--quiet", "--detach", "HEAD");
        foreach (var testDir in c.Application.TestProjects.Select(p => Path.GetDirectoryName(p)!.Replace('\\', '/')).Distinct())
        {
            // Remove whatever the workflow put there (including new files), then restore the original bytes.
            await Git.RunAsync(originalDir, "rm", "-r", "-q", "--ignore-unmatch", "--", testDir);
            await Git.RunAsync(originalDir, "checkout", baseRecord.BaseCommit, "--", testDir);
        }
        foreach (var project in c.Application.TestProjects)
        {
            log($"Original tests against implementation: {project}");
            var result = await TestRunner.RunAsync(originalDir, project, Path.Combine(evalDir, "results", "original"), env, timeout, noBuild: false);
            record.OriginalTestsAgainstImplementation.Add(result);
        }
        record.Regressions = Regressions(baseRecord, record.OriginalTestsAgainstImplementation);

        // (c) Hidden behavioural suite, copied in only now and only into the evaluation clone.
        var hiddenDest = Path.Combine(app, c.Evaluation.HiddenSuiteDestination);
        Fs.CopyDirectory(c.HiddenSuitePath, hiddenDest);
        var hiddenProject = Directory.GetFiles(hiddenDest, "*.csproj").Single();
        log("Hidden behavioural suite ...");
        if (record.ImplementationBuilds)
        {
            record.Hidden = await TestRunner.RunAsync(app, Path.GetRelativePath(app, hiddenProject), Path.Combine(evalDir, "results", "hidden"), env, timeout, noBuild: false);
            if (record.Hidden.Tests.Count == 0)
            {
                // Hidden suite could not compile/run against this implementation: infrastructure, not behaviour.
                record.Hidden.Tests = HiddenCatalog(c).Select(name => new TestCaseResult
                {
                    Id = name, Name = name, Outcome = TestOutcome.Indeterminate, Cause = FailureCause.Infrastructure,
                    Message = record.Hidden.InfrastructureError,
                }).ToList();
            }
        }
        else
        {
            record.Hidden = new TestProjectResult
            {
                Project = Fs.RelativeUnix(app, hiddenProject),
                InfrastructureError = "Implementation does not build.",
                Tests = HiddenCatalog(c).Select(name => new TestCaseResult
                {
                    Id = name, Name = name, Outcome = TestOutcome.Fail, Cause = FailureCause.ImplementationBuild,
                    Message = "Implementation does not build: " + record.BuildDiagnostics,
                }).ToList(),
            };
        }

        // (d) Optional mutation control on the workflow's own tests.
        if (mutationControl && run is not null && record.ImplementationBuilds)
            record.RevertedProductionControl = await RevertedProductionControlAsync(c, store, run, baseRecord, evalDir, env, timeout, log);

        record.EndedAt = DateTimeOffset.UtcNow;
        Json.Write(Path.Combine(evalDir, "evaluation.json"), record);
        log($"Evaluation written: {Path.Combine(evalDir, "evaluation.json")}");
        return record;
    }

    public static List<string> DiscoverTestProjects(string app, ExperimentConfig c)
    {
        var excluded = c.Application.ExcludedTestProjects.Select(e => e.Path.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var testsRoot = Path.Combine(app, "tests");
        if (!Directory.Exists(testsRoot)) return [];
        return Directory.GetFiles(testsRoot, "*.csproj", SearchOption.AllDirectories)
            .Select(p => Fs.RelativeUnix(app, p))
            .Where(p => !excluded.Contains(p) && !p.Contains(c.Evaluation.HiddenSuiteDestination.TrimStart('/'), StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    public static List<RegressionRecord> Regressions(BaseRecord b, IEnumerable<TestProjectResult> now)
    {
        var list = new List<RegressionRecord>();
        foreach (var project in now)
        {
            var baseline = b.BaselineTests.FirstOrDefault(t => t.Project.Equals(project.Project, StringComparison.OrdinalIgnoreCase));
            if (baseline is null) continue;
            var passedAtBaseline = baseline.Tests.Where(t => t.Outcome == TestOutcome.Pass).ToDictionary(t => t.Id, t => t);
            if (project.Tests.Count == 0 && passedAtBaseline.Count > 0)
            {
                list.Add(new RegressionRecord(project.Project, "*", "pass", "not-run", project.InfrastructureError));
                continue;
            }
            var nowById = project.Tests.ToDictionary(t => t.Id, t => t);
            foreach (var (id, _) in passedAtBaseline)
            {
                if (!nowById.TryGetValue(id, out var current)) list.Add(new RegressionRecord(project.Project, id, "pass", "missing", null));
                else if (current.Outcome != TestOutcome.Pass) list.Add(new RegressionRecord(project.Project, id, "pass", current.Outcome.ToString().ToLowerInvariant(), current.Message));
            }
        }
        return list;
    }

    /// <summary>Hidden test names from the suite's catalog file (used when the suite cannot execute).</summary>
    public static List<string> HiddenCatalog(ExperimentConfig c)
    {
        var catalog = Path.Combine(c.HiddenSuitePath, "catalog.json");
        if (!File.Exists(catalog)) return ["hidden-suite"];
        return Json.Read<HiddenCatalogFile>(catalog).Tests.Select(t => t.Id).ToList();
    }

    private static async Task<MutationControlResult> RevertedProductionControlAsync(ExperimentConfig c, RunStore store, RunRecord run, BaseRecord b,
        string evalDir, Dictionary<string, string?> env, TimeSpan timeout, Action<string> log)
    {
        var result = new MutationControlResult { Description = "src/ restored to the base commit; workflow's final tests kept." };
        var changes = Json.Read<ChangeSummary>(store.EvidencePath(run.RunId, "changes", "changes.json"));
        var touchedTestDirs = changes.Files.Where(f => f.IsTest && f.Status != "D").Select(f => f.Path).ToList();
        var dir = Path.Combine(evalDir, "mutation-revert-src");
        await Git.RunAsync(evalDir, "clone", "--quiet", "--no-hardlinks", "app", "mutation-revert-src");
        await Git.RunAsync(dir, "checkout", b.BaseCommit, "--", "src");
        var projects = DiscoverTestProjects(dir, c)
            .Where(p => touchedTestDirs.Any(t => t.StartsWith(Path.GetDirectoryName(p)!.Replace('\\', '/') + "/", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        result.AgentTestProjects = projects;
        if (projects.Count == 0) { result.Detail = "Workflow added or changed no tests; nothing to evaluate (counts as not detected)."; return result; }
        foreach (var p in projects)
        {
            log($"Mutation control: {p}");
            result.Results.Add(await TestRunner.RunAsync(dir, p, Path.Combine(evalDir, "results", "mutation"), env, timeout, noBuild: false));
        }
        var failing = result.Results.Sum(r => r.Failed);
        var broken = result.Results.Count(r => r.Tests.Count == 0);
        result.Detected = failing > 0 || broken > 0;
        result.Detail = failing > 0 ? $"{failing} workflow test(s) fail without the fix (detected)."
            : broken > 0 ? "Workflow tests do not compile without the new production code (detected by coupling, weaker evidence)."
            : "All workflow tests pass without the fix (NOT detected).";
        return result;
    }
}

public sealed class HiddenCatalogFile
{
    public string Status { get; set; } = "";
    public List<HiddenCatalogEntry> Tests { get; set; } = [];
}

public sealed class HiddenCatalogEntry
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = "";
    public string Expectation { get; set; } = "";
    public bool Critical { get; set; }
    public List<string> Decisions { get; set; } = [];
}
