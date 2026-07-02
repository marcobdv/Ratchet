using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodeStack.Ratchet.Core;
using Microsoft.Extensions.AI;

namespace CodeStack.Ratchet.Llm;

/// <summary>
/// An <see cref="ILlmClient"/> backed by any Microsoft.Extensions.AI
/// <see cref="IChatClient"/>. This is where Ratchet "adopts IChatClient": the hand-rolled
/// loop keeps calling <c>ILlmClient.CompleteAsync</c>, while underneath any provider with an
/// <c>IChatClient</c> (Anthropic via <see cref="AnthropicChatClient"/>, OpenAI, Azure, Ollama,
/// …) — and MCP tools, which are AITools — work unchanged. The transcript, tree, and handover
/// stay owned by Ratchet; only the model call is delegated.
/// </summary>
public sealed class ChatClientLlm : ILlmClient, IDisposable
{
    private readonly IChatClient _chat;
    private readonly string _model;
    private readonly int _maxTokens;
    private readonly string _system;

    /// <param name="system">The <c>gen_ai.system</c> label for telemetry (anthropic, openai, openrouter, …).</param>
    public ChatClientLlm(IChatClient chat, string model, int maxTokens = 4096, string system = "openai")
    {
        _chat = chat;
        _model = model;
        _maxTokens = maxTokens;
        _system = system;
    }

    public async Task<LlmResponse> CompleteAsync(
        string systemPrompt,
        Conversation conversation,
        IReadOnlyCollection<ITool> tools,
        Action<string> onTextDelta,
        CancellationToken ct)
    {
        var messages = ToChatMessages(systemPrompt, conversation);
        var options = new ChatOptions
        {
            ModelId = _model,
            MaxOutputTokens = _maxTokens,
            Tools = tools.Select(t => (AITool)new DeclarationFunction(t)).ToList(),
        };

        using var span = RatchetTelemetry.StartChat(_system, _model);
        var started = Stopwatch.GetTimestamp();

        try
        {
            var text = new StringBuilder();
            var toolCalls = new List<FunctionCallContent>();
            var reasoning = new List<TextReasoningContent>();
            long inputTokens = 0, outputTokens = 0;
            ChatFinishReason? finishReason = null;

            await foreach (var update in _chat.GetStreamingResponseAsync(messages, options, ct).ConfigureAwait(false))
            {
                if (update.FinishReason is { } fr) finishReason = fr;
                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        // Reasoning before TextContent: keep thinking out of the visible text.
                        case TextReasoningContent trc:
                            reasoning.Add(trc);
                            break;
                        case TextContent t when t.Text.Length > 0:
                            text.Append(t.Text);
                            onTextDelta(t.Text);
                            break;
                        case FunctionCallContent call:
                            toolCalls.Add(call);
                            break;
                        case UsageContent usage:
                            inputTokens += usage.Details.InputTokenCount ?? 0;
                            outputTokens += usage.Details.OutputTokenCount ?? 0;
                            break;
                    }
                }
            }

            var blocks = new List<ContentBlock>();
            // Thinking first: the API requires an assistant turn to replay its thinking
            // blocks ahead of text/tool_use. Empty Text = redacted (opaque) thinking.
            foreach (var trc in reasoning)
                blocks.Add(trc.Text.Length > 0
                    ? new ThinkingBlock(trc.Text, trc.ProtectedData ?? "")
                    : new RedactedThinkingBlock(trc.ProtectedData ?? ""));
            if (text.Length > 0)
                blocks.Add(new TextBlock(text.ToString()));
            foreach (var call in toolCalls)
            {
                var argsJson = JsonSerializer.Serialize(call.Arguments ?? new Dictionary<string, object?>());
                blocks.Add(new ToolUseBlock(call.CallId, call.Name, argsJson));
            }

