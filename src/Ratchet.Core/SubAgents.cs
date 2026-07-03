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
/// <summary>
/// Guards against runaway nested delegation (a sub-agent that spawns a team that spawns
/// sub-agents…). Depth flows down async contexts via <see cref="AsyncLocal{T}"/>; a change
/// in one member's flow is invisible to its siblings, so it counts nesting, not fan-out width.
/// </summary>
internal static class Delegation
{
    private static readonly AsyncLocal<int> _depth = new();

    /// <summary>Maximum nesting of delegate/team calls. Test hook.</summary>
    internal static int MaxDepth = 3;

    public static int Depth => _depth.Value;
    public static bool AtLimit => _depth.Value >= MaxDepth;

    public static IDisposable Enter()
    {
        _depth.Value++;
        return new Pop();
    }

    private sealed class Pop : IDisposable
    {
        private bool _done;
        public void Dispose() { if (!_done) { _done = true; _depth.Value--; } }
    }
}

/// <summary>
/// Bounds a nested agent's tool loop by counting assistant turns through the observer seam
/// and cancelling once the budget is hit — a looping delegate can't burn tokens forever, and
/// the loop itself stays untouched (the cap lives on <see cref="IAgentObserver"/>).
/// </summary>
internal sealed class BudgetObserver : IAgentObserver
{
    private readonly int _maxTurns;
    private readonly CancellationTokenSource _cts;
    private int _turns;

    public BudgetObserver(int maxTurns, CancellationTokenSource cts) { _maxTurns = maxTurns; _cts = cts; }

    public void OnMessageAppended(Message message)
    {
        if (message.Role == Role.Assistant && ++_turns >= _maxTurns)
            _cts.Cancel();
    }

    public void OnAssistantTextDelta(string delta) { }
    public void OnAssistantTextEnd() { }
    public void OnToolCall(string toolName, string inputJson) { }
    public void OnToolResult(string toolName, string content, bool isError) { }
    public void OnUsage(int inputTokens, int outputTokens) { }
}

public sealed class DelegateTool : ITool
{
    private readonly ILlmClient _llm;
    private readonly string _systemPrompt;
    private readonly IReadOnlyList<ITool> _tools;
    private readonly IToolGate _gate;
    private readonly int _maxTurns;

    /// <param name="gate">Scopes the delegate to its role — e.g. a <see cref="ReadOnlyGate"/> for an
    /// investigator. Defaults to <see cref="AllowAllGate"/> (the nested agent is as free as the parent).</param>
    /// <param name="maxTurns">Iteration ceiling for the nested loop (a runaway delegate is cut off).</param>
    public DelegateTool(string name, string description, string systemPrompt, ILlmClient llm,
        IReadOnlyList<ITool> tools, IToolGate? gate = null, int maxTurns = 16)
    {
        Name = name;
        Description = description;
        _systemPrompt = systemPrompt;
        _llm = llm;
        _tools = tools;
        _gate = gate ?? AllowAllGate.Instance;
        _maxTurns = maxTurns;
    }

    public string Name { get; }
    public string Description { get; }

    public string InputSchemaJson => """
        {"type":"object","properties":{"task":{"type":"string","description":"The question or task to delegate, with any context the delegate needs (it does not see this conversation)."}},"required":["task"]}
        """;

    public async Task<string> ExecuteAsync(string inputJson, CancellationToken ct)
    {
        var task = Json.GetString(inputJson, "task");
        if (Delegation.AtLimit)
            return $"(delegation refused: nesting limit {Delegation.MaxDepth} reached — a delegate cannot keep spawning delegates)";
        using var _ = Delegation.Enter();

        var conversation = new Conversation();
        conversation.Add(Message.UserText(task));

        // Own budget: cancel this delegate's loop after _maxTurns without touching the
        // caller's token. Its own cancellation is not an error — return what it produced.
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var registry = new ToolRegistry(_tools);
        var agent = new Agent(_llm, registry, _systemPrompt, new BudgetObserver(_maxTurns, budgetCts), _gate);
        try
        {
            await agent.RunTurnAsync(conversation, budgetCts.Token);
        }
        catch (OperationCanceledException) when (budgetCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Hit its own iteration budget — fall through and return the partial work.
        }

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
public static partial class SubAgents
{
    /// <param name="root">The workspace the explore sub-agent is confined to
    /// (default: the current directory).</param>
    public static IEnumerable<ITool> Build(ILlmClient llm, string? root = null)
    {
        root ??= Directory.GetCurrentDirectory();

        // The explorer investigates and must not mutate anything — and must not read
        // outside the workspace either. Read-only is enforced by the ReadOnlyGate in the
        // loop; the workspace scope is enforced by the tools themselves (a rooted read +
        // a rooted search), so neither constraint can be prompted around. Read-only
        // WITHOUT scoping would still let a delegate page ~/.ssh into a transcript.
        IReadOnlyList<ITool> exploreTools = [new ReadTool(access: null, root: root), new SearchTool(root)];
        yield return new DelegateTool(
            "explore",
            "Delegate a focused, READ-ONLY investigation of the codebase to a sub-agent. Input: a clear " +
            "question plus any context (it does not see this conversation). It reads files and searches " +
            "code within the workspace, then returns findings. Use it to investigate without filling your own context.",
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
