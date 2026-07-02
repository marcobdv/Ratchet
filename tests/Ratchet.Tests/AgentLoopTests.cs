using CodeStack.Ratchet.Core;
using CodeStack.Ratchet.Tests.Support;
using Xunit;

namespace CodeStack.Ratchet.Tests;

/// <summary>
/// The loop under a scripted <see cref="ILlmClient"/>: the invariants the loop itself
/// creates (tool dispatch, result pairing, gate enforcement, error-as-result).
/// </summary>
public sealed class AgentLoopTests
{
    private static Agent MakeAgent(ILlmClient llm, IToolGate? gate = null, params ITool[] tools) =>
        new(llm, new ToolRegistry(tools), "system", NullObserver.Instance, gate);

    [Fact]
    public async Task PlainTextTurn_AddsOneAssistantMessage_AndStops()
    {
        var llm = new ScriptedLlmClient().Enqueue(ScriptedLlmClient.Text("hello"));
        var convo = new Conversation();
        convo.Add(Message.UserText("hi"));

        await MakeAgent(llm).RunTurnAsync(convo, CancellationToken.None);

        Assert.Equal(2, convo.Messages.Count);
        Assert.Equal(Role.Assistant, convo.Messages[1].Role);
        Assert.Single(llm.CallTranscripts); // exactly one model call, no tool loop
    }

    [Fact]
    public async Task ToolUseTurn_RunsTool_PairsResultById_AndLoops()
    {
        var tool = new RecordingTool("echo", input => "echoed:" + input);
        var llm = new ScriptedLlmClient()
            .Enqueue(ScriptedLlmClient.ToolCall("toolu_1", "echo", """{"x":1}"""))
            .Enqueue(ScriptedLlmClient.Text("done"));
        var convo = new Conversation();
        convo.Add(Message.UserText("go"));

        await MakeAgent(llm, tools: tool).RunTurnAsync(convo, CancellationToken.None);

        Assert.Equal("""{"x":1}""", Assert.Single(tool.Inputs));

        // user, assistant(tool_use), user(tool_result), assistant(text)
        Assert.Equal(4, convo.Messages.Count);
        var result = Assert.IsType<ToolResultBlock>(Assert.Single(convo.Messages[2].Content));
        Assert.Equal("toolu_1", result.ToolUseId);
        Assert.False(result.IsError);
        Assert.Equal("echoed:" + """{"x":1}""", result.Content);

        // The second model call saw the tool result appended.
        Assert.Equal(2, llm.CallTranscripts.Count);
        Assert.Equal(3, llm.CallTranscripts[1].Count);
    }

    [Fact]
    public async Task ParallelToolCalls_ProduceOneUserMessage_WithAllResultsInOrder()
    {
        var a = new RecordingTool("a", _ => "ra");
        var b = new RecordingTool("b", _ => "rb");
        var llm = new ScriptedLlmClient()
            .Enqueue(ScriptedLlmClient.Blocks("tool_use",
                new ToolUseBlock("id_a", "a", "{}"),
                new ToolUseBlock("id_b", "b", "{}")))
            .Enqueue(ScriptedLlmClient.Text("done"));
        var convo = new Conversation();
        convo.Add(Message.UserText("go"));

        await MakeAgent(llm, tools: new ITool[] { a, b }).RunTurnAsync(convo, CancellationToken.None);

        // API requirement: all tool_results for one assistant message in ONE user message.
        var resultMsg = convo.Messages[2];
        Assert.Equal(Role.User, resultMsg.Role);
        var results = resultMsg.Content.Cast<ToolResultBlock>().ToList();
        Assert.Equal(new[] { "id_a", "id_b" }, results.Select(r => r.ToolUseId));
        Assert.Equal(new[] { "ra", "rb" }, results.Select(r => r.Content));
    }

    [Fact]
    public async Task FaultingTool_BecomesErrorResult_NotException()
    {
        var boom = new RecordingTool("boom", _ => throw new InvalidOperationException("kaput"));
        var llm = new ScriptedLlmClient()
            .Enqueue(ScriptedLlmClient.ToolCall("t1", "boom", "{}"))
            .Enqueue(ScriptedLlmClient.Text("recovered"));
        var convo = new Conversation();
        convo.Add(Message.UserText("go"));

        await MakeAgent(llm, tools: boom).RunTurnAsync(convo, CancellationToken.None);

        var result = Assert.IsType<ToolResultBlock>(Assert.Single(convo.Messages[2].Content));
        Assert.True(result.IsError);
        Assert.Contains("kaput", result.Content);
    }

