using System.Text;
using CodeStack.Ratchet.Core;
using CodeStack.Ratchet.Tools.Mcp;
using CodeStack.Ratchet.Workflow;

namespace CodeStack.Ratchet.Cli;

/// <summary>
/// <c>ratchet --mcp-serve</c>: Ratchet as an MCP server, headless, for a frontier
/// orchestrator (Claude Code) to delegate implementation work to. The economics: the
/// caller plans on its subscription; Ratchet burns local/cheap tokens executing the plan
/// (RATCHET_PROVIDER / RATCHET_MODEL come from the server entry's env in .mcp.json).
///
/// Deliberately exposes THREE COARSE delegation tools, not Ratchet's toolset — the
/// caller has its own read/edit/bash; what it lacks is "run this to completion on
/// another model" (see ADR-0013):
///
///   - ratchet_implement — execute a provided plan to completion (one full agent turn,
///     or a phased workflow when RATCHET_MCP_WORKFLOW names a YAML — gates instead of
///     a human watching);
///   - ratchet_task — a smaller one-shot job;
///   - ratchet_run — inspect recorded workflow runs (read-only).
///
/// Implement/task calls are SERIALIZED (one repo, one pair of hands); each persists its
/// session/run like the REPL does, so `ratchet --resume` and `--run` work afterwards.
/// </summary>
internal static class McpServeMode
{
    public static async Task<int> RunAsync(
        ILlmClient llm,
        IReadOnlyList<ITool> baseTools,
        string systemPrompt,
        IAgentObserver observer,
        IToolGate gate,
        string gateModeName,
        ISessionStore store,
        ShellSpec shell,
        SkillCatalog skills,
        Func<string, string, ILlmClient> resolveClient,
        Stream protocolIn,
        Stream protocolOut,
        CancellationToken ct)
    {
        var cwd = Directory.GetCurrentDirectory();
        var runStore = new FileRunStore(cwd);
        var workflowFile = Environment.GetEnvironmentVariable("RATCHET_MCP_WORKFLOW");

        var tools = new List<ITool>
        {
            new AgentTurnTool(
                "ratchet_implement",
                "Delegate the IMPLEMENTATION of a provided plan to Ratchet, a coding agent running " +
                "locally in this repository" + (string.IsNullOrWhiteSpace(workflowFile)
                    ? "" : " (runs a phased implement/verify workflow with quality gates)") + ". " +
                "Pass the COMPLETE plan — Ratchet starts cold and sees nothing but this text and the repo. " +
                "Include: what to build/change, relevant file paths, constraints, and how to verify. " +
                "Ratchet edits files, runs tests, and returns a report; expect minutes for real work " +
                $"(progress is streamed). Calls are serialized. Mutating tools are gated '{gateModeName}'.",
                """{"type":"object","properties":{"plan":{"type":"string","description":"The full implementation plan/spec, self-contained."}},"required":["plan"]}""",
                "plan",
                plan => "You are executing a provided implementation plan, headless — no human is watching, " +
                        "so do not ask questions; make reasonable decisions and note them in your report. " +
                        "Read files before editing, keep changes focused on the plan, run `run_tests` before " +
                        "finishing, and end with a brief report: what changed (files), test status, and any " +
                        "deviations from the plan.\n\n# The plan\n\n" + plan,
                llm, baseTools, systemPrompt, observer, gate, store,
                workflowFile, skills, shell, resolveClient, runStore),

            new AgentTurnTool(
                "ratchet_task",
                "Run a smaller one-shot task on Ratchet (local coding agent in this repository): a " +
                "focused edit, a codebase question, a test run. Same agent as ratchet_implement but " +
                "without plan-execution framing, and never workflow-backed. Calls are serialized.",
                """{"type":"object","properties":{"prompt":{"type":"string","description":"The task, self-contained."}},"required":["prompt"]}""",
                "prompt",
                prompt => prompt,
                llm, baseTools, systemPrompt, observer, gate, store,
                workflowFile: null, skills, shell, resolveClient, runStore),

            new RunInspectTool(runStore),
        };

        // The council (ADR-0011) is delegation too — organized deliberation instead of
        // execution — so it earns a spot on the wire: the caller brings a decision, the
        // personas argue cold on Ratchet's models, and the Analysis Brief comes back
        // (with a Decision Record template written to .ratchet/council for the human).
        // Whatever claimed the `council` name is served: a project-defined council file
        // overrides the built-in ad-hoc one, same as in the REPL. No serialization
        // needed — deliberation reads the repo, it doesn't edit it.
        if (baseTools.FirstOrDefault(t => t.Name == "council") is { } council)
            tools.Add(new RelabeledTool(
                "ratchet_council",
                "Convene Ratchet's deliberation council on an architectural decision with no prior art. " +
                "Independent personas argue from COLD, separate contexts on Ratchet's (typically local) " +
                "models; a clerk organizes their locked outputs into an Analysis Brief — consensus, " +
                "contradictions, blind spots — WITHOUT a recommendation; a Decision Record template is " +
                "written to .ratchet/council for the human to complete. Pass `decision` with ALL context " +
                "(constraints, options, what makes it novel) — the personas see nothing but that text and " +
                "the repo. If the schema offers `members`, you may name the roster (defined agents and/or " +
                "built-in personas: architect, skeptic, developer, domain); a fixed-roster council ignores " +
                "it. Use it BEFORE implementing when a choice is genuinely open; the deliberation is the " +
                "product, the decision stays with the human.",
                council));

        Console.WriteLine($"ratchet --mcp-serve  ·  gate: {gateModeName}  ·  cwd: {cwd}" +
            (string.IsNullOrWhiteSpace(workflowFile) ? "" : $"  ·  implement workflow: {workflowFile}"));

        await McpServe.RunAsync(
            tools,
            serverName: "ratchet",
            version: "0.14",
            instructions:
                "Ratchet is a coding agent living inside this repository, running on its own " +
                "(typically local/cheap) models. Delegate implementation to it with ratchet_implement " +
                "(pass a complete, self-contained plan), smaller jobs with ratchet_task, and inspect " +
                "past workflow runs with ratchet_run. For an architectural decision with no prior art, " +
                "convene ratchet_council first — independent cold perspectives organized into an " +
                "Analysis Brief; the human decides.",
            new ModelContextProtocol.Server.StreamServerTransport(protocolIn, protocolOut, "ratchet"),
            ct).ConfigureAwait(false);

        return 0;
    }

