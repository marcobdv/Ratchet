using System.Text;
using CodeStack.Ratchet.Core;
using CodeStack.Ratchet.Llm;
using CodeStack.Ratchet.Storage.Sqlite;
using CodeStack.Ratchet.Tools.Mcp;
using CodeStack.Ratchet.Tools.Roslyn;

// Register MSBuild before any Roslyn MSBuildWorkspace type loads (must be first).
MsBuildBootstrap.Ensure();

// Roslyn pipeline self-check (no API key needed): `ratchet --roslyn-check`.
if (args.Contains("--roslyn-check"))
{
    using var rs = new RoslynToolset(Directory.GetCurrentDirectory());
    foreach (var (tool, input) in new[]
             {
                 ("roslyn_diagnostics", "{}"),
                 ("roslyn_find_symbol", "{\"name\":\"Agent\"}"),
                 ("roslyn_find_references", "{\"name\":\"CompleteAsync\"}"),
             })
    {
        Console.WriteLine($"── {tool} {input} ──");
        var t = rs.Tools.First(x => x.Name == tool);
        Console.WriteLine(await t.ExecuteAsync(input, CancellationToken.None));
        Console.WriteLine();
    }
    return 0;
}

// ---- configuration --------------------------------------------------------
// API key from env so it never lands in source. Model is overridable so you can
// swap it without recompiling when a new one ships.
var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine("Set ANTHROPIC_API_KEY first:  setx ANTHROPIC_API_KEY \"sk-ant-...\"");
    return 1;
}

var model = Environment.GetEnvironmentVariable("RATCHET_MODEL") ?? "claude-sonnet-4-6";

// Shell for the bash tool: bash | cmd | pwsh. Defaults per-OS when unset.
var shell = ShellSpec.FromName(Environment.GetEnvironmentVariable("RATCHET_SHELL"));

// Auto-compaction threshold: when a turn's input context exceeds this many tokens,
// Ratchet authors a handover and starts fresh seeded with it (the self-triggered
// version of /handover). 0 / unset disables it; /compact triggers it manually.
int.TryParse(Environment.GetEnvironmentVariable("RATCHET_CONTEXT_LIMIT"), out var contextLimit);

// ---- composition root -----------------------------------------------------
// Storage backend: file (default) or sqlite. Both implement ISessionStore, so
// nothing below this line knows or cares which is in use — that's the seam.
ISessionStore store = (Environment.GetEnvironmentVariable("RATCHET_STORE")?.ToLowerInvariant()) switch
{
    "sqlite" => new SqliteSessionStore(Directory.GetCurrentDirectory()),
    _ => new FileSessionStore(Directory.GetCurrentDirectory()),
};
var handovers = new FileHandoverStore(Directory.GetCurrentDirectory());

// `ratchet --handover <id>` resumes COLD: a fresh session whose system prompt
// carries the handover doc as its working set, plus a `recall` tool wired to the
// prior session's tree so the model can page omitted detail back in. This is the
// deliberate alternative to in-place compaction — authored loss, not silent.
string? handoverContext = null;
ITool? recallTool = null;
var hi = Array.IndexOf(args, "--handover");
if (hi >= 0 && hi + 1 < args.Length)
{
    var srcId = args[hi + 1];
    var h = handovers.Load(srcId);
    if (h is null)
    {
        Console.Error.WriteLine($"No handover saved for session '{srcId}'.");
        (store as IDisposable)?.Dispose();
        return 1;
    }
    handoverContext = h.Content;
    recallTool = new RecallTool(store, h.SourceSessionId);
    Console.WriteLine($"resuming cold from handover of {h.SourceSessionId} ({h.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm})");
}

// Agent Skills discovered under .ratchet/skills (+ .claude/skills, ~/.ratchet/skills):
// names+descriptions go in the prompt; full bodies load on demand via load_skill.
var skills = SkillCatalog.Discover(Directory.GetCurrentDirectory());

