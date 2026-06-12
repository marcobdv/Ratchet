namespace CodeStack.Ratchet.Core;

/// <summary>
/// The one model call. Everything provider-specific (Anthropic vs OpenAI wire
/// formats) lives behind this seam. The loop only knows "send the transcript +
/// tool specs, get an assistant message back".
/// </summary>
public interface ILlmClient
{
    Task<LlmResponse> CompleteAsync(
        string systemPrompt,
        Conversation conversation,
        IReadOnlyCollection<ITool> tools,
        CancellationToken ct);
}

/// <summary>
/// What came back from one model call: the assistant message (which may contain
/// text and/or tool-use blocks) plus token counts for the observability print.
/// StopReason "tool_use" is the loop's signal to run tools and continue.
/// </summary>
public sealed record LlmResponse(
    Message AssistantMessage,
    string StopReason,
    int InputTokens,
    int OutputTokens);
