namespace CodeStack.Ratchet.Workflow;

public enum RunStatus { Running, Completed, Failed }

/// <summary>One recorded event in a run's trace (classification, skip, gate, consult, conflict…).</summary>
public sealed record RunEvent(string Kind, string Phase, string Detail);

/// <summary>
/// Run-level recording seam. The single highest-leverage judgment (classification)
/// and every gate/consult/escalation/conflict is emitted here so a bad skip is
/// *diffable after the fact* — you can see the skip was wrong, not the code. Distinct
/// from <c>IAgentObserver</c>, which is per-token inside a phase.
/// </summary>
public interface IWorkflowObserver
{
    void Classified(string workType, string reasoning);
    void PhaseStart(string phaseId, string driverTier, IReadOnlyList<string> skills, string loadPolicy);
    void Consult(string phaseId, int n, int max, string advice);
    void Gate(string phaseId, string kind, string outcome, string reason);
    void Escalation(string fromPhase, string toPhase, string reason);
    void Promotion(string phaseId, string fromTier, string toTier);
    void Conflict(string phaseId, string detail);
    void PhaseEnd(string phaseId, string summary);
    void RunEnd(RunStatus status, string reason);
}

/// <summary>
/// The recorded result of one workflow run: the classification (choice + reasoning),
/// the ordered event trace, and the final status. Implements <see cref="IWorkflowObserver"/>
/// so the scheduler simply records into it, and forwards to an optional echo sink
/// (e.g. the console) so live output and the durable trace stay in lockstep.
/// </summary>
public sealed class WorkflowRun : IWorkflowObserver
{
    private readonly IWorkflowObserver? _echo;

    public WorkflowRun(string runId, string task, IWorkflowObserver? echo = null)
    {
        RunId = runId;
        Task = task;
        _echo = echo;
    }

    public string RunId { get; }
    public string Task { get; }
    public string? WorkType { get; private set; }
    public string? ClassifierReasoning { get; private set; }
    public RunStatus Status { get; private set; } = RunStatus.Running;
    public string FailReason { get; private set; } = "";
    public List<RunEvent> Events { get; } = new();

    /// <summary>Per-model-tier token spend across the whole run (driver, classifier, judge, advisor, handover).</summary>
    public CostTally Cost { get; } = new();

    /// <summary>Rehydrate a run from a persisted snapshot (for resume) without re-echoing events.</summary>
    public void SeedFrom(string? workType, string? reasoning, IEnumerable<RunEvent> events, CostTally cost)
    {
        WorkType = workType;
        ClassifierReasoning = reasoning;
        Events.AddRange(events);
        Cost.Merge(cost);
    }

    private void Add(string kind, string phase, string detail) => Events.Add(new RunEvent(kind, phase, detail));

    public void Classified(string workType, string reasoning)
    {
        WorkType = workType; ClassifierReasoning = reasoning;
        Add("classified", "-", $"{workType}: {reasoning}");
        _echo?.Classified(workType, reasoning);
    }

    public void PhaseStart(string phaseId, string driverTier, IReadOnlyList<string> skills, string loadPolicy)
    {
        Add("phase_start", phaseId, $"driver={driverTier} skills=[{string.Join(", ", skills)}] load={loadPolicy}");
        _echo?.PhaseStart(phaseId, driverTier, skills, loadPolicy);
    }

    public void Consult(string phaseId, int n, int max, string advice)
    {
        Add("consult", phaseId, $"{n}/{max}: {Trunc(advice)}");
        _echo?.Consult(phaseId, n, max, advice);
    }

    public void Gate(string phaseId, string kind, string outcome, string reason)
    {
        Add("gate", phaseId, $"{kind} -> {outcome}{(reason.Length > 0 ? ": " + Trunc(reason) : "")}");
        _echo?.Gate(phaseId, kind, outcome, reason);
    }

    public void Escalation(string fromPhase, string toPhase, string reason)
    {
        Add("escalation", fromPhase, $"-> {toPhase}: {Trunc(reason)}");
        _echo?.Escalation(fromPhase, toPhase, reason);
    }

    public void Promotion(string phaseId, string fromTier, string toTier)
    {
        Add("promote", phaseId, $"{fromTier} -> {toTier}");
        _echo?.Promotion(phaseId, fromTier, toTier);
    }

    public void Conflict(string phaseId, string detail)
    {
        Add("conflict", phaseId, detail);
        _echo?.Conflict(phaseId, detail);
    }

    public void PhaseEnd(string phaseId, string summary)
    {
        Add("phase_end", phaseId, Trunc(summary));
        _echo?.PhaseEnd(phaseId, summary);
    }

    public void RunEnd(RunStatus status, string reason)
    {
        Status = status; FailReason = reason;
        Add("run_end", "-", $"{status}{(reason.Length > 0 ? ": " + reason : "")}");
        _echo?.RunEnd(status, reason);
    }

    private static string Trunc(string s)
    {
        s = s.ReplaceLineEndings(" ").Trim();
        return s.Length > 200 ? s[..200] + "…" : s;
    }
}

/// <summary>Token spend for one model tier.</summary>
public sealed class TierCost
{
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public int Calls { get; set; }
}

/// <summary>
/// Per-tier token accounting across a workflow run. The whole "cheap drivers, frontier
/// gates" thesis is unfalsifiable without this — it makes "is the advisor/classifier
/// paying for itself?" an answerable question. Tokens flow in automatically via the
/// metered LLM client wrapping every tier; no call site has to remember to report.
/// </summary>
public sealed class CostTally
{
    public Dictionary<string, TierCost> ByTier { get; set; } = new(StringComparer.Ordinal);

    public void Add(string tier, long inputTokens, long outputTokens)
    {
        var c = Entry(tier);
        c.InputTokens += inputTokens;
        c.OutputTokens += outputTokens;
        c.Calls++;
    }

    /// <summary>Fold another tally in (used when rehydrating a resumed run).</summary>
    public void Merge(CostTally other)
    {
        foreach (var (tier, c) in other.ByTier)
        {
            var e = Entry(tier);
            e.InputTokens += c.InputTokens;
            e.OutputTokens += c.OutputTokens;
            e.Calls += c.Calls;
        }
    }

    private TierCost Entry(string tier)
    {
        if (!ByTier.TryGetValue(tier, out var c)) ByTier[tier] = c = new TierCost();
        return c;
    }

    public long TotalInput => ByTier.Values.Sum(c => c.InputTokens);
    public long TotalOutput => ByTier.Values.Sum(c => c.OutputTokens);
    public int TotalCalls => ByTier.Values.Sum(c => c.Calls);

    public string Render()
    {
        if (ByTier.Count == 0) return "(no model calls)";
        var sb = new System.Text.StringBuilder("cost by tier:");
        foreach (var (tier, c) in ByTier.OrderByDescending(kv => kv.Value.InputTokens + kv.Value.OutputTokens))
            sb.Append($"\n  {tier}: {c.Calls} calls · {c.InputTokens} in / {c.OutputTokens} out");
        sb.Append($"\n  TOTAL: {TotalCalls} calls · {TotalInput} in / {TotalOutput} out");
        return sb.ToString();
    }
}
