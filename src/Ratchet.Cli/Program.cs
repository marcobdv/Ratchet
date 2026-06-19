using System.Text;
using CodeStack.Ratchet.Core;
using CodeStack.Ratchet.Llm;
using CodeStack.Ratchet.Storage.Sqlite;
using CodeStack.Ratchet.Tools.Mcp;
using CodeStack.Ratchet.Tools.Roslyn;
using CodeStack.Ratchet.Workflow;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

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

// Inspect persisted workflow runs (read-only; no API key needed).
if (args.Contains("--runs"))
{
    var runs = new FileRunStore(Directory.GetCurrentDirectory()).List();
    if (runs.Count == 0) { Console.WriteLine("(no workflow runs yet)"); return 0; }
    foreach (var r in runs)
        Console.WriteLine($"  {r.RunId}  ·  {r.Status,-9}  ·  {r.WorkType ?? "-",-12}  ·  {r.Task}");
    return 0;
}
var runShowIdx = Array.IndexOf(args, "--run");
if (runShowIdx >= 0 && runShowIdx + 1 < args.Length)
{
    var snap = new FileRunStore(Directory.GetCurrentDirectory()).Load(args[runShowIdx + 1]);
    if (snap is null) { Console.Error.WriteLine($"no run '{args[runShowIdx + 1]}'"); return 1; }
    Console.WriteLine($"run {snap.RunId}  ·  {snap.Status}  ·  work_type={snap.WorkType}  ·  task: {snap.Task}");
    if (!string.IsNullOrWhiteSpace(snap.ClassifierReasoning)) Console.WriteLine($"  reasoning: {snap.ClassifierReasoning}");
    foreach (var e in snap.Events) Console.WriteLine($"  [{e.Kind}] {e.Phase}: {e.Detail}");
    Console.WriteLine("  " + snap.Cost.Render().Replace("\n", "\n  "));
    if (snap.IsResumable) Console.WriteLine($"  (resumable: ratchet --workflow-resume {snap.RunId})");
    return 0;
}

// Routing telemetry: the feedback loop. Aggregate the reactive layer's promotions per
// (work_type, phase) across all run records — a high rate means the cheap predictive
// default for that key was wrong and should be retuned upward (a readable diff, not a retrain).
if (args.Contains("--routing-stats"))
{
    var runs = new FileRunStore(Directory.GetCurrentDirectory()).List();
    var agg = new Dictionary<(string wt, string phase), (int starts, int fails, int promotes)>();
    foreach (var snap in runs)
    {
        var wt = snap.WorkType ?? "-";
        foreach (var ev in snap.Events)
        {
            if (ev.Phase == "-") continue;
            var key = (wt, ev.Phase);
            var cur = agg.GetValueOrDefault(key);
            if (ev.Kind == "phase_start") cur.starts++;
            else if (ev.Kind == "promote") cur.promotes++;
            else if (ev.Kind == "gate" && ev.Detail.Contains("fail")) cur.fails++;
            agg[key] = cur;
        }
    }
    if (agg.Count == 0) { Console.WriteLine("(no routing telemetry yet — run some workflows)"); return 0; }
    Console.WriteLine("routing telemetry by (work_type, phase) — high escalation_rate ⇒ retune that phase's cheap default upward:");
    foreach (var ((wt, phase), v) in agg.OrderByDescending(a => a.Value.starts > 0 ? (double)a.Value.promotes / a.Value.starts : 0))
    {
        var rate = v.starts > 0 ? (double)v.promotes / v.starts : 0;
        Console.WriteLine($"  {wt,-12} {phase,-10}  starts={v.starts,-4} gate_fails={v.fails,-4} promotes={v.promotes,-4} escalation_rate={rate:P0}");
    }
    return 0;
}

// ---- configuration --------------------------------------------------------
// Provider-agnostic: RATCHET_PROVIDER picks the backend (default anthropic), RATCHET_MODEL
// the model. Keys come from each provider's env var — see ResolveClient. Not tied to one
// vendor: OpenRouter (one key, hundreds of models), OpenAI, Groq, any OpenAI-compatible
// endpoint, or a local server all drop in here.
var provider = (Environment.GetEnvironmentVariable("RATCHET_PROVIDER") ?? "anthropic").Trim().ToLowerInvariant();
var model = Environment.GetEnvironmentVariable("RATCHET_MODEL") ?? DefaultModelFor(provider) ?? "";
if (string.IsNullOrWhiteSpace(model))
{
    Console.Error.WriteLine($"Provider '{provider}' needs a model id: set RATCHET_MODEL " +
        "(e.g. openrouter → anthropic/claude-sonnet-4 or openai/gpt-4o; groq → llama-3.3-70b-versatile).");
    return 1;
}

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

