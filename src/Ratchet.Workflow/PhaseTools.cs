using System.Text;
using System.Text.Json;
using CodeStack.Ratchet.Core;

namespace CodeStack.Ratchet.Workflow;

/// <summary>
/// Mutable per-phase advisor state, shared between the <see cref="ConsultAdvisorTool"/>
/// (which increments it) and the <see cref="WriteGuardTool"/> (which reads it). One
/// instance per running phase.
/// </summary>
public sealed class ConsultState
{
    public int Count { get; set; }
    public bool Consulted { get; set; }
    public bool RequireConsultBeforeWrite { get; set; }
}

/// <summary>
/// The reimplemented advisor pattern (the Anthropic advisor *tool* is API-only and
/// can't attach to a local driver, but the *pattern* ports): a cheap driver consults
/// a stronger model mid-work. It forwards the work transcript so far plus the driver's
/// question to the advisor tier and returns a short course-correction.
///
/// Two design rules are enforced here, not left to the prompt:
///   - <b>Loop ceiling</b> (<c>max_consults</c>): past the ceiling it refuses and tells
///     the driver to proceed or escalate, rather than consulting in circles.
///   - It records every consult on the run so the advisor's value is eval-able.
/// Timing is the driver's judgment (that's the advisor's defining limitation); the
/// before-first-write lever lives in <see cref="WriteGuardTool"/>.
/// </summary>
public sealed class ConsultAdvisorTool : ITool
{
    private readonly ILlmClient _advisor;
    private readonly AdvisorSpec _spec;
    private readonly Conversation _transcript;   // shared with the driver agent
    private readonly ConsultState _state;
    private readonly IWorkflowObserver _run;
    private readonly string _phaseId;

    public ConsultAdvisorTool(ILlmClient advisor, AdvisorSpec spec, Conversation transcript,
        ConsultState state, IWorkflowObserver run, string phaseId)
    {
        _advisor = advisor; _spec = spec; _transcript = transcript;
        _state = state; _run = run; _phaseId = phaseId;
    }

    public string Name => "consult_advisor";
    public string Description =>
        "Consult a stronger advisor model for a short course-correction. Forwards the work so far " +
        "plus your question; returns a brief plan/critique. " + _spec.ConsultWhen;
    public string InputSchemaJson => """
        {"type":"object","properties":{"question":{"type":"string","description":"What you want a second opinion on, with any specifics."}},"required":["question"]}
        """;

    public async Task<string> ExecuteAsync(string inputJson, CancellationToken ct)
    {
        var question = Json2.Str(inputJson, "question") ?? "";

        if (_state.Count >= _spec.MaxConsults)
            return $"Advisor ceiling reached ({_spec.MaxConsults} consults). Do NOT consult again on this — " +
                   "either proceed with your best judgment or request_escalation to a phase that can re-scope.";

        var convo = new Conversation();
        convo.Add(Message.UserText(
            "You are a senior advisor giving a SHORT course-correction to a less capable driver agent " +
            "mid-task. Be concrete and brief: confirm the approach or redirect it, name the next concrete " +
            "step, and flag any wrong framing you see. Here is the work transcript so far:\n\n" +
            Flatten(_transcript) +
            "\n\n--- The driver asks ---\n" + question));

        var advice = new StringBuilder();
        var resp = await _advisor.CompleteAsync(
            "You are an expert software advisor. Reply with a few tight sentences, not an essay.",
            convo, Array.Empty<ITool>(), s => advice.Append(s), ct);
        var text = string.Concat(resp.AssistantMessage.Content.OfType<TextBlock>().Select(t => t.Text)).Trim();
        if (text.Length == 0) text = advice.ToString().Trim();

        _state.Count++;
        _state.Consulted = true;
        _run.Consult(_phaseId, _state.Count, _spec.MaxConsults, text);
        return text.Length == 0 ? "(advisor returned no text)" : text;
    }