    [Fact]
    public async Task UnknownTool_BecomesErrorResult()
    {
        var llm = new ScriptedLlmClient()
            .Enqueue(ScriptedLlmClient.ToolCall("t1", "no_such_tool", "{}"))
            .Enqueue(ScriptedLlmClient.Text("ok"));
        var convo = new Conversation();
        convo.Add(Message.UserText("go"));

        await MakeAgent(llm).RunTurnAsync(convo, CancellationToken.None);

        var result = Assert.IsType<ToolResultBlock>(Assert.Single(convo.Messages[2].Content));
        Assert.True(result.IsError);
        Assert.Contains("Unknown tool", result.Content);
    }

    [Fact]
    public async Task ReadOnlyGate_DeniesMutatingTool_AndToolNeverRuns()
    {
        var write = new RecordingTool("write", _ => "wrote");
        var llm = new ScriptedLlmClient()
            .Enqueue(ScriptedLlmClient.ToolCall("t1", "write", "{}"))
            .Enqueue(ScriptedLlmClient.Text("ok"));
        var convo = new Conversation();
        convo.Add(Message.UserText("go"));

        await MakeAgent(llm, new ReadOnlyGate(), write).RunTurnAsync(convo, CancellationToken.None);

        Assert.Empty(write.Inputs); // enforcement in the loop, not the prompt
        var result = Assert.IsType<ToolResultBlock>(Assert.Single(convo.Messages[2].Content));
        Assert.True(result.IsError);
        Assert.Contains("Permission denied", result.Content);
    }

    [Fact]
    public async Task ReadOnlyGate_AllowsReadTool()
    {
        var read = new RecordingTool("read", _ => "contents");
        var llm = new ScriptedLlmClient()
            .Enqueue(ScriptedLlmClient.ToolCall("t1", "read", "{}"))
            .Enqueue(ScriptedLlmClient.Text("ok"));
        var convo = new Conversation();
        convo.Add(Message.UserText("go"));

        await MakeAgent(llm, new ReadOnlyGate(), read).RunTurnAsync(convo, CancellationToken.None);

        Assert.Single(read.Inputs);
        var result = Assert.IsType<ToolResultBlock>(Assert.Single(convo.Messages[2].Content));
        Assert.False(result.IsError);
    }

    [Fact]
    public async Task FaultingGate_BecomesErrorResult_NotACrash()
    {
        var gate = new ThrowingGate();
        var tool = new RecordingTool("echo");
        var llm = new ScriptedLlmClient()
            .Enqueue(ScriptedLlmClient.ToolCall("t1", "echo", "{}"))
            .Enqueue(ScriptedLlmClient.Text("ok"));
        var convo = new Conversation();
        convo.Add(Message.UserText("go"));

        await MakeAgent(llm, gate, tool).RunTurnAsync(convo, CancellationToken.None);

        Assert.Empty(tool.Inputs);
        var result = Assert.IsType<ToolResultBlock>(Assert.Single(convo.Messages[2].Content));
        Assert.True(result.IsError);
    }

    // ---- the stop-reason policy (ADR-0010) -----------------------------------

    [Fact]
    public async Task MaxTokensStopWithPendingToolUse_ClosesThemWithErrorResults()
    {
        var tool = new RecordingTool("echo");
        var llm = new ScriptedLlmClient()
            .Enqueue(ScriptedLlmClient.ToolCall("t1", "echo", "{}", stopReason: "max_tokens"));
        var convo = new Conversation();
        convo.Add(Message.UserText("go"));

        await MakeAgent(llm, tools: tool).RunTurnAsync(convo, CancellationToken.None);

        // The transcript stays valid: every tool_use answered, the tool never ran,
        // the turn ended (no second model call), and the result names the interruption.
        Assert.Empty(OrphanedToolUseIds(convo));
        Assert.Empty(tool.Inputs);
        Assert.Single(llm.CallTranscripts);
        var result = Assert.IsType<ToolResultBlock>(Assert.Single(convo.Messages[2].Content));
        Assert.True(result.IsError);
        Assert.Contains("max_tokens", result.Content);
    }