// Provider seam: every provider flows through ILlmClient. Anthropic rides IChatClient
// (AnthropicChatClient) or the hand-rolled wire client (`anthropic-native`); every other
// backend is OpenAI-compatible and flows through OpenAiChatClient. ResolveClient maps the
// provider name to a client and resolves its key/base URL from env. Built before the tool
// list because the sub-agents run nested loops over this same client.
ILlmClient llm;
try { llm = ResolveClient(provider, model); }
catch (Exception ex) { Console.Error.WriteLine(ex.Message); (store as IDisposable)?.Dispose(); return 1; }

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
baseTools.AddRange(GitTools.Build(Directory.GetCurrentDirectory()));        // git_status / git_diff (read-only)
baseTools.AddRange(GitTools.BuildWrite(Directory.GetCurrentDirectory()));   // git_commit / git_create_branch (mutating → gated)
baseTools.AddRange(SubAgents.Build(llm, shell));                            // explore sub-agent + advisors

// Roslyn semantic-C# tools (loads the workspace's solution/project on first use).
using var roslyn = new RoslynToolset(Directory.GetCurrentDirectory());
baseTools.AddRange(roslyn.Tools);

// MCP servers from .mcp.json: each server tool becomes a Ratchet ITool.
await using var mcp = await McpToolset.ConnectAsync(Directory.GetCurrentDirectory(), Console.WriteLine, CancellationToken.None);
baseTools.AddRange(mcp.Tools);

var observer = new ConsoleObserver();

// Permission gate. RATCHET_GATE = off (default, historical YOLO) | prompt (ask before
// mutating tools: bash/write/edit/git_commit/git_create_branch/roslyn_rename) | deny
// (block them). Read-only tools always pass. Used by both the REPL agent and workflows.
var gate = new ConsoleToolGate(Environment.GetEnvironmentVariable("RATCHET_GATE"));

// OpenTelemetry: the agent/clients/workflow are instrumented in Core with the BCL
// diagnostics API; here we wire the SDK + exporters. RATCHET_OTEL = off (default) |
// console | otlp (to OTEL_EXPORTER_OTLP_ENDPOINT, default localhost:4317). Disposed at
// exit to flush spans/metrics. Covers both the REPL and workflow runs below.
using var otel = OTel.Enable(Environment.GetEnvironmentVariable("RATCHET_OTEL"), "ratchet");

