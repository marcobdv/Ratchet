using System.Text.Json;
using CodeStack.Ratchet.Core;
using CodeStack.Ratchet.Llm;
using Microsoft.Extensions.AI;
using Xunit;

namespace CodeStack.Ratchet.Tests;

/// <summary>
/// The thinking-block round trip: parsed off the stream (AnthropicSseTests), it must
/// also survive persistence and be replayed VERBATIM in the next request — the API
/// rejects a tool-using assistant turn replayed without its signed thinking block.
/// </summary>
public sealed class ThinkingRoundTripTests
{
    private static readonly Message AssistantWithThinking = new(Role.Assistant, new ContentBlock[]
    {
        new ThinkingBlock("let me reason about this", "sig-abc=="),
        new RedactedThinkingBlock("ENCRYPTED=="),
        new ToolUseBlock("toolu_1", "read", """{"path":"a.txt"}"""),
    });

    [Fact]
    public void MessageJson_PersistsThinkingBlocks()
    {
        var restored = MessageJson.Deserialize(MessageJson.Serialize(AssistantWithThinking));

        Assert.Equal(3, restored.Content.Count);
        var thinking = Assert.IsType<ThinkingBlock>(restored.Content[0]);
        Assert.Equal("let me reason about this", thinking.Thinking);
        Assert.Equal("sig-abc==", thinking.Signature);
        Assert.Equal("ENCRYPTED==", Assert.IsType<RedactedThinkingBlock>(restored.Content[1]).Data);
    }

    [Fact]
    public void AnthropicRequest_ReplaysThinkingVerbatim_WithoutCacheControl()
    {
        var convo = new Conversation();
        convo.Add(Message.UserText("go"));
        convo.Add(AssistantWithThinking);
        convo.Add(Message.UserToolResults(new ContentBlock[] { new ToolResultBlock("toolu_1", "data", false) }));

        using var client = new AnthropicClient("test-key", "test-model");
        var json = client.BuildRequestJson("system", convo, Array.Empty<ITool>());

        using var doc = JsonDocument.Parse(json);
        var assistant = doc.RootElement.GetProperty("messages")[1];
        var blocks = assistant.GetProperty("content").EnumerateArray().ToList();

        Assert.Equal("thinking", blocks[0].GetProperty("type").GetString());
        Assert.Equal("let me reason about this", blocks[0].GetProperty("thinking").GetString());
        Assert.Equal("sig-abc==", blocks[0].GetProperty("signature").GetString());
        Assert.False(blocks[0].TryGetProperty("cache_control", out _)); // forbidden on thinking

        Assert.Equal("redacted_thinking", blocks[1].GetProperty("type").GetString());
        Assert.Equal("ENCRYPTED==", blocks[1].GetProperty("data").GetString());

        Assert.Equal("tool_use", blocks[2].GetProperty("type").GetString());
    }

    [Fact]
    public async Task ChatClientLlm_MapsReasoningBothWays_ThinkingFirst()
    {
        // Downstream (stream -> blocks): reasoning arrives with the tool call; the
        // assembled message must lead with the thinking block.
        var updates = new[]
        {
            new ChatResponseUpdate(ChatRole.Assistant,
            [
                new TextReasoningContent("pondering") { ProtectedData = "sig==" },
                new FunctionCallContent("id1", "read", new Dictionary<string, object?>()),
            ]) { FinishReason = ChatFinishReason.ToolCalls },
        };
        var recorder = new RecordingChatClient(updates);
        var llm = new ChatClientLlm(recorder, "m");

        var convo = new Conversation();
        convo.Add(Message.UserText("go"));
        var response = await llm.CompleteAsync("sys", convo, Array.Empty<ITool>(), _ => { }, CancellationToken.None);

        var thinking = Assert.IsType<ThinkingBlock>(response.AssistantMessage.Content[0]);
        Assert.Equal("pondering", thinking.Thinking);
        Assert.Equal("sig==", thinking.Signature);
        Assert.IsType<ToolUseBlock>(response.AssistantMessage.Content[1]);

        // Upstream (blocks -> request messages): replaying that assistant turn maps the
        // thinking back to TextReasoningContent with the signature intact.
        convo.Add(response.AssistantMessage);
        convo.Add(Message.UserToolResults(new ContentBlock[] { new ToolResultBlock("id1", "data", false) }));
        recorder.Enqueue(new ChatResponseUpdate(ChatRole.Assistant, "done"));

        await llm.CompleteAsync("sys", convo, Array.Empty<ITool>(), _ => { }, CancellationToken.None);

        var replayedAssistant = recorder.LastMessages!.First(m => m.Role == ChatRole.Assistant);
        var reasoning = Assert.IsType<TextReasoningContent>(replayedAssistant.Contents[0]);
        Assert.Equal("pondering", reasoning.Text);
        Assert.Equal("sig==", reasoning.ProtectedData);
    }

    private sealed class RecordingChatClient : IChatClient
    {
        private readonly Queue<ChatResponseUpdate[]> _script = new();
        public List<ChatMessage>? LastMessages { get; private set; }

        public RecordingChatClient(ChatResponseUpdate[] first) => _script.Enqueue(first);
        public void Enqueue(params ChatResponseUpdate[] updates) => _script.Enqueue(updates);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastMessages = messages.ToList();
            foreach (var u in _script.Dequeue())
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