// AGENTS.md (pi-style project context), the skill list, and — when resuming — the handover
// doc are all prepended to the system prompt. Plain text, no magic.
var systemPrompt = BuildSystemPrompt(handoverContext, skills.Describe());

// Provider seam: every provider flows through IChatClient (ChatClientLlm), except the
// original hand-rolled wire client kept as `anthropic-native` for wire-level transparency.
//   unset / "anthropic"  -> Anthropic via IChatClient (AnthropicChatClient)
//   "anthropic-native"   -> the original wire ILlmClient (no IChatClient)
// Other IChatClient providers (OpenAI, Azure, Ollama) drop in here as one more case.
// Built before the tool list because the sub-agents run nested loops over this same client.
var provider = Environment.GetEnvironmentVariable("RATCHET_PROVIDER")?.ToLowerInvariant();
ILlmClient llm = provider switch
{
    "anthropic-native" => new AnthropicClient(apiKey, model),
    _ => new ChatClientLlm(new AnthropicChatClient(apiKey, model), model),
};

// Shared read-before-write guard (read/write mark a file "known"; edit requires it),
// and the opt-in ConPTY shell (RATCHET_PTY=1) for a real TTY on Windows.
var access = new FileAccessLog();
var usePty = (Environment.GetEnvironmentVariable("RATCHET_PTY")?.Trim().ToLowerInvariant()) is "1" or "true" or "on";

// Base tool set (everything except `recall`, which is added per-agent below so
// auto-compaction can swap in a fresh one pointing at the just-archived session).
var planTool = new PlanTool();
var baseTools = new List<ITool>
{
    new ReadTool(access), new WriteTool(access), new EditTool(access), new BashTool(shell, usePty),
    new SkillTool(skills), planTool,
    new TestTool(shell, Environment.GetEnvironmentVariable("RATCHET_TEST_CMD")),
};
baseTools.AddRange(GitTools.Build(Directory.GetCurrentDirectory()));   // git_status / git_diff (read-only)
baseTools.AddRange(SubAgents.Build(llm, shell));                       // explore sub-agent + advisors

// Roslyn semantic-C# tools (loads the workspace's solution/project on first use).
using var roslyn = new RoslynToolset(Directory.GetCurrentDirectory());
baseTools.AddRange(roslyn.Tools);

// MCP servers from .mcp.json: each server tool becomes a Ratchet ITool.
await using var mcp = await McpToolset.ConnectAsync(Directory.GetCurrentDirectory(), Console.WriteLine, CancellationToken.None);
baseTools.AddRange(mcp.Tools);

var observer = new ConsoleObserver();

// The agent is rebuilt on compaction with a new system prompt + recall tool, so
// construct it through a factory over the (mutable) systemPrompt and recallTool.
Agent BuildAgent() =>
    new(llm, new ToolRegistry(recallTool is null ? baseTools : baseTools.Append(recallTool)), systemPrompt, observer);

var agent = BuildAgent();

// Sessions are trees of message nodes (see SessionTree). The path root..HEAD is
// the live conversation; rewinding HEAD and continuing forks a new branch.
var tree = new SessionTree();
string? sessionId = null;

// `ratchet --continue` (or -c) reopens the most recent session — unless we're
// resuming cold from a handover, which deliberately starts fresh.
if (handoverContext is null && args.Any(a => a is "--continue" or "-c"))
{
    var latest = store.List().FirstOrDefault();
    if (latest is not null)
    {
        tree = store.Load(latest.Id)!;
        sessionId = latest.Id;
        Console.WriteLine($"continued session {sessionId} ({tree.Count} nodes)");
    }
}

Console.WriteLine($"ratchet v0  ·  model: {model}  ·  provider: {provider ?? "anthropic"}  ·  shell: {shell.Name}  ·  cwd: {Directory.GetCurrentDirectory()}");
Console.WriteLine(recallTool is not null
    ? "Resumed from a handover — `recall` is available to page back into the prior session.\n"
    : "Type a request, or /help for commands. Ctrl+C to quit.\n");