    private static string Flatten(Conversation c)
    {
        var sb = new StringBuilder();
        foreach (var m in c.Messages)
        {
            sb.Append(m.Role == Role.User ? "USER: " : "ASSISTANT: ");
            foreach (var b in m.Content)
            {
                switch (b)
                {
                    case TextBlock t: sb.Append(t.Text); break;
                    case ToolUseBlock u when u.Name == "consult_advisor": break;   // skip the pending call
                    case ToolUseBlock u: sb.Append($"[calls {u.Name} {u.InputJson}]"); break;
                    case ToolResultBlock r: sb.Append($"[result: {r.Content}]"); break;
                }
            }
            sb.Append('\n');
        }
        return sb.ToString().Trim();
    }
}

/// <summary>
/// Decorates a state-changing tool (write/edit/bash) to enforce the
/// <b>advisor-before-first-write</b> rule on non-trivial work: refuse the first
/// state-changing action until the driver has consulted the advisor. This is the one
/// lever that recovers a determinism guarantee from a judgment-timed tool — and it's
/// gated on work_type (trivial work never sets the flag), so it can't over-fire on
/// tasks with a straightforward first step.
/// </summary>
public sealed class WriteGuardTool : ITool
{
    private readonly ITool _inner;
    private readonly ConsultState _state;

    public WriteGuardTool(ITool inner, ConsultState state) { _inner = inner; _state = state; }

    public string Name => _inner.Name;
    public string Description => _inner.Description;
    public string InputSchemaJson => _inner.InputSchemaJson;

    public Task<string> ExecuteAsync(string inputJson, CancellationToken ct)
    {
        if (_state.RequireConsultBeforeWrite && !_state.Consulted)
            return Task.FromResult(
                $"Blocked: consult_advisor before your first '{_inner.Name}' on this non-trivial change " +
                "(workflow rule). Call consult_advisor with your plan, then retry.");
        return _inner.ExecuteAsync(inputJson, ct);
    }
}

/// <summary>Carries an escalation request from the driver out to the scheduler.</summary>
public sealed class EscalationRequest
{
    public string? TargetPhase { get; set; }
    public string Reason { get; set; } = "";
    public bool Requested => TargetPhase is not null;
}

/// <summary>
/// Lets a phase fail *back up* the spine: if "implement" finds the trivial change
/// actually touches a shared token in twelve places, it requests re-entry of "plan".
/// Bounded to the phase's configured escalation targets — the model can't invent a
/// jump. The scheduler reads the request after the phase and routes (with a ceiling).
/// </summary>
public sealed class EscalateTool : ITool
{
    private readonly IReadOnlyList<string> _allowed;
    private readonly EscalationRequest _request;

    public EscalateTool(IReadOnlyList<string> allowedTargets, EscalationRequest request)
    {
        _allowed = allowedTargets; _request = request;
    }

    public string Name => "request_escalation";
    public string Description =>
        "Signal that this work is bigger than its current sizing and an earlier phase must be re-entered " +
        $"(e.g. re-plan). Allowed targets: {(_allowed.Count == 0 ? "(none)" : string.Join(", ", _allowed))}. " +
        "The scheduler re-enters that phase after this one.";
    public string InputSchemaJson => """
        {"type":"object","properties":{"phase":{"type":"string","description":"The earlier phase to re-enter."},"reason":{"type":"string","description":"Why the current sizing was wrong."}},"required":["phase","reason"]}
        """;

    public Task<string> ExecuteAsync(string inputJson, CancellationToken ct)
    {
        var phase = Json2.Str(inputJson, "phase") ?? "";
        var reason = Json2.Str(inputJson, "reason") ?? "";
        if (!_allowed.Contains(phase))
            return Task.FromResult($"Cannot escalate to '{phase}'. Allowed: {(_allowed.Count == 0 ? "(none)" : string.Join(", ", _allowed))}.");
        _request.TargetPhase = phase;
        _request.Reason = reason;
        return Task.FromResult($"Escalation to '{phase}' recorded; it will be re-entered after this phase.");
    }
}

/// <summary>Tiny JSON helper (Core's Json is internal to its assembly).</summary>
internal static class Json2
{
    public static string? Str(string json, string prop)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
    }
}
