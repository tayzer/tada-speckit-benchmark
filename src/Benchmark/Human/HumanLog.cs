using Benchmark.Util;

namespace Benchmark.Human;

public enum InteractionKind
{
    /// <summary>The agent asked a business/requirements question (productive discovery).</summary>
    ClarificationQuestion,
    /// <summary>The operator answered a business question (ideally from the decisions register).</summary>
    ClarificationAnswer,
    /// <summary>Operator reviewed a spec/acceptance artifact (Spec Kit spec/plan, TADA Gherkin, baseline review).</summary>
    SpecReview,
    /// <summary>Operator approved something the workflow asked for (plan, baseline, permission).</summary>
    Approval,
    /// <summary>Operator rejected/declined a request.</summary>
    Rejection,
    /// <summary>Operator had to correct an agent mistake (wrong assumption, wrong file, broken build...).</summary>
    CorrectivePrompt,
    /// <summary>Operator had to fix tooling/environment (install, path, permissions, crashed process).</summary>
    ToolingIntervention,
    /// <summary>Operator issued a workflow-driving command (e.g. /speckit-plan) required by the documented process.</summary>
    WorkflowCommand,
    Note,
    TimerStart,
    TimerStop,
}

public enum ActivityKind { Review, Answer, Troubleshoot, Approve, Other }

public sealed class HumanEvent
{
    public DateTimeOffset Timestamp { get; set; }
    public InteractionKind Kind { get; set; }
    public ActivityKind? Activity { get; set; }
    public string? DecisionId { get; set; }
    public string? Note { get; set; }
    /// <summary>"operator" for CLI-recorded events, "harness" for automatically recorded ones.</summary>
    public string Source { get; set; } = "operator";
    /// <summary>Optional manually supplied duration when a timer was not running.</summary>
    public double? ManualSeconds { get; set; }
}

public sealed class HumanEffortSummary
{
    public Dictionary<string, int> EventCounts { get; set; } = new();
    public int ProductiveClarifications { get; set; }
    public int CorrectiveInterventions { get; set; }
    public int ToolingInterventions { get; set; }
    public int ApprovalDecisions { get; set; }
    public int Reviews { get; set; }
    public int WorkflowCommands { get; set; }
    public Dictionary<string, double> ActiveSecondsByActivity { get; set; } = new();
    /// <summary>Union of all timer intervals (overlapping timers are not double-counted) plus manual durations.</summary>
    public double TotalActiveSeconds { get; set; }
    public List<string> Warnings { get; set; } = [];
    public List<string> DecisionsUsed { get; set; } = [];
}

/// <summary>Append-only JSONL log of human involvement for one run.</summary>
public sealed class HumanLog(string path)
{
    public string Path { get; } = path;

    public void Append(HumanEvent e) => Fs.AppendLine(Path, System.Text.Json.JsonSerializer.Serialize(e, Json.Options).ReplaceLineEndings(" "));

    public IReadOnlyList<HumanEvent> Read()
    {
        if (!File.Exists(Path)) return [];
        return File.ReadAllLines(Path).Where(l => !string.IsNullOrWhiteSpace(l)).Select(Json.Deserialize<HumanEvent>).ToList();
    }

    public IReadOnlyList<ActivityKind> OpenTimers()
    {
        var open = new List<ActivityKind>();
        foreach (var e in Read().OrderBy(e => e.Timestamp))
        {
            if (e.Kind == InteractionKind.TimerStart && e.Activity is { } a) open.Add(a);
            if (e.Kind == InteractionKind.TimerStop && e.Activity is { } b) open.Remove(b);
        }
        return open;
    }

    public static HumanEffortSummary Summarise(IEnumerable<HumanEvent> events, DateTimeOffset? runEnd = null)
    {
        var ordered = events.OrderBy(e => e.Timestamp).ToList();
        var s = new HumanEffortSummary();
        foreach (var g in ordered.Where(e => e.Kind is not (InteractionKind.TimerStart or InteractionKind.TimerStop)).GroupBy(e => e.Kind))
            s.EventCounts[KebabName(g.Key)] = g.Count();
        s.ProductiveClarifications = ordered.Count(e => e.Kind == InteractionKind.ClarificationQuestion);
        s.CorrectiveInterventions = ordered.Count(e => e.Kind == InteractionKind.CorrectivePrompt);
        s.ToolingInterventions = ordered.Count(e => e.Kind == InteractionKind.ToolingIntervention);
        s.ApprovalDecisions = ordered.Count(e => e.Kind is InteractionKind.Approval or InteractionKind.Rejection);
        s.Reviews = ordered.Count(e => e.Kind == InteractionKind.SpecReview);
        s.WorkflowCommands = ordered.Count(e => e.Kind == InteractionKind.WorkflowCommand);
        s.DecisionsUsed = ordered.Select(e => e.DecisionId).OfType<string>().Distinct().OrderBy(d => d).ToList();

        var intervals = new List<(DateTimeOffset Start, DateTimeOffset End, ActivityKind Activity)>();
        var open = new Dictionary<ActivityKind, DateTimeOffset>();
        foreach (var e in ordered)
        {
            if (e.Kind == InteractionKind.TimerStart && e.Activity is { } a)
            {
                if (open.ContainsKey(a)) s.Warnings.Add($"Timer '{KebabName(a)}' started twice without stop at {e.Timestamp:O}; earlier start kept.");
                else open[a] = e.Timestamp;
            }
            else if (e.Kind == InteractionKind.TimerStop && e.Activity is { } b)
            {
                if (open.Remove(b, out var start)) intervals.Add((start, e.Timestamp, b));
                else s.Warnings.Add($"Timer '{KebabName(b)}' stopped at {e.Timestamp:O} without a start; ignored.");
            }
        }
        foreach (var (activity, start) in open)
        {
            if (runEnd is { } end && end > start)
            {
                intervals.Add((start, end, activity));
                s.Warnings.Add($"Timer '{KebabName(activity)}' was never stopped; closed at run end ({end:O}). Verify this is accurate.");
            }
            else s.Warnings.Add($"Timer '{KebabName(activity)}' was never stopped and has no run end; excluded.");
        }

        foreach (var g in intervals.GroupBy(i => i.Activity))
            s.ActiveSecondsByActivity[KebabName(g.Key)] = UnionSeconds(g.Select(i => (i.Start, i.End)));
        var manual = ordered.Where(e => e.ManualSeconds is > 0).ToList();
        foreach (var m in manual)
        {
            var key = KebabName(m.Activity ?? ActivityKind.Other);
            s.ActiveSecondsByActivity[key] = s.ActiveSecondsByActivity.GetValueOrDefault(key) + m.ManualSeconds!.Value;
        }
        s.TotalActiveSeconds = UnionSeconds(intervals.Select(i => (i.Start, i.End))) + manual.Sum(m => m.ManualSeconds!.Value);
        return s;
    }

    public static double UnionSeconds(IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> intervals)
    {
        double total = 0;
        DateTimeOffset? curStart = null, curEnd = null;
        foreach (var (start, end) in intervals.Where(i => i.End > i.Start).OrderBy(i => i.Start))
        {
            if (curEnd is null || start > curEnd) { if (curEnd is not null) total += (curEnd.Value - curStart!.Value).TotalSeconds; curStart = start; curEnd = end; }
            else if (end > curEnd) curEnd = end;
        }
        if (curEnd is not null) total += (curEnd.Value - curStart!.Value).TotalSeconds;
        return total;
    }

    public static string KebabName<T>(T value) where T : struct, Enum =>
        System.Text.Json.JsonNamingPolicy.KebabCaseLower.ConvertName(value.ToString());
}