// ---- the REPL: read a human line, run one agent turn, repeat --------------
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

while (!cts.IsCancellationRequested)
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.Write("you> ");
    Console.ResetColor();

    var line = Console.ReadLine();
    if (line is null) break;                 // EOF (piped input ended)
    if (string.IsNullOrWhiteSpace(line)) continue;

    // Slash-commands are handled locally and never sent to the model.
    if (line.StartsWith('/'))
    {
        await HandleCommandAsync(line);
        Console.WriteLine();
        continue;
    }

    // Add the human turn under HEAD, then run the agent over the root..HEAD path.
    tree.Append(Message.UserText(line));
    var conversation = tree.MaterializeConversation();
    var baseCount = conversation.Messages.Count;

    try
    {
        await agent.RunTurnAsync(conversation, cts.Token);

        // Fold the new assistant/tool messages back into the tree as a chain,
        // advancing HEAD — keeping the tree the single source of truth.
        for (var i = baseCount; i < conversation.Messages.Count; i++)
            tree.Append(conversation.Messages[i]);

        var wasNew = sessionId is null;
        sessionId = store.Save(sessionId, tree);
        if (wasNew)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  · session {sessionId} (auto-saving)");
            Console.ResetColor();
        }

        // Auto-compaction: once the context grows past the configured limit, fold it
        // into a handover and continue in a fresh session seeded with that doc.
        if (contextLimit > 0 && observer.LastInputTokens >= contextLimit && tree.Count > 0)
            await CompactAsync();
    }
    catch (OperationCanceledException)
    {
        break;
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[error] {ex.Message}");
        Console.ResetColor();
    }

    Console.WriteLine();
}

(llm as IDisposable)?.Dispose();
(store as IDisposable)?.Dispose();
Console.WriteLine("bye.");
return 0;

// ---- slash-command handling (local; never hits the model) -----------------
async Task HandleCommandAsync(string input)
{
    var parts = input.Split(' ', 2, StringSplitOptions.TrimEntries);
    var arg = parts.Length > 1 ? parts[1] : "";

    switch (parts[0].ToLowerInvariant())
    {
        case "/sessions":
            var sessions = store.List();
            if (sessions.Count == 0) { Console.WriteLine("  (no saved sessions yet)"); break; }
            foreach (var s in sessions)
                Console.WriteLine($"  {s.Id}  ·  {s.MessageCount,3} nodes  ·  {s.UpdatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}  ·  {s.Preview}");
            break;

        case "/resume":
            if (arg.Length == 0) { Console.WriteLine("  usage: /resume <id>"); break; }
            var loaded = store.Load(arg);
            if (loaded is null) { Console.WriteLine($"  no session '{arg}'"); break; }
            tree = loaded;
            sessionId = arg;
            Console.WriteLine($"  resumed '{arg}' ({tree.Count} nodes, head {tree.HeadId ?? "—"})");
            break;

        case "/new":
            tree = new SessionTree();
            sessionId = null;
            Console.WriteLine("  started a new session");
            break;

        case "/tree":
            PrintTree();
            break;

        case "/rewind":
            var n = 1;
            if (arg.Length > 0 && !int.TryParse(arg, out n)) { Console.WriteLine("  usage: /rewind [n]"); break; }
            tree.RewindTurns(n);
            if (tree.Count > 0) sessionId = store.Save(sessionId, tree);
            Console.WriteLine($"  rewound {n} turn(s) — head now {tree.HeadId ?? "(empty)"}. Continue to branch.");
            break;

        case "/goto":
            if (arg.Length == 0) { Console.WriteLine("  usage: /goto <node-id>"); break; }
            if (!tree.Goto(arg)) { Console.WriteLine($"  no node '{arg}' — see /tree"); break; }
            if (tree.Count > 0) sessionId = store.Save(sessionId, tree);
            Console.WriteLine($"  head now {arg}");
            break;

        case "/handover":
            if (tree.Count == 0) { Console.WriteLine("  nothing to hand over yet"); break; }
            // Persist first, so the handover's `recall` has a cold store to read.
            sessionId = store.Save(sessionId, tree);
            Console.WriteLine("  writing handover…\n");
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            string doc;
            try { doc = await new HandoverGenerator(llm).GenerateAsync(tree.MaterializeConversation(), Console.Write, cts.Token); }
            finally { Console.ResetColor(); }
            var file = handovers.Save(new Handover(sessionId!, tree.HeadId, DateTime.UtcNow, doc));
            Console.WriteLine($"\n\n  saved → {file}");
            Console.WriteLine($"  review/edit it, then resume cold with:  ratchet --handover {sessionId}");
            break;

        case "/handovers":
            var hs = handovers.List();
            if (hs.Count == 0) { Console.WriteLine("  (no handovers yet — write one with /handover)"); break; }
            foreach (var id in hs) Console.WriteLine($"  {id}   →   ratchet --handover {id}");
            break;

        case "/compact":
            if (tree.Count == 0) { Console.WriteLine("  nothing to compact yet"); break; }
            await CompactAsync();
            break;

        case "/help":
            Console.WriteLine("  /sessions       list saved sessions");
            Console.WriteLine("  /resume <id>    load a session and continue");
            Console.WriteLine("  /new            start a fresh session");
            Console.WriteLine("  /tree           show the session tree (► marks HEAD)");
            Console.WriteLine("  /rewind [n]     move HEAD back n turns; continue to branch");
            Console.WriteLine("  /goto <node>    jump HEAD to a node (e.g. another branch tip)");
            Console.WriteLine("  /handover       write a handover doc for resuming cold later");
            Console.WriteLine("  /handovers      list handovers (with their resume commands)");
            Console.WriteLine("  /compact        fold this session into a handover and continue fresh");
            Console.WriteLine("  Ctrl+C          quit");
            break;

        default:
            Console.WriteLine($"  unknown command '{parts[0]}' — try /help");
            break;
    }
}

