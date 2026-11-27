using System.Security;
using System.Text.Json.Nodes;
using Benchmark.Config;
using Benchmark.Evaluation;
using Benchmark.Util;

namespace Benchmark.Batch;

internal static class BatchFixtures
{
    private static readonly string[] StateDirectories = ["baselines", "snapshots", "reviews", "approvals"];

    public static void ConfigureRestore(string path, string feed, string? tadaFeed = null)
    {
        string Source(string name, string value) => $"<add key=\"{name}\" value=\"{SecurityElement.Escape(Path.GetFullPath(value))}\" />";
        Fs.WriteAtomic(Path.Combine(path, "NuGet.Config"), $"""
            <configuration><packageSources><clear />{Source("packages", feed)}{(tadaFeed is null ? "" : Source("tada", tadaFeed))}</packageSources></configuration>
            """ + "\n");
    }

    public static async Task PrepareApprovalsAsync(string source, string output, string feed, string plugin, CancellationToken ct)
    {
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new BenchmarkException("Choose a new approval directory.");
        string tadaFeed = Path.Combine(plugin, "feed");
        foreach (string task in InventoryFixture.Tasks)
        {
            ct.ThrowIfCancellationRequested();
            string app = Path.Combine(output, task);
            InventoryFixture.Materialize(source, app, task, "original", tada: true);
            ConfigureRestore(app, feed, tadaFeed);
            foreach (var args in new string[][]
            {
                ["restore", "Acceptance/Inventory.Acceptance.csproj", "--locked-mode"],
                ["tool", "restore", "--configfile", "NuGet.Config"]
            })
            {
                var process = await ProcessRunner.RunAsync(new ProcessSpec("dotnet", args, app) { Timeout = TimeSpan.FromMinutes(3) }, ct);
                if (!process.Succeeded) throw new BenchmarkException($"Fixture setup failed for {task}: {process.Stdout}\n{process.Stderr}");
            }
            var prepared = await LocalTada(app, ["baseline", "prepare"], ct);
            string baseline = Required(prepared, "baseline");
            var review = await LocalTada(app, ["baseline", "review", "--baseline", baseline], ct);
            string reviewId = Required(review["review"], "id");
            Json.Write(Path.Combine(output, task + "-review.json"), review);
            Console.WriteLine($"{task} workspace: {app}");
            Console.WriteLine($"Review {task}-review.json and the materialized oracle before approving review {reviewId}.");
            Console.WriteLine($"From the {task} workspace: dotnet tool run tada baseline approve --review {reviewId} --by \"OWNER NAME\"");
        }
    }

    public static List<FixtureApproval> Register(string directory)
    {
        string receipt = Path.Combine(directory, "approvals.json");
        if (File.Exists(receipt)) return ReadApprovals(directory).Values.ToList();
        var expected = InventoryFixture.Tasks.Select(task =>
        {
            var review = Json.Read<JsonObject>(Path.Combine(directory, task + "-review.json"))["review"];
            string id = Required(review, "id");
            string baseline = Required(review, "baseline");
            string state = Path.Combine(directory, task, ".tada");
            var match = Directory.GetFiles(Path.Combine(state, "approvals"), "*.json")
                .Select(Json.Read<JsonObject>)
                .Where(x => x["review"]?.ToString() == id && x["baseline"]?.ToString() == baseline).ToList();
            if (match.Count != 1) throw new BenchmarkException($"Exactly one owner approval is required for {task}, baseline {baseline}.");
            var approval = match[0];
            string by = Required(approval, "by");
            if (string.IsNullOrWhiteSpace(by) || by.Contains("automated", StringComparison.OrdinalIgnoreCase) || by == "OWNER NAME")
                throw new BenchmarkException("Expected a named human fixture approver.");
            return new FixtureApproval(task, baseline, id, Required(approval, "id"), by, OracleHash(Path.Combine(directory, task)));
        }).ToList();
        Json.Write(receipt, expected);
        return expected;
    }

    public static IReadOnlyDictionary<string, FixtureApproval> ReadApprovals(string directory)
    {
        var receipt = Path.Combine(directory, "approvals.json");
        if (!File.Exists(receipt)) throw new BenchmarkException("No registered owner-approved fixture baselines. Run batch-fixtures and register the owner approvals.");
        var all = Json.Read<List<FixtureApproval>>(receipt);
        if (all.Count != InventoryFixture.Tasks.Length || all.Select(a => a.Task).Distinct().Count() != all.Count)
            throw new BenchmarkException("The approval receipt must name both tasks exactly once.");
        foreach (var approval in all)
        {
            if (!InventoryFixture.Tasks.Contains(approval.Task) || !Guid.TryParseExact(approval.ReviewId, "N", out _) ||
                !Guid.TryParseExact(approval.ApprovalId, "N", out _) || approval.Baseline is not { Length: 71 } ||
                !approval.Baseline.StartsWith("sha256:", StringComparison.Ordinal) || !approval.Baseline[7..].All(Uri.IsHexDigit))
                throw new BenchmarkException("Malformed fixture approval receipt.");
            string app = Path.Combine(directory, approval.Task);
            var review = Json.Read<JsonObject>(Path.Combine(app, ".tada", "reviews", approval.ReviewId + ".json"));
            var record = Json.Read<JsonObject>(Path.Combine(app, ".tada", "approvals", approval.ApprovalId + ".json"));
            if (Required(record, "baseline") != approval.Baseline || Required(record, "review") != approval.ReviewId ||
                Required(record, "id") != approval.ApprovalId || Required(record, "by") != approval.ApprovedBy ||
                Required(review, "id") != approval.ReviewId || Required(review, "baseline") != approval.Baseline ||
                OracleHash(app) != approval.OracleSha256)
                throw new BenchmarkException($"Fixture approval drift for {approval.Task}.");
        }
        return all.ToDictionary(a => a.Task, StringComparer.Ordinal);
    }

