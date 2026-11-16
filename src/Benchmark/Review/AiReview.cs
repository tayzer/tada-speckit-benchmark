using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Benchmark.Claude;
using Benchmark.Config;
using Benchmark.Runs;
using Benchmark.Util;
using Benchmark.Workspace;

namespace Benchmark.Review;

// Independent AI engineering review. See docs/ai-review.md.
//
//   review-bundle  builds blinded bundles for a pair of runs (no model calls)
//   ai-review      runs each configured reviewer on the bundles, then a process review per run, and writes
//                  <dataRoot>/ai-reviews/<setId>/summary.{json,md}; `compare` shows the latest summary.
//
// Blinding: the run-to-bundle mapping lives in <dataRoot>/ai-reviews/<setId>/key.json. Reviewers run with their
// working directory under <dataRoot>/ai-review-work/<setId>/<call>/ and deny rules on everything else in the
// data root and the harness, so they cannot read the key, run evidence or the other passes.

public sealed class BundleEntry
{
    public string BundleId { get; set; } = "";
    public string RunId { get; set; } = "";
    public string WorkflowId { get; set; } = "";
    public List<string> Shown { get; set; } = [];
    public List<string> Collapsed { get; set; } = [];
    public List<string> Excluded { get; set; } = [];
    public Dictionary<string, int> LeakCounts { get; set; } = new();
    public string PatchSha256 { get; set; } = "";
}

public sealed class ReviewSetKey
{
    public string SetId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string ProtocolSha256 { get; set; } = "";
    public bool ProtocolApproved { get; set; }
    public int Seed { get; set; }
    public List<BundleEntry> Bundles { get; set; } = [];
}

public sealed class CodeReviewOfChange
{
    public Dictionary<string, int> Scores { get; set; } = new();
    public Dictionary<string, string> Evidence { get; set; } = new();
    public List<string> RisksDiscovered { get; set; } = [];
    public List<string> RisksMissed { get; set; } = [];
    public List<string> IncorrectAssumptions { get; set; } = [];
    public string RegressionAssessment { get; set; } = "";
    public List<string> BlindingClues { get; set; } = [];
}

public sealed class ProcessReviewOutput
{
    public List<int> WorkflowSteps { get; set; } = [];
    public List<int> BusinessAnswers { get; set; } = [];
    public List<int> CorrectivePrompts { get; set; } = [];
    public List<int> ToolingInterventions { get; set; } = [];
    public List<int> Other { get; set; } = [];
    public List<string> NecessaryQuestionsIdentified { get; set; } = [];
    public List<string> IncorrectAssumptions { get; set; } = [];
    public List<string> VerificationOmissions { get; set; } = [];
    public string Notes { get; set; } = "";
}

/// <summary>One reviewer's code review of one run in one pass (after unblinding).</summary>
public sealed class CodePass
{
    public int Pass { get; set; }
    public string Label { get; set; } = "";
    public CodeReviewOfChange Review { get; set; } = new();
    public string Comparison { get; set; } = "";
    /// <summary>`path:line` citations in the evidence, and how many point past the end of the cited post-change file.</summary>
    public int Citations { get; set; }
    public int CitationsOutOfRange { get; set; }
}

public sealed class AiRunSummary
{
    public string RunId { get; set; } = "";
    public string WorkflowId { get; set; } = "";
    public string BundleId { get; set; } = "";
    public Dictionary<string, int> LeakCounts { get; set; } = new();
    public Dictionary<string, List<CodePass>> Code { get; set; } = new();
    public Dictionary<string, ProcessReviewOutput> Process { get; set; } = new();
}

public sealed class AiCallRecord
{
    public string Name { get; set; } = "";
    public string Reviewer { get; set; } = "";
    public int Attempts { get; set; }
    public bool Succeeded { get; set; }
    public string? Error { get; set; }
    public decimal? CostUsd { get; set; }
    public int PermissionDenials { get; set; }
    public double DurationSeconds { get; set; }
}

public sealed class AiReviewSummary
{
    public string SetId { get; set; } = "";
    public DateTimeOffset GeneratedAt { get; set; }
    public string ProtocolSha256 { get; set; } = "";
    public bool ProtocolApproved { get; set; }
    public List<string> Reviewers { get; set; } = [];
    public int Passes { get; set; }
    public List<AiRunSummary> Runs { get; set; } = [];
    public List<AiCallRecord> Calls { get; set; } = [];
    public List<string> Flags { get; set; } = [];
    public decimal TotalCostUsd => Calls.Sum(c => c.CostUsd ?? 0m);
}

