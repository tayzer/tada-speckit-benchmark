using Benchmark.Claude;
using Benchmark.Config;
using Benchmark.Telemetry;
using Benchmark.Util;
using Benchmark.Workspace;

namespace Benchmark.Runs;

public sealed class ChangedFile
{
    public string Path { get; set; } = "";
    public string Status { get; set; } = "";
    public int Added { get; set; }
    public int Removed { get; set; }
    public bool IsTest { get; set; }
    public bool IsWorkflowArtifact { get; set; }
}

public sealed class ChangeSummary
{
    public string FromCommit { get; set; } = "";
    public string ToCommit { get; set; } = "";
    public List<ChangedFile> Files { get; set; } = [];
    public int ProductionFilesChanged => Files.Count(f => !f.IsTest && !f.IsWorkflowArtifact);
    public int TestFilesChanged => Files.Count(f => f.IsTest);
    public int WorkflowArtifactFiles => Files.Count(f => f.IsWorkflowArtifact);
    public int ExistingTestFilesModifiedOrDeleted => Files.Count(f => f.IsTest && f.Status is "M" or "D");
    public int LinesAdded => Files.Sum(f => f.Added);
    public int LinesRemoved => Files.Sum(f => f.Removed);
}

public sealed class TelemetryRecord
{
    public string Source { get; set; } = "";
    public UsageSummary Usage { get; set; } = new();
    public ActivitySummary Activity { get; set; } = new();
    public List<string> DynamicActivationFindings { get; set; } = [];
    public string DynamicActivationStatus { get; set; } = "";
    public List<Telemetry.InitRecord> InitEvents { get; set; } = [];
    public List<string> Unavailable { get; set; } = [];
    public string? TranscriptMain { get; set; }
    public int TranscriptSubagentFiles { get; set; }
    public double AgentExecutionSeconds { get; set; }
    public string AgentExecutionBasis { get; set; } = "";
    public double SessionWallClockSeconds { get; set; }
    public InteractionSummary? Interaction { get; set; }
    public string? ChatExport { get; set; }
}

public static class RunFinisher
{
    private static readonly string[] WorkflowArtifactPrefixes = [".specify/", "specs/", ".claude/", ".tada/", "features/", "tada.json", ".config/dotnet-tools.json"];

