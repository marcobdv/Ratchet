using CodeStack.Ratchet.Core;
using CodeStack.Ratchet.Llm;

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

// Sessions live per-project under .ratchet/sessions/ in the working directory.
// The transcript is the agent's whole memory, so persistence is just serializing it.
var store = new FileSessionStore(Directory.GetCurrentDirectory());
var conversation = new Conversation();
string? sessionId = null;

// `ratchet --continue` (or -c) reopens the most recent session in this folder.
if (args.Any(a => a is "--continue" or "-c"))
{
    var latest = store.List().FirstOrDefault();
    if (latest is not null)
    {
        conversation = store.Load(latest.Id)!;
        sessionId = latest.Id;
        Console.WriteLine($"continued session {sessionId} ({conversation.Messages.Count} messages)");
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

    conversation.Add(Message.UserText(line));

    try
    {
        await agent.RunTurnAsync(conversation, cts.Token);

        // Auto-save after every completed turn — never lose work.
        var wasNew = sessionId is null;
        sessionId = store.Save(sessionId, conversation);
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
                Console.WriteLine($"  {s.Id}  ·  {s.MessageCount,3} msgs  ·  {s.UpdatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}  ·  {s.Preview}");
            break;

        case "/resume":
            if (arg.Length == 0) { Console.WriteLine("  usage: /resume <id>"); break; }
            var loaded = store.Load(arg);
            if (loaded is null) { Console.WriteLine($"  no session '{arg}'"); break; }
            conversation = loaded;
            sessionId = arg;
            Console.WriteLine($"  resumed '{arg}' ({conversation.Messages.Count} messages)");
            break;

        case "/new":
            conversation = new Conversation();
            sessionId = null;
            Console.WriteLine("  started a new session");
            break;

        case "/help":
            Console.WriteLine("  /sessions       list saved sessions");
            Console.WriteLine("  /resume <id>    load a session and continue");
            Console.WriteLine("  /new            start a fresh session");
            Console.WriteLine("  Ctrl+C          quit");
            break;

        default:
            Console.WriteLine($"  unknown command '{parts[0]}' — try /help");
            break;
    }
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
