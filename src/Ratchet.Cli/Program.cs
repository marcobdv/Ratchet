using System.Text;
using CodeStack.Ratchet.Core;
using CodeStack.Ratchet.Llm;
using CodeStack.Ratchet.Storage.Sqlite;

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

// AGENTS.md (pi-style project context) and, when resuming, the handover doc are
// both prepended to the system prompt — plain text, no magic.
var systemPrompt = BuildSystemPrompt(handoverContext);

var toolList = new List<ITool> { new ReadTool(), new WriteTool(), new EditTool(), new BashTool(shell) };
if (recallTool is not null) toolList.Add(recallTool);
var tools = new ToolRegistry(toolList);

using var llm = new AnthropicClient(apiKey, model);
var observer = new ConsoleObserver();
var agent = new Agent(llm, tools, systemPrompt, observer);

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

Console.WriteLine($"ratchet v0  ·  model: {model}  ·  shell: {shell.Name}  ·  cwd: {Directory.GetCurrentDirectory()}");
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

        case "/help":
            Console.WriteLine("  /sessions       list saved sessions");
            Console.WriteLine("  /resume <id>    load a session and continue");
            Console.WriteLine("  /new            start a fresh session");
            Console.WriteLine("  /tree           show the session tree (► marks HEAD)");
            Console.WriteLine("  /rewind [n]     move HEAD back n turns; continue to branch");
            Console.WriteLine("  /goto <node>    jump HEAD to a node (e.g. another branch tip)");
            Console.WriteLine("  /handover       write a handover doc for resuming cold later");
            Console.WriteLine("  /handovers      list handovers (with their resume commands)");
            Console.WriteLine("  Ctrl+C          quit");
            break;

        default:
            Console.WriteLine($"  unknown command '{parts[0]}' — try /help");
            break;
    }
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

static string BuildSystemPrompt(string? handover)
{
    const string baseline =
        "You are Ratchet, a minimal coding agent running on the user's machine. " +
        "You have four tools: read, write, edit, bash. Prefer reading files before " +
        "editing them. Keep going until the task is done, then stop and report briefly.";

    var sb = new StringBuilder(baseline);

    var agentsMd = Path.Combine(Directory.GetCurrentDirectory(), "AGENTS.md");
    if (File.Exists(agentsMd))
        sb.Append("\n\n# Project context (AGENTS.md)\n").Append(File.ReadAllText(agentsMd));

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
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  · tokens: {inputTokens} in / {outputTokens} out");
        Console.ResetColor();
    }
}
