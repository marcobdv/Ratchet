using System.Text;
using CodeStack.Ratchet.Core;

namespace CodeStack.Ratchet.Workflow;

/// <summary>
/// Resolves a base tool name (read/write/edit/bash/load_skill/…) to an ITool. The
/// scheduler supplies <c>consult_advisor</c>, <c>recall</c>, and <c>request_escalation</c>
/// itself (they're per-phase), so the resolver only needs to know the plain tools.
/// </summary>
public delegate ITool? BaseToolResolver(string name);

/// <summary>
/// The orchestrator = scheduler + judge, split. THIS is the dumb, deterministic half:
/// it reads the workflow, runs each phase, evaluates the gate, and routes to the next
/// phase. No LLM judgment lives here — that shows up only at the intake classifier and
/// at judge gates, each a bounded call through <see cref="ILlmClient"/>. The guarantees
/// (floors run, gates are checked, loops/escalation are bounded) live in this control flow.
///
/// The agent loop (<c>Agent.RunTurnAsync</c>) is untouched: each phase is one ordinary
/// <see cref="Agent"/> with that phase's prompt, tool subset, and model tier.
/// </summary>
public sealed class WorkflowScheduler
{
    private readonly WorkflowConfig _config;
    private readonly Func<ModelTier, ILlmClient> _clientFactory;
    private readonly BaseToolResolver _baseTools;
    private readonly ISessionStore _store;
    private readonly ShellSpec _shell;
    private readonly SkillCatalog? _skills;
    private readonly IAgentObserver _agentObserver;
    private readonly IWorkflowObserver? _echo;
    private readonly string _runId;

    private readonly Dictionary<string, ILlmClient> _clients = new(StringComparer.Ordinal);

    private const int EscalationBudget = 4;   // global ceiling on "this was bigger than sized" jumps

    public WorkflowScheduler(
        WorkflowConfig config,
        Func<ModelTier, ILlmClient> clientFactory,
        BaseToolResolver baseTools,
        ISessionStore store,
        ShellSpec shell,
        string runId,
        SkillCatalog? skills = null,
        IAgentObserver? agentObserver = null,
        IWorkflowObserver? echo = null)
    {
        _config = config;
        _clientFactory = clientFactory;
        _baseTools = baseTools;
        _store = store;
        _shell = shell;
        _runId = runId;
        _skills = skills;
        _agentObserver = agentObserver ?? NullObserver.Instance;
        _echo = echo;
    }

    private ILlmClient Client(string tierName)
    {
        if (!_clients.TryGetValue(tierName, out var c))
            _clients[tierName] = c = _clientFactory(_config.Models[tierName]);
        return c;
    }

