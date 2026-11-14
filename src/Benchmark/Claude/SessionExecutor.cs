using System.Text.Json.Nodes;
using Benchmark.Config;
using Benchmark.Human;
using Benchmark.Runs;
using Benchmark.Util;
using Benchmark.Workspace;

namespace Benchmark.Claude;

/// <summary>Launches Claude Code for a prepared run in interactive (Mode A) or managed (Mode B) form.</summary>
public static class SessionExecutor
{
    public static async Task<InvocationRecord> StartInteractiveAsync(ExperimentConfig c, RunStore store, RunRecord run, bool resume, CancellationToken ct)
    {
        Guard(store, run, resume, ExecutionMode.Interactive);
        var args = BaseArgs(run, resume);
        if (!resume) args.Add(run.InitialMessage);
        var invocation = NewInvocation(run, ExecutionMode.Interactive, resume, resume ? null : run.InitialMessage, resume ? null : "initial-task");
        invocation.Arguments = Sanitise(args, run);
        run.Mode = ExecutionMode.Interactive;
        run.Status = RunStatus.Running;
        store.Save(run);

        Console.WriteLine($"Launching interactive Claude Code session {run.SessionId} in {run.WorkspacePath}");
        var (file, prefix) = ResolveClaude(c);
        var result = await ProcessRunner.RunInteractiveAsync(new ProcessSpec(file, [.. prefix, .. args], run.WorkspacePath)
        {
            Environment = ClaudeLaunch.AgentEnvironment(c.Agent, WorkspaceManager.NugetDir(store, run.RunId)),
        }, ct);
        Complete(invocation, result);
        run.Invocations.Add(invocation);
        store.Save(run);
        return invocation;
    }

    public static async Task<InvocationRecord> RunManagedAsync(ExperimentConfig c, RunStore store, RunRecord run, string message, string messageKind, bool resume, CancellationToken ct)
    {
        Guard(store, run, resume, ExecutionMode.Managed);
        var args = BaseArgs(run, resume);
        args.AddRange(ClaudeLaunch.ManagedArgs(c.Agent));
        ClaudeLaunch.AssertNoForbidden(args);
        var invocation = NewInvocation(run, ExecutionMode.Managed, resume, message, messageKind);
        invocation.Arguments = Sanitise(args, run);
        invocation.StdoutFile = $"invocations/{invocation.InvocationId}.stdout.jsonl";
        invocation.StderrFile = $"invocations/{invocation.InvocationId}.stderr.txt";
        store.WriteEvidence(run.RunId, $"invocations/{invocation.InvocationId}.message.txt", message);
        run.Mode = ExecutionMode.Managed;
        run.Status = RunStatus.Running;
        store.Save(run);

        var (file, prefix) = ResolveClaude(c);
        var result = await ProcessRunner.RunAsync(new ProcessSpec(file, [.. prefix, .. args], run.WorkspacePath)
        {
            Environment = ClaudeLaunch.AgentEnvironment(c.Agent, WorkspaceManager.NugetDir(store, run.RunId)),
            Timeout = TimeSpan.FromMinutes(c.Agent.ManagedInvocationTimeoutMinutes),
            StdoutPath = store.EvidencePath(run.RunId, invocation.StdoutFile),
            StderrPath = store.EvidencePath(run.RunId, invocation.StderrFile),
            StdinText = message,
            OnStdoutLine = Progress,
        }, ct);
        Complete(invocation, result);
        if (c.Evidence.RedactSecrets)
        {
            foreach (var f in new[] { invocation.StdoutFile, invocation.StderrFile })
            {
                var p = store.EvidencePath(run.RunId, f);
                if (File.Exists(p)) Redactor.RedactFile(p, p, redact: true);
            }
        }
        run.Invocations.Add(invocation);
        store.Save(run);
        Console.WriteLine($"Invocation {invocation.InvocationId} ended: exit={invocation.ExitCode} timedOut={invocation.TimedOut} cancelled={invocation.Cancelled} ({invocation.DurationSeconds:0}s)");
        return invocation;
    }