// `ratchet --workflow <file.yaml> "<task>"` runs the task through a phased workflow
// orchestrator (research→plan→implement→verify→review) instead of the single REPL
// loop. The scheduler is deterministic; LLM judgment shows up only at the intake
// classifier and judge gates. See docs/workflow-orchestration.md.
var wfIdx = Array.IndexOf(args, "--workflow");
var wfResumeIdx = Array.IndexOf(args, "--workflow-resume");
if ((wfIdx >= 0 && wfIdx + 1 < args.Length) || (wfResumeIdx >= 0 && wfResumeIdx + 1 < args.Length))
{
    // Tier -> ILlmClient via the same provider resolver as the REPL: a tier can be
    // anthropic, openrouter, openai, groq, local, or any OpenAI-compatible endpoint, so
    // a workflow can mix (e.g. a local cheap driver with an OpenRouter frontier judge).
    Func<ModelTier, ILlmClient> clientFactory = tier => ResolveClient(tier.Provider.Trim().ToLowerInvariant(), tier.Model);

    // Base tools resolve by name from the already-assembled set (read/write/edit/bash/
    // load_skill/run_tests/git_*/roslyn_*/…). consult_advisor, recall and request_escalation
    // are supplied per-phase by the scheduler, so they're intentionally not here.
    var toolByName = new Dictionary<string, ITool>(StringComparer.Ordinal);
    foreach (var t in baseTools) toolByName[t.Name] = t;
    BaseToolResolver resolveTool = n => toolByName.GetValueOrDefault(n);

    var runStore = new FileRunStore(Directory.GetCurrentDirectory());
    var knownSkills = skills.Skills.Select(s => s.Name).ToList();

    // --workflow-resume <id> continues an interrupted run from its last checkpoint
    // (same config, same sizing); --workflow <file> "<task>" starts a fresh run.
    RunSnapshot? resume = null;
    string wfFile, wfTask, runId;
    if (wfResumeIdx >= 0 && wfResumeIdx + 1 < args.Length)
    {
        var resumeId = args[wfResumeIdx + 1];
        resume = runStore.Load(resumeId);
        if (resume is null) { Console.Error.WriteLine($"no run '{resumeId}'"); return 1; }
        if (!resume.IsResumable) { Console.Error.WriteLine($"run '{resumeId}' is {resume.Status}, not resumable"); return 1; }
        wfFile = resume.WorkflowFile; wfTask = resume.Task; runId = resume.RunId;
        Console.WriteLine($"resuming run {runId} (work_type {resume.WorkType}) from phase index {resume.Idx}\n");
    }
    else
    {
        wfFile = args[wfIdx + 1];
        wfTask = string.Join(' ', args.Skip(wfIdx + 2));
        if (string.IsNullOrWhiteSpace(wfTask)) { Console.Write("workflow task> "); wfTask = Console.ReadLine() ?? ""; }
        if (string.IsNullOrWhiteSpace(wfTask)) { Console.Error.WriteLine("no task given"); return 1; }
        runId = "wf-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
    }

    WorkflowConfig wf;
    try { wf = WorkflowLoader.Load(wfFile, knownSkills); }
    catch (WorkflowConfigException ex) { Console.Error.WriteLine(ex.Message); return 1; }
    catch (Exception ex) { Console.Error.WriteLine($"could not load workflow '{wfFile}': {ex.Message}"); return 1; }

    Console.WriteLine($"workflow '{wf.Name}'  ·  run {runId}  ·  gate {gate.ModeName}  ·  task: {wfTask}\n");

    var scheduler = new WorkflowScheduler(
        wf, clientFactory, resolveTool, store, shell, runId,
        skills, observer, new ConsoleWorkflowObserver(), gate, runStore, wfFile);

    WorkflowRun result;
    try { result = await scheduler.RunAsync(wfTask, CancellationToken.None, resume); }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[workflow error] {ex.Message}");
        Console.Error.WriteLine($"  run {runId} checkpointed — resume with:  ratchet --workflow-resume {runId}");
        return 1;
    }

    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine("\n" + result.Cost.Render());
    Console.WriteLine($"run record: .ratchet/runs/{runId}.json  ·  inspect with: ratchet --run {runId}");
    Console.ResetColor();

    (llm as IDisposable)?.Dispose();
    (store as IDisposable)?.Dispose();
    return result.Status == RunStatus.Completed ? 0 : 2;
}

// The agent is rebuilt on compaction with a new system prompt + recall tool, so
// construct it through a factory over the (mutable) systemPrompt and recallTool.
Agent BuildAgent() =>
    new(llm, new ToolRegistry(recallTool is null ? baseTools : baseTools.Append(recallTool)), systemPrompt, observer, gate);

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

Console.WriteLine($"ratchet v0  ·  model: {model}  ·  provider: {provider}  ·  gate: {gate.ModeName}  ·  shell: {shell.Name}  ·  cwd: {Directory.GetCurrentDirectory()}");
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

// Default model only for Anthropic (its slug is stable here); every other provider must
// name its model because slugs differ per backend (openrouter "vendor/model", etc.).
static string? DefaultModelFor(string provider) => provider switch
{
    "anthropic" or "anthropic-native" => "claude-sonnet-4-6",
    _ => null,
};

