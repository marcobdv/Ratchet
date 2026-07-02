using CodeStack.Ratchet.Core;
using CodeStack.Ratchet.Llm;
using Microsoft.Extensions.AI;
using Xunit;

namespace CodeStack.Ratchet.Tests;

/// <summary>
/// The IChatClient adapter: the loop's stop_reason must reflect the provider's real
/// finish reason — a max_tokens truncation may not masquerade as a clean end_turn.
/// </summary>
public sealed class ChatClientLlmTests
{
    private static Task<LlmResponse> Complete(params ChatResponseUpdate[] updates)
    {
        var llm = new ChatClientLlm(new ScriptedChatClient(updates), "test-model");
        var convo = new Conversation();
        convo.Add(Message.UserText("hi"));
        return llm.CompleteAsync("system", convo, Array.Empty<ITool>(), _ => { }, CancellationToken.None);
    }

    [Fact]
    public async Task LengthFinish_MapsToMaxTokens_NotEndTurn()
    {
        var response = await Complete(
            new ChatResponseUpdate(ChatRole.Assistant, "truncated answ"),
            new ChatResponseUpdate(ChatRole.Assistant, []) { FinishReason = ChatFinishReason.Length });

        Assert.Equal("max_tokens", response.StopReason);
    }

    [Fact]
    public async Task LengthFinish_WithToolCalls_IsStillMaxTokens()
    {
        var call = new ChatResponseUpdate(ChatRole.Assistant,
            [new FunctionCallContent("id1", "read", new Dictionary<string, object?>())])
        {
            FinishReason = ChatFinishReason.Length,
        };
        var response = await Complete(call);

        Assert.Equal("max_tokens", response.StopReason); // truncation must stay visible
        Assert.Single(response.AssistantMessage.Content.OfType<ToolUseBlock>());
    }

    [Fact]
    public async Task ToolCallsFinish_MapsToToolUse()
    {
        var call = new ChatResponseUpdate(ChatRole.Assistant,
            [new FunctionCallContent("id1", "read", new Dictionary<string, object?>())])
        {
            FinishReason = ChatFinishReason.ToolCalls,
        };
        var response = await Complete(call);

        Assert.Equal("tool_use", response.StopReason);
    }

    [Fact]
    public async Task NoFinishReason_FallsBackToInference()
    {
        var response = await Complete(new ChatResponseUpdate(ChatRole.Assistant, "plain answer"));
        Assert.Equal("end_turn", response.StopReason);
    }

    private sealed class ScriptedChatClient : IChatClient
    {
        private readonly ChatResponseUpdate[] _updates;
        public ScriptedChatClient(ChatResponseUpdate[] updates) => _updates = updates;

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var u in _updates)
            {
                yield return u;
                await Task.Yield();
            }
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