    private static void Guard(RunStore store, RunRecord run, bool resume, ExecutionMode mode)
    {
        store.EnsureWritable(run.RunId);
        if (run.Status is RunStatus.Finished or RunStatus.Sealed) throw new BenchmarkException("Run already finished; create a new run.");
        if (!resume && run.Invocations.Count > 0) throw new BenchmarkException("Session already started; use --resume / `benchmark continue`.");
        if (resume && run.Invocations.Count == 0) throw new BenchmarkException("Nothing to resume; start the run first.");
        if (run.Mode is { } existing && existing != mode) throw new BenchmarkException($"Run was started in {existing} mode; modes cannot be mixed within one run.");
        if (run.Setup.ActivationFindings.Count > 0)
            Console.Error.WriteLine("WARNING: static activation findings exist: " + string.Join("; ", run.Setup.ActivationFindings));
    }

    private static List<string> BaseArgs(RunRecord run, bool resume) =>
        [.. run.Agent.CommonArgs, .. run.Agent.WorkflowArgs, .. ClaudeLaunch.SessionArgs(run.SessionId, resume)];

    private static InvocationRecord NewInvocation(RunRecord run, ExecutionMode mode, bool resume, string? message, string? kind) => new()
    {
        InvocationId = (run.Invocations.Count + 1).ToString("000"),
        Mode = mode,
        IsResume = resume,
        StartedAt = DateTimeOffset.UtcNow,
        MessageSha256 = message is null ? null : Fs.Sha256Text(message),
        MessageKind = kind,
    };

    private static void Complete(InvocationRecord inv, ProcessResult r)
    {
        inv.StartedAt = r.StartedAt;
        inv.EndedAt = r.EndedAt;
        inv.ExitCode = r.ExitCode;
        inv.TimedOut = r.TimedOut;
        inv.Cancelled = r.Cancelled;
        inv.LaunchError = r.LaunchError;
    }

    private static List<string> Sanitise(IEnumerable<string> args, RunRecord run) =>
        args.Select(a => a == run.InitialMessage ? $"<initial-message sha256={run.InitialMessageSha256}>" : a).ToList();

    private static (string File, List<string> Prefix) ResolveClaude(ExperimentConfig c)
    {
        var resolved = ProcessRunner.ResolveOnPath(c.Agent.ClaudeExecutable) ?? throw new BenchmarkException($"Claude Code executable '{c.Agent.ClaudeExecutable}' not found on PATH.");
        return ProcessRunner.LaunchTarget(resolved);
    }

    private static void Progress(string line)
    {
        if (Json.TryParseNode(line) is not JsonObject o) return;
        var type = o["type"]?.ToString();
        if (type == "assistant" && o["message"]?["content"] is JsonArray content)
        {
            foreach (var block in content.OfType<JsonObject>())
            {
                var t = block["type"]?.ToString();
                if (t == "tool_use") Console.WriteLine($"  [tool] {block["name"]}");
                else if (t == "text" && block["text"]?.ToString() is { Length: > 0 } text)
                    Console.WriteLine("  [assistant] " + (text.Length > 160 ? text[..160].ReplaceLineEndings(" ") + "…" : text.ReplaceLineEndings(" ")));
            }
        }
        else if (type == "result") Console.WriteLine($"  [result] {o["subtype"]} turns={o["num_turns"]} cost=${o["total_cost_usd"]}");
    }

    /// <summary>Builds the follow-up message for a managed continuation and logs the human event.</summary>
    public static (string Message, InteractionKind Kind) FollowUp(DecisionRegister register, HumanLog log, string? message, string? decisionId, InteractionKind kind)
    {
        string text;
        if (decisionId is not null)
        {
            var d = register.Get(decisionId);
            if (d.Answer is null) throw new BenchmarkException($"Decision {d.Id} has no answer yet; the benchmark owner must decide it first.");
            text = message is null ? d.Answer : message + "\n\n" + d.Answer;
            kind = InteractionKind.ClarificationAnswer;
        }
        else text = message ?? throw new BenchmarkException("Provide --message, --message-file or --decision.");
        log.Append(new HumanEvent { Timestamp = DateTimeOffset.UtcNow, Kind = kind, DecisionId = decisionId, Note = Fs.Sha256Text(text)[..12] + " (message sha256 prefix)", Source = "harness" });
        return (text, kind);
    }
}
