using System.Text;

namespace CodeStack.Ratchet.Core;

/// <summary>
/// Delegation as a tool. A <see cref="DelegateTool"/> is an <see cref="ITool"/> that, when called,
/// runs a *nested* <see cref="Agent"/> — its own loop over the same <see cref="ILlmClient"/> with a
/// scoped tool set and a specialist system prompt — and returns that agent's final text to the
/// caller. This is the whole "sub-agent / advisor" idea expressed on Ratchet's existing seams:
/// no framework, the loop is reused, and the parent keeps ownership of the transcript.
///
///   - A <b>sub-agent</b> (e.g. "explore") is given a read-only-ish tool subset and does work.
///   - An <b>advisor</b> is given no tools and returns a focused second opinion.
/// </summary>
public sealed class DelegateTool : ITool
{
    private readonly ILlmClient _llm;
    private readonly string _systemPrompt;
    private readonly IReadOnlyList<ITool> _tools;
    private readonly IToolGate _gate;

    /// <param name="gate">Scopes the delegate to its role — e.g. a <see cref="ReadOnlyGate"/> for an
    /// investigator. Defaults to <see cref="AllowAllGate"/> (the nested agent is as free as the parent).</param>
    public DelegateTool(string name, string description, string systemPrompt, ILlmClient llm,
        IReadOnlyList<ITool> tools, IToolGate? gate = null)
    {
        Name = name;
        Description = description;
        _systemPrompt = systemPrompt;
        _llm = llm;
        _tools = tools;
        _gate = gate ?? AllowAllGate.Instance;
    }

    public string Name { get; }
    public string Description { get; }

    public string InputSchemaJson => """
        {"type":"object","properties":{"task":{"type":"string","description":"The question or task to delegate, with any context the delegate needs (it does not see this conversation)."}},"required":["task"]}
        """;

    public async Task<string> ExecuteAsync(string inputJson, CancellationToken ct)
    {
        var task = Json.GetString(inputJson, "task");

        var conversation = new Conversation();
        conversation.Add(Message.UserText(task));

        var registry = new ToolRegistry(_tools);
        var agent = new Agent(_llm, registry, _systemPrompt, NullObserver.Instance, _gate);
        await agent.RunTurnAsync(conversation, ct);

        var text = LastAssistantText(conversation);
        return string.IsNullOrWhiteSpace(text) ? "(the delegate returned no text)" : text;
    }

    private static string LastAssistantText(Conversation conversation)
    {
        for (var i = conversation.Messages.Count - 1; i >= 0; i--)
        {
            var msg = conversation.Messages[i];
            if (msg.Role != Role.Assistant) continue;
            var sb = new StringBuilder();
            foreach (var block in msg.Content)
                if (block is TextBlock t) sb.Append(t.Text);
            if (sb.Length > 0) return sb.ToString();
        }
        return "";
    }
}

/// <summary>Builds Ratchet's delegation tools: one investigative sub-agent and three advisors.</summary>
public static class SubAgents
{
    public static IEnumerable<ITool> Build(ILlmClient llm)
    {
        // The explorer investigates and must not mutate anything. Even though the top-level agent
        // is YOLO by design, a *delegated* agent is scoped to its role: it gets only read-only
        // tools (read + search), and a ReadOnlyGate enforces that in the loop so the constraint
        // can't be prompted around — no raw shell, no write/edit.
        IReadOnlyList<ITool> exploreTools = [new ReadTool(), new SearchTool()];
        yield return new DelegateTool(
            "explore",
            "Delegate a focused, READ-ONLY investigation of the codebase to a sub-agent. Input: a clear " +
            "question plus any context (it does not see this conversation). It reads files and searches " +
            "code, then returns findings. Use it to investigate without filling your own context.",
            ExplorerPrompt, llm, exploreTools, gate: new ReadOnlyGate());

        yield return new DelegateTool(
            "security_advisor",
            "Consult an independent security specialist for a second opinion. Input: your question plus " +
            "the relevant code or design (paste it — the advisor has no tools). Returns prioritized " +
            "security findings and fixes.",
            SecurityPrompt, llm, []);

        yield return new DelegateTool(
            "performance_advisor",
            "Consult an independent performance specialist. Input: your question plus the relevant code " +
            "(paste it — no tools). Returns concrete performance analysis and suggestions.",
            PerformancePrompt, llm, []);

        yield return new DelegateTool(
            "architecture_advisor",
            "Consult an independent software-architecture specialist. Input: your question plus context " +
            "about the design (paste it — no tools). Returns feedback on boundaries, coupling, testability " +
            "and simplicity.",
            ArchitecturePrompt, llm, []);
    }

    private const string ExplorerPrompt =
        "You are a read-only exploration sub-agent. Investigate the codebase to answer the given question " +
        "using the `read` and `search` tools (search does regex content search and filename globbing). You " +
        "cannot modify anything — that's enforced. Find the relevant code, then return a concise findings " +
        "report: the answer, the key locations as file:line, and how the pieces fit. Cite locations; don't " +
        "speculate beyond what you found.";

    private const string SecurityPrompt =
        "You are a security advisor giving a focused second opinion. You have no tools; reason only about what is " +
        "provided. Identify concrete security issues (injection, authz/authn, secret handling, unsafe deserialization, " +
        "path traversal, SSRF, crypto misuse, missing validation), rank by severity, and give specific fixes. If the " +
        "code is sound, say so plainly.";

    private const string PerformancePrompt =
        "You are a performance advisor giving a focused second opinion. You have no tools. Identify real hotspots " +
        "(allocations, quadratic/N+1 patterns, blocking or chatty I/O, missing async, cacheable repeated work, poor " +
        "complexity) and give concrete, measurable suggestions. Avoid premature-optimization advice; if the code is " +
        "fine for its scale, say so.";

    private const string ArchitecturePrompt =
        "You are a software-architecture advisor giving a focused second opinion. You have no tools. Evaluate " +
        "boundaries, coupling and cohesion, testability, error handling, and simplicity. Recommend pragmatic " +
        "improvements that fit the codebase; call out overengineering and prefer the simplest design that works.";
}

/// <summary>An observer that discards everything — used for nested sub-agent runs.</summary>
public sealed class NullObserver : IAgentObserver
{
    public static readonly NullObserver Instance = new();
    public void OnAssistantTextDelta(string delta) { }
    public void OnAssistantTextEnd() { }
    public void OnToolCall(string toolName, string inputJson) { }
    public void OnToolResult(string toolName, string content, bool isError) { }
    public void OnUsage(int inputTokens, int outputTokens) { }
}