    [Fact]
    public async Task MaxTokensStopWithoutToolUse_JustEndsTheTurn()
    {
        var llm = new ScriptedLlmClient()
            .Enqueue(ScriptedLlmClient.Text("truncated answ", stopReason: "max_tokens"));
        var convo = new Conversation();
        convo.Add(Message.UserText("go"));

        await MakeAgent(llm).RunTurnAsync(convo, CancellationToken.None);

        Assert.Equal(2, convo.Messages.Count);
        Assert.Single(llm.CallTranscripts);
    }

    [Fact]
    public async Task OnMessageAppended_FiresForEveryAppendedMessage_InOrder()
    {
        var recorder = new AppendRecorder();
        var tool = new RecordingTool("echo");
        var llm = new ScriptedLlmClient()
            .Enqueue(ScriptedLlmClient.ToolCall("t1", "echo", "{}"))
            .Enqueue(ScriptedLlmClient.Text("done"));
        var convo = new Conversation();
        convo.Add(Message.UserText("go"));

        var agent = new Agent(llm, new ToolRegistry(new[] { tool }), "system", recorder);
        await agent.RunTurnAsync(convo, CancellationToken.None);

        // assistant(tool_use), user(tool_result), assistant(text) — the durable trail
        // a host would checkpoint incrementally (the prior user prompt predates the turn).
        Assert.Equal(3, recorder.Appended.Count);
        Assert.Equal(Role.Assistant, recorder.Appended[0].Role);
        Assert.IsType<ToolResultBlock>(recorder.Appended[1].Content[0]);
        Assert.Equal("done", ((TextBlock)recorder.Appended[2].Content[0]).Text);
    }

    [Fact]
    public async Task OnMessageAppended_SeesCompletedWork_EvenWhenALaterModelCallThrows()
    {
        var recorder = new AppendRecorder();
        var tool = new RecordingTool("echo");
        var llm = new ScriptedLlmClient()
            .Enqueue(ScriptedLlmClient.ToolCall("t1", "echo", "{}"))          // iteration 1 completes
            .EnqueueThrow(new InvalidOperationException("network died"));      // iteration 2 model call fails
        var convo = new Conversation();
        convo.Add(Message.UserText("go"));

        var agent = new Agent(llm, new ToolRegistry(new[] { tool }), "system", recorder);
        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.RunTurnAsync(convo, CancellationToken.None));

        // The assistant tool_use and its result were appended before the failure — a host
        // checkpointing on OnMessageAppended keeps that work instead of dropping the turn.
        Assert.Equal(2, recorder.Appended.Count);
        Assert.IsType<ToolResultBlock>(recorder.Appended[1].Content[0]);
    }

    [Fact]
    public async Task CancelledTool_PropagatesCancellation_Promptly()
    {
        using var cts = new CancellationTokenSource();
        var tool = new RecordingTool("slow", _ => { cts.Cancel(); cts.Token.ThrowIfCancellationRequested(); return "never"; });
        var llm = new ScriptedLlmClient()
            .Enqueue(ScriptedLlmClient.ToolCall("t1", "slow", "{}"));
        var convo = new Conversation();
        convo.Add(Message.UserText("go"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => MakeAgent(llm, tools: tool).RunTurnAsync(convo, cts.Token));

        Assert.Single(llm.CallTranscripts); // no second model call after cancellation
    }

    private static IReadOnlyList<string> OrphanedToolUseIds(Conversation convo)
    {
        var orphaned = new List<string>();
        for (var i = 0; i < convo.Messages.Count; i++)
        {
            foreach (var use in convo.Messages[i].Content.OfType<ToolUseBlock>())
            {
                var answered = i + 1 < convo.Messages.Count &&
                    convo.Messages[i + 1].Content.OfType<ToolResultBlock>()
                        .Any(r => r.ToolUseId == use.Id);
                if (!answered) orphaned.Add(use.Id);
            }
        }
        return orphaned;
    }

    private sealed class ThrowingGate : IToolGate
    {
        public Task<ToolGateDecision> CheckAsync(string toolName, string inputJson, CancellationToken ct) =>
            throw new InvalidOperationException("gate exploded");
    }

    private sealed class AppendRecorder : IAgentObserver
    {
        public List<Message> Appended { get; } = new();
        public void OnMessageAppended(Message message) => Appended.Add(message);
        public void OnAssistantTextDelta(string delta) { }
        public void OnAssistantTextEnd() { }
        public void OnToolCall(string toolName, string inputJson) { }
        public void OnToolResult(string toolName, string content, bool isError) { }
        public void OnUsage(int inputTokens, int outputTokens) { }
    }
}
