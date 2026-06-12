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

    public Agent(ILlmClient llm, ToolRegistry tools, string systemPrompt, IAgentObserver observer)
    {
        _llm = llm;
        _tools = tools;
        _systemPrompt = systemPrompt;
        _observer = observer;
    }

    /// <summary>
    /// Run one human turn to completion: keep calling the model and executing
    /// tools until the model stops asking for them.
    /// </summary>
    public async Task RunTurnAsync(Conversation conversation, CancellationToken ct)
    {
        while (true)
        {
            // Text streams out live through the observer; the assembled message
            // (with any tool-use blocks) still comes back for the loop to act on.
            var streamedText = false;
            void OnDelta(string d) { streamedText = true; _observer.OnAssistantTextDelta(d); }

            var response = await _llm.CompleteAsync(_systemPrompt, conversation, _tools.All, OnDelta, ct);
            conversation.Add(response.AssistantMessage);

            if (streamedText) _observer.OnAssistantTextEnd();
            _observer.OnUsage(response.InputTokens, response.OutputTokens);

            // No tool calls -> the assistant is done with this turn.
            var toolUses = response.AssistantMessage.Content.OfType<ToolUseBlock>().ToList();
            if (response.StopReason != "tool_use" || toolUses.Count == 0)
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

            conversation.Add(Message.UserToolResults(results));
            // Loop: the model now sees the results and decides what to do next.
        }
    }

    private async Task<(string content, bool isError)> ExecuteToolAsync(ToolUseBlock call, CancellationToken ct)
    {
        if (!_tools.TryGet(call.Name, out var tool))
            return ($"Unknown tool '{call.Name}'.", true);

        try
        {
            var result = await tool.ExecuteAsync(call.InputJson, ct);
            return (result, false);
        }
        catch (Exception ex)
        {
            // Tool failures are not agent failures: hand the error back to the
            // model as a result so it can recover, rather than crashing the loop.
            return ($"Tool '{call.Name}' threw: {ex.Message}", true);
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
}