// Compaction = self-triggered handover. Persist the current session (so its tree is
// a cold store), author a handover from it, then continue in a FRESH session whose
// system prompt carries that handover and whose `recall` searches the archived one —
// the same machinery as `ratchet --handover`, applied in-process.
async Task CompactAsync()
{
    var priorId = store.Save(sessionId, tree);
    Console.ForegroundColor = ConsoleColor.DarkCyan;
    Console.WriteLine($"\n  · context ~{observer.LastInputTokens} input tokens — compacting into a handover…\n");
    string doc;
    try { doc = await new HandoverGenerator(llm).GenerateAsync(tree.MaterializeConversation(), Console.Write, cts.Token); }
    finally { Console.ResetColor(); }
    handovers.Save(new Handover(priorId, tree.HeadId, DateTime.UtcNow, doc));

    // Reseed: fresh tree, handover-backed prompt, recall wired to the archived session.
    systemPrompt = BuildSystemPrompt(doc, skills.Describe());
    recallTool = new RecallTool(store, priorId);
    agent = BuildAgent();
    tree = new SessionTree();
    sessionId = null;

    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine($"\n  · compacted → handover of {priorId}. Continuing fresh; `recall` searches {priorId}.");
    Console.ResetColor();
}

// Walk the tree depth-first from the roots, indenting by depth, marking HEAD.
void PrintTree()
{
    if (tree.Count == 0) { Console.WriteLine("  (empty)"); return; }

    void Walk(SessionTree.Node node, int depth)
    {
        var head = node.Id == tree.HeadId;
        if (head) Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"  {(head ? "►" : " ")} {node.Id,3}  {new string(' ', depth * 2)}{Describe(node.Message)}");
        if (head) Console.ResetColor();

        foreach (var child in tree.ChildrenOf(node.Id))
            Walk(child, depth + 1);
    }

    foreach (var root in tree.ChildrenOf(null))
        Walk(root, 0);
}

