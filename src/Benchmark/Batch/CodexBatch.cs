using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Benchmark.Cli;
using Benchmark.Config;
using Benchmark.Evaluation;
using Benchmark.Util;

namespace Benchmark.Batch;

internal static class CodexBatch
{
    private static readonly TimeSpan CodingLimit = TimeSpan.FromMinutes(20);

    public static async Task<int> RunAsync(string command, Args args, Action<string> log, CancellationToken ct)
    {
        string root = ExperimentConfig.FindHarnessRoot(Directory.GetCurrentDirectory());
        string fixture = Path.Combine(root, "fixtures", "codex-sdd");
        if (command == "batch-report")
        {
            log(BatchEvidence.Report(Path.GetFullPath(args.Require("batch"))));
            return 0;
        }
        if (command == "batch-fixtures")
        {
            string output = Path.GetFullPath(args.Require("out"));
            string suite = args.Get("suite") ?? "codex-sdd";
            bool register = args.Flag("register");
            if (suite == HandoffFixture.Suite)
            {
                if (register)
                {
                    HandoffApprovals.Register(output);
                    log(Path.Combine(output, "approvals.json"));
                }
                else
                {
                    await HandoffApprovals.PrepareAsync(fixture, Path.Combine(root, "fixtures", HandoffFixture.Suite), output,
                        Path.GetFullPath(args.Require("feed")), Path.GetFullPath(args.Require("tada-plugin")), ct);
                    log(Path.Combine(output, "OWNER-REVIEW.md"));
                }
            }
            else if (suite != "codex-sdd") throw new BenchmarkException("Unknown benchmark suite.");
            else if (register)
            {
                BatchFixtures.Register(output);
                log(Path.Combine(output, "approvals.json"));
            }
            else await BatchFixtures.PrepareApprovalsAsync(fixture, output, Path.GetFullPath(args.Require("feed")),
                Path.GetFullPath(args.Require("tada-plugin")), ct);
            return 0;
        }
        if (command == "batch-controls")
        {
            string suite = args.Get("suite") ?? "codex-sdd";
            string output = Path.Combine(Path.GetFullPath(args.Require("out")), "controls-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(output);
            string feed = Path.GetFullPath(args.Require("feed"));
            var worker = new BatchWorker(root, output, new() { Dotnet = "dotnet", NugetFeed = feed }, trustedLocal: true);
            if (suite == HandoffFixture.Suite)
            {
                string handoffSource = Path.Combine(root, "fixtures", HandoffFixture.Suite);
                var controls = await HandoffFixture.ValidateControlsAsync(worker, fixture, handoffSource,
                    Path.Combine(output, "fixtures"), Path.Combine(output, "checks"), ct);
                await HandoffFixture.VerifyPromotionAsync(worker, fixture, handoffSource, Path.Combine(output, "promotion"), controls, ct);
                log($"All {controls.Count} handoff fixture controls matched expected outcomes. {output}");
                log("Model-free Git and persisted database promotion passed.");
            }
            else
            {
                if (suite != "codex-sdd") throw new BenchmarkException("Unknown benchmark suite.");
                await BatchFixtures.ValidateControlsAsync(worker, fixture, Path.Combine(output, "fixtures"), Path.Combine(output, "checks"), ct);
                log($"All eight fixture controls matched expected outcomes. {output}");
            }
            return 0;
        }

        string? origin = null;
        bool retryOpenSpec = false;
        HandoffBatchResume? resume = null;
        BatchManifest batch;
        if (command == "batch-resume")
        {
            resume = new HandoffBatchResume(Path.GetFullPath(args.Require("batch")));
            batch = resume.Batch;
        }
        else if (command == "batch-reevaluate")
        {
            origin = Path.GetFullPath(args.Require("batch"));
            retryOpenSpec = args.Flag("retry-openspec");
            var original = BatchEvidence.ReadManifest(origin, requireSeal: true);
            if (original.Suite == HandoffFixture.Suite)
                throw new BenchmarkException("Handoff submissions depend on their own sealed predecessor. Use batch-report to regenerate the handoff evidence.");
            if (original.Synthetic || original.PreflightOnly || original.Cancelled || original.PreflightFailure is not null)
                throw new BenchmarkException("Evaluation recovery requires a completed live batch.");
            batch = original with
            {
                Id = BatchEvidence.NewId(), StartedAt = DateTimeOffset.UtcNow,
                OriginBatchId = original.Id, OriginManifestSha256 = Fs.Sha256File(Path.Combine(origin, "batch.json")),
                Pins = new(original.Pins)
            };
        }
        else
        {
            string suite = args.Require("suite");
            if (suite is not ("codex-sdd" or HandoffFixture.Suite)) throw new BenchmarkException("Unknown benchmark suite.");
            bool synthetic = args.Flag("dry-run");
            bool preflightOnly = args.Flag("preflight-only");
            if (synthetic && preflightOnly) throw new BenchmarkException("Choose either --dry-run or --preflight-only.");
            string model = args.Get("model") ?? (suite == HandoffFixture.Suite ? "gpt-6.1-sol" : BatchWorker.DefaultModel);
            if (args.Flag("model") || string.IsNullOrWhiteSpace(model)) throw new BenchmarkException("--model requires a Codex model ID.");
            batch = BatchEvidence.Create(Integer(args.Get("seed"), 1729, "seed"), Integer(args.Get("repeats"), 1, "repeats"),
                synthetic, preflightOnly, model, suite);
        }
        bool handoff = batch.Suite == HandoffFixture.Suite;
        string? handoffFixture = handoff ? Path.Combine(root, "fixtures", HandoffFixture.Suite) : null;
        string? handoffApprovals = handoff
            ? Path.GetFullPath(args.Get("approvals") ?? Path.Combine(root, ".local", batch.Suite, "owner-review")) : null;
        string configPath = Path.GetFullPath(args.Get("config") ?? Path.Combine(root, ".local", "codex-sdd", "worker.json"));
        string evidence = Path.Combine(Path.GetFullPath(args.Get("out") ?? Path.Combine(root, ".local", batch.Suite, "batches")), batch.Id);
        Directory.CreateDirectory(evidence);
        Json.Write(Path.Combine(evidence, "batch.json"), batch);
        bool codingOutput = true;
        try
        {
            if (batch.Synthetic)
            {
                await ScheduleAsync(evidence, batch, (slot, attempt, token) =>
                    SyntheticAsync(slot, attempt, token), log, ct);
            }
            else
            {
                if (!OperatingSystem.IsWindows()) throw new BenchmarkException("The Codex batch requires the dedicated Windows worker.");
                if (!File.Exists(configPath)) throw new BenchmarkException($"Worker configuration missing at {configPath}. See docs/codex-batch.md.");
                if (handoffApprovals is not null) HandoffApprovals.Read(handoffApprovals);
                string state = Path.Combine(root, ".local", "codex-sdd");
                Directory.CreateDirectory(state);
                using var lease = new FileStream(Path.Combine(state, "live.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                using var isolation = SandboxReadBoundary.Protect(root, evidence);
                var remaining = resume?.RetainCompleted(evidence, log);
                var (work, worker, config, controls, bases, approvals) = await PreflightAsync(root, fixture, configPath, evidence, batch,
                    log, ct, handoffFixture, handoffApprovals);
                Task RunSlot(BatchSlot slot, BatchAttempt attempt, CancellationToken token) =>
                    AttemptAsync(slot, attempt, worker, config, fixture, evidence, batch.Id, controls, bases, approvals, token, handoffFixture);
                if (batch.PreflightOnly) log("Preflight passed. Zero model calls.");
                else if (origin is not null)
                    codingOutput = await ReevaluateAsync(origin, evidence, work, batch, worker, config, controls,
                        retryOpenSpec ? RunSlot : null, log, ct);
                else if (resume is not null)
                {
                    resume.VerifyPins();
                    codingOutput = await ScheduleAsync(evidence, batch with { Slots = remaining! }, RunSlot, log, ct);
                    codingOutput |= batch.Resume!.RetainedSlots.Count > 0;
                }
                else codingOutput = await ScheduleAsync(evidence, batch,
                    RunSlot, log, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { batch.Cancelled = true; }
        catch (Exception error) when (error is BenchmarkException or IOException or UnauthorizedAccessException or JsonException)
        {
            batch.PreflightFailure = error.Message;
            log("Batch preflight blocked: " + error.Message);
        }
        finally
        {
            string manifest = Path.Combine(evidence, "batch.json");
            Json.Write(manifest, batch);
            Fs.WriteAtomic(Path.Combine(evidence, "batch.sha256"), Fs.Sha256File(manifest) + "\n");
            log(BatchEvidence.Report(evidence));
        }
        if (batch.Cancelled) return 130;
        return batch.PreflightFailure is null && codingOutput ? 0 : 2;
    }

    private static int Integer(string? value, int fallback, string name) =>
        value is null ? fallback : int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int integer) ? integer
        : throw new BenchmarkException($"--{name} requires an integer.");

    private static async Task<bool> ReevaluateAsync(string origin, string evidence, string work, BatchManifest batch,
        BatchWorker worker, BatchConfiguration config, Dictionary<(string, string), string> controls,
        Func<BatchSlot, BatchAttempt, CancellationToken, Task>? retryOpenSpec, Action<string> log, CancellationToken ct)
    {
        var original = BatchEvidence.ReadManifest(origin, requireSeal: true);
        if (Fs.Sha256File(Path.Combine(origin, "batch.json")) != batch.OriginManifestSha256 ||
            original.Pins.Any(pin => batch.Pins.GetValueOrDefault(pin.Key) != pin.Value))
            throw new BenchmarkException("Evaluation recovery inputs differ from the original batch pins.");
        bool codingOutput = false;
        foreach (var slot in batch.Slots)
        {
            ct.ThrowIfCancellationRequested();
            string source = BatchEvidence.AttemptPath(origin, slot);
            string target = BatchEvidence.AttemptPath(evidence, slot);
            var attempt = BatchEvidence.ReadAttempt(origin, slot);
            codingOutput |= attempt.HasCodingOutput();
            if (retryOpenSpec is not null && slot.Workflow == "openspec" && attempt.Status == "integrity-failed" &&
                Directory.EnumerateFiles(Path.Combine(source, "coding"), "*-propose.out").Any(file =>
                    File.ReadLines(file).Any(line => line.Contains("The term 'openspec' is not recognized", StringComparison.OrdinalIgnoreCase))))
            {
                log($"Retrying {slot.Id} with the pinned OpenSpec CLI");
                codingOutput |= await ScheduleAsync(evidence, batch with { Slots = [slot] }, (selected, retried, token) =>
                {
                    retried.ReranCoding = true;
                    return retryOpenSpec(selected, retried, token);
                }, log, ct);
                continue;
            }
            if (attempt.Status is not ("build-failed" or "indeterminate") ||
                !Directory.Exists(Path.Combine(source, "evaluation")) ||
                !Directory.EnumerateFiles(Path.Combine(source, "evaluation"), "candidate-*.err")
                    .Any(file => BatchWorker.IsSandboxSetupFailure(File.ReadAllText(file))))
            {
                Fs.CopyDirectory(source, target);
                continue;
            }

            log($"Re-evaluating {slot.Id} from its sealed submission");
            string coding = Path.Combine(target, "coding");
            Fs.CopyDirectory(Path.Combine(source, "coding"), coding);
            foreach (string reviewFile in Directory.EnumerateFiles(source, "review.*"))
                File.Copy(reviewFile, Path.Combine(target, Path.GetFileName(reviewFile)));
            try
            {
                BatchEvidence.Verify(coding);
                attempt.Reevaluated = true;
                attempt.Status = "submitted";
                attempt.Detail = null;
                await BatchScoring.EvaluateAsync(worker, config, attempt, Path.Combine(coding, "submission"),
                    Path.Combine(work, slot.Id, "evaluation"), Path.Combine(target, "evaluation"), controls, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                attempt.Status = "cancelled";
                throw;
            }
            catch (Exception error) when (error is BenchmarkException or IOException or UnauthorizedAccessException or JsonException
                or System.Xml.XmlException or System.Net.Http.HttpRequestException)
            {
                attempt.Status = "indeterminate";
                attempt.Detail = Redactor.Redact(error.Message, Redactor.SecretEnvironmentValues());
            }
            finally
            {
                BatchEvidence.Save(evidence, attempt);
                BatchEvidence.Seal(target);
            }
        }
        return codingOutput;
    }

    internal static async Task<bool> ScheduleAsync(string evidence, BatchManifest batch,
        Func<BatchSlot, BatchAttempt, CancellationToken, Task> execute, Action<string> log, CancellationToken ct)
    {
        bool codingOutput = false;
        foreach (var slot in batch.Slots)
        {
            ct.ThrowIfCancellationRequested();
            string directory = BatchEvidence.AttemptPath(evidence, slot);
            string coding = Path.Combine(directory, "coding");
            Directory.CreateDirectory(coding);
            var attempt = new BatchAttempt { Slot = slot };
            var clock = System.Diagnostics.Stopwatch.StartNew();
            log($"Running {slot.Id}");
            try
            {
                if (batch.Suite == HandoffFixture.Suite && slot.Task == HandoffFixture.Second)
                {
                    var first = BatchEvidence.ReadAttempt(evidence, HandoffPredecessor(slot));
                    if (first.Status != "pass")
                    {
                        attempt.Status = "blocked-by-ticket-1";
                        attempt.Detail = $"Preceding ticket ended with {first.Status}.";
                    }
                    else await execute(slot, attempt, ct);
                }
                else await execute(slot, attempt, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                attempt.Status = "cancelled";
                throw;
            }
            catch (Exception error) when (error is BenchmarkException or IOException or UnauthorizedAccessException or JsonException
                or System.Xml.XmlException or System.Net.Http.HttpRequestException)
            {
                if (attempt.Status is "pending" or "coding") attempt.Status = "blocked";
                else if (attempt.Status == "submitted") attempt.Status = "indeterminate";
                attempt.Detail = Redactor.Redact(error.Message, Redactor.SecretEnvironmentValues());
            }
            finally
            {
                if (!File.Exists(Path.Combine(coding, "seal.json"))) BatchEvidence.Seal(coding);
                if (batch.Suite == HandoffFixture.Suite) attempt.ElapsedSeconds = clock.Elapsed.TotalSeconds;
                BatchEvidence.Save(evidence, attempt);
                BatchEvidence.Seal(directory);
                codingOutput |= attempt.HasCodingOutput();
            }
        }
        return codingOutput;
    }

    internal static BatchSlot HandoffPredecessor(BatchSlot slot) =>
        new($"{HandoffFixture.First}-{slot.Workflow}-{slot.Repeat:00}", HandoffFixture.First, slot.Workflow, slot.Repeat);

    private static async Task<(string Work, BatchWorker Worker, BatchConfiguration Config,
        Dictionary<(string, string), string> Controls, Dictionary<string, (string Path, string Commit)> Bases,
        IReadOnlyDictionary<string, FixtureApproval> Approvals)> PreflightAsync(
        string root, string fixture, string configPath, string evidence, BatchManifest batch, Action<string> log, CancellationToken ct,
        string? handoffFixture = null, string? handoffApprovals = null)
    {
        var config = Json.Read<BatchConfiguration>(configPath);
        if (handoffApprovals is not null) config.Approvals = handoffApprovals;
        foreach (string file in new[] { config.CredentialFile, config.Dotnet, config.Codex, config.Git, config.PowerShell })
            if (!Path.IsPathFullyQualified(file) || !File.Exists(file)) throw new BenchmarkException($"Worker config requires an existing absolute file path: {file}");
        foreach (string directory in new[] { config.WorkerRoot, config.WorkerProfile, config.NugetFeed, config.TadaPlugin,
            config.SpecKitTemplate, config.OpenSpecTemplate, config.Approvals })
            if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory)) throw new BenchmarkException($"Worker config requires an existing absolute directory: {directory}");
        if (Fs.IsUnder(config.WorkerRoot, root) || Fs.IsUnder(root, config.WorkerRoot) ||
            Fs.IsUnder(evidence, config.WorkerRoot) || Fs.IsUnder(config.WorkerRoot, evidence))
            throw new BenchmarkException("Worker workspace root must be separate from the harness and evidence roots.");
        string tadaManifest = Path.Combine(config.TadaPlugin, ".codex-plugin", "plugin.json");
        if (BatchFixtures.Required(Json.Read<JsonObject>(tadaManifest), "version") != batch.Pins["tada"])
            throw new BenchmarkException("Pinned TADA manifest version differs.");
        void CheckArtifact(string path, string expected, string key)
        {
            foreach (string _ in Submission.Files(path)) { }
            string actual = Fs.Sha256Directory(path);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new BenchmarkException($"Pinned {key} artifact hash differs.");
            batch.Pins[key + "-sha256"] = actual;
        }
        CheckArtifact(config.TadaPlugin, config.TadaPluginSha256, "tada");
        CheckArtifact(config.SpecKitTemplate, config.SpecKitTemplateSha256, "speckit");
        CheckArtifact(config.OpenSpecTemplate, config.OpenSpecTemplateSha256, "openspec");
        ValidateSkills(config.SpecKitTemplate, "speckit");
        ValidateSkills(config.OpenSpecTemplate, "openspec");
        var approvals = handoffFixture is not null ? HandoffApprovals.Read(config.Approvals) : BatchFixtures.ReadApprovals(config.Approvals);
        batch.Pins["fixture-sha256"] = Fs.Sha256Directory(fixture);
        if (handoffFixture is not null)
        {
            batch.Pins["handoff-fixture-sha256"] = Fs.Sha256Directory(handoffFixture, includeBuildOutput: true);
            batch.Pins["handoff-evaluator-sha256"] = Fs.Sha256File(Path.Combine(root, "src", "Benchmark", "Batch", "HandoffChecks.cs"));
            batch.Pins["handoff-runner-sha256"] = Fs.Sha256File(typeof(CodexBatch).Assembly.Location);
        }
        batch.Pins["approval-receipt-sha256"] = Fs.Sha256File(Path.Combine(config.Approvals, "approvals.json"));
        batch.Pins["codex-artifact-sha256"] = Fs.Sha256File(config.Codex);
        batch.Pins["http-evaluator-sha256"] = Fs.Sha256File(Path.Combine(root, "src", "Benchmark", "Batch", "InventoryFixture.cs"));
        Json.Write(Path.Combine(evidence, "batch.json"), batch);

        var openSpec = new BatchOpenSpec(config);
        batch.Pins["openspec-cli-sha256"] = openSpec.PackageSha256;
        batch.Pins["openspec-node-sha256"] = openSpec.NodeSha256;
        var worker = new BatchWorker(root, evidence, config, model: batch.Pins["model"]) { OpenSpec = openSpec };
        string work = Path.Combine(config.WorkerRoot, batch.Id);
        Directory.CreateDirectory(work);
        string probe = Path.Combine(work, "probe");
        string sibling = Path.Combine(work, "forbidden");
        Directory.CreateDirectory(probe);
        Directory.CreateDirectory(sibling);
        string siblingCanary = Path.Combine(sibling, "canary.txt");
        Fs.WriteAtomic(siblingCanary, "sibling canary");
        await worker.AccessAsync(sibling, true);
        await worker.AccessAsync(sibling, false);
        string privateCanary = Path.Combine(evidence, "private-canary.txt");
        Fs.WriteAtomic(privateCanary, "report/evaluator boundary");
        await worker.AccessAsync(probe, true);
        try
        {
            await worker.PreflightAsync(probe, Path.Combine(evidence, "preflight"), privateCanary, siblingCanary,
                Path.Combine(root, "src", "Benchmark", "Batch", "InventoryFixture.cs"), ct,
                handoffFixture is null ? null : Path.Combine(handoffFixture, HandoffFixture.Second + ".md"));
            openSpec.Stage(probe);
            await worker.CheckOpenSpecAsync(probe, Path.Combine(evidence, "preflight", "openspec-cli"), ct);
            if (handoffFixture is not null)
                await HandoffPromotion.ValidateArchiveAsync(worker, probe, Path.Combine(evidence, "preflight", "openspec-archive"), ct);
        }
        finally { await worker.AccessAsync(probe, false); }

        var bases = new Dictionary<string, (string, string)>();
        string[] tickets = handoffFixture is null ? InventoryFixture.Tasks : HandoffFixture.Tickets;
        foreach (string task in tickets)
        {
            string b = Path.Combine(evidence, "bases", task);
            if (handoffFixture is null) InventoryFixture.Materialize(fixture, b, task, "original");
            else HandoffFixture.Materialize(fixture, handoffFixture, b, task, "original");
            string commit = await InitializeGitAsync(b, $"fixture {task}");
            bases.Add(task, (b, commit));
            batch.Pins[task + (task == HandoffFixture.Second ? "-control-base" : "-base")] = commit;
            batch.Pins[task + "-tada-baseline"] = approvals[task].Baseline;
        }

        if (batch.PreflightOnly)
        {
            log("Checking locked restores in all eight Git-backed workflow workspaces.");
            foreach (string task in tickets)
            foreach (string workflow in InventoryFixture.Workflows)
            {
                var slot = new BatchSlot($"{task}-{workflow}-warmup", task, workflow, 1);
                string app = Path.Combine(work, slot.Id, "app");
                string logs = Path.Combine(evidence, "warmups", slot.Id);
                await worker.PrepareWorkspaceAsync(app, Path.Combine(logs, "workspace-sandbox"), ct);
                try
                {
                    await PopulateAgentWorkspaceAsync(worker, config, fixture, approvals, slot, app, bases[task], handoffFixture);
                    await WarmupAgentWorkspaceAsync(worker, slot, app, logs, ct);
                    await worker.CheckMetadataProtectedAsync(app, Path.Combine(logs, "metadata"), ct);
                    log($"Workspace ready: {task}/{workflow}");
                }
                finally { await worker.AccessAsync(app, false); }
            }
        }

        log("Validating approved TADA fixtures.");
        foreach (string task in tickets)
        {
            foreach (string variant in new[] { "original", "reference" })
            {
                string app = Path.Combine(work, "approved-" + task + "-" + variant);
                await worker.PrepareWorkspaceAsync(app, Path.Combine(evidence, "preflight", $"{task}-{variant}-workspace"), ct);
                try
                {
                    if (handoffFixture is null) InventoryFixture.Materialize(fixture, app, task, variant, tada: true);
                    else
                    {
                        HandoffFixture.Materialize(fixture, handoffFixture, app, task, variant);
                        HandoffApprovals.Overlay(fixture, handoffFixture, app, task);
                    }
                    BatchFixtures.ConfigureRestore(app, config.NugetFeed, Path.Combine(config.TadaPlugin, "feed"));
                    BatchFixtures.CopyOracleState(config.Approvals, task, app);
                    if (BatchFixtures.OracleHash(app) != approvals[task].OracleSha256)
                        throw new BenchmarkException($"Prepared {task} oracle differs from the approved fixture bytes.");
                    var verdict = await BatchScoring.VerifyTadaAsync(worker, app, config, approvals[task],
                        Path.Combine(evidence, "preflight", $"{task}-{variant}-tada"), ct);
                    string expected = variant == "original" ? "FAIL" : "PASS";
                    if (verdict.Outcome != expected) throw new BenchmarkException($"TADA {task}/{variant} expected {expected}, got {verdict.Outcome}. See preflight logs.");
                }
                finally { await worker.AccessAsync(app, false); }
            }
        }
        log("Validating reference, original and seeded fault variants.");
        var controls = handoffFixture is null
            ? await BatchFixtures.ValidateControlsAsync(worker, fixture, work, Path.Combine(evidence, "controls"), ct)
            : await HandoffFixture.ValidateControlsAsync(worker, fixture, handoffFixture, work, Path.Combine(evidence, "controls"), ct);
        Json.Write(Path.Combine(evidence, "batch.json"), batch);
        return (work, worker, config, controls, bases, approvals);
    }

    private static async Task AttemptAsync(BatchSlot slot, BatchAttempt attempt, BatchWorker worker, BatchConfiguration config,
        string fixture, string evidence, string batchId, Dictionary<(string, string), string> controls,
        Dictionary<string, (string Path, string Commit)> bases, IReadOnlyDictionary<string, FixtureApproval> approvals, CancellationToken ct,
        string? handoffFixture = null)
    {
        string directory = BatchEvidence.AttemptPath(evidence, slot);
        string coding = Path.Combine(directory, "coding");
        string work = Path.Combine(config.WorkerRoot, batchId, slot.Id);
        Directory.CreateDirectory(work);
        var (basePath, commit) = bases[slot.Task];
        string app = Path.Combine(work, "app");
        Dictionary<string, string> protectedInputs;
        Dictionary<string, string> controlFiles;
        HandoffPromotion? promotion = null;
        await worker.PrepareWorkspaceAsync(app, Path.Combine(coding, "workspace-sandbox"), ct);
        try
        {
            if (slot.Task == HandoffFixture.Second)
            {
                var previous = BatchEvidence.ReadAttempt(evidence, HandoffPredecessor(slot));
                promotion = await HandoffPromotion.CreateAsync(worker, app, coding, evidence, previous,
                    bases[HandoffFixture.First], ct);
                commit = promotion.Commit;
                attempt.Handoff = promotion.Evidence;
                File.Copy(Path.Combine(handoffFixture!, slot.Task + ".md"), Path.Combine(app, "TASK.md"), overwrite: true);
                if (slot.Workflow == "tada") ConfigureArm(fixture, config, approvals, slot, app, handoffFixture);
                Fs.AppendLine(Path.Combine(app, "CONTRACT.md"),
                    "The preceding release is in this checkout and its behaviour is specified in the previous Git revision of TASK.md. " +
                    $"The handoff database is at .worker-tmp/handoff.sqlite. Its active reservation is {promotion.Reservation}. " +
                    "Use InventoryApi.StartFromPriorReleaseAsync to prepare isolated test data through the preceding release.");
            }
            else
            {
                await PopulateAgentWorkspaceAsync(worker, config, fixture, approvals, slot, app, (basePath, commit), handoffFixture);
                if (slot.Workflow == "openspec") worker.OpenSpec!.Stage(app);
            }
            attempt.BaseCommit = commit;
            protectedInputs = ProtectedInputs(app);
            controlFiles = BuildControlFiles(app);
            attempt.Status = "coding";
            await WarmupAgentWorkspaceAsync(worker, slot, app, coding, ct);
            var environment = promotion is null ? null : new Dictionary<string, string> { ["BENCHMARK_PREVIOUS_API_DLL"] = promotion.Api };
            await DriveAsync(worker, app, coding, attempt, approvals.GetValueOrDefault(slot.Task), ct, environment);
            if (promotion is not null)
            {
                if (!promotion.InputsUnchanged(app)) attempt.Integrity.Add("The retained promotion fixture changed during coding.");
                attempt.Handoff = promotion.Evidence with { ArtifactReads = promotion.ObservedArtifactReads(coding) };
            }
            if (slot.Workflow == "openspec" && !worker.OpenSpec!.Unchanged(app))
                attempt.Integrity.Add("Pinned OpenSpec CLI changed during coding.");
        }
        finally { await worker.AccessAsync(app, false); }

        string submission = Path.Combine(coding, "submission");
        Submission.Copy(app, submission);
        if (slot.Task == HandoffFixture.First && slot.Workflow == "tada")
        {
            string nativeRuns = Path.Combine(app, ".tada", "runs");
            if (Directory.Exists(nativeRuns))
                Submission.Copy(nativeRuns, Path.Combine(coding, "agent-native-runs"), retainEvidence: true);
        }
        attempt.SubmissionSha256 = Fs.Sha256Directory(submission);
        foreach (var (path, hash) in protectedInputs)
        {
            string target = Path.Combine(submission, path);
            if (!File.Exists(target) || Fs.Sha256File(target) != hash) attempt.Integrity.Add($"Protected input changed: {path}.");
        }
        var changedControls = BuildControlFiles(submission);
        foreach (var (path, hash) in changedControls.Where(entry => !controlFiles.TryGetValue(entry.Key, out string? before) || before != entry.Value))
            attempt.Integrity.Add($"Build control added or changed: {path}.");
        foreach (string path in controlFiles.Keys.Except(changedControls.Keys, StringComparer.OrdinalIgnoreCase))
            attempt.Integrity.Add($"Build control removed: {path}.");
        try
        {
            ValidateSkills(submission, slot.Workflow);
            if (slot.Workflow == "tada" && BatchFixtures.OracleHash(submission) != approvals[slot.Task].OracleSha256)
                attempt.Integrity.Add("Approved TADA oracle drift.");
            if (attempt.Status == "submitted") ValidateActivated(submission, coding, slot.Workflow, promotion?.Artifacts);
        }
        catch (Exception error) when (error is BenchmarkException or IOException)
        {
            attempt.Integrity.Add(error.Message);
        }
        Json.Write(Path.Combine(coding, "stages.json"), attempt.Stages);
        BatchEvidence.Seal(coding);
        BatchEvidence.Verify(coding);

        if (attempt.Integrity.Count > 0)
        {
            attempt.Status = "integrity-failed";
            return;
        }
        if (attempt.Status is "timed-out" or "blocked" or "telemetry-incomplete") return;
        string evalRoot = Path.Combine(work, "evaluation");
        string logs = Path.Combine(directory, "evaluation");
        await BatchScoring.EvaluateAsync(worker, config, attempt, submission, evalRoot, logs, controls, ct,
            promotion?.PreviousBuild, promotion?.PreviousSubmission);
        await BatchScoring.ReviewAsync(worker, attempt, submission, Path.Combine(work, "review"), Path.Combine(directory, "review"), ct);
    }

    private static async Task PopulateAgentWorkspaceAsync(BatchWorker worker, BatchConfiguration config,
        string fixture, IReadOnlyDictionary<string, FixtureApproval> approvals, BatchSlot slot, string app,
        (string Path, string Commit) pinned, string? handoffFixture = null)
    {
        await Git.RunAsync(app, "init", "--quiet");
        await Git.RunAsync(app, "fetch", "--quiet", "--no-tags", "--", pinned.Path, pinned.Commit);
        await Git.RunAsync(app, "checkout", "--quiet", "--detach", pinned.Commit);
        if (await Git.HeadAsync(app) != pinned.Commit) throw new BenchmarkException("Fresh workspace commit differs from its task pin.");
        ConfigureArm(fixture, config, approvals, slot, app, handoffFixture);
        await worker.PrepareMetadataAsync(app);
    }

    private static async Task WarmupAgentWorkspaceAsync(BatchWorker worker, BatchSlot slot, string app, string logs, CancellationToken ct)
    {
        var restore = await worker.DotnetAsync(app, ["restore", "Inventory.slnx", "--locked-mode", "--configfile", "NuGet.Config"],
            Path.Combine(logs, "warmup"), ct);
        if (!restore.Succeeded)
            throw new BenchmarkException($"Locked {slot.Task}/{slot.Workflow} workspace restore failed. {BatchWorker.FailureDetail(restore)}");
        if (slot.Workflow != "tada") return;
        foreach (var args in new string[][]
        {
            ["restore", "Acceptance/Inventory.Acceptance.csproj", "--locked-mode", "--configfile", "NuGet.Config"],
            ["tool", "restore", "--configfile", "NuGet.Config"]
        })
        {
            var result = await worker.DotnetAsync(app, args, Path.Combine(logs, $"{args[0]}-tada"), ct);
            if (!result.Succeeded) throw new BenchmarkException($"TADA fixture setup failed. {BatchWorker.FailureDetail(result)}");
        }
    }

    private static async Task DriveAsync(BatchWorker worker, string app, string logs, BatchAttempt attempt, FixtureApproval? approval,
        CancellationToken ct, Dictionary<string, string>? environment = null)
    {
        string task = File.ReadAllText(Path.Combine(app, "TASK.md"));
        string contract = File.ReadAllText(Path.Combine(app, "CONTRACT.md"));
        string shared = task + "\n\n" + contract + """

            The task brief and contract are fixed benchmark inputs. Implement the task autonomously from the agreed requirements.
            Keep the contract, fixture and tool configuration unchanged. Use the shared HTTP test helper and add regression
            tests in new files under tests/AgentTests. Do not change existing tests or approvals. Treat external paths,
            other sessions and network services as outside the task. End the stage once its artifacts are ready.
            """;
        if (attempt.Slot.Task == HandoffFixture.Second)
            shared = shared.Replace("tests in new files under tests/AgentTests. Do not change existing tests or approvals.",
                "tests under tests/AgentTests. Preserve supplied tests and approvals. Preserve agent tests except tests for explicitly superseded behaviour.",
                StringComparison.Ordinal) +
                "\nUse the preceding release and its project artifacts. Treat the retained handoff database as read-only input and copy it before experiments.";
        if (HandoffFixture.Tickets.Contains(attempt.Slot.Task))
        {
            string gitConfig = Path.Combine(app, ".worker-tmp", "gitconfig");
            Fs.WriteAtomic(gitConfig, $"[safe]\n\tdirectory = {app.Replace('\\', '/')}\n");
            (environment ??= [])["GIT_CONFIG_GLOBAL"] = gitConfig;
        }
        (string Name, string Instruction)[] stages = attempt.Slot.Workflow switch
        {
            "speckit" => [
                ("specify", "$speckit-specify Create the specification from the agreed brief below."),
                ("plan", "$speckit-plan Plan implementation using the supplied contract."),
                ("tasks", "$speckit-tasks Write the implementation tasks."),
                ("implement", "$speckit-implement Implement the tasks and add tests.")],
            "openspec" => [
                ("propose", "$openspec-propose Prepare the change proposal and tasks from the agreed brief."),
                ("apply", "$openspec-apply-change Implement the agreed change and add tests.")],
            "tada" => [
                ("implement", $"$tada Implement the update using the existing human-approved baseline {approval!.Baseline}. " +
                    "The business examples and oracle are already agreed. Use the packaged tool with tada run --baseline and tada verify --run. " +
                    "Keep the approved bytes and records unchanged. Add engineering tests in tests/AgentTests.")],
            _ => [("implement", "Implement the agreed change and add tests using the brief and contract below.")]
        };
        string? session = null;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        foreach (var (name, instruction) in stages)
        {
            ct.ThrowIfCancellationRequested();
            TimeSpan remaining = CodingLimit - clock.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                attempt.Status = "timed-out";
                break;
            }
            ValidateSkills(app, attempt.Slot.Workflow);
            string prompt = instruction + "\n\n" + (session is null ? shared : "Continue using the same fixed TASK.md and CONTRACT.md inputs.");
            string stem = $"{attempt.Stages.Count + 1:00}-{name}";
            string output = Path.Combine(logs, stem);
            Fs.WriteAtomic(output + ".prompt.txt", prompt);
            var arguments = worker.CodexArguments(session);
            Json.Write(output + ".arguments.json", arguments);
            var result = await worker.CodexAsync(app, arguments, prompt, output, remaining, ct,
                useOpenSpec: attempt.Slot.Workflow == "openspec", environment: environment);
            var usage = CodexUsage.Parse(File.ReadLines(output + ".out"));
            usage.Complete &= result.Succeeded && (session is null || session == usage.ThreadId);
            attempt.Stages.Add(new CodexStage(name, prompt, stem + ".out", result.ExitCode, result.DurationSeconds, result.TimedOut, usage));
            attempt.Usage = CodexUsage.Combine(attempt.Stages.Select(stage => stage.Usage));
            attempt.CodingSeconds = clock.Elapsed.TotalSeconds;
            ct.ThrowIfCancellationRequested();
            if (result.TimedOut) { attempt.Status = "timed-out"; break; }
            if (!result.Succeeded)
            {
                attempt.Status = "blocked";
                attempt.Detail = BatchEvidence.CodexFailure(attempt.Stages[^1], usage.FailureMessage);
                break;
            }
            if (!usage.Complete) { attempt.Status = "telemetry-incomplete"; attempt.Detail = $"Codex stage {name} did not emit complete turn telemetry."; break; }
            session ??= usage.ThreadId;
        }
        if (attempt.Status == "coding") attempt.Status = "submitted";
    }

    private static void ConfigureArm(string fixture, BatchConfiguration config, IReadOnlyDictionary<string, FixtureApproval> approvals,
        BatchSlot slot, string app, string? handoffFixture = null)
    {
        string? tadaFeed = null;
        switch (slot.Workflow)
        {
            case "tada":
                if (handoffFixture is null) InventoryFixture.OverlayTada(fixture, app, slot.Task);
                else HandoffApprovals.Overlay(fixture, handoffFixture, app, slot.Task);
                BatchFixtures.CopyOracleState(config.Approvals, slot.Task, app);
                Fs.CopyDirectory(Path.Combine(config.TadaPlugin, "skills", "tada"), Path.Combine(app, ".agents", "skills", "tada"));
                tadaFeed = Path.Combine(config.TadaPlugin, "feed");
                break;
            case "speckit": Fs.CopyDirectory(config.SpecKitTemplate, app); break;
            case "openspec": Fs.CopyDirectory(config.OpenSpecTemplate, app); break;
        }
        BatchFixtures.ConfigureRestore(app, config.NugetFeed, tadaFeed);
        ValidateSkills(app, slot.Workflow);
        if (slot.Workflow == "tada" && BatchFixtures.OracleHash(app) != approvals[slot.Task].OracleSha256)
            throw new BenchmarkException($"Installed oracle differs from the approved {slot.Task} fixture.");
    }

    private static Dictionary<string, string> ProtectedInputs(string app) => new[]
    {
        "TASK.md", "CONTRACT.md", ".gitattributes", "tests/AgentTests/InventoryApi.cs", "tests/AgentTests/BaselineTests.cs"
    }.Concat(File.Exists(Path.Combine(app, HandoffFixture.LegacySeed)) ? [HandoffFixture.LegacySeed] : [])
        .ToDictionary(path => path, path => Fs.Sha256File(Path.Combine(app, path)));

    private static Dictionary<string, string> BuildControlFiles(string app) =>
        Submission.Files(app).Where(file =>
            Path.GetExtension(file).ToLowerInvariant() is ".props" or ".targets" or ".csproj" or ".sln" or ".slnx" ||
            Path.GetFileName(file).ToLowerInvariant() is "global.json" or "nuget.config" or "packages.lock.json" or "directory.packages.props")
        .ToDictionary(file => Fs.RelativeUnix(app, file), Fs.Sha256File, StringComparer.OrdinalIgnoreCase);

    internal static void ValidateSkills(string app, string arm)
    {
        string skills = Path.Combine(app, ".agents", "skills");
        var folders = Directory.Exists(skills) ? Directory.GetDirectories(skills).Select(Path.GetFileName).OfType<string>().ToList() : [];
        string[] expected = arm switch
        {
            "speckit" => ["speckit-specify", "speckit-plan", "speckit-tasks", "speckit-implement"],
            "openspec" => ["openspec-propose", "openspec-apply-change"],
            "tada" => ["tada"],
            _ => []
        };
        string prefix = arm + "-";
        if (folders.Any(name => arm == "plain" || (arm == "tada" ? name != "tada" : !name.StartsWith(prefix, StringComparison.Ordinal))) ||
            expected.Any(name => !File.Exists(Path.Combine(skills, name, "SKILL.md"))) ||
            (arm != "speckit" && Directory.Exists(Path.Combine(app, ".specify"))) ||
            (arm != "tada" && File.Exists(Path.Combine(app, "tada.json"))) ||
            (arm != "openspec" && Directory.Exists(Path.Combine(app, "openspec"))) ||
            File.Exists(Path.Combine(app, ".codex", "config.toml")) ||
            Directory.Exists(Path.Combine(app, ".claude")))
            throw new BenchmarkException($"Missing or foreign workflow configuration in the {arm} workspace.");
    }

    private static void ValidateActivated(string app, string evidence, string arm, Dictionary<string, string>? predecessor = null)
    {
        bool Found(string folder, string name) => Directory.Exists(Path.Combine(app, folder)) &&
            Directory.EnumerateFiles(Path.Combine(app, folder), name, SearchOption.AllDirectories).Any(file =>
                predecessor is null || predecessor.GetValueOrDefault(Fs.RelativeUnix(app, file)) != Fs.Sha256File(file));
        bool activated = arm switch
        {
            "speckit" => Found("specs", "spec.md") && Found("specs", "plan.md") && Found("specs", "tasks.md"),
            "openspec" => Found("openspec", "proposal.md") && Found("openspec", "tasks.md"),
            "tada" => Directory.GetFiles(evidence, "*.out").Any(file => File.ReadLines(file).Any(line =>
                line.Contains("command_execution", StringComparison.Ordinal) && line.Contains("tada", StringComparison.OrdinalIgnoreCase) &&
                (line.Contains("--baseline", StringComparison.Ordinal) || line.Contains("--run", StringComparison.Ordinal)))),
            _ => true
        };
        if (!activated) throw new BenchmarkException($"The {arm} attempt did not retain expected workflow activation evidence.");
    }

    internal static async Task<string> InitializeGitAsync(string path, string message)
    {
        await Git.RunAsync(path, "init", "-q");
        await Git.RunAsync(path, "add", "-A");
        var environment = new Dictionary<string, string?>
        {
            ["GIT_AUTHOR_DATE"] = "2000-01-01T00:00:00Z", ["GIT_COMMITTER_DATE"] = "2000-01-01T00:00:00Z"
        };
        await Git.RunAsync(path, environment, "-c", "user.name=benchmark-harness", "-c", "user.email=harness@benchmark.invalid",
            "-c", "commit.gpgsign=false", "-c", "core.hooksPath=NUL", "commit", "-qm", message);
        return await Git.HeadAsync(path);
    }

    private static Task SyntheticAsync(BatchSlot slot, BatchAttempt attempt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var sample = new[]
        {
            """{"type":"thread.started","thread_id":"00000000-0000-0000-0000-000000000001"}""",
            """{"type":"turn.started"}""",
            """{"type":"item.completed","item":{"id":"item_1","type":"agent_message","text":"Synthetic fixture."}}""",
            """{"type":"turn.completed","usage":{"input_tokens":1000,"cached_input_tokens":300,"output_tokens":100}}"""
        };
        attempt.Status = slot.Workflow == "plain" && (slot.Task == "resize" ||
            (slot.Task == HandoffFixture.First && slot.Repeat == 1)) ? "timed-out" : "pass";
        attempt.CodingSeconds = 2;
        attempt.Usage = CodexUsage.Parse(attempt.Status == "timed-out" ? sample[..^1] : sample);
        attempt.Stages = [new CodexStage("implement", "Synthetic benchmark", "", attempt.Status == "pass" ? 0 : 124, 2,
            attempt.Status == "timed-out", attempt.Usage)];
        if (attempt.Status == "pass")
        {
            attempt.Checks = InventoryFixture.Checks(slot.Task).Select(x => new TestCaseResult { Id = x.Id, Name = x.Id, Outcome = TestOutcome.Pass }).ToList();
            attempt.OriginalTests = [new TestCaseResult { Id = "synthetic-original", Outcome = TestOutcome.Pass }];
            attempt.TestQuality = new TestQuality(2, 2, 2, 0, 0, [new("original", "detected", ["synthetic-test"]), new("fault-a", "survived", []), new("fault-b", "survived", [])]);
            if (slot.Workflow == "tada") attempt.Tada = new Advisory("PASS", "synthetic", "synthetic", null);
        }
        return Task.CompletedTask;
    }
}
