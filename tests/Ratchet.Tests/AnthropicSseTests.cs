using System.Text;
using CodeStack.Ratchet.Core;
using CodeStack.Ratchet.Llm;
using Xunit;

namespace CodeStack.Ratchet.Tests;

/// <summary>
/// The hand-rolled SSE parser (<c>AnthropicClient.ConsumeStreamAsync</c>) against
/// canned Messages API event streams — the wire format is the pedagogical core of
/// the project, so it gets wire-level tests.
/// </summary>
public sealed class AnthropicSseTests
{
    private static Task<LlmResponse> Consume(string sse, Action<string>? onDelta = null)
    {
        var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(sse)));
        return AnthropicClient.ConsumeStreamAsync(reader, onDelta ?? (_ => { }), CancellationToken.None);
    }

    [Fact]
    public async Task TextStream_AssemblesText_TokensAndStopReason()
    {
        var deltas = new List<string>();
        var response = await Consume("""
            event: message_start
            data: {"type":"message_start","message":{"usage":{"input_tokens":25,"cache_creation_input_tokens":10,"cache_read_input_tokens":5}}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

            event: ping
            data: {"type":"ping"}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello, "}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"world"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":12}}

            event: message_stop
            data: {"type":"message_stop"}

            """, deltas.Add);

        var text = Assert.IsType<TextBlock>(Assert.Single(response.AssistantMessage.Content));
        Assert.Equal("Hello, world", text.Text);
        Assert.Equal(new[] { "Hello, ", "world" }, deltas); // streamed live, in order
        Assert.Equal("end_turn", response.StopReason);
        Assert.Equal(40, response.InputTokens); // 25 + 10 cache-write + 5 cache-read
        Assert.Equal(12, response.OutputTokens);
    }

    [Fact]
    public async Task ToolUseStream_ReassemblesInputJson_FromPartialFragments()
    {
        var response = await Consume("""
            data: {"type":"message_start","message":{"usage":{"input_tokens":10}}}
            data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Reading the file."}}
            data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_abc","name":"read","input":{}}}
            data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"pa"}}
            data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"th\":\"a"}}
            data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":".txt\"}"}}
            data: {"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":30}}
            data: {"type":"message_stop"}

            """);

        Assert.Equal("tool_use", response.StopReason);
        Assert.Equal(2, response.AssistantMessage.Content.Count);
        var use = Assert.IsType<ToolUseBlock>(response.AssistantMessage.Content[1]);
        Assert.Equal("toolu_abc", use.Id);
        Assert.Equal("read", use.Name);
        Assert.Equal("""{"path":"a.txt"}""", use.InputJson); // fragments joined then parsed whole
    }

    [Fact]
    public async Task ToolUseWithNoArguments_YieldsEmptyObjectJson()
    {
        var response = await Consume("""
            data: {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_1","name":"git_status","input":{}}}
            data: {"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":5}}
            data: {"type":"message_stop"}

            """);

        var use = Assert.IsType<ToolUseBlock>(Assert.Single(response.AssistantMessage.Content));
        Assert.Equal("{}", use.InputJson); // never empty-string JSON
    }

    [Fact]
    public async Task ErrorEvent_Throws_Typed_WithTheApiMessage()
    {
        var ex = await Assert.ThrowsAsync<LlmException>(() => Consume("""
            data: {"type":"message_start","message":{"usage":{"input_tokens":10}}}
            data: {"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}

            """));
        Assert.Contains("Overloaded", ex.Message);
        Assert.Equal("overloaded_error", ex.ErrorType);
        Assert.True(ex.Retryable);
    }

    [Fact]
    public async Task BlocksAreOrderedByIndex_NotArrivalOrder()
    {
        // Nothing in SSE guarantees block 0 finishes before block 1 starts; ordering
        // must come from the index, which the SortedDictionary provides.
        var response = await Consume("""
            data: {"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}
            data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"second"}}
            data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"first"}}
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":2}}
            data: {"type":"message_stop"}

            """);

        Assert.Equal(
            new[] { "first", "second" },
            response.AssistantMessage.Content.Cast<TextBlock>().Select(t => t.Text));
    }

    [Fact]
    public async Task TruncatedStream_Throws_NotReportsSuccess()
    {
        // Cut cleanly between events (no message_stop ever arrives) — the case a
        // dropped connection produces. Must surface as an interruption, not a
        // short-but-successful answer.
        await Assert.ThrowsAsync<LlmStreamInterruptedException>(() => Consume("""
            data: {"type":"message_start","message":{"usage":{"input_tokens":10}}}
            data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"half an answer"}}

            """));
    }

    [Fact]
    public async Task StreamCutAfterMessageDeltaButBeforeMessageStop_StillThrows()
    {
        await Assert.ThrowsAsync<LlmStreamInterruptedException>(() => Consume("""
            data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"almost done"}}
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":3}}

            """));
    }

    [Fact]
    public async Task ThinkingBlocks_ArePreserved_WithSignature_AndNotStreamedAsText()
    {
        var deltas = new List<string>();
        var response = await Consume("""
            data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}
            data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"reasoning"}}
            data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"..."}}
            data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"sig=="}}
            data: {"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}
            data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"answer"}}
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":9}}
            data: {"type":"message_stop"}

            """, deltas.Add);

        Assert.Equal(2, response.AssistantMessage.Content.Count);
        var thinking = Assert.IsType<ThinkingBlock>(response.AssistantMessage.Content[0]);
        Assert.Equal("reasoning...", thinking.Thinking);
        Assert.Equal("sig==", thinking.Signature);   // replay requires the signature verbatim
        Assert.Equal("answer", ((TextBlock)response.AssistantMessage.Content[1]).Text);
        Assert.Equal(new[] { "answer" }, deltas);    // thinking never leaks into the text stream
    }

    [Fact]
    public async Task RedactedThinking_IsCarriedOpaquely()
    {
        var response = await Consume("""
            data: {"type":"content_block_start","index":0,"content_block":{"type":"redacted_thinking","data":"ENCRYPTED=="}}
            data: {"type":"content_block_stop","index":0}
            data: {"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}
            data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"ok"}}
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":2}}
            data: {"type":"message_stop"}

            """);

        var redacted = Assert.IsType<RedactedThinkingBlock>(response.AssistantMessage.Content[0]);
        Assert.Equal("ENCRYPTED==", redacted.Data);
    }
}