// Maps a provider name + model to an ILlmClient, resolving key/base URL from env. This
// is the one place that knows provider specifics; everything else just holds an ILlmClient.
//   anthropic / anthropic-native  -> ANTHROPIC_API_KEY
//   openrouter                    -> OPENROUTER_API_KEY | RATCHET_API_KEY   (base openrouter.ai/api/v1)
//   openai                        -> OPENAI_API_KEY     | RATCHET_API_KEY
//   groq                          -> GROQ_API_KEY       | RATCHET_API_KEY
//   local / ollama                -> RATCHET_LOCAL_API_KEY (optional), RATCHET_LOCAL_BASE_URL
//   generic / unknown + RATCHET_BASE_URL -> RATCHET_API_KEY
// RATCHET_BASE_URL overrides the base URL for any OpenAI-compatible provider (proxies, etc.).
static ILlmClient ResolveClient(string provider, string model)
{
    static string? Env(string n) => Environment.GetEnvironmentVariable(n);
    static string? FirstEnv(params string[] names) =>
        names.Select(Env).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
    static string RequireKey(string who, params string[] names)
    {
        var k = FirstEnv(names);
        if (string.IsNullOrWhiteSpace(k))
            throw new InvalidOperationException($"{who}: set {string.Join(" or ", names)}.");
        return k!;
    }
    // 'system' is the gen_ai.system telemetry label.
    static ILlmClient OpenAi(string defaultBase, string model, string? key, string system, IReadOnlyDictionary<string, string>? headers = null) =>
        new ChatClientLlm(new OpenAiChatClient(Env("RATCHET_BASE_URL") ?? defaultBase, model, key, extraHeaders: headers), model, system: system);

    switch (provider)
    {
        case "anthropic":
            return new ChatClientLlm(new AnthropicChatClient(RequireKey("Anthropic provider", "ANTHROPIC_API_KEY"), model), model, system: "anthropic");
        case "anthropic-native":
            return new AnthropicClient(RequireKey("Anthropic provider", "ANTHROPIC_API_KEY"), model);

        case "openrouter":
            return OpenAi("https://openrouter.ai/api/v1", model,
                RequireKey("OpenRouter provider", "OPENROUTER_API_KEY", "RATCHET_API_KEY"), "openrouter",
                new Dictionary<string, string>
                {
                    ["HTTP-Referer"] = Env("RATCHET_OPENROUTER_REFERER") ?? "https://github.com/marcobdv/Ratchet",
                    ["X-Title"] = Env("RATCHET_OPENROUTER_TITLE") ?? "Ratchet",
                });
        case "openai":
            return OpenAi("https://api.openai.com/v1", model, RequireKey("OpenAI provider", "OPENAI_API_KEY", "RATCHET_API_KEY"), "openai");
        case "groq":
            return OpenAi("https://api.groq.com/openai/v1", model, RequireKey("Groq provider", "GROQ_API_KEY", "RATCHET_API_KEY"), "groq");
        case "local":
        case "ollama":
            return OpenAi(Env("RATCHET_LOCAL_BASE_URL") ?? "http://localhost:11434/v1", model, Env("RATCHET_LOCAL_API_KEY"), "local");

        default:
            // "generic", "openai-compatible", or any unknown name: treat as an OpenAI-compatible
            // endpoint if a base URL is provided; otherwise fail with guidance.
            var baseUrl = Env("RATCHET_BASE_URL");
            if (!string.IsNullOrWhiteSpace(baseUrl))
                return new ChatClientLlm(new OpenAiChatClient(baseUrl!, model, FirstEnv("RATCHET_API_KEY")), model, system: "openai-compatible");
            throw new InvalidOperationException(
                $"unknown provider '{provider}'. Use anthropic | anthropic-native | openrouter | openai | groq | local " +
                "| any OpenAI-compatible endpoint via RATCHET_BASE_URL (+ RATCHET_API_KEY).");
    }
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

/// <summary>
/// Renders the workflow run trace to the console: the classification, each phase
/// start, consults, gate outcomes, escalations, and conflicts. This is the live view
/// of the recorded run — the same events <see cref="WorkflowRun"/> keeps for audit.
/// </summary>
sealed class ConsoleWorkflowObserver : IWorkflowObserver
{
    private static void Line(ConsoleColor c, string s)
    {
        Console.ForegroundColor = c;
        Console.WriteLine(s);
        Console.ResetColor();
    }

    public void Classified(string workType, string reasoning) =>
        Line(ConsoleColor.Magenta, $"▣ classified: {workType} — {reasoning}");

    public void PhaseStart(string phaseId, string driverTier, IReadOnlyList<string> skills, string loadPolicy) =>
        Line(ConsoleColor.Cyan, $"\n┌─ phase: {phaseId}  ·  driver: {driverTier}  ·  skills: [{string.Join(", ", skills)}] ({loadPolicy})");

    public void Consult(string phaseId, int n, int max, string advice) =>
        Line(ConsoleColor.Blue, $"│  advisor {n}/{max}: {Trunc(advice)}");

    public void Gate(string phaseId, string kind, string outcome, string reason) =>
        Line(outcome == "pass" ? ConsoleColor.Green : ConsoleColor.Red,
            $"└─ gate [{kind}] → {outcome.ToUpperInvariant()}  {Trunc(reason)}");

    public void Escalation(string fromPhase, string toPhase, string reason) =>
        Line(ConsoleColor.Yellow, $"↑ escalation {fromPhase} → {toPhase}: {Trunc(reason)}");

    public void Promotion(string phaseId, string fromTier, string toTier) =>
        Line(ConsoleColor.Yellow, $"⇪ promote {phaseId}: {fromTier} → {toTier} (red gate)");

    public void Conflict(string phaseId, string detail) =>
        Line(ConsoleColor.DarkYellow, $"⚠ conflict in {phaseId}: {detail}");

    public void PhaseEnd(string phaseId, string summary) { /* the gate line below is the visible marker */ }

    public void RunEnd(RunStatus status, string reason) =>
        Line(status == RunStatus.Completed ? ConsoleColor.Green : ConsoleColor.Red,
            $"\n■ run {status}{(reason.Length > 0 ? ": " + reason : "")}");

    private static string Trunc(string s)
    {
        s = s.ReplaceLineEndings(" ").Trim();
        return s.Length > 160 ? s[..160] + "…" : s;
    }
}

/// <summary>
/// The interactive permission gate. Read-only tools always pass; mutating ones
/// (bash/write/edit/git_commit/git_create_branch/roslyn_rename) are governed by the
/// mode: <c>off</c> allows everything (pi-plain YOLO, the default), <c>prompt</c> asks
/// the user (with an "always allow this tool" option), <c>deny</c> blocks them. In a
/// non-interactive process, <c>prompt</c> degrades to deny rather than hang.
/// </summary>
sealed class ConsoleToolGate : IToolGate
{
    private enum Mode { Off, Prompt, Deny }
    private readonly Mode _mode;
    private static readonly HashSet<string> Mutating = new(StringComparer.Ordinal)
    { "bash", "write", "edit", "git_commit", "git_create_branch", "roslyn_rename" };
    private readonly HashSet<string> _alwaysAllow = new(StringComparer.Ordinal);

    public ConsoleToolGate(string? mode) => _mode = (mode?.Trim().ToLowerInvariant()) switch
    {
        "prompt" or "ask" => Mode.Prompt,
        "deny" => Mode.Deny,
        _ => Mode.Off,
    };

    public string ModeName => _mode.ToString().ToLowerInvariant();

    public Task<ToolGateDecision> CheckAsync(string toolName, string inputJson, CancellationToken ct)
    {
        if (_mode == Mode.Off || !Mutating.Contains(toolName) || _alwaysAllow.Contains(toolName))
            return Task.FromResult(ToolGateDecision.Allow);
        if (_mode == Mode.Deny)
            return Task.FromResult(ToolGateDecision.Deny("permission gate is in deny mode"));

        // prompt mode
        if (Console.IsInputRedirected)
            return Task.FromResult(ToolGateDecision.Deny("approval required but input is non-interactive (set RATCHET_GATE=off to allow)"));

        Console.ForegroundColor = ConsoleColor.Yellow;
        var preview = inputJson.Length > 160 ? inputJson[..160] + "…" : inputJson;
        Console.Write($"\n[gate] allow  {toolName} {preview}  ? [y/N/a=always] ");
        Console.ResetColor();
        var ans = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
        if (ans == "a") { _alwaysAllow.Add(toolName); return Task.FromResult(ToolGateDecision.Allow); }
        return Task.FromResult(ans is "y" or "yes"
            ? ToolGateDecision.Allow
            : ToolGateDecision.Deny("the user declined this action"));
    }
}

/// <summary>
/// Wires the OpenTelemetry SDK to Ratchet's instrumentation (the <c>Ratchet.Agent</c>
/// ActivitySource + Meter) and exports it. Off by default; <c>RATCHET_OTEL=console</c>
/// prints spans/metrics to stdout, <c>otlp</c> ships them to an OTLP collector
/// (Jaeger/Tempo/Grafana/…) at <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> (default localhost:4317).
/// The returned handle flushes pending exports on dispose.
/// </summary>
static class OTel
{
    public static IDisposable? Enable(string? mode, string serviceName)
    {
        mode = mode?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(mode) || mode is "off" or "0" or "false" or "none") return null;
        var console = mode is "console" or "stdout";

        var resource = ResourceBuilder.CreateDefault().AddService(serviceName);
        var tracer = Sdk.CreateTracerProviderBuilder().SetResourceBuilder(resource).AddSource(RatchetTelemetry.Name);
        var meter = Sdk.CreateMeterProviderBuilder().SetResourceBuilder(resource).AddMeter(RatchetTelemetry.Name);
        if (console) { tracer.AddConsoleExporter(); meter.AddConsoleExporter(); }
        else { tracer.AddOtlpExporter(); meter.AddOtlpExporter(); }

        var tp = tracer.Build();
        var mp = meter.Build();
        Console.Error.WriteLine(console
            ? "[otel] exporting ratchet traces + metrics to the console"
            : $"[otel] exporting ratchet traces + metrics via OTLP to {Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT") ?? "http://localhost:4317"}");
        return new Handle(tp, mp);
    }

    private sealed class Handle : IDisposable
    {
        private readonly TracerProvider _tp;
        private readonly MeterProvider _mp;
        public Handle(TracerProvider tp, MeterProvider mp) { _tp = tp; _mp = mp; }
        public void Dispose() { _tp.Dispose(); _mp.Dispose(); }   // dispose flushes pending exports
    }
}
