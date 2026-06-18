using CodeStack.Ratchet.Core;

namespace CodeStack.Ratchet.Workflow;

/// <summary>
/// Wraps a tier's <see cref="ILlmClient"/> and records its token usage into a shared
/// <see cref="CostTally"/>. Because the scheduler routes EVERY completion for a tier
/// through this one wrapper — driver phases, the classifier, judge gates, advisor
/// consults, handover authoring — per-tier accounting is captured without any call
/// site remembering to report. That's the point: cost legibility falls out of the seam.
/// </summary>
internal sealed class MeteredLlmClient : ILlmClient
{
    private readonly ILlmClient _inner;
    private readonly string _tier;
    private readonly CostTally _tally;

    public MeteredLlmClient(ILlmClient inner, string tier, CostTally tally)
    {
        _inner = inner;
        _tier = tier;
        _tally = tally;
    }

    public async Task<LlmResponse> CompleteAsync(
        string systemPrompt, Conversation conversation, IReadOnlyCollection<ITool> tools,
        Action<string> onTextDelta, CancellationToken ct)
    {
        var resp = await _inner.CompleteAsync(systemPrompt, conversation, tools, onTextDelta, ct);
        _tally.Add(_tier, resp.InputTokens, resp.OutputTokens);
        return resp;
    }
}
