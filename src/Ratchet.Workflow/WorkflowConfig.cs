namespace CodeStack.Ratchet.Workflow;

/// <summary>
/// The validated, in-memory shape of a workflow. This is the *domain* model the
/// scheduler runs against — already checked by <see cref="WorkflowLoader"/>, with
/// advisor inheritance resolved and every name guaranteed to resolve. The YAML DTOs
/// that produce it stay private to the loader; nothing downstream sees raw YAML.
///
/// Structure only lives here (the deterministic spine, the gates, the tiers). The
/// cognition inside a phase is soft text (role prompts, skills) — per the design's
/// "structure the control flow, not the thinking".
/// </summary>
public sealed record WorkflowConfig(
    int Version,
    string Name,
    bool RecordClassification,
    IReadOnlyDictionary<string, ModelTier> Models,
    string DefaultDriverTier,
    AdvisorSpec? DefaultAdvisor,
    SkillLoading SkillLoading,
    IReadOnlyList<PhaseSpec> Spine,
    IReadOnlyDictionary<string, WorkTypeSpec> WorkTypes)
{
    public PhaseSpec? Phase(string id) => Spine.FirstOrDefault(p => p.Id == id);

    /// <summary>Spine index of a phase id, or -1 — used to keep escalation in spine order.</summary>
    public int SpineIndex(string id)
    {
        for (var i = 0; i < Spine.Count; i++) if (Spine[i].Id == id) return i;
        return -1;
    }

    /// <summary>Floor (non-skippable) phase ids — the guarantees that no work_type may drop.</summary>
    public IReadOnlyList<string> FloorPhases =>
        Spine.Where(p => !p.Skippable).Select(p => p.Id).ToList();
}

/// <summary>A named model tier. Phases reference the name, never the model — swap in one place.</summary>
public sealed record ModelTier(string Name, string Provider, string Model);

/// <summary>The advisor a driver MAY consult during a phase. Null on a phase = no advisor.</summary>
public sealed record AdvisorSpec(string ModelTier, string ConsultWhen, int MaxConsults);

public enum GateKind { None, Command, Judge }

/// <summary>
/// A phase-exit gate. <see cref="GateKind.Command"/> routes on a process exit code
/// (the cheapest, strongest judge); <see cref="GateKind.Judge"/> spends a frontier
/// model on judgment an exit code can't express, optionally with fresh context.
/// <see cref="OnFail"/> is "stop", "loop" (re-run the gated phase), or a spine phase id.
/// </summary>
public sealed record GateSpec(
    GateKind Type,
    string? Run,
    string? JudgeAgent,
    bool FreshContext,
    string? ModelTier,
    string OnFail,
    int MaxLoops)
{
    public static readonly GateSpec None = new(GateKind.None, null, null, false, null, "stop", 0);
}

/// <summary>One phase of the spine: its role, driver tier, advisor, tool subset, gate, escalation targets.</summary>
public sealed record PhaseSpec(
    string Id,
    bool Skippable,
    string Role,
    string DriverTier,
    AdvisorSpec? Advisor,
    IReadOnlyList<string> Tools,
    GateSpec Gate,
    IReadOnlyList<string> Escalation);

/// <summary>
/// One work_type overlay: the ordered phase subset to run (must include floors) and
/// the eligible skills per phase. Skills live here, not on the spine, so one block
/// reads as the whole diffable story of a work_type.
/// </summary>
public sealed record WorkTypeSpec(
    string Name,
    IReadOnlyList<string> Phases,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Skills)
{
    public IReadOnlyList<string> SkillsFor(string phaseId) =>
        Skills.TryGetValue(phaseId, out var s) ? s : Array.Empty<string>();
}

/// <summary>Skill load policy: small eligible set → load all; larger → progressive disclosure.</summary>
public sealed record SkillLoading(int Threshold, string StrategyAbove)
{
    public bool LoadAll(int count) => count <= Threshold;
}

/// <summary>Thrown when a workflow file can't be loaded or fails validation. Carries all errors.</summary>
public sealed class WorkflowConfigException : Exception
{
    public IReadOnlyList<string> Errors { get; }
    public WorkflowConfigException(IReadOnlyList<string> errors)
        : base("Invalid workflow config:\n  - " + string.Join("\n  - ", errors)) => Errors = errors;
    public WorkflowConfigException(string error) : this(new[] { error }) { }
}