    private static string? GetStringArg(string inputJson, string name)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(inputJson);
            return doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// One delegated agent job = one full agent turn (or one workflow run) over the same
    /// tool set the REPL uses. Mirrors the REPL's durability: the session persists even
    /// when the turn fails or is cancelled, and a dangling tool_use is closed so the
    /// stored transcript stays API-valid (ADR-0010).
    /// </summary>
    private sealed class AgentTurnTool : IProgressTool
    {
        // One repo, one pair of hands: concurrent implement/task calls would fight over
        // the working tree, so they queue here.
        private static readonly SemaphoreSlim OneAtATime = new(1, 1);

        private readonly Func<string, string> _frame;
        private readonly string _argName;
        private readonly ILlmClient _llm;
        private readonly IReadOnlyList<ITool> _baseTools;
        private readonly string _systemPrompt;
        private readonly IAgentObserver _observer;
        private readonly IToolGate _gate;
        private readonly ISessionStore _store;
        private readonly string? _workflowFile;
        private readonly SkillCatalog _skills;
        private readonly ShellSpec _shell;
        private readonly Func<string, string, ILlmClient> _resolveClient;
        private readonly FileRunStore _runStore;

        public AgentTurnTool(
            string name, string description, string schema, string argName, Func<string, string> frame,
            ILlmClient llm, IReadOnlyList<ITool> baseTools, string systemPrompt, IAgentObserver observer,
            IToolGate gate, ISessionStore store, string? workflowFile, SkillCatalog skills,
            ShellSpec shell, Func<string, string, ILlmClient> resolveClient, FileRunStore runStore)
        {
            Name = name;
            Description = description;
            InputSchemaJson = schema;
            _argName = argName;
            _frame = frame;
            _llm = llm;
            _baseTools = baseTools;
            _systemPrompt = systemPrompt;
            _observer = observer;
            _gate = gate;
            _store = store;
            _workflowFile = workflowFile;
            _skills = skills;
            _shell = shell;
            _resolveClient = resolveClient;
            _runStore = runStore;
        }

        public string Name { get; }
        public string Description { get; }
        public string InputSchemaJson { get; }

        public Task<string> ExecuteAsync(string inputJson, CancellationToken ct) =>
            ExecuteAsync(inputJson, _ => { }, ct);