    public static async Task<TelemetryRecord> FinishAsync(ExperimentConfig c, RunStore store, RunRecord run, PricingTable? pricing, bool seal, Action<string> log)
    {
        store.EnsureWritable(run.RunId);
        if (run.Invocations.Count == 0) throw new BenchmarkException("Run has no invocations; nothing to finish.");
        var w = c.Workflow(run.WorkflowId);

        // 1. Final code state (no modification of the workspace's HEAD, index or files).
        log("Snapshotting final workspace state ...");
        run.FinalCommit = await Git.SnapshotWorkingTreeAsync(run.WorkspacePath, run.SetupCommit, $"benchmark: final state of run {run.RunId}");
        run.FinalBranch = $"benchmark/final-{run.RunId}";
        await Git.RunAsync(run.WorkspacePath, "update-ref", $"refs/heads/{run.FinalBranch}", run.FinalCommit);
        var patch = await Git.RunAsync(run.WorkspacePath, "diff", "--binary", run.SetupCommit, run.FinalCommit);
        store.WriteEvidence(run.RunId, "changes/final.patch", patch + "\n");
        var changes = await ChangesAsync(run.WorkspacePath, run.SetupCommit, run.FinalCommit);
        store.WriteEvidence(run.RunId, "changes/changes.json", Json.Serialize(changes));
        var setupPatch = await Git.RunAsync(run.WorkspacePath, "diff", "--stat", run.BaseCommit, run.SetupCommit);
        store.WriteEvidence(run.RunId, "changes/workflow-setup.stat.txt", setupPatch + "\n");

        // 2. Telemetry.
        var telemetry = new TelemetryRecord();
        var forbidden = WorkspaceManager.ForbiddenRoots(c, store, run.RunId);
        var (mainTranscript, subTranscripts) = ClaudeLaunch.TranscriptPaths(run.WorkspacePath, run.SessionId);
        ParsedSession? transcript = null;
        if (File.Exists(mainTranscript))
        {
            log($"Importing transcript {mainTranscript} (+{subTranscripts.Count} subagent files)");
            Redactor.RedactFile(mainTranscript, store.EvidencePath(run.RunId, "transcripts", "session.jsonl"), c.Evidence.RedactSecrets);
            foreach (var s in subTranscripts)
                Redactor.RedactFile(s, store.EvidencePath(run.RunId, "transcripts", "subagents", Path.GetFileName(s)), c.Evidence.RedactSecrets);
            transcript = ClaudeEventParser.ParseTranscripts(mainTranscript, subTranscripts);
            telemetry.TranscriptMain = "transcripts/session.jsonl";
            telemetry.TranscriptSubagentFiles = subTranscripts.Count;
            telemetry.Interaction = ExportChat(c, store, run, subTranscripts.Select(Path.GetFileName).OfType<string>().ToList());
            telemetry.ChatExport = "chat.md";
        }
        else telemetry.Unavailable.Add($"Session transcript not found at {mainTranscript} (CLAUDE_CONFIG_DIR or project-dir encoding may differ, or the session never started).");

        if (run.Mode == ExecutionMode.Managed)
        {
            var captures = run.Invocations.Select(inv => new InvocationCapture
            {
                InvocationId = inv.InvocationId,
                IsResume = inv.IsResume,
                StartedAt = inv.StartedAt,
                ProcessSucceeded = inv.ExitCode == 0 && !inv.TimedOut && !inv.Cancelled,
                Parsed = File.Exists(store.EvidencePath(run.RunId, inv.StdoutFile!))
                    ? ClaudeEventParser.ParseStreamJson(File.ReadLines(store.EvidencePath(run.RunId, inv.StdoutFile!)))
                    : new ParsedSession(),
            }).ToList();
            telemetry.Source = "stream-json (managed -p invocations)";
            telemetry.Usage = UsageAggregator.FromInvocations(captures, c.Agent.Model, pricing);
            var merged = Merge(captures.Select(x => x.Parsed));
            telemetry.Activity = ActivityAnalyzer.Analyze(merged, forbidden);
            telemetry.InitEvents = merged.Inits;
            if (transcript is not null)
            {
                // Effort is only observable in the transcript on this CLI version.
                telemetry.Usage.ObservedEfforts = telemetry.Usage.ObservedEfforts
                    .Concat(transcript.Messages.Values.Select(m => m.Effort).OfType<string>()).Distinct().OrderBy(e => e).ToList();
            }
            telemetry.AgentExecutionSeconds = run.Invocations.Sum(i => i.DurationSeconds);
            telemetry.AgentExecutionBasis = "Sum of managed claude -p process durations.";
        }
        else
        {
            telemetry.Source = "transcript (interactive session; internal undocumented format)";
            if (transcript is not null)
            {
                telemetry.Usage = UsageAggregator.FromTranscript(transcript, c.Agent.Model, pricing);
                telemetry.Activity = ActivityAnalyzer.Analyze(transcript, forbidden);
                var cost = transcript.CostStates.LastOrDefault();
                if (cost?.TotalApiDurationMs is { } api)
                {
                    telemetry.AgentExecutionSeconds = (api + (cost.TotalToolDurationMs ?? 0)) / 1000.0;
                    telemetry.AgentExecutionBasis = "Transcript cost-state: total API duration + total tool duration (excludes time waiting for the human).";
                }
            }
            else telemetry.Usage = new UsageSummary { Warnings = ["No telemetry available for this interactive run."] };
            telemetry.Unavailable.Add("Interactive mode: the system/init event is not written to transcripts, so plugin/skill activation is verified statically and via `benchmark verify-activation`.");
            telemetry.Unavailable.Add("Interactive mode: permission prompts the operator approved are not recorded in transcripts (rejections are). Their wait time counts as tool time, not human time.");
        }
        telemetry.SessionWallClockSeconds = (run.Invocations.Max(i => i.EndedAt) - run.Invocations.Min(i => i.StartedAt)).TotalSeconds;
        if (telemetry.AgentExecutionBasis == "")
        {
            telemetry.AgentExecutionSeconds = (telemetry.Usage.ApiDurationMs ?? 0) / 1000.0;
            telemetry.AgentExecutionBasis = "API duration only (tool duration unavailable).";
        }

        // 3. Activation (dynamic, from init events when available).
        if (telemetry.InitEvents.Count > 0)
        {
            telemetry.DynamicActivationFindings = telemetry.InitEvents.SelectMany(i => WorkflowSetup.DynamicChecks(w, i)).Distinct().ToList();
            telemetry.DynamicActivationStatus = telemetry.DynamicActivationFindings.Count == 0 ? "verified" : "failed";
        }
        else
        {
            var probe = store.EvidencePath(run.RunId, "activation-probe.json");
            telemetry.DynamicActivationStatus = File.Exists(probe) ? "verified-by-probe" : "not-observable";
        }
        if (telemetry.Activity.IsolationViolations.Count > 0)
            run.Deviations.Add($"ISOLATION: {telemetry.Activity.IsolationViolations.Count} tool call(s) referenced forbidden paths (see telemetry.json).");
        if (!telemetry.Usage.Complete) run.Deviations.Add("Telemetry incomplete (see telemetry warnings).");
        if (telemetry.Usage.Warnings.Any(x => x.StartsWith("MODEL_MISMATCH", StringComparison.Ordinal))) run.Deviations.Add("Model mismatch on main thread: run potentially incomparable.");

        store.WriteEvidence(run.RunId, "telemetry.json", Json.Serialize(telemetry));
        run.Status = RunStatus.Finished;
        run.FinishedAt = DateTimeOffset.UtcNow;
        store.Save(run);
        if (seal)
        {
            store.Seal(run);
            log($"Run {run.RunId} sealed. Evidence is now immutable.");
        }
        return telemetry;
    }

