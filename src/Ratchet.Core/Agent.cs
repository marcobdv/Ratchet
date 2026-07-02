using System.Diagnostics;

namespace CodeStack.Ratchet.Core;

/// <summary>
/// The agent loop. This is the whole idea of a coding agent in one method:
///
///   1. Send the transcript + tool specs to the model.
///   2. Stream its text out live as it arrives.
///   3. If the model asked for tools (stop_reason == "tool_use"):
///        run each tool, append the results as a user message, and loop.
///      Otherwise: the turn is done, hand control back to the human.
///
/// Everything else in an agent (planning, sub-agents, MCP, semantic search) is
/// an elaboration on top of this. pi's insight is that the elaborations are
/// optional; the loop is not.
/// </summary>
public sealed class Agent
{
    private readonly ILlmClient _llm;
    private readonly ToolRegistry _tools;
    private readonly string _systemPrompt;
    private readonly IAgentObserver _observer;
    private readonly IToolGate _gate;

    public Agent(ILlmClient llm, ToolRegistry tools, string systemPrompt, IAgentObserver observer, IToolGate? gate = null)
    {
        _llm = llm;
        _tools = tools;
        _systemPrompt = systemPrompt;
        _observer = observer;
        _gate = gate ?? AllowAllGate.Instance;
    }

    /// <summary>
    /// Run one human turn to completion: keep calling the model and executing
    /// tools until the model stops asking for them.
    /// </summary>
    public async Task RunTurnAsync(Conversation conversation, CancellationToken ct)
    {
        using var turn = RatchetTelemetry.StartTurn();
        try
        {
        while (true)
        {
            // Text streams out live through the observer; the assembled message
            // (with any tool-use blocks) still comes back for the loop to act on.
            var streamedText = false;
            void OnDelta(string d) { streamedText = true; _observer.OnAssistantTextDelta(d); }

            var response = await _llm.CompleteAsync(_systemPrompt, conversation, _tools.All, OnDelta, ct);
            Append(conversation, response.AssistantMessage);

            if (streamedText) _observer.OnAssistantTextEnd();
            _observer.OnUsage(response.InputTokens, response.OutputTokens);

            // No tool calls -> the assistant is done with this turn.
            var toolUses = response.AssistantMessage.Content.OfType<ToolUseBlock>().ToList();
            if (response.StopReason != "tool_use")
            {
                // Stop-reason policy (ADR-0010): a non-tool_use stop can still carry
                // tool_use blocks — max_tokens can cut the response off mid-call. The
                // API requires every tool_use to be answered by a tool_result in the
                // next user message; leaving them orphaned poisons the transcript and
                // 400s every subsequent call. Close them with error results so the
                // model sees the interruption instead of the session dying.
                if (toolUses.Count > 0)
                    Append(conversation, Message.UserToolResults(toolUses.Select(u => (ContentBlock)new ToolResultBlock(
                        u.Id,
                        $"[not executed: the response was interrupted (stop_reason={response.StopReason}) before this tool could run]",
                        true)).ToList()));
                return;
            }
            if (toolUses.Count == 0)
                return;

            // Run every requested tool, collect results, feed them back as one user message.
            var results = new List<ContentBlock>(toolUses.Count);
            foreach (var call in toolUses)
            {
                _observer.OnToolCall(call.Name, call.InputJson);
                var (content, isError) = await ExecuteToolAsync(call, ct);
                _observer.OnToolResult(call.Name, content, isError);
                results.Add(new ToolResultBlock(call.Id, content, isError));
            }

            Append(conversation, Message.UserToolResults(results));
            // Loop: the model now sees the results and decides what to do next.
        }
        }
        catch (Exception ex)
        {
            // A turn that fails (e.g. the model call throws out of the loop) must surface
            // on the span, not close Ok — otherwise turn-level error monitoring misses it.
            RatchetTelemetry.Fail(turn, ex);
            throw;
        }
    }

    // Every message the loop appends flows through here so an observer can persist turn
    // progress durably (OnMessageAppended). Without this seam, a turn's intermediate
    // messages lived only in the transient Conversation until the host folded them back
    // on success — a mid-turn failure dropped completed tool work the model had done.
    private void Append(Conversation conversation, Message message)
    {
        conversation.Add(message);
        _observer.OnMessageAppended(message);
    }

    private async Task<(string content, bool isError)> ExecuteToolAsync(ToolUseBlock call, CancellationToken ct)
    {
        if (!_tools.TryGet(call.Name, out var tool))
            return ($"Unknown tool '{call.Name}'.", true);

        using var span = RatchetTelemetry.StartTool(call.Name);
        var started = Stopwatch.GetTimestamp();

        try
        {
            // Permission gate inside the try: a denial OR a faulting custom gate comes back
            // as an error result the model can adapt to, never an exception that kills the turn.
            var decision = await _gate.CheckAsync(call.Name, call.InputJson, ct);
            if (!decision.Allowed)
            {
                RatchetTelemetry.RecordGateDenial(call.Name);
                span?.SetTag("ratchet.gate.denied", true);
                span?.SetStatus(ActivityStatusCode.Error, "permission denied");
                RatchetTelemetry.RecordTool(call.Name, "permission_denied", Stopwatch.GetElapsedTime(started).TotalSeconds);
                return ($"Permission denied for '{call.Name}': {decision.Reason}", true);
            }

            var result = await tool.ExecuteAsync(call.InputJson, ct);
            RatchetTelemetry.RecordTool(call.Name, errorType: null, Stopwatch.GetElapsedTime(started).TotalSeconds);
            return (result, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // User cancellation is not a tool failure: propagate promptly instead of
            // feeding an error result back and calling the model with a dead token.
            throw;
        }
        catch (Exception ex)
        {
            // Tool (or gate) faults are not agent failures: hand the error back to the
            // model as a result so it can recover, rather than crashing the loop.
            span?.SetStatus(ActivityStatusCode.Error, ex.Message);
            RatchetTelemetry.RecordTool(call.Name, ex.GetType().Name, Stopwatch.GetElapsedTime(started).TotalSeconds);
            return ($"Tool '{call.Name}' failed: {ex.Message}", true);
        }
    }
}

/// <summary>
/// Observability seam. pi's big lesson is "know exactly what hits the context".
/// Every interesting event in the loop flows through here so the CLI (or later a
/// log sink, a TUI, an audit trail) can render it without the loop caring how.
/// </summary>
public interface IAgentObserver
{
    void OnAssistantTextDelta(string delta);
    void OnAssistantTextEnd();
    void OnToolCall(string toolName, string inputJson);
    void OnToolResult(string toolName, string content, bool isError);
    void OnUsage(int inputTokens, int outputTokens);

    /// <summary>
    /// A message was just appended to the conversation (the assistant turn, then its
    /// tool-results). The durability seam: a host can fold + persist incrementally here
    /// so a turn that fails on a later model call doesn't lose the tool work already done.
    /// Default no-op — observers that only render text can ignore it.
    /// </summary>
    void OnMessageAppended(Message message) { }
}
