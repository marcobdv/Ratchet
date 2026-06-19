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

        var text = new StringBuilder();
        var toolCalls = new List<FunctionCallContent>();
        long inputTokens = 0, outputTokens = 0;

        await foreach (var update in _chat.GetStreamingResponseAsync(messages, options, ct).ConfigureAwait(false))
        {
            foreach (var content in update.Contents)
            {
                switch (content)
                {
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
        if (text.Length > 0)
            blocks.Add(new TextBlock(text.ToString()));
        foreach (var call in toolCalls)
        {
            var argsJson = JsonSerializer.Serialize(call.Arguments ?? new Dictionary<string, object?>());
            blocks.Add(new ToolUseBlock(call.CallId, call.Name, argsJson));
        }

        var stopReason = toolCalls.Count > 0 ? "tool_use" : "end_turn";

        span?.SetTag("gen_ai.usage.input_tokens", inputTokens);
        span?.SetTag("gen_ai.usage.output_tokens", outputTokens);
        span?.SetTag("gen_ai.response.finish_reasons", new[] { stopReason });
        RatchetTelemetry.RecordChat(_system, _model, (int)inputTokens, (int)outputTokens,
            Stopwatch.GetElapsedTime(started).TotalSeconds, stopReason);

        return new LlmResponse(new Message(Role.Assistant, blocks), stopReason, (int)inputTokens, (int)outputTokens);
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
                var contents = toolResults
                    .Select(r => (AIContent)new FunctionResultContent(r.ToolUseId, r.Content))
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