    /// <summary>Writes chat.md from the redacted transcript copies and returns the automatically derived interaction summary.</summary>
    private static InteractionSummary ExportChat(ExperimentConfig c, RunStore store, RunRecord run, IReadOnlyList<string> subagentFiles)
    {
        var main = Conversation.Read(File.ReadLines(store.EvidencePath(run.RunId, "transcripts", "session.jsonl")));
        var decisionsPath = c.ResolveExperimentPath(c.DecisionsFile);
        var answers = File.Exists(decisionsPath)
            ? Human.DecisionRegister.Load(decisionsPath).Decisions.Where(d => d.Answer is not null).ToDictionary(d => d.Id, d => d.Answer!)
            : null;
        var summary = Conversation.Analyze(main, TimeSpan.FromMinutes(c.Evidence.HumanIdleCapMinutes), answers);
        var subs = subagentFiles
            .Select(f => (Name: Path.GetFileNameWithoutExtension(f), Entries: (IReadOnlyList<ConversationEntry>)Conversation.Read(File.ReadLines(store.EvidencePath(run.RunId, "transcripts", "subagents", f)))))
            .ToList();
        store.WriteEvidence(run.RunId, "chat.md", Conversation.RenderMarkdown($"Conversation: {run.WorkflowId} run {run.RunId}", main, summary, subs));
        return summary;
    }

    public static ParsedSession Merge(IEnumerable<ParsedSession> sessions)
    {
        var merged = new ParsedSession { SourceKind = "stream-json" };
        foreach (var s in sessions)
        {
            merged.Inits.AddRange(s.Inits);
            foreach (var (id, m) in s.Messages) merged.Messages.TryAdd(id, m);
            merged.ToolUses.AddRange(s.ToolUses.Where(t => merged.ToolUses.All(x => x.Id != t.Id || t.Id == "")));
            merged.Results.AddRange(s.Results);
            merged.HumanMessages.AddRange(s.HumanMessages);
            merged.SystemEvents.AddRange(s.SystemEvents);
            merged.SessionIds.UnionWith(s.SessionIds);
        }
        return merged;
    }

    public static async Task<ChangeSummary> ChangesAsync(string repo, string from, string to)
    {
        var summary = new ChangeSummary { FromCommit = from, ToCommit = to };
        var nameStatus = await Git.RunAsync(repo, "diff", "--name-status", "--no-renames", from, to);
        var numstat = await Git.RunAsync(repo, "diff", "--numstat", "--no-renames", from, to);
        var counts = numstat.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('\t')).Where(p => p.Length >= 3)
            .ToDictionary(p => p[2], p => (Added: int.TryParse(p[0], out var a) ? a : 0, Removed: int.TryParse(p[1], out var r) ? r : 0));
        foreach (var line in nameStatus.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 2) continue;
            var path = parts[^1];
            var c = counts.GetValueOrDefault(path);
            summary.Files.Add(new ChangedFile
            {
                Path = path, Status = parts[0][..1], Added = c.Added, Removed = c.Removed,
                IsTest = IsTestPath(path),
                IsWorkflowArtifact = WorkflowArtifactPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)),
            });
        }
        return summary;
    }

    public static bool IsTestPath(string path) =>
        path.StartsWith("tests/", StringComparison.OrdinalIgnoreCase) || path.Contains("Tests/", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".feature", StringComparison.OrdinalIgnoreCase);
}