public static class Glob
{
    /// <summary>Matches a repo-relative '/' path against a glob with '**', '*' and '?'. A pattern without '/' matches the file name anywhere.</summary>
    public static bool IsMatch(string path, string glob)
    {
        path = path.Replace('\\', '/');
        if (!glob.Contains('/')) glob = "**/" + glob;
        var re = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var ch = glob[i];
            if (ch == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                var slash = i + 2 < glob.Length && glob[i + 2] == '/';
                re.Append(slash ? "(?:.*/)?" : ".*");
                i += slash ? 2 : 1;
            }
            else if (ch == '*') re.Append("[^/]*");
            else if (ch == '?') re.Append("[^/]");
            else re.Append(Regex.Escape(ch.ToString()));
        }
        re.Append('$');
        return Regex.IsMatch(path, re.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static bool AnyMatch(string path, IEnumerable<string> globs) => globs.Any(g => IsMatch(path, g));
}

public static class AiReview
{
    public static string ReviewsRoot(RunStore store) => Path.Combine(store.DataRoot, "ai-reviews");
    public static string WorkRoot(RunStore store) => Path.Combine(store.DataRoot, "ai-review-work");
    public static string SetDir(RunStore store, string setId) => Path.Combine(ReviewsRoot(store), setId);

    public static readonly string[] ReviewerAllowedTools = ["Read", "Glob", "Grep"];
    public static readonly string[] ReviewerDisallowedTools =
        ["Bash", "PowerShell", "Edit", "Write", "NotebookEdit", "WebFetch", "WebSearch", "Agent", "Task"];

    // ---------------------------------------------------------------- protocol

    /// <summary>Hash of everything that defines the review: prompt files, filters, reviewers, passes and tool policy.</summary>
    public static string ProtocolSha256(ExperimentConfig c)
    {
        var a = c.AiReview;
        var dir = c.ResolveExperimentPath(a.PromptsDirectory);
        var prompts = Directory.Exists(dir) ? Fs.Sha256Directory(dir) : "missing";
        var definition = Json.Serialize(new
        {
            prompts,
            a.Reviewers, a.Passes, a.ExcludeGlobs, a.CollapseGlobs, a.ScaffoldGlobs, a.LeakTerms,
            tools = ReviewerAllowedTools, denied = ReviewerDisallowedTools,
            dimensions = Rubric.Dimensions,
        });
        return Fs.Sha256Text(definition);
    }

    public static bool IsApproved(ExperimentConfig c) =>
        c.AiReview.Status.Equals("approved", StringComparison.OrdinalIgnoreCase)
        && ProtocolSha256(c).Equals(c.AiReview.ApprovedProtocolSha256, StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- run selection

    /// <summary>The latest finished run of each workflow made with the current pins. Exactly two workflows required.</summary>
    public static List<RunRecord> DefaultPair(ExperimentConfig c, RunStore store)
    {
        var runs = store.All()
            .Where(r => r.ExperimentId == c.ExperimentId && !r.Synthetic && r.Status is RunStatus.Finished or RunStatus.Sealed && Cli.SimpleRun.MatchesCurrentPin(c, r))
            .GroupBy(r => r.WorkflowId)
            .Select(g => g.OrderByDescending(r => r.CreatedAt).First())
            .ToList();
        if (runs.Count != 2) throw new BenchmarkException($"Expected one current run for each of two workflows, found {runs.Count}. Pass --runs 'a,b'.");
        return runs;
    }

    // ---------------------------------------------------------------- bundles

    public static async Task<ReviewSetKey> BuildBundlesAsync(ExperimentConfig c, RunStore store, IReadOnlyList<RunRecord> runs, Action<string> log)
    {
        if (runs.Count != 2) throw new BenchmarkException("A blinded review needs exactly two runs.");
        if (runs[0].WorkflowId == runs[1].WorkflowId) throw new BenchmarkException("The two runs must come from different workflows.");
        if (c.AiReview.Reviewers.Count == 0) throw new BenchmarkException("aiReview.reviewers is empty in experiment.json.");

        var setId = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
        var seed = BitConverter.ToInt32(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(setId)), 0);
        var ordered = runs.OrderBy(r => r.RunId, StringComparer.Ordinal).ToList();
        if (new Random(seed).Next(2) == 1) ordered.Reverse();

        var key = new ReviewSetKey
        {
            SetId = setId, CreatedAt = DateTimeOffset.UtcNow, Seed = seed,
            ProtocolSha256 = ProtocolSha256(c), ProtocolApproved = IsApproved(c),
        };
        var setDir = SetDir(store, setId);
        for (var i = 0; i < ordered.Count; i++)
        {
            var bundleId = $"bundle-{i + 1}";
            var entry = await BuildBundleAsync(c, store, ordered[i], Path.Combine(setDir, "bundles", bundleId));
            entry.BundleId = bundleId;
            key.Bundles.Add(entry);
            log($"{bundleId}: {entry.Shown.Count} file(s) shown, {entry.Collapsed.Count} collapsed, {entry.Excluded.Count} excluded"
                + (entry.LeakCounts.Count > 0 ? $"; workflow words present: {string.Join(", ", entry.LeakCounts.Select(kv => $"{kv.Key}×{kv.Value}"))}" : ""));
        }
        Json.Write(Path.Combine(setDir, "key.json"), key);
        return key;
    }

