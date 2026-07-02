namespace CodeStack.Ratchet.Core;

/// <summary>
/// The conversation transcript. This is the agent's entire memory: a flat list
/// of messages that grows every turn and is re-sent in full on each model call.
/// There is no hidden state — what you see here is exactly what the model sees.
/// </summary>
public sealed class Conversation
{
    private readonly List<Message> _messages = new();

    public IReadOnlyList<Message> Messages => _messages;

    public void Add(Message message) => _messages.Add(message);
}

public enum Role
{
    User,
    Assistant
}

/// <summary>
/// One message in the transcript. A message carries one or more content blocks.
/// The assistant may emit text and tool-use blocks in the same message; the user
/// turn that follows carries the matching tool-result blocks.
/// </summary>
public sealed record Message(Role Role, IReadOnlyList<ContentBlock> Content)
{
    public static Message UserText(string text) =>
        new(Role.User, new ContentBlock[] { new TextBlock(text) });

    public static Message UserToolResults(IReadOnlyList<ContentBlock> results) =>
        new(Role.User, results);
}

/// <summary>
/// Base for the four content block shapes that cross the wire. Modelling these
/// explicitly (rather than as raw JSON) is the whole point of the learning
/// exercise — you can see exactly what a tool call and its result look like.
/// </summary>
public abstract record ContentBlock;

public sealed record TextBlock(string Text) : ContentBlock;

/// <summary>An assistant request to run a tool, with a correlation id.</summary>
public sealed record ToolUseBlock(string Id, string Name, string InputJson) : ContentBlock;

/// <summary>The user-side reply to a ToolUseBlock, matched by ToolUseId.</summary>
public sealed record ToolResultBlock(string ToolUseId, string Content, bool IsError) : ContentBlock;

/// <summary>
/// Extended reasoning from a thinking-enabled model. The signature is the API's
/// integrity stamp: an assistant turn that used tools must be replayed with its
/// thinking block (signature included) verbatim, or the request is rejected —
/// so this block round-trips through the transcript untouched.
/// </summary>
public sealed record ThinkingBlock(string Thinking, string Signature) : ContentBlock;

/// <summary>
/// Reasoning the API returned encrypted (safety-filtered). Fully opaque: carried
/// and replayed verbatim, never displayed.
/// </summary>
public sealed record RedactedThinkingBlock(string Data) : ContentBlock;