    public async Task<WorkflowRun> RunAsync(string task, CancellationToken ct)
    {
        var run = new WorkflowRun(_runId, task, _echo);

        // 1. Intake classifier — one judgment, recorded. Use the advisor tier if there
        //    is one (this is the highest-leverage call), else the default driver.
        var classifierTier = _config.DefaultAdvisor?.ModelTier ?? _config.DefaultDriverTier;
        var (workType, reasoning) = await new Classifier(Client(classifierTier)).ClassifyAsync(task, _config, ct);
        run.Classified(workType, _config.RecordClassification ? reasoning : "(recording disabled)");

        var wt = _config.WorkTypes[workType];   // classifier only returns validated keys

        // 2. Spine + work_type -> ordered phase plan (floors guaranteed by the loader).
        var plan = new List<string>(wt.Phases);
        var idx = 0;
        var loopCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var escalations = 0;
        var attempt = new Dictionary<string, int>(StringComparer.Ordinal);
        var handoff = new List<(string phase, string doc)>();
        string? priorSession = null;
        var stepBudget = plan.Count * 6 + 16;   // backstop against any routing pathology

        while (idx < plan.Count)
        {
            if (stepBudget-- <= 0) { run.RunEnd(RunStatus.Failed, "step budget exhausted (routing loop)"); return run; }

            var phaseId = plan[idx];
            var phase = _config.Phase(phaseId)!;
            attempt[phaseId] = attempt.GetValueOrDefault(phaseId) + 1;

            var result = await RunPhaseAsync(phase, workType, task, handoff, priorSession, run, ct);

            // Persist the phase transcript so the NEXT phase's `recall` can page into it.
            var sessionId = $"{_runId}.{phaseId}.{attempt[phaseId]}";
            PersistConversation(sessionId, result.Conversation);
            priorSession = sessionId;

            // Author the working-set handoff (v0.5 machinery) — also the judge gate's input.
            var doc = await new HandoverGenerator(Client(phase.DriverTier))
                .GenerateAsync(result.Conversation, null, ct);
            // Replace any prior doc for this phase (a loop re-authors it) then append.
            handoff.RemoveAll(h => h.phase == phaseId);
            handoff.Add((phaseId, doc));
            run.PhaseEnd(phaseId, doc);

            // 3a. Escalation takes priority: a phase that proved bigger re-enters earlier.
            if (result.Escalation.Requested)
            {
                if (++escalations > EscalationBudget)
                { run.RunEnd(RunStatus.Failed, "escalation budget exhausted"); return run; }
                var target = result.Escalation.TargetPhase!;
                run.Escalation(phaseId, target, result.Escalation.Reason);
                RouteTo(plan, ref idx, target);
                continue;
            }

            // 3b. Gate.
            var outcome = await EvaluateGateAsync(phase, task, doc, result.Conversation, ct);
            var kind = phase.Gate.Type.ToString().ToLowerInvariant();
            if (phase.Gate.Type == GateKind.None) { idx++; continue; }

            run.Gate(phaseId, kind, outcome.Pass ? "pass" : "fail", outcome.Reason);

            if (outcome.Pass) { idx++; continue; }

            // Conflict signal: advisor was consulted yet the gate went red — high-value to eval.
            if (result.Consult.Count > 0)
                run.Conflict(phaseId, $"advisor consulted {result.Consult.Count}× but {phaseId} gate failed");

            // Gate failed -> route by on_fail, bounded by max_loops on the gated phase.
            var route = phase.Gate.OnFail;
            if (route == "stop") { run.RunEnd(RunStatus.Failed, $"{phaseId} gate failed: {outcome.Reason}"); return run; }

            loopCounts[phaseId] = loopCounts.GetValueOrDefault(phaseId) + 1;
            if (loopCounts[phaseId] > phase.Gate.MaxLoops)
            { run.RunEnd(RunStatus.Failed, $"{phaseId} gate exceeded max_loops ({phase.Gate.MaxLoops})"); return run; }

            if (route == "loop") continue;          // re-run the same phase
            RouteTo(plan, ref idx, route);          // re-enter a named earlier phase (e.g. verify -> implement)
        }

        run.RunEnd(RunStatus.Completed, "");
        return run;
    }

    // ---- one phase ---------------------------------------------------------

    private sealed record PhaseResult(Conversation Conversation, ConsultState Consult, EscalationRequest Escalation);

    private async Task<PhaseResult> RunPhaseAsync(
        PhaseSpec phase, string workType, string task,
        IReadOnlyList<(string phase, string doc)> handoff, string? priorSession,
        IWorkflowObserver run, CancellationToken ct)
    {
        var eligibleSkills = (_config.WorkTypes[workType]).SkillsFor(phase.Id);
        var loadAll = _config.SkillLoading.LoadAll(eligibleSkills.Count);
        var loadPolicy = eligibleSkills.Count == 0 ? "none" : loadAll ? "load_all" : _config.SkillLoading.StrategyAbove;
        run.PhaseStart(phase.Id, phase.DriverTier, eligibleSkills, loadPolicy);

        var consult = new ConsultState
        {
            // Advisor-before-first-write: only on non-trivial work, only if an advisor exists.
            RequireConsultBeforeWrite = phase.Advisor is not null
                && !string.Equals(workType, "trivial", StringComparison.OrdinalIgnoreCase),
        };
        var escalation = new EscalationRequest();

        var conversation = new Conversation();
        conversation.Add(Message.UserText(task));

        var systemPrompt = BuildPhasePrompt(phase, task, handoff, eligibleSkills, loadAll);
        var tools = BuildPhaseTools(phase, conversation, consult, escalation, priorSession, run);

        var agent = new Agent(Client(phase.DriverTier), new ToolRegistry(tools), systemPrompt, _agentObserver);
        await agent.RunTurnAsync(conversation, ct);

        return new PhaseResult(conversation, consult, escalation);
    }

