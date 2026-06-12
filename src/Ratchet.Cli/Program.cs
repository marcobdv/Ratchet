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

// AGENTS.md, pi-style: if one sits in the working dir, prepend it to the system
// prompt. This is the entire "project context" mechanism — plain text, no magic.
var systemPrompt = BuildSystemPrompt();

// ---- composition root -----------------------------------------------------
var tools = new ToolRegistry(new ITool[]
{
    new ReadTool(),
    new WriteTool(),
    new EditTool(),
    new BashTool(shell),
});

using var llm = new AnthropicClient(apiKey, model);
var observer = new ConsoleObserver();
var agent = new Agent(llm, tools, systemPrompt, observer);

// Sessions are trees of message nodes (see SessionTree). The path root..HEAD is
// the live conversation; rewinding HEAD and continuing forks a new branch.
// Storage backend: file (default) or sqlite. Both implement ISessionStore, so
// nothing below this line knows or cares which is in use — that's the seam.
ISessionStore store = (Environment.GetEnvironmentVariable("RATCHET_STORE")?.ToLowerInvariant()) switch
{
    "sqlite" => new SqliteSessionStore(Directory.GetCurrentDirectory()),
    _ => new FileSessionStore(Directory.GetCurrentDirectory()),
};
var tree = new SessionTree();
string? sessionId = null;

// `ratchet --continue` (or -c) reopens the most recent session in this folder.
if (args.Any(a => a is "--continue" or "-c"))
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
Console.WriteLine("Type a request, or /help for commands. Ctrl+C to quit.\n");

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
        HandleCommand(line);
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
void HandleCommand(string input)
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

        case "/help":
            Console.WriteLine("  /sessions       list saved sessions");
            Console.WriteLine("  /resume <id>    load a session and continue");
            Console.WriteLine("  /new            start a fresh session");
            Console.WriteLine("  /tree           show the session tree (► marks HEAD)");
            Console.WriteLine("  /rewind [n]     move HEAD back n turns; continue to branch");
            Console.WriteLine("  /goto <node>    jump HEAD to a node (e.g. another branch tip)");
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

static string BuildSystemPrompt()
{
    const string baseline =
        "You are Ratchet, a minimal coding agent running on the user's machine. " +
        "You have four tools: read, write, edit, bash. Prefer reading files before " +
        "editing them. Keep going until the task is done, then stop and report briefly.";

    var agentsMd = Path.Combine(Directory.GetCurrentDirectory(), "AGENTS.md");
    return File.Exists(agentsMd)
        ? baseline + "\n\n# Project context (AGENTS.md)\n" + File.ReadAllText(agentsMd)
        : baseline;
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