    private static async Task<BundleEntry> BuildBundleAsync(ExperimentConfig c, RunStore store, RunRecord run, string dir)
    {
        var a = c.AiReview;
        var patchPath = store.EvidencePath(run.RunId, "changes", "final.patch");
        if (!File.Exists(patchPath)) throw new BenchmarkException($"No final.patch for {run.RunId}; finish the run first.");
        var chunks = SplitPatch(Fs.NormalizeNewlines(File.ReadAllText(patchPath)));
        var changes = Json.Deserialize<JsonObject>(File.ReadAllText(store.EvidencePath(run.RunId, "changes", "changes.json")));
        var counts = changes["files"]!.AsArray().ToDictionary(f => f!["path"]!.ToString(), f => (Added: (int)f!["added"]!, Removed: (int)f!["removed"]!, Status: f!["status"]!.ToString()));
        var toCommit = changes["toCommit"]?.ToString();

        var entry = new BundleEntry { RunId = run.RunId, WorkflowId = run.WorkflowId };
        var patch = new StringBuilder();
        var files = new StringBuilder("# Files in this change\n\n| File | Status | + | - | Shown |\n|---|---|---|---|---|\n");
        Directory.CreateDirectory(Path.Combine(dir, "after"));
        foreach (var (path, text) in chunks)
        {
            var (added, removed, status) = counts.TryGetValue(path, out var n) ? n : (0, 0, "?");
            if (Glob.AnyMatch(path, a.ExcludeGlobs)) { entry.Excluded.Add(path); continue; }
            if (Glob.AnyMatch(path, a.ScaffoldGlobs))
            {
                entry.Collapsed.Add(path);
                files.AppendLine($"| `{path}` | {status} | {added} | {removed} | no (tool-generated boilerplate; present in the repository) |");
                continue;
            }
            if (Glob.AnyMatch(path, a.CollapseGlobs))
            {
                entry.Collapsed.Add(path);
                files.AppendLine($"| `{path}` | {status} | {added} | {removed} | no (generated) |");
                continue;
            }
            entry.Shown.Add(path);
            patch.Append(text);
            files.AppendLine($"| `{path}` | {status} | {added} | {removed} | yes |");
            if (status != "D" && toCommit is not null && Directory.Exists(run.WorkspacePath))
            {
                try
                {
                    var content = await Git.RunAsync(run.WorkspacePath, "show", $"{toCommit}:{path}");
                    var target = Path.Combine(dir, "after", path.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    Fs.WriteAtomic(target, Scrub(content, run) + "\n");
                }
                catch (BenchmarkException) { /* binary or unavailable: the patch still shows it */ }
            }
        }
        var patchText = Scrub(patch.ToString(), run);
        Fs.WriteAtomic(Path.Combine(dir, "changes.patch"), patchText);
        Fs.WriteAtomic(Path.Combine(dir, "files.md"), files.ToString());
        var failures = Scrub(ExistingTestFailures(store, run), run);
        Fs.WriteAtomic(Path.Combine(dir, "existing-test-failures.md"), failures);
        entry.PatchSha256 = Fs.Sha256Text(patchText);

        var all = string.Join("\n", Fs.EnumerateFilesSorted(dir).Select(File.ReadAllText));
        foreach (var term in a.LeakTerms)
        {
            var count = Regex.Matches(all, "(?<![A-Za-z])" + Regex.Escape(term), RegexOptions.IgnoreCase).Count; // "Metadata" is not "tada"
            if (count > 0) entry.LeakCounts[term] = count;
        }
        return entry;
    }

    /// <summary>Splits a git patch into (path, chunk text) per file, using the b/ path.</summary>
    public static List<(string Path, string Text)> SplitPatch(string patch)
    {
        var result = new List<(string, string)>();
        var starts = Regex.Matches(patch, @"^diff --git a/(.+?) b/(.+)$", RegexOptions.Multiline);
        for (var i = 0; i < starts.Count; i++)
        {
            var start = starts[i].Index;
            var end = i + 1 < starts.Count ? starts[i + 1].Index : patch.Length;
            result.Add((starts[i].Groups[2].Value.Trim().Trim('"'), patch[start..end]));
        }
        return result;
    }

    /// <summary>Removes run-identifying absolute paths (workspace, data root) from reviewer-visible text.</summary>
    private static string Scrub(string text, RunRecord run)
    {
        foreach (var p in new[] { run.WorkspacePath, Path.GetDirectoryName(run.WorkspacePath) ?? "" }.Where(p => p.Length > 3))
        {
            text = text.Replace(p, "<app>", StringComparison.OrdinalIgnoreCase);
            text = text.Replace(p.Replace('\\', '/'), "<app>", StringComparison.OrdinalIgnoreCase);
        }
        return text.Replace(run.RunId, "<run>", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExistingTestFailures(RunStore store, RunRecord run)
    {
        var sb = new StringBuilder("# Existing tests that passed before the change and fail after it\n\n");
        var eval = Directory.Exists(store.EvaluationsRoot)
            ? Directory.GetDirectories(store.EvaluationsRoot, "*-" + run.RunId).OrderBy(d => d, StringComparer.Ordinal).LastOrDefault()
            : null;
        var path = eval is null ? null : Path.Combine(eval, "evaluation.json");
        if (path is null || !File.Exists(path)) return sb.AppendLine("Not evaluated.").ToString();
        var regressions = JsonNode.Parse(File.ReadAllText(path))?["regressions"]?.AsArray() ?? [];
        if (regressions.Count == 0) return sb.AppendLine("None.").ToString();
        foreach (var r in regressions)
            sb.AppendLine($"- `{r?["test"]}` ({r?["project"]}): {r?["message"]?.ToString().Replace("\r", "").Replace("\n", " ")}");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- running reviewers

    public static async Task<AiReviewSummary> RunAsync(ExperimentConfig c, RunStore store, ReviewSetKey key, bool codeOnly, bool processOnly, Action<string> log, CancellationToken ct)
    {
        var a = c.AiReview;
        var setDir = SetDir(store, key.SetId);
        var promptsDir = c.ResolveExperimentPath(a.PromptsDirectory);
        var codePrompt = File.ReadAllText(Path.Combine(promptsDir, "code-review.md"));
        var processPrompt = File.ReadAllText(Path.Combine(promptsDir, "process-review.md"));
        var summary = new AiReviewSummary
        {
            SetId = key.SetId, GeneratedAt = DateTimeOffset.UtcNow, ProtocolSha256 = key.ProtocolSha256, ProtocolApproved = key.ProtocolApproved,
            Reviewers = a.Reviewers.Select(r => r.Model).ToList(), Passes = a.Passes,
            Runs = key.Bundles.Select(b => new AiRunSummary { RunId = b.RunId, WorkflowId = b.WorkflowId, BundleId = b.BundleId, LeakCounts = b.LeakCounts }).ToList(),
        };
        var common = CommonFiles(c, promptsDir);
        var baseRepo = Path.Combine(BasePreparer.BaseDir(store, c), "repo");

        if (!processOnly)
            foreach (var reviewer in a.Reviewers)
                for (var pass = 1; pass <= a.Passes; pass++)
                {
                    // Odd passes show bundle-1 as A, even passes bundle-2 as A.
                    var first = key.Bundles[(pass - 1) % 2];
                    var second = key.Bundles[pass % 2];
                    var name = $"code-{Slug(reviewer.Model)}-pass{pass}";
                    var work = PrepareWork(store, key.SetId, name, common);
                    Fs.WriteAtomic(Path.Combine(work, "README.md"),
                        $"# Original application source\n\nThe unchanged starting version of the application is at:\n\n    {baseRepo}\n\nRead it with the Read, Glob and Grep tools. Do not modify it.\n");
                    Fs.CopyDirectory(Path.Combine(setDir, "bundles", first.BundleId), Path.Combine(work, "change-A"));
                    Fs.CopyDirectory(Path.Combine(setDir, "bundles", second.BundleId), Path.Combine(work, "change-B"));
                    log($"Code review {name}: A={first.BundleId}, B={second.BundleId} ...");
                    var (call, json) = await CallWithRetryAsync(c, store, key.SetId, reviewer, name, work, codePrompt, [baseRepo], ParseCodeReview, ct);
                    summary.Calls.Add(call);
                    if (json is not { } parsed) continue;
                    foreach (var (label, bundle) in new[] { ("A", first), ("B", second) })
                    {
                        var runSummary = summary.Runs.Single(r => r.BundleId == bundle.BundleId);
                        if (!runSummary.Code.TryGetValue(reviewer.Model, out var list)) runSummary.Code[reviewer.Model] = list = [];
                        var (cited, outOfRange) = CheckCitations(parsed.Changes[label], Path.Combine(setDir, "bundles", bundle.BundleId, "after"));
                        list.Add(new CodePass { Pass = pass, Label = label, Review = parsed.Changes[label], Comparison = parsed.Comparison, Citations = cited, CitationsOutOfRange = outOfRange });
                    }
                }

        if (!codeOnly)
            foreach (var reviewer in a.Reviewers)
                foreach (var runSummary in summary.Runs)
                {
                    var name = $"process-{Slug(reviewer.Model)}-{runSummary.BundleId}";
                    var work = PrepareWork(store, key.SetId, name, common.Where(f => f.Name != "rubric.md").ToList());
                    var run = store.Load(runSummary.RunId);
                    File.Copy(store.EvidencePath(run.RunId, "chat.md"), Path.Combine(work, "chat.md"));
                    log($"Process review {name} ...");
                    var (call, parsed) = await CallWithRetryAsync(c, store, key.SetId, reviewer, name, work, processPrompt, [], ParseProcessReview, ct);
                    summary.Calls.Add(call);
                    if (parsed is not null) runSummary.Process[reviewer.Model] = parsed;
                }

        summary.Flags.AddRange(ComputeFlags(summary));
        Json.Write(Path.Combine(setDir, "summary.json"), summary);
        Fs.WriteAtomic(Path.Combine(setDir, "summary.md"), SummaryMarkdown(summary));
        return summary;
    }

    private sealed record CommonFile(string Name, string Content);

    private static List<CommonFile> CommonFiles(ExperimentConfig c, string promptsDir)
    {
        var decisions = Human.DecisionRegister.Load(c.ResolveExperimentPath(c.DecisionsFile));
        var sb = new StringBuilder("# Business decisions\n\nApproved answers to the business questions for this task.\n\n");
        foreach (var d in decisions.Decisions.Where(d => d.Status == "approved"))
            sb.AppendLine($"## {d.Id}. {d.Topic}\n\n**Question.** {d.Question}\n\n**Answer.** {d.Answer}\n");
        return
        [
            new("task.md", "# Task\n\n" + c.PromptText + "\n"),
            new("decisions.md", sb.ToString()),
            new("rubric.md", File.ReadAllText(Path.Combine(promptsDir, "rubric.md"))),
        ];
    }

    private static string PrepareWork(RunStore store, string setId, string name, IReadOnlyList<CommonFile> files)
    {
        var work = Path.Combine(WorkRoot(store), setId, name);
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);
        foreach (var f in files) Fs.WriteAtomic(Path.Combine(work, f.Name), f.Content);
        return work;
    }

    private static string Slug(string model) => Regex.Replace(model, "[^A-Za-z0-9.-]", "-");

    private static async Task<(AiCallRecord, T?)> CallWithRetryAsync<T>(ExperimentConfig c, RunStore store, string setId, AiReviewerConfig reviewer,
        string name, string work, string prompt, IReadOnlyList<string> addDirs, Func<string, (T? Value, string? Error)> parse, CancellationToken ct) where T : class
    {
        var record = new AiCallRecord { Name = name, Reviewer = reviewer.Model };
        // One retry, only when the reply is unusable (not to shop for a better score). Both attempts are kept.
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            record.Attempts = attempt;
            var outcome = await CallClaudeAsync(c, store, setId, reviewer, $"{name}.attempt{attempt}", work, prompt, addDirs, ct);
            record.CostUsd = (record.CostUsd ?? 0m) + (outcome.CostUsd ?? 0m);
            record.PermissionDenials += outcome.PermissionDenials;
            record.DurationSeconds += outcome.DurationSeconds;
            if (outcome.Error is not null) { record.Error = outcome.Error; continue; }
            var (value, error) = parse(outcome.Text!);
            if (value is not null) { record.Succeeded = true; record.Error = null; return (record, value); }
            record.Error = "Unusable reply: " + error;
        }
        return (record, null);
    }

    private sealed record ClaudeOutcome(string? Text, string? Error, decimal? CostUsd, int PermissionDenials, double DurationSeconds);

    private static async Task<ClaudeOutcome> CallClaudeAsync(ExperimentConfig c, RunStore store, string setId, AiReviewerConfig reviewer,
        string name, string work, string prompt, IReadOnlyList<string> addDirs, CancellationToken ct)
    {
        var a = c.AiReview;
        var evidence = Path.Combine(SetDir(store, setId), "calls");
        Directory.CreateDirectory(evidence);
        var policy = new AgentConfig { PermissionMode = "default", AllowedTools = [.. ReviewerAllowedTools], DisallowedTools = [.. ReviewerDisallowedTools] };
        var (settings, _) = ClaudeLaunch.BuildRunSettings(policy, ReviewerForbiddenRoots(c, store, work), work);
        var settingsPath = Path.Combine(evidence, name + ".settings.json");
        Fs.WriteAtomic(settingsPath, settings.ToJsonString(Json.Options));

        List<string> args =
        [
            "-p", "--output-format", "stream-json", "--verbose",
            "--model", reviewer.Model, "--effort", reviewer.Effort,
            "--setting-sources", "project,local", "--strict-mcp-config",
            "--settings", settingsPath, "--permission-mode", "default",
            "--no-session-persistence",
            "--max-budget-usd", a.MaxBudgetUsdPerCall.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--allowedTools", .. ReviewerAllowedTools,
            "--disallowedTools", .. ReviewerDisallowedTools,
        ];
        foreach (var d in addDirs) args.AddRange(["--add-dir", d]);
        ClaudeLaunch.AssertNoForbidden(args);
        Fs.WriteAtomic(Path.Combine(evidence, name + ".args.json"), Json.Serialize(args));

        var resolved = ProcessRunner.ResolveOnPath(c.Agent.ClaudeExecutable) ?? throw new BenchmarkException($"Claude Code executable '{c.Agent.ClaudeExecutable}' not found on PATH.");
        var (file, prefix) = ProcessRunner.LaunchTarget(resolved);
        var result = await ProcessRunner.RunAsync(new ProcessSpec(file, [.. prefix, .. args], work)
        {
            Environment = new Dictionary<string, string?>
            {
                ["CLAUDE_CODE_EFFORT_LEVEL"] = null, ["ANTHROPIC_MODEL"] = null, ["DISABLE_AUTOUPDATER"] = "1",
            },
            Timeout = TimeSpan.FromMinutes(a.TimeoutMinutes),
            StdinText = prompt,
            StdoutPath = Path.Combine(evidence, name + ".stdout.jsonl"),
            StderrPath = Path.Combine(evidence, name + ".stderr.txt"),
        }, ct);
        return ReadOutcome(result);
    }

    private static ClaudeOutcome ReadOutcome(ProcessResult result)
    {
        JsonNode? final = null;
        foreach (var line in result.Stdout.Split('\n'))
            if (Json.TryParseNode(line) is { } node && node["type"]?.ToString() == "result") final = node;
        var cost = final?["total_cost_usd"] is { } c ? (decimal?)c.GetValue<decimal>() : null;
        var denials = final?["permission_denials"]?.AsArray().Count ?? 0;
        if (result.LaunchError is not null) return new(null, result.LaunchError, cost, denials, result.DurationSeconds);
        if (result.TimedOut) return new(null, "timed out", cost, denials, result.DurationSeconds);
        if (final is null) return new(null, $"no result event (exit {result.ExitCode}): {result.Stderr.Trim()}", cost, denials, result.DurationSeconds);
        if (final["is_error"]?.GetValue<bool>() == true) return new(null, $"CLI reported error: {final["subtype"]} {final["result"]}", cost, denials, result.DurationSeconds);
        var text = final["structured_output"]?.ToJsonString() ?? final["result"]?.ToString();
        return new(text, string.IsNullOrWhiteSpace(text) ? "empty result" : null, cost, denials, result.DurationSeconds);
    }

    /// <summary>Deny everything in the data root except the base source tree and this call's own work dir; deny the harness.</summary>
    public static List<string> ReviewerForbiddenRoots(ExperimentConfig c, RunStore store, string work)
    {
        var roots = new List<string> { c.HarnessRoot };
        var workRoot = WorkRoot(store);
        if (Directory.Exists(store.DataRoot))
            roots.AddRange(Directory.GetDirectories(store.DataRoot)
                .Where(d => !Path.GetFullPath(d).Equals(Path.GetFullPath(workRoot), StringComparison.OrdinalIgnoreCase)
                         && !Path.GetFullPath(d).Equals(Path.GetFullPath(store.BaseRoot), StringComparison.OrdinalIgnoreCase)));
        if (Directory.Exists(workRoot))
            foreach (var set in Directory.GetDirectories(workRoot))
                foreach (var other in Directory.GetDirectories(set))
                    if (!Path.GetFullPath(other).Equals(Path.GetFullPath(work), StringComparison.OrdinalIgnoreCase)) roots.Add(other);
        return roots;
    }

    /// <summary>
    /// Counts `file.ext:N` citations in the evidence and those whose line N is past the end of the matching file in
    /// after/ (matched by path suffix). Out-of-range lines usually mean the reviewer cited patch lines.
    /// </summary>
    public static (int Cited, int OutOfRange) CheckCitations(CodeReviewOfChange review, string afterDir)
    {
        if (!Directory.Exists(afterDir)) return (0, 0);
        var files = Directory.EnumerateFiles(afterDir, "*", SearchOption.AllDirectories)
            .Select(f => (Rel: Fs.RelativeUnix(afterDir, f), Lines: File.ReadLines(f).Count())).ToList();
        int cited = 0, outOfRange = 0;
        foreach (var text in review.Evidence.Values)
            foreach (Match m in Regex.Matches(text, @"([\w./-]+\.[A-Za-z]+):(\d+)"))
            {
                var name = m.Groups[1].Value.TrimStart('.', '/');
                var match = files.Where(f => f.Rel.EndsWith(name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (match.Count != 1) continue;
                cited++;
                if (int.Parse(m.Groups[2].Value) > match[0].Lines) outOfRange++;
            }
        return (cited, outOfRange);
    }

    // ---------------------------------------------------------------- parsing

    public sealed record CodeReviewOutput(Dictionary<string, CodeReviewOfChange> Changes, string Comparison);

    public static JsonObject? ExtractJson(string text)
    {
        var candidates = new List<string> { text.Trim() };
        foreach (Match m in Regex.Matches(text, @"```(?:json)?\s*\n(.*?)\n```", RegexOptions.Singleline)) candidates.Add(m.Groups[1].Value);
        var first = text.IndexOf('{');
        var last = text.LastIndexOf('}');
        if (first >= 0 && last > first) candidates.Add(text[first..(last + 1)]);
        foreach (var candidate in candidates)
        {
            try { if (JsonNode.Parse(candidate) is JsonObject o) return o; }
            catch (System.Text.Json.JsonException) { }
        }
        return null;
    }

    public static (CodeReviewOutput? Value, string? Error) ParseCodeReview(string text)
    {
        var o = ExtractJson(text);
        if (o is null) return (null, "no JSON object found");
        var changes = new Dictionary<string, CodeReviewOfChange>();
        foreach (var label in new[] { "A", "B" })
        {
            if (o[label] is not JsonObject change) return (null, $"missing {label}");
            CodeReviewOfChange review;
            try { review = Json.Deserialize<CodeReviewOfChange>(change.ToJsonString()); }
            catch (System.Text.Json.JsonException ex) { return (null, $"{label}: {ex.Message}"); }
            foreach (var d in Rubric.Dimensions)
                if (!review.Scores.TryGetValue(d, out var s) || s is < 1 or > 5) return (null, $"{label}: score for '{d}' missing or not 1-5");
            var extra = review.Scores.Keys.Except(Rubric.Dimensions).ToList();
            if (extra.Count > 0) return (null, $"{label}: unknown dimension(s) {string.Join(", ", extra)}");
            changes[label] = review;
        }
        return (new CodeReviewOutput(changes, o["comparison"]?.ToString() ?? ""), null);
    }

    public static (ProcessReviewOutput? Value, string? Error) ParseProcessReview(string text)
    {
        var o = ExtractJson(text);
        if (o is null) return (null, "no JSON object found");
        try
        {
            var value = Json.Deserialize<ProcessReviewOutput>(o.ToJsonString());
            return o["correctivePrompts"] is JsonArray && o["toolingInterventions"] is JsonArray ? (value, null) : (null, "missing correctivePrompts/toolingInterventions");
        }
        catch (System.Text.Json.JsonException ex) { return (null, ex.Message); }
    }

    // ---------------------------------------------------------------- aggregation

    public static double? Mean(IEnumerable<int> values) { var l = values.ToList(); return l.Count == 0 ? null : l.Average(); }

    public static List<string> ComputeFlags(AiReviewSummary s)
    {
        var flags = new List<string>();
        if (!s.ProtocolApproved) flags.Add("DRAFT protocol: the review prompts/filters are not owner-approved (aiReview.status/approvedProtocolSha256).");
        foreach (var call in s.Calls.Where(c => !c.Succeeded)) flags.Add($"Call {call.Name} failed after {call.Attempts} attempt(s): {call.Error}");
        foreach (var call in s.Calls.Where(c => c.Succeeded && c.Attempts > 1)) flags.Add($"Call {call.Name} needed a retry (first reply unusable).");
        foreach (var r in s.Runs)
        {
            foreach (var (reviewer, passes) in r.Code)
                foreach (var p in passes.Where(p => p.CitationsOutOfRange > 0))
                    flags.Add($"CITATIONS: {reviewer} pass {p.Pass} on {r.WorkflowId}: {p.CitationsOutOfRange} of {p.Citations} line citations point past the end of the file (probably patch lines); check that evidence by hand.");
            foreach (var (reviewer, passes) in r.Code)
                foreach (var d in Rubric.Dimensions)
                {
                    var scores = passes.Select(p => p.Review.Scores[d]).ToList();
                    if (scores.Count > 1 && scores.Max() - scores.Min() >= 2)
                        flags.Add($"ORDER-SENSITIVE: {reviewer} scored {r.WorkflowId} `{d}` {string.Join("/", scores)} across passes.");
                }
            if (r.Code.Count > 1)
                foreach (var d in Rubric.Dimensions)
                {
                    var means = r.Code.ToDictionary(kv => kv.Key, kv => Mean(kv.Value.Select(p => p.Review.Scores[d])) ?? 0);
                    if (means.Values.Max() - means.Values.Min() >= 1.5)
                        flags.Add($"REVIEWERS DISAGREE: {r.WorkflowId} `{d}`: {string.Join(", ", means.Select(kv => $"{kv.Key} {kv.Value:0.#}"))}.");
                }
            if (r.LeakCounts.Count > 0)
                flags.Add($"Blinding leak: the {r.WorkflowId} bundle contains {string.Join(", ", r.LeakCounts.Select(kv => $"'{kv.Key}'×{kv.Value}"))}.");
            foreach (var (reviewer, passes) in r.Code)
            {
                var clues = passes.SelectMany(p => p.Review.BlindingClues).Distinct().ToList();
                if (clues.Count > 0) flags.Add($"Blinding: {reviewer} noticed process clues in {r.WorkflowId}: {string.Join("; ", clues.Take(3))}");
            }
            if (r.Process.Count > 1)
            {
                var corrective = r.Process.ToDictionary(kv => kv.Key, kv => string.Join(",", kv.Value.CorrectivePrompts.OrderBy(x => x)));
                if (corrective.Values.Distinct().Count() > 1)
                    flags.Add($"REVIEWERS DISAGREE on corrective prompts for {r.WorkflowId}: {string.Join("; ", corrective.Select(kv => $"{kv.Key} [{kv.Value}]"))}.");
            }
        }
        return flags;
    }

    public static string SummaryMarkdown(AiReviewSummary s)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# AI engineering review {s.SetId}\n");
        sb.AppendLine($"Protocol `{s.ProtocolSha256[..12]}` ({(s.ProtocolApproved ? "owner-approved" : "**DRAFT**")}) · reviewers {string.Join(", ", s.Reviewers)} · {s.Passes} blinded pass(es) each · cost ${s.TotalCostUsd:0.00}\n");
        sb.AppendLine("Scores are listed per pass (pass 1 / pass 2 ...). The change shown first as \"A\" alternates between passes.\n");
        sb.AppendLine("## Scores\n");
        sb.AppendLine("| Dimension | " + string.Join(" | ", s.Runs.Select(r => r.WorkflowId)) + " |");
        sb.AppendLine("|---|" + string.Concat(s.Runs.Select(_ => "---|")));
        foreach (var d in Rubric.Dimensions)
            sb.AppendLine($"| {d} | " + string.Join(" | ", s.Runs.Select(r => ScoreCell(r, d))) + " |");
        sb.AppendLine();
        if (s.Flags.Count > 0) { sb.AppendLine("## Flags\n"); foreach (var f in s.Flags) sb.AppendLine("- " + f); sb.AppendLine(); }
        foreach (var r in s.Runs)
        {
            sb.AppendLine($"## {r.WorkflowId} `{r.RunId}`\n");
            foreach (var (reviewer, passes) in r.Code)
                foreach (var p in passes)
                {
                    sb.AppendLine($"### {reviewer}, pass {p.Pass} (shown as {p.Label})\n");
                    foreach (var d in Rubric.Dimensions) sb.AppendLine($"- **{d}: {p.Review.Scores[d]}**. {p.Review.Evidence.GetValueOrDefault(d)}");
                    if (p.Review.RisksMissed.Count > 0) sb.AppendLine("- Risks missed: " + string.Join("; ", p.Review.RisksMissed));
                    if (p.Review.IncorrectAssumptions.Count > 0) sb.AppendLine("- Incorrect assumptions: " + string.Join("; ", p.Review.IncorrectAssumptions));
                    sb.AppendLine("- Existing test failures: " + p.Review.RegressionAssessment);
                    sb.AppendLine();
                }
            foreach (var (reviewer, pr) in r.Process)
            {
                sb.AppendLine($"### Process review, {reviewer}\n");
                sb.AppendLine($"- Corrective prompts: {Hs(pr.CorrectivePrompts)}; tooling interventions: {Hs(pr.ToolingInterventions)}; workflow steps: {Hs(pr.WorkflowSteps)}; business answers: {Hs(pr.BusinessAnswers)}; other: {Hs(pr.Other)}");
                if (pr.VerificationOmissions.Count > 0) sb.AppendLine("- Verification omissions: " + string.Join("; ", pr.VerificationOmissions));
                if (pr.IncorrectAssumptions.Count > 0) sb.AppendLine("- Incorrect assumptions: " + string.Join("; ", pr.IncorrectAssumptions));
                if (pr.Notes.Length > 0) sb.AppendLine("- Notes: " + pr.Notes);
                sb.AppendLine();
            }
        }
        var comparisons = s.Runs.SelectMany(r => r.Code.SelectMany(kv => kv.Value.Where(p => p.Label == "A").Select(p => (Reviewer: kv.Key, p.Pass, p.Comparison, First: r.WorkflowId)))).OrderBy(x => x.Reviewer).ThenBy(x => x.Pass);
        sb.AppendLine("## Reviewer comparisons\n");
        foreach (var x in comparisons) sb.AppendLine($"- **{x.Reviewer}, pass {x.Pass}** (A = {x.First}): {x.Comparison}");
        sb.AppendLine();
        sb.AppendLine("## Calls\n");
        foreach (var call in s.Calls) sb.AppendLine($"- {call.Name}: {(call.Succeeded ? "ok" : "FAILED")}, attempts {call.Attempts}, ${call.CostUsd ?? 0:0.00}, {call.DurationSeconds / 60:0.0} min, permission denials {call.PermissionDenials}{(call.Error is null ? "" : ", " + call.Error)}");
        return sb.ToString();
    }

    public static string ScoreCell(AiRunSummary r, string dimension) =>
        r.Code.Count == 0 ? "–" : string.Join(" · ", r.Code.Select(kv => $"{ShortModel(kv.Key)} {string.Join("/", kv.Value.OrderBy(p => p.Pass).Select(p => p.Review.Scores[dimension]))}"));

    public static string ShortModel(string model) => model.Replace("claude-", "", StringComparison.Ordinal);

    private static string Hs(List<int> h) => h.Count == 0 ? "none" : string.Join(", ", h.OrderBy(x => x).Select(x => "H" + x));

    /// <summary>The most recent summary that covers the given run, if any.</summary>
    public static AiReviewSummary? LatestFor(RunStore store, string runId)
    {
        var root = ReviewsRoot(store);
        if (!Directory.Exists(root)) return null;
        foreach (var dir in Directory.GetDirectories(root).OrderByDescending(d => d, StringComparer.Ordinal))
        {
            var path = Path.Combine(dir, "summary.json");
            if (!File.Exists(path)) continue;
            var s = Json.Read<AiReviewSummary>(path);
            if (s.Runs.Any(r => r.RunId == runId)) return s;
        }
        return null;
    }
}