    public static void CopyOracleState(string approvedRoot, string task, string app, bool includeApprovals = true)
    {
        foreach (string name in StateDirectories.Where(name => includeApprovals || name != "approvals"))
            Fs.CopyDirectory(Path.Combine(approvedRoot, task, ".tada", name), Path.Combine(app, ".tada", name));
    }

    public static string OracleHash(string app, bool includeHistory = true)
    {
        var paths = new List<string>
        {
            "tada.json", "NuGet.Config", "global.json", "Directory.Build.props",
            ".config/dotnet-tools.json", "src/Inventory/Inventory.csproj", "src/Inventory/packages.lock.json"
        };
        paths.AddRange(new[] { "Acceptance", "features" }.Concat(includeHistory ? StateDirectories.Select(x => ".tada/" + x) : [])
            .SelectMany(root => Fs.EnumerateFilesSorted(Path.Combine(app, root)).Select(file => Fs.RelativeUnix(app, file))));
        return Fs.Sha256Text(string.Join("\n", paths.Order(StringComparer.Ordinal).Select(name =>
            name + ":" + Fs.Sha256File(Path.Combine(app, name)))));
    }

    public static async Task<IEnumerable<TestCaseResult>> RunChecksAsync(BatchWorker worker, string app, string logs, string task, CancellationToken ct)
    {
        var checks = new List<TestCaseResult>();
        foreach (var check in InventoryFixture.Checks(task))
        {
            ct.ThrowIfCancellationRequested();
            var result = new TestCaseResult { Id = check.Id, Name = check.Id };
            try
            {
                await worker.WithApiAsync(app, Path.Combine(app, InventoryFixture.Assembly), Path.Combine(logs, check.Id), ct,
                    async (http, token) => { await check.Run(http, token); return true; });
                result.Outcome = TestOutcome.Pass;
            }
            catch (FixtureAssertion error)
            {
                result.Outcome = TestOutcome.Fail;
                result.Cause = FailureCause.Behaviour;
                result.Message = error.Message;
            }
            catch (Exception error) when (!ct.IsCancellationRequested && error is IOException or HttpRequestException or OperationCanceledException)
            {
                result.Outcome = TestOutcome.Indeterminate;
                result.Cause = FailureCause.Infrastructure;
                result.Message = error.Message;
            }
            checks.Add(result);
        }
        return checks;
    }

    public static async Task<Dictionary<(string Task, string Variant), string>> ValidateControlsAsync(
        BatchWorker worker, string source, string workerRoot, string logs, CancellationToken ct)
    {
        var apps = new Dictionary<(string, string), string>();
        foreach (string task in InventoryFixture.Tasks)
        foreach (string variant in InventoryFixture.Variants)
        {
            string app = Path.Combine(workerRoot, "control-" + task + "-" + variant);
            string evidence = Path.Combine(logs, task + "-" + variant);
            await worker.PrepareWorkspaceAsync(app, Path.Combine(evidence, "workspace-sandbox"), ct);
            try
            {
                InventoryFixture.Materialize(source, app, task, variant);
                var build = await worker.BuildAsync(app, InventoryFixture.TestProject, Path.Combine(evidence, "fixture"), ct);
                if (!build.Succeeded) throw new BenchmarkException($"Fixture {task}/{variant} did not build. See fixture controls.");
                if (variant == "reference")
                {
                    var baseline = await BatchScoring.RunTestsAsync(worker, app, Path.Combine(app, InventoryFixture.Assembly),
                        Path.Combine(evidence, "baseline-tests"), ct);
                    if (baseline.Tests.Count == 0 || baseline.Tests.Any(test => test.Outcome != TestOutcome.Pass))
                        throw new BenchmarkException($"Original test scaffold failed against the {task} reference. See {evidence}.");
                }
                var outcomes = (await RunChecksAsync(worker, app, evidence, task, ct)).ToList();
                Json.Write(Path.Combine(evidence, "outcomes.json"), outcomes);
                var failures = outcomes.Where(x => x.Outcome == TestOutcome.Fail).Select(x => x.Id).Order(StringComparer.Ordinal);
                var expected = InventoryFixture.ExpectedControlFailures(task, variant).Order(StringComparer.Ordinal);
                if (outcomes.Any(x => x.Outcome == TestOutcome.Indeterminate) || !failures.SequenceEqual(expected))
                    throw new BenchmarkException($"Fixture control {task}/{variant} did not produce the expected assertion outcomes. See {evidence}.");
                apps.Add((task, variant), app);
            }
            finally { await worker.AccessAsync(app, false); }
        }
        return apps;
    }

    internal static async Task<JsonObject> LocalTada(string app, string[] args, CancellationToken ct)
    {
        var process = await ProcessRunner.RunAsync(new ProcessSpec("dotnet", ["tool", "run", "tada", .. args, "--json"], app)
        {
            Timeout = TimeSpan.FromMinutes(2)
        }, ct);
        if (!process.Succeeded || Json.TryParseNode(process.Stdout.Trim()) is not JsonObject json)
            throw new BenchmarkException($"Fixture TADA command failed: {process.Stdout}\n{process.Stderr}");
        return json;
    }

    public static string Required(JsonNode? node, string key) =>
        node?[key]?.ToString() is { Length: > 0 } value ? value : throw new BenchmarkException($"Required JSON field '{key}' missing.");
}