    private string BuildPhasePrompt(
        PhaseSpec phase, string task, IReadOnlyList<(string phase, string doc)> handoff,
        IReadOnlyList<string> eligibleSkills, bool loadAll)
    {
        var sb = new StringBuilder();
        sb.Append("You are the `").Append(phase.Id).Append("` phase of an automated coding workflow.\n");
        sb.Append("Role: ").Append(phase.Role).Append("\n\n");
        sb.Append("Do only this phase's job, then stop and report what you did. A later gate will check it.\n");

        if (handoff.Count > 0)
        {
            sb.Append("\n# Working set handed over from earlier phases\n");
            sb.Append("Treat this as authoritative context. For detail it omits, use the `recall` tool.\n\n");
            foreach (var (p, doc) in handoff)
                sb.Append("## From `").Append(p).Append("`\n").Append(doc).Append("\n\n");
        }

        if (eligibleSkills.Count > 0)
        {
            if (loadAll && _skills is not null)
            {
                sb.Append("\n# Skills (apply these)\n");
                foreach (var name in eligibleSkills)
                {
                    var skill = _skills.Find(name);
                    if (skill is null) continue;
                    sb.Append("## ").Append(name).Append('\n');
                    try { sb.Append(File.ReadAllText(skill.SkillFile)).Append("\n\n"); } catch { }
                }
            }
            else
            {
                sb.Append("\n# Skills available (call load_skill <name> before the matching work)\n");
                foreach (var name in eligibleSkills)
                {
                    var desc = _skills?.Find(name)?.Description ?? "";
                    sb.Append("  - ").Append(name).Append(desc.Length > 0 ? ": " + desc : "").Append('\n');
                }
            }
        }
        return sb.ToString();
    }

    private List<ITool> BuildPhaseTools(
        PhaseSpec phase, Conversation conversation, ConsultState consult,
        EscalationRequest escalation, string? priorSession, IWorkflowObserver run)
    {
        var byName = new Dictionary<string, ITool>(StringComparer.Ordinal);
        foreach (var name in phase.Tools)
        {
            ITool? tool = name switch
            {
                "consult_advisor" => phase.Advisor is null
                    ? null
                    : new ConsultAdvisorTool(Client(phase.Advisor.ModelTier), phase.Advisor, conversation, consult, run, phase.Id),
                "recall" => priorSession is null ? null : new RecallTool(_store, priorSession),
                "request_escalation" or "escalate" => new EscalateTool(phase.Escalation, escalation),
                _ => _baseTools(name),
            };
            if (tool is null) continue;

            // Enforce advisor-before-first-write by wrapping the state-changing tools.
            if (consult.RequireConsultBeforeWrite && name is "write" or "edit" or "bash")
                tool = new WriteGuardTool(tool, consult);

            byName[tool.Name] = tool;
        }

        // If a phase can escalate but didn't list the tool, still give it the lever.
        if (phase.Escalation.Count > 0 && !byName.ContainsKey("request_escalation"))
            byName["request_escalation"] = new EscalateTool(phase.Escalation, escalation);

        return byName.Values.ToList();
    }

    private async Task<GateOutcome> EvaluateGateAsync(PhaseSpec phase, string task, string doc, Conversation convo, CancellationToken ct)
    {
        switch (phase.Gate.Type)
        {
            case GateKind.Command:
                return await Gates.RunCommandAsync(phase.Gate.Run!, _shell, ct);
            case GateKind.Judge:
                var transcript = phase.Gate.FreshContext ? null : Flatten(convo);
                return await Gates.RunJudgeAsync(
                    Client(phase.Gate.ModelTier!), phase.Gate.JudgeAgent!, phase.Gate.FreshContext,
                    task, doc, transcript, ct);
            default:
                return new GateOutcome(true, "");
        }
    }

    // ---- routing -----------------------------------------------------------

    /// <summary>
    /// Re-enter <paramref name="target"/>: if it's already in the plan, jump back to it
    /// (re-running it and everything after, in order); if it was skipped, splice it in at
    /// its spine-ordered position. Either way <paramref name="idx"/> lands on it so it runs next.
    /// </summary>
    private void RouteTo(List<string> plan, ref int idx, string target)
    {
        var existing = plan.IndexOf(target);
        if (existing >= 0) { idx = existing; return; }

        var insertAt = 0;
        while (insertAt < plan.Count && _config.SpineIndex(plan[insertAt]) < _config.SpineIndex(target))
            insertAt++;
        plan.Insert(insertAt, target);
        idx = insertAt;
    }

    // ---- persistence + flatten --------------------------------------------

    private void PersistConversation(string sessionId, Conversation conversation)
    {
        var tree = new SessionTree();
        foreach (var m in conversation.Messages) tree.Append(m);
        _store.Save(sessionId, tree);
    }

    private static string Flatten(Conversation c)
    {
        var sb = new StringBuilder();
        foreach (var m in c.Messages)
        {
            sb.Append(m.Role == Role.User ? "USER: " : "ASSISTANT: ");
            foreach (var b in m.Content)
            {
                switch (b)
                {
                    case TextBlock t: sb.Append(t.Text); break;
                    case ToolUseBlock u: sb.Append($"[calls {u.Name} {u.InputJson}]"); break;
                    case ToolResultBlock r: sb.Append($"[result: {r.Content}]"); break;
                }
            }
            sb.Append('\n');
        }
        return sb.ToString().Trim();
    }
}