            // Map the provider's finish reason instead of inferring success: max_tokens
            // truncation must be visible to the loop, not disguised as a clean end_turn.
            var stopReason = finishReason switch
            {
                { } fr when fr == ChatFinishReason.Length => "max_tokens",
                { } fr when fr == ChatFinishReason.ToolCalls => "tool_use",
                { } fr when fr == ChatFinishReason.ContentFilter => "refusal",
                { } fr when fr == ChatFinishReason.Stop => toolCalls.Count > 0 ? "tool_use" : "end_turn",
                _ => toolCalls.Count > 0 ? "tool_use" : "end_turn",
            };
            RatchetTelemetry.RecordChatResult(span, _system, _model, (int)inputTokens, (int)outputTokens,
                Stopwatch.GetElapsedTime(started).TotalSeconds, stopReason);

            return new LlmResponse(new Message(Role.Assistant, blocks), stopReason, (int)inputTokens, (int)outputTokens);
        }
        catch (Exception ex)
        {
            // A failed model call must not look like a successful span (else error/latency
            // dashboards under-report). Record the failed call and rethrow.
            RatchetTelemetry.RecordChatError(span, _system, _model, Stopwatch.GetElapsedTime(started).TotalSeconds, ex);
            throw;
        }
    }

    // ---- Ratchet transcript -> M.E.AI messages ----------------------------

    private static List<ChatMessage> ToChatMessages(string systemPrompt, Conversation conversation)
    {
        var messages = new List<ChatMessage> { new(ChatRole.System, systemPrompt) };

        foreach (var msg in conversation.Messages)
        {
            // Tool results form their own ChatRole.Tool message; otherwise map by role.
            var toolResults = msg.Content.OfType<ToolResultBlock>().ToList();
            if (toolResults.Count > 0)
            {
                // M.E.AI's FunctionResultContent has no error flag, and the downstream
                // tool_result blocks never carry is_error on this path — so mark failures
                // in the content itself, otherwise the model is told every tool succeeded.
                var contents = toolResults
                    .Select(r => (AIContent)new FunctionResultContent(
                        r.ToolUseId, r.IsError ? "[tool error] " + r.Content : r.Content))
                    .ToList();
                messages.Add(new ChatMessage(ChatRole.Tool, contents));
                continue;
            }

            var blocks = new List<AIContent>();
            foreach (var block in msg.Content)
            {
                switch (block)
                {
                    case TextBlock t:
                        blocks.Add(new TextContent(t.Text));
                        break;
                    case ThinkingBlock th:
                        blocks.Add(new TextReasoningContent(th.Thinking) { ProtectedData = th.Signature });
                        break;
                    case RedactedThinkingBlock rt:
                        blocks.Add(new TextReasoningContent("") { ProtectedData = rt.Data });
                        break;
                    case ToolUseBlock u:
                        var args = u.InputJson.Length == 0
                            ? new Dictionary<string, object?>()
                            : JsonSerializer.Deserialize<Dictionary<string, object?>>(u.InputJson)
                              ?? new Dictionary<string, object?>();
                        blocks.Add(new FunctionCallContent(u.Id, u.Name, args));
                        break;
                }
            }
            messages.Add(new ChatMessage(msg.Role == Role.Assistant ? ChatRole.Assistant : ChatRole.User, blocks));
        }

        return messages;
    }

    public void Dispose() => _chat.Dispose();

    /// <summary>
    /// Wraps a Ratchet <see cref="ITool"/> as an <see cref="AIFunction"/> so it is sent to the
    /// model as a tool spec. It is never actually invoked here — Ratchet's loop runs the tool —
    /// so <see cref="InvokeCoreAsync"/> throws. We only need it to carry name/description/schema.
    /// </summary>
    private sealed class DeclarationFunction : AIFunction
    {
        private readonly ITool _tool;
        private readonly JsonElement _schema;

        public DeclarationFunction(ITool tool)
        {
            _tool = tool;
            using var doc = JsonDocument.Parse(tool.InputSchemaJson);
            _schema = doc.RootElement.Clone();
        }

        public override string Name => _tool.Name;
        public override string Description => _tool.Description;
        public override JsonElement JsonSchema => _schema;

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Ratchet executes tools in its own loop; this declaration is not invocable.");
    }
}
