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
