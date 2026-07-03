using CodeStack.Ratchet.Core;

namespace CodeStack.Ratchet.Tests.Support;

/// <summary>
/// The scripted <see cref="ILlmClient"/> the design docs call for: returns a fixed
/// sequence of responses and records what each call saw, so loop behaviour is
/// testable without a network.
/// </summary>
public sealed class ScriptedLlmClient : ILlmClient
{
    private readonly Queue<Func<Conversation, LlmResponse>> _script = new();

    public List<IReadOnlyList<Message>> CallTranscripts { get; } = new();
    public List<string> SystemPrompts { get; } = new();

    public ScriptedLlmClient Enqueue(LlmResponse response)
    {
        _script.Enqueue(_ => response);
        return this;
    }

    public ScriptedLlmClient Enqueue(Func<Conversation, LlmResponse> respond)
    {
        _script.Enqueue(respond);
        return this;
    }

    public ScriptedLlmClient EnqueueThrow(Exception ex)
    {
        _script.Enqueue(_ => throw ex);
        return this;
    }

    public Task<LlmResponse> CompleteAsync(
        string systemPrompt,
        Conversation conversation,
        IReadOnlyCollection<ITool> tools,
        Action<string> onTextDelta,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();   // honor cancellation, as the real clients do
        SystemPrompts.Add(systemPrompt);
        CallTranscripts.Add(conversation.Messages.ToList());
        if (_script.Count == 0)
            throw new InvalidOperationException("ScriptedLlmClient: no scripted response left for this call.");
        var response = _script.Dequeue()(conversation);
        foreach (var text in response.AssistantMessage.Content.OfType<TextBlock>())
            onTextDelta(text.Text);
        return Task.FromResult(response);
    }

    // -- response shorthands --------------------------------------------------

    public static LlmResponse Text(string text, string stopReason = "end_turn") =>
        new(new Message(Role.Assistant, new ContentBlock[] { new TextBlock(text) }), stopReason, 10, 5);

    public static LlmResponse ToolCall(string id, string name, string inputJson, string stopReason = "tool_use") =>
        new(new Message(Role.Assistant, new ContentBlock[] { new ToolUseBlock(id, name, inputJson) }), stopReason, 10, 5);

    public static LlmResponse Blocks(string stopReason, params ContentBlock[] blocks) =>
        new(new Message(Role.Assistant, blocks), stopReason, 10, 5);
}

/// <summary>A tool that records its inputs and returns a canned result (or throws).</summary>
public sealed class RecordingTool : ITool
{
    private readonly Func<string, string> _execute;

    public RecordingTool(string name, Func<string, string>? execute = null)
    {
        Name = name;
        _execute = execute ?? (_ => "ok");
    }

    public string Name { get; }
    public string Description => "test tool";
    public string InputSchemaJson => """{"type":"object","properties":{}}""";
    public List<string> Inputs { get; } = new();

    public Task<string> ExecuteAsync(string inputJson, CancellationToken ct)
    {
        Inputs.Add(inputJson);
        return Task.FromResult(_execute(inputJson));
    }
}

