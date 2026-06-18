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
