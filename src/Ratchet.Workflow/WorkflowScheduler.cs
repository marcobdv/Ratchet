using System.Diagnostics;
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
    private readonly IToolGate _gate;
    private readonly IRunStore? _runStore;
    private readonly string? _workflowFile;
    private readonly string _runId;

    private readonly Dictionary<string, ILlmClient> _clients = new(StringComparer.Ordinal);
    private CostTally _tally = new();   // set per run; metered clients write here

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
        IWorkflowObserver? echo = null,
        IToolGate? gate = null,
        IRunStore? runStore = null,
        string? workflowFile = null)
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
        _gate = gate ?? AllowAllGate.Instance;
        _runStore = runStore;
        _workflowFile = workflowFile;
    }

    // Every completion for a tier is metered into _tally, so per-tier cost falls out
    // automatically across drivers, classifier, judges, advisors, and handovers.
    private ILlmClient Client(string tierName)
    {
        if (!_clients.TryGetValue(tierName, out var c))
            _clients[tierName] = c = new MeteredLlmClient(_clientFactory(_config.Models[tierName]), tierName, _tally);
        return c;
    }

    public Task<WorkflowRun> RunAsync(string task, CancellationToken ct) => RunAsync(task, ct, null);

    /// <summary>
    /// Run (or resume) the workflow. With <paramref name="resume"/> the loop state is
    /// rehydrated from a snapshot and continues from the last checkpointed phase — the
    /// classifier does not re-run, so the original sizing is preserved. Without it, a
    /// fresh run classifies first. A checkpoint is written before each phase, so an
    /// interrupted run (e.g. a transient API error) resumes by re-running only that phase.
    /// </summary>
    public async Task<WorkflowRun> RunAsync(string task, CancellationToken ct, RunSnapshot? resume)
    {
        _clients.Clear();
        using var runSpan = RatchetTelemetry.Activity.StartActivity("workflow.run", ActivityKind.Internal);
        runSpan?.SetTag("ratchet.workflow.name", _config.Name);
        runSpan?.SetTag("ratchet.workflow.run_id", _runId);

        var run = new WorkflowRun(_runId, task, _echo);
        if (resume is not null)
            run.SeedFrom(resume.WorkType, resume.ClassifierReasoning, resume.Events, resume.Cost);
        _tally = run.Cost;   // metered clients accumulate onto the (possibly seeded) total

        string workType;
        List<string> plan;
        int idx, escalations;
        Dictionary<string, int> loopCounts, attempt;
        Dictionary<string, string> currentTier;   // reactive layer: each phase's promoted driver tier
        List<(string phase, string doc)> handoff;
        string? priorSession;

        if (resume is not null)
        {
            workType = resume.WorkType ?? _config.WorkTypes.Keys.First();
            plan = new List<string>(resume.Plan);
            idx = resume.Idx;
            loopCounts = new Dictionary<string, int>(resume.LoopCounts, StringComparer.Ordinal);
            attempt = new Dictionary<string, int>(resume.Attempt, StringComparer.Ordinal);
            currentTier = new Dictionary<string, string>(resume.CurrentTier, StringComparer.Ordinal);
            escalations = resume.Escalations;
            handoff = resume.Handoff.Select(h => (h.Phase, h.Doc)).ToList();
            priorSession = resume.PriorSession;
        }
        else
        {
            // Intake classifier — one judgment, recorded. Use the advisor tier if there is
            // one (highest-leverage call), else the default driver.
            var classifierTier = _config.DefaultAdvisor?.ModelTier ?? _config.DefaultDriverTier;
            string wtName, reasoning;
            using (var clsSpan = RatchetTelemetry.Activity.StartActivity("classify", ActivityKind.Internal))
            {
                (wtName, reasoning) = await new Classifier(Client(classifierTier)).ClassifyAsync(task, _config, ct);
                clsSpan?.SetTag("ratchet.work_type", wtName);
            }
            run.Classified(wtName, _config.RecordClassification ? reasoning : "(recording disabled)");
            workType = wtName;
            plan = new List<string>(_config.WorkTypes[workType].Phases);
            idx = 0; escalations = 0;
            loopCounts = new(StringComparer.Ordinal);
            attempt = new(StringComparer.Ordinal);
            currentTier = new(StringComparer.Ordinal);
            handoff = new();
            priorSession = null;
        }

        // Guard the lookup: a resumed run whose config was edited (work_type renamed/removed)
        // should fail gracefully here, not throw KeyNotFoundException mid-flight.
        if (!_config.WorkTypes.TryGetValue(workType, out var wt))
        {
            run.RunEnd(RunStatus.Failed, $"work_type '{workType}' is not defined in this workflow (renamed or removed?).");
            return run;
        }
        runSpan?.SetTag("ratchet.work_type", workType);
        var stepBudget = plan.Count * 6 + 16;   // backstop against any routing pathology

        void Checkpoint(string status) => _runStore?.Save(new RunSnapshot
        {
            RunId = _runId, Task = task, WorkflowFile = _workflowFile ?? "", Status = status,
            FailReason = run.FailReason, WorkType = run.WorkType, ClassifierReasoning = run.ClassifierReasoning,
            Plan = new List<string>(plan), Idx = idx,
            LoopCounts = new Dictionary<string, int>(loopCounts),
            Attempt = new Dictionary<string, int>(attempt),
            Escalations = escalations,
            Handoff = handoff.Select(h => new HandoffEntry { Phase = h.phase, Doc = h.doc }).ToList(),
            PriorSession = priorSession,
            CurrentTier = new Dictionary<string, string>(currentTier),
            Events = run.Events.ToList(),
            Cost = run.Cost,
        });

        while (idx < plan.Count)
        {
            if (stepBudget-- <= 0) { run.RunEnd(RunStatus.Failed, "step budget exhausted (routing loop)"); Checkpoint("failed"); return run; }

            // Checkpoint reflects "about to run plan[idx]" — so a crash here resumes by
            // re-running exactly this phase, with prior handoff/recall intact.
            Checkpoint("running");

            var phaseId = plan[idx];
            var phase = _config.Phase(phaseId)!;
            attempt[phaseId] = attempt.GetValueOrDefault(phaseId) + 1;

            // PREDICTIVE tier: pick the starting tier for (phase, work_type) the first time we
            // enter this phase; thereafter the REACTIVE layer may have promoted it (below).
            if (!currentTier.ContainsKey(phaseId)) currentTier[phaseId] = _config.StartingTier(wt, phaseId);
            var driverTier = currentTier[phaseId];

            var result = await RunPhaseAsync(phase, driverTier, workType, task, handoff, priorSession, run, ct);

            // Persist the phase transcript so the NEXT phase's `recall` can page into it.
            var sessionId = $"{_runId}.{phaseId}.{attempt[phaseId]}";
            PersistConversation(sessionId, result.Conversation);
            priorSession = sessionId;

            // Author the working-set handoff (v0.5 machinery) — also the judge gate's input.
            var doc = await new HandoverGenerator(Client(driverTier))
                .GenerateAsync(result.Conversation, null, ct);
            handoff.RemoveAll(h => h.phase == phaseId);
            handoff.Add((phaseId, doc));
            run.PhaseEnd(phaseId, doc);

            // 3a. Escalation takes priority: a phase that proved bigger re-enters earlier.
            if (result.Escalation.Requested)
            {
                if (++escalations > EscalationBudget)
                { run.RunEnd(RunStatus.Failed, "escalation budget exhausted"); Checkpoint("failed"); return run; }
                var target = result.Escalation.TargetPhase!;
                run.Escalation(phaseId, target, result.Escalation.Reason);
                RouteTo(plan, ref idx, target);
                continue;
            }

            // 3b. Gate.
            if (phase.Gate.Type == GateKind.None) { idx++; continue; }
            var outcome = await EvaluateGateAsync(phase, task, doc, result.Conversation, ct);
            var kind = phase.Gate.Type.ToString().ToLowerInvariant();
            run.Gate(phaseId, kind, outcome.Pass ? "pass" : "fail", outcome.Reason);

            if (outcome.Pass) { idx++; continue; }

            // Conflict signal: advisor was consulted yet the gate went red — high-value to eval.
            if (result.Consult.Count > 0)
                run.Conflict(phaseId, $"advisor consulted {result.Consult.Count}× but {phaseId} gate failed");

            // Gate failed -> route by on_fail, bounded by max_loops on the gated phase.
            var route = phase.Gate.OnFail;
            if (route == "stop") { run.RunEnd(RunStatus.Failed, $"{phaseId} gate failed: {outcome.Reason}"); Checkpoint("failed"); return run; }

            loopCounts[phaseId] = loopCounts.GetValueOrDefault(phaseId) + 1;
            if (loopCounts[phaseId] > phase.Gate.MaxLoops)
            { run.RunEnd(RunStatus.Failed, $"{phaseId} gate exceeded max_loops ({phase.Gate.MaxLoops})"); Checkpoint("failed"); return run; }

            // REACTIVE promotion: a red gate means the work wasn't good enough, so promote the
            // driver of the phase that re-runs one rung up the ladder rather than retry at the
            // same tier (a stronger driver changes the work; consulting harder doesn't). Bounded
            // by the same max_loops and the ladder top; a work_type may cap it with promote:false.
            var promoteTarget = route == "loop" ? phaseId : route;
            if (wt.Promote)
            {
                var from = currentTier.GetValueOrDefault(promoteTarget, _config.StartingTier(wt, promoteTarget));
                var to = _config.PromoteTier(from);
                if (to != from)
                {
                    currentTier[promoteTarget] = to;
                    if (_config.RecordPromotions) run.Promotion(promoteTarget, from, to);
                }
            }

            if (route == "loop") continue;          // re-run the same phase
            RouteTo(plan, ref idx, route);          // re-enter a named earlier phase (e.g. verify -> implement)
        }

        run.RunEnd(RunStatus.Completed, "");
        Checkpoint("completed");
        return run;
    }

    // ---- one phase ---------------------------------------------------------

    private sealed record PhaseResult(Conversation Conversation, ConsultState Consult, EscalationRequest Escalation);

    private async Task<PhaseResult> RunPhaseAsync(
        PhaseSpec phase, string driverTier, string workType, string task,
        IReadOnlyList<(string phase, string doc)> handoff, string? priorSession,
        IWorkflowObserver run, CancellationToken ct)
    {
        using var phaseSpan = RatchetTelemetry.Activity.StartActivity($"phase {phase.Id}", ActivityKind.Internal);
        phaseSpan?.SetTag("ratchet.phase", phase.Id);
        phaseSpan?.SetTag("ratchet.work_type", workType);
        phaseSpan?.SetTag("ratchet.driver_tier", driverTier);

        var eligibleSkills = (_config.WorkTypes[workType]).SkillsFor(phase.Id);
        var loadAll = _config.SkillLoading.LoadAll(eligibleSkills.Count);
        var loadPolicy = eligibleSkills.Count == 0 ? "none" : loadAll ? "load_all" : _config.SkillLoading.StrategyAbove;
        run.PhaseStart(phase.Id, driverTier, eligibleSkills, loadPolicy);

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

        var agent = new Agent(Client(driverTier), new ToolRegistry(tools), systemPrompt, _agentObserver, _gate);
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
                    // Don't silently drop a skill the prompt claims to apply: mark a read failure
                    // so the gap is visible rather than an empty body the driver is told to follow.
                    try { sb.Append(File.ReadAllText(skill.SkillFile)).Append("\n\n"); }
                    catch (Exception ex) { sb.Append("(skill body unavailable: ").Append(ex.Message).Append(")\n\n"); }
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
        if (phase.Gate.Type == GateKind.None) return new GateOutcome(true, "");

        using var gateSpan = RatchetTelemetry.Activity.StartActivity(
            $"gate {phase.Gate.Type.ToString().ToLowerInvariant()}", ActivityKind.Internal);
        gateSpan?.SetTag("ratchet.phase", phase.Id);

        var outcome = phase.Gate.Type switch
        {
            GateKind.Command => await Gates.RunCommandAsync(phase.Gate.Run!, _shell, ct),
            GateKind.Judge => await Gates.RunJudgeAsync(
                Client(phase.Gate.ModelTier!), phase.Gate.JudgeAgent!, phase.Gate.FreshContext,
                task, doc, phase.Gate.FreshContext ? null : Flatten(convo), ct),
            _ => new GateOutcome(true, ""),
        };

        gateSpan?.SetTag("ratchet.gate.pass", outcome.Pass);
        if (!outcome.Pass) gateSpan?.SetStatus(ActivityStatusCode.Error, "gate failed");
        return outcome;
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
