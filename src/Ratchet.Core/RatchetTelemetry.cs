using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace CodeStack.Ratchet.Core;

/// <summary>
/// Vendor-neutral OpenTelemetry instrumentation for the agent, built on the BCL
/// diagnostics API (<see cref="ActivitySource"/> + <see cref="Meter"/>) — NOT the
/// OpenTelemetry SDK. That keeps Core free of any vendor dependency: when nothing is
/// listening, <c>StartActivity</c> returns null and the instrument records are no-ops,
/// so the agent pays nothing. The CLI opts in by wiring the OpenTelemetry SDK to the
/// source/meter named here and exporting (OTLP, console, …) — the same "instrument in
/// Core, configure at the composition root" seam as everything else.
///
/// Span and attribute names follow the OpenTelemetry GenAI semantic conventions
/// (<c>gen_ai.*</c>) where they apply, so any OTel backend renders them natively.
/// </summary>
public static class RatchetTelemetry
{
    /// <summary>The name an exporter subscribes to: <c>AddSource(...)</c> / <c>AddMeter(...)</c>.</summary>
    public const string Name = "Ratchet.Agent";

    public static readonly ActivitySource Activity = new(Name);
    public static readonly Meter Meter = new(Name);

    // GenAI-convention metrics + a few Ratchet-specific ones.
    private static readonly Histogram<long> TokenUsage =
        Meter.CreateHistogram<long>("gen_ai.client.token.usage", "token", "Tokens used per model call");
    private static readonly Histogram<double> OpDuration =
        Meter.CreateHistogram<double>("gen_ai.client.operation.duration", "s", "Model call duration");
    private static readonly Counter<long> ToolCalls =
        Meter.CreateCounter<long>("ratchet.tool.calls", "{call}", "Tool invocations");
    private static readonly Histogram<double> ToolDuration =
        Meter.CreateHistogram<double>("ratchet.tool.duration", "s", "Tool execution duration");
    private static readonly Counter<long> GateDenials =
        Meter.CreateCounter<long>("ratchet.gate.denials", "{denial}", "Tool calls blocked by the permission gate");

    // ---- spans -------------------------------------------------------------

    /// <summary>The span around one human turn run to completion (the agent loop).</summary>
    public static Activity? StartTurn() => Activity.StartActivity("agent.turn", ActivityKind.Internal);

    /// <summary>A GenAI "chat" client span; the LLM client owns it because it knows the model.</summary>
    public static Activity? StartChat(string system, string model)
    {
        var a = Activity.StartActivity($"chat {model}", ActivityKind.Client);
        a?.SetTag("gen_ai.operation.name", "chat");
        a?.SetTag("gen_ai.provider.name", system);   // gen_ai.system is deprecated in the conventions
        a?.SetTag("gen_ai.request.model", model);
        return a;
    }

    /// <summary>A tool-execution span (also covers the gate decision).</summary>
    public static Activity? StartTool(string toolName)
    {
        var a = Activity.StartActivity($"execute_tool {toolName}", ActivityKind.Internal);
        a?.SetTag("gen_ai.operation.name", "execute_tool");
        a?.SetTag("gen_ai.tool.name", toolName);
        return a;
    }

    // ---- metric recording --------------------------------------------------

    /// <summary>
    /// Finalize a successful chat span: stamp the GenAI usage/finish tags AND record the
    /// metrics. Shared by every LLM client so the span shape can't drift between providers.
    /// </summary>
    public static void RecordChatResult(Activity? span, string system, string model,
        int inputTokens, int outputTokens, double seconds, string finishReason)
    {
        span?.SetTag("gen_ai.usage.input_tokens", inputTokens);
        span?.SetTag("gen_ai.usage.output_tokens", outputTokens);
        span?.SetTag("gen_ai.response.finish_reasons", new[] { finishReason });   // span only; not a metric dim
        RecordChat(system, model, inputTokens, outputTokens, seconds, null);
    }

    /// <summary>Mark a span failed with the exception message (span only, no metric).</summary>
    public static void Fail(Activity? span, Exception ex) => span?.SetStatus(ActivityStatusCode.Error, ex.Message);

    /// <summary>
    /// Finalize a FAILED chat call: mark the span and record the duration metric with an
    /// error.type, so error-rate and latency dashboards see failures (not just successes).
    /// Call in a catch before rethrowing.
    /// </summary>
    public static void RecordChatError(Activity? span, string system, string model, double seconds, Exception ex)
    {
        span?.SetStatus(ActivityStatusCode.Error, ex.Message);
        RecordChat(system, model, null, null, seconds, ex.GetType().Name);
    }

    /// <summary>
    /// Record duration (always) and token usage (when known) for one model call, tagged per
    /// GenAI conventions. <paramref name="errorType"/> is null on success, else the error.type.
    /// </summary>
    private static void RecordChat(string system, string model, int? inputTokens, int? outputTokens, double seconds, string? errorType)
    {
        var prov = new KeyValuePair<string, object?>("gen_ai.provider.name", system);
        var mod = new KeyValuePair<string, object?>("gen_ai.request.model", model);
        var op = new KeyValuePair<string, object?>("gen_ai.operation.name", "chat");
        if (inputTokens is int it) TokenUsage.Record(it, prov, mod, op, new("gen_ai.token.type", "input"));
        if (outputTokens is int ot) TokenUsage.Record(ot, prov, mod, op, new("gen_ai.token.type", "output"));
        if (errorType is null) OpDuration.Record(seconds, prov, mod, op);
        else OpDuration.Record(seconds, prov, mod, op, new("error.type", errorType));
    }

    /// <param name="errorType">null on success; otherwise the conventional error.type (e.g. exception name).</param>
    public static void RecordTool(string toolName, string? errorType, double seconds)
    {
        var name = new KeyValuePair<string, object?>("gen_ai.tool.name", toolName);
        if (errorType is null)
        {
            ToolCalls.Add(1, name);
            ToolDuration.Record(seconds, name);
        }
        else
        {
            var err = new KeyValuePair<string, object?>("error.type", errorType);
            ToolCalls.Add(1, name, err);
            ToolDuration.Record(seconds, name, err);
        }
    }

    public static void RecordGateDenial(string toolName) =>
        GateDenials.Add(1, new KeyValuePair<string, object?>("gen_ai.tool.name", toolName));
}