static string Describe(Message m)
{
    var role = m.Role == Role.User ? "user" : "asst";
    foreach (var b in m.Content)
        if (b is TextBlock t && !string.IsNullOrWhiteSpace(t.Text))
            return $"{role}: {Trunc(t.Text)}";

    var tool = m.Content.OfType<ToolUseBlock>().FirstOrDefault();
    if (tool is not null) return $"{role}: [tool {tool.Name}]";
    if (m.Content.OfType<ToolResultBlock>().Any()) return $"{role}: [tool result]";
    return $"{role}:";
}

static string Trunc(string s)
{
    s = s.ReplaceLineEndings(" ");
    return s.Length > 50 ? s[..50] + "…" : s;
}

static string BuildSystemPrompt(string? handover, string skillList)
{
    const string baseline =
        "You are Ratchet, a coding agent running on the user's machine. " +
        "Core tools: read, write, edit, bash. Always read a file before you edit it — the edit tool " +
        "requires it and the match must be unique (use replace_all for sweeping changes). " +
        "Use `update_plan` to keep an explicit checklist for any multi-step task. " +
        "Use `run_tests` to run and check the suite, and `git_status` / `git_diff` to see what you've changed. " +
        "You can also delegate: call `explore` to hand a read-only investigation to a sub-agent " +
        "(it returns findings without filling your context), and the *_advisor tools to consult a " +
        "specialist for a second opinion (paste the relevant code into your question). " +
        "Keep going until the task is done, then stop and report briefly.";

    var sb = new StringBuilder(baseline);

    var agentsMd = Path.Combine(Directory.GetCurrentDirectory(), "AGENTS.md");
    if (File.Exists(agentsMd))
        sb.Append("\n\n# Project context (AGENTS.md)\n").Append(File.ReadAllText(agentsMd));

    if (!string.IsNullOrWhiteSpace(skillList))
        sb.Append("\n\n# Skills available (call load_skill <name> for full instructions before the matching task)\n")
          .Append(skillList);

    if (!string.IsNullOrWhiteSpace(handover))
        sb.Append("\n\n# Resumed session — handover from a prior session\n")
          .Append("Treat the following as your working context: it was authored at the end of a ")
          .Append("previous session so you can continue cold. Detail it omits is not gone — call ")
          .Append("the `recall` tool to search the prior session's full transcript.\n\n")
          .Append(handover);

    return sb.ToString();
}

/// <summary>
/// Renders loop events to the console. This is the only place that knows about
/// colours and formatting — the loop stays presentation-free. Swap this for a
/// file logger or TUI later without touching Core.
/// </summary>
sealed class ConsoleObserver : IAgentObserver
{
    /// <summary>Input tokens of the most recent model call — the auto-compaction signal.</summary>
    public int LastInputTokens { get; private set; }

    public void OnAssistantTextDelta(string delta)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write(delta);                 // no newline — fragments flow inline
    }

    public void OnAssistantTextEnd()
    {
        Console.ResetColor();
        Console.WriteLine();                  // close the streamed line
    }

    public void OnToolCall(string toolName, string inputJson)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        var preview = inputJson.Length > 120 ? inputJson[..120] + "…" : inputJson;
        Console.WriteLine($"  → {toolName} {preview}");
        Console.ResetColor();
    }

    public void OnToolResult(string toolName, string content, bool isError)
    {
        Console.ForegroundColor = isError ? ConsoleColor.Red : ConsoleColor.DarkGray;
        var preview = content.Length > 200 ? content[..200] + "…" : content;
        Console.WriteLine($"  ← {toolName}{(isError ? " [error]" : "")}: {preview.Replace("\n", "\n     ")}");
        Console.ResetColor();
    }

    public void OnUsage(int inputTokens, int outputTokens)
    {
        LastInputTokens = inputTokens;
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  · tokens: {inputTokens} in / {outputTokens} out");
        Console.ResetColor();
    }
}