        public async Task<string> ExecuteAsync(string inputJson, Action<string> progress, CancellationToken ct)
        {
            var request = GetStringArg(inputJson, _argName);
            if (string.IsNullOrWhiteSpace(request)) return $"{Name}: no '{_argName}' provided.";

            await OneAtATime.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return _workflowFile is not null
                    ? await RunWorkflowAsync(request, progress, ct).ConfigureAwait(false)
                    : await RunTurnAsync(request, progress, ct).ConfigureAwait(false);
            }
            finally
            {
                OneAtATime.Release();
            }
        }

        private async Task<string> RunTurnAsync(string request, Action<string> progress, CancellationToken ct)
        {
            var tally = new TallyingObserver(_observer, progress);
            var agent = new Agent(_llm, new ToolRegistry(_baseTools), _systemPrompt, tally, _gate);

            var tree = new SessionTree();
            tree.Append(Message.UserText(_frame(request)));
            var conversation = tree.MaterializeConversation();
            var baseCount = conversation.Messages.Count;

            Exception? failure = null;
            try
            {
                await agent.RunTurnAsync(conversation, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { failure = ex; }

            // Durability, exactly as the REPL: persist what completed, close a dangling
            // tool_use so the stored transcript stays API-valid.
            string? sessionId = null;
            if (conversation.Messages.Count > baseCount || failure is null)
            {
                var last = conversation.Messages[^1];
                var dangling = last.Role == Role.Assistant
                    ? last.Content.OfType<ToolUseBlock>().ToList()
                    : new List<ToolUseBlock>();
                if (dangling.Count > 0)
                    conversation.Add(Message.UserToolResults(dangling
                        .Select(u => (ContentBlock)new ToolResultBlock(u.Id, "[not executed: interrupted]", true))
                        .ToList()));

                for (var i = baseCount; i < conversation.Messages.Count; i++)
                    tree.Append(conversation.Messages[i]);
                sessionId = _store.Save(null, tree);
            }

            if (failure is not null)
                return $"{Name} FAILED: {failure.Message}\n" +
                       (sessionId is null ? "" : $"Partial work persisted as session {sessionId} (ratchet /resume {sessionId}).");

            // The report = the agent's final assistant text.
            var report = conversation.Messages
                .LastOrDefault(m => m.Role == Role.Assistant && m.Content.OfType<TextBlock>().Any())
                ?.Content.OfType<TextBlock>().Select(t => t.Text)
                .Aggregate(new StringBuilder(), (sb, t) => sb.Append(t)).ToString() ?? "(no report)";

            return $"{report}\n\n---\nsession: {sessionId}  ·  tokens: {tally.InputTokens} in / {tally.OutputTokens} out";
        }

        private async Task<string> RunWorkflowAsync(string plan, Action<string> progress, CancellationToken ct)
        {
            WorkflowConfig wf;
            try { wf = WorkflowLoader.Load(_workflowFile!, _skills.Skills.Select(s => s.Name).ToList()); }
            catch (Exception ex) { return $"{Name}: could not load workflow '{_workflowFile}': {ex.Message}"; }

            var toolByName = new Dictionary<string, ITool>(StringComparer.Ordinal);
            foreach (var t in _baseTools) toolByName[t.Name] = t;

            var runId = "wf-" + SessionId.NewId();
            var scheduler = new WorkflowScheduler(
                wf,
                tier => _resolveClient(tier.Provider.Trim().ToLowerInvariant(), tier.Model),
                n => toolByName.GetValueOrDefault(n),
                _store, _shell, runId, _skills,
                new TallyingObserver(_observer, progress),
                new ForwardingWorkflowObserver(progress),
                _gate, _runStore, _workflowFile!);

            WorkflowRun result;
            try { result = await scheduler.RunAsync(plan, ct, resume: null).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                return $"{Name} FAILED: {ex.Message}\nRun {runId} checkpointed — resumable with: ratchet --workflow-resume {runId}";
            }

            return $"Workflow '{wf.Name}' {result.Status} (work_type {result.WorkType}).\n\n" +
                   result.Cost.Render() +
                   $"\n\nrun: {runId} — details via ratchet_run or `ratchet --run {runId}`" +
                   (result.Status == RunStatus.Completed ? "" : $"\nresume with: ratchet --workflow-resume {runId}");
        }
    }

    /// <summary>Read-only view over the recorded workflow runs — the audit trail for the caller.</summary>
    private sealed class RunInspectTool : ITool
    {
        private readonly FileRunStore _runStore;
        public RunInspectTool(FileRunStore runStore) => _runStore = runStore;

        public string Name => "ratchet_run";
        public string Description =>
            "Inspect Ratchet's recorded workflow runs (read-only). Without run_id: list all runs. " +
            "With run_id: that run's classification, phase/gate event trace, and per-tier cost.";
        public string InputSchemaJson =>
            """{"type":"object","properties":{"run_id":{"type":"string","description":"A run id from the list; omit to list all runs."}}}""";

        public Task<string> ExecuteAsync(string inputJson, CancellationToken ct)
        {
            var id = GetStringArg(inputJson, "run_id");
            if (string.IsNullOrWhiteSpace(id))
            {
                var runs = _runStore.List();
                return Task.FromResult(runs.Count == 0
                    ? "(no workflow runs yet)"
                    : string.Join("\n", runs.Select(r => $"{r.RunId}  ·  {r.Status,-9}  ·  {r.WorkType ?? "-",-12}  ·  {r.Task}")));
            }

            var snap = _runStore.Load(id);
            if (snap is null) return Task.FromResult($"no run '{id}'");

            var sb = new StringBuilder()
                .Append("run ").Append(snap.RunId).Append("  ·  ").Append(snap.Status)
                .Append("  ·  work_type=").Append(snap.WorkType).Append("  ·  task: ").AppendLine(snap.Task);
            if (!string.IsNullOrWhiteSpace(snap.ClassifierReasoning))
                sb.Append("reasoning: ").AppendLine(snap.ClassifierReasoning);
            foreach (var e in snap.Events) sb.Append('[').Append(e.Kind).Append("] ").Append(e.Phase).Append(": ").AppendLine(e.Detail);
            sb.AppendLine(snap.Cost.Render());
            if (snap.IsResumable) sb.Append("(resumable: ratchet --workflow-resume ").Append(snap.RunId).Append(')');
            return Task.FromResult(sb.ToString());
        }
    }

    /// <summary>
    /// Wraps the console observer (already redirected to stderr in serve mode) with per-call
    /// token tallying and progress forwarding: each tool call becomes an MCP progress
    /// milestone, which is what the caller's timeout clock resets on.
    /// </summary>
    private sealed class TallyingObserver : IAgentObserver
    {
        private readonly IAgentObserver _inner;
        private readonly Action<string> _progress;
        public int InputTokens;
        public int OutputTokens;

        public TallyingObserver(IAgentObserver inner, Action<string> progress)
        {
            _inner = inner;
            _progress = progress;
        }

        public void OnAssistantTextDelta(string delta) => _inner.OnAssistantTextDelta(delta);
        public void OnAssistantTextEnd() => _inner.OnAssistantTextEnd();

        public void OnToolCall(string toolName, string inputJson)
        {
            _inner.OnToolCall(toolName, inputJson);
            var preview = inputJson.ReplaceLineEndings(" ");
            _progress($"→ {toolName} {(preview.Length > 80 ? preview[..80] + "…" : preview)}");
        }

        public void OnToolResult(string toolName, string content, bool isError) =>
            _inner.OnToolResult(toolName, content, isError);

        public void OnUsage(int inputTokens, int outputTokens)
        {
            InputTokens = inputTokens;          // last call's context size, like the REPL tracks
            OutputTokens += outputTokens;       // output accumulates across the turn's calls
            _inner.OnUsage(inputTokens, outputTokens);
        }

        public void OnMessageAppended(Message message) => _inner.OnMessageAppended(message);
    }

    /// <summary>Workflow events → progress milestones (and stderr via the console observer's colours).</summary>
    private sealed class ForwardingWorkflowObserver : IWorkflowObserver
    {
        private readonly Action<string> _progress;
        public ForwardingWorkflowObserver(Action<string> progress) => _progress = progress;

        public void Classified(string workType, string reasoning) => _progress($"classified: {workType}");
        public void PhaseStart(string phaseId, string driverTier, IReadOnlyList<string> skills, string loadPolicy) =>
            _progress($"phase {phaseId} (driver {driverTier})");
        public void Consult(string phaseId, int n, int max, string advice) => _progress($"advisor {n}/{max} in {phaseId}");
        public void Gate(string phaseId, string kind, string outcome, string reason) =>
            _progress($"gate [{kind}] {phaseId} → {outcome}");
        public void Escalation(string fromPhase, string toPhase, string reason) => _progress($"escalation {fromPhase} → {toPhase}");
        public void Promotion(string phaseId, string fromTier, string toTier) => _progress($"promote {phaseId}: {fromTier} → {toTier}");
        public void Conflict(string phaseId, string detail) => _progress($"conflict in {phaseId}");
        public void PhaseEnd(string phaseId, string summary) { }
        public void RunEnd(RunStatus status, string reason) => _progress($"run {status}");
    }
}
