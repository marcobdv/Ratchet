# 6. Deterministic orchestrator; LLM judgment only at the classifier and judge gates

- Status: Accepted
- Landed: v0.8 (orchestrator), hardened in v0.9 (gates, runs, resume, land)
- Relates to: [0004](0004-handover-not-compaction.md), [0007](0007-two-layer-routing-not-a-router.md)
- Design doc: [../workflow-orchestration.md](../workflow-orchestration.md)

## Context

The obvious way to make an agent tackle a larger task is to let it plan its own work:
an outer LLM loop that decides what to do next, spawns sub-agents, judges its own
progress, and decides when it's done. It's flexible, but the control flow lives inside
model output, which makes it non-reproducible, hard to bound (when does it stop?), and
opaque after the fact (a bad decision is buried in a token stream that scrolled away).

We wanted phased execution — research → plan → implement → verify → review → land,
cheap models doing the bulk and frontier spend concentrated where it matters — without
the control flow itself becoming a thing only a model understands.

## Decision

Make the **orchestrator deterministic** and confine LLM judgment to two narrow points.
The design's rule: *structure the control flow, not the thinking.*

- A fixed, ordered **spine** of phases. Each phase is its own `Agent` with its own
  role prompt, tool subset, model tier, and skills. The phase's *thinking* is soft
  (prompts, skills); its *structure* is code.
- The scheduler is plain control flow: it runs phases, evaluates **gates**, and routes.
  Pass advances; fail **loops back** (bounded by `max_loops`); a phase that proves
  bigger than its sizing **escalates** up the spine with fresh context.
- LLM judgment appears in exactly two places: the **intake classifier** (one call that
  sizes the task into a `work_type` selecting which phases run) and **judge gates**
  (spend a frontier model on merge-readiness judgment an exit code can't express).
  **Command gates** route on a process exit code — the cheapest, strongest judge of all
  (e.g. `dotnet build`).
- **Floors** (`verify`, `review`) always run regardless of classification — the
  guarantees no `work_type` may drop.

Phase-to-phase context handoff *is* ADR-0004: an authored handover doc plus `recall`
into the prior phase's transcript, not a shared mutable blackboard.

## Alternatives considered

- **Agent-driven planning loop (the outer LLM decides everything).** Maximally
  flexible; non-reproducible, hard to bound, opaque. Rejected: coding has machine-
  checkable ground truth (does it build? do tests pass?), so a deterministic spine with
  a command gate beats a model deciding whether it's finished.
- **A rigid pipeline with no loop-back or escalation.** Reproducible but brittle —
  the first failed gate or mis-sized task dead-ends. Bounded loop-back and escalation
  add adaptivity without surrendering determinism.
- **A shared scratchpad for phase context.** Simpler than handover-per-phase, but it
  re-introduces the silent-context problem ADR-0004 rejected. Reusing handover keeps
  every boundary authored and diffable.

## Consequences

- A run is reproducible and inspectable. v0.9 made this concrete: the classification +
  reasoning, every gate outcome, advisor consults, and advisor↔gate conflicts are
  written to `.ratchet/runs/<id>.json`, so **a bad skip is diffable after the fact**
  rather than a console line that scrolled away (`--runs` / `--run`).
- Bounded everything: `max_loops` caps loop-back, the classifier caps which phases run,
  floors cap what can be skipped. The run terminates.
- It checkpoints before each phase, so an interrupted run resumes from the last good
  phase (`--workflow-resume`) without re-classifying — the prerequisite for unattended
  runs.
- The deterministic spine is exactly what ADR-0007 hangs routing on: the `(phase,
  work_type)` key already exists, so routing needs no separate router.
- The orchestrator is a separate project (`Ratchet.Workflow`, +YamlDotNet) above the
  seams; the agent loop, tree, and handover never changed (ADR-0001, ADR-0002).
- Cost: a phased run makes more model calls than one undifferentiated agent, and the
  YAML spine is a real artifact to author and maintain. The payoff is reproducibility,
  bounded cost, and the cheap-driver/frontier-gate economics being *measurable* (v0.9
  per-tier cost accounting) rather than asserted.
