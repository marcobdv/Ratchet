namespace CodeStack.Ratchet.Core;

/// <summary>
/// A permission gate's verdict on one tool call: allowed, or denied with a reason
/// the model sees (so it can adapt instead of crashing).
/// </summary>
public sealed record ToolGateDecision(bool Allowed, string Reason)
{
    public static readonly ToolGateDecision Allow = new(true, "");
    public static ToolGateDecision Deny(string reason) => new(false, reason);
}

/// <summary>
/// The permission seam the README always promised. Consulted in
/// <see cref="Agent.ExecuteToolAsync"/> *before* a tool runs, so the guarantee — "this
/// mutating action does not happen without approval" — lives in the loop's control
/// flow, not in a prompt the model can wander past. A denial is returned to the model
/// as an error tool-result, not an exception: the loop keeps going, the model recovers.
///
/// Default is <see cref="AllowAllGate"/> (the historical YOLO behaviour), so existing
/// callers are unchanged until they opt into a real policy.
/// </summary>
public interface IToolGate
{
    Task<ToolGateDecision> CheckAsync(string toolName, string inputJson, CancellationToken ct);
}

/// <summary>The no-op gate: every call allowed. Preserves pi-plain YOLO by default.</summary>
public sealed class AllowAllGate : IToolGate
{
    public static readonly AllowAllGate Instance = new();
    public Task<ToolGateDecision> CheckAsync(string toolName, string inputJson, CancellationToken ct) =>
        Task.FromResult(ToolGateDecision.Allow);
}

/// <summary>
/// Deny-by-default gate for a read-only role: only tools that cannot mutate state are
/// allowed; everything else (write/edit/bash/git_commit/rename/MCP/…) is refused. This is
/// how a <i>delegated</i> sub-agent is scoped to its role even though the top-level agent is
/// YOLO — the constraint is enforced in the loop, not left to the sub-agent's prompt. New
/// tools are denied unless explicitly added here, so the safe default is "can't mutate".
/// </summary>
public sealed class ReadOnlyGate : IToolGate
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "read", "search", "recall", "load_skill",
        "git_status", "git_diff",
        "roslyn_diagnostics", "roslyn_find_symbol", "roslyn_find_references", "roslyn_outline",
    };

    public Task<ToolGateDecision> CheckAsync(string toolName, string inputJson, CancellationToken ct) =>
        Task.FromResult(Allowed.Contains(toolName)
            ? ToolGateDecision.Allow
            : ToolGateDecision.Deny($"'{toolName}' is not available to this read-only sub-agent."));
}
