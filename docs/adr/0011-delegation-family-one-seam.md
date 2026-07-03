# 11. Sub-agents, teams, and council are one seam at three altitudes

- Status: Accepted
- Landed: v0.13

## Context

Ratchet had one delegation primitive: `DelegateTool`, a nested `Agent` with its own
prompt, tools, gate, and model (the `explore` sub-agent and the advisors). Two larger
shapes were wanted:

- **Agent teams** — a roster of named sub-agents, dispatched (optionally in parallel)
  and merged — "like Claude Code's agent teams."
- **Council mode** — a deliberation harness for architectural decisions with no prior
  art: independent personas argue from cold, separate contexts; a synthesizer organizes
  their locked outputs into an Analysis Brief; a human writes the Decision Record. (Spec:
  the Council Tool, generalized from the Pre-R Deliberation Pattern; its Analysis-Brief
  schema derives from OpenRouter Fusion's judge, but Fusion emits a prose *answer* via an
  auto-judge, where a council keeps the human in the synthesis seat and emits a *decision*.)

The risk was three parallel subsystems with their own definition formats, dispatch code,
and lifecycles — exactly the accretion ADR-0001 exists to prevent.

## Decision

All three are the **same seam at three altitudes of orchestration**, composed from the
existing `DelegateTool` / `ILlmClient` primitives — no loop edit:

- **Sub-agent** = one `DelegateTool`.
- **Team** = N members dispatched in parallel (`TeamTool`, a single tool that fans out
  internally with `Task.WhenAll`, since the loop runs tools sequentially) + an optional
  lead synthesis pass that merges.
- **Council** = a team run under the deliberation protocol (`CouncilTool`): cold
  independent personas, a clerk that **organizes but does not decide**, and a Decision
  Record template dropped for the human.

One definition format serves all three: a Claude-Code-compatible `*.md` with YAML
frontmatter under `.claude/agents` / `.ratchet/agents`. A file with no `members` is a
sub-agent; with `members` it is a team; with `members` + `mode: council` it is a council.
Per-member `model:` routing gives the "Council of Reeds" multi-model path for free
through the provider seam.

Two invariants define council mode and are enforced structurally, not by prompt: the
synthesizer runs **only after** every perspective is collected (locked), and personas
**never** see the Brief. Phase 1 ships as the clerk (Brief, no recommendation); the
design reserves a Phase 2 recommendation rendered *after* the contradictions, so the same
anchoring guard that protects the personas from each other protects the human from the
synthesizer.

## Alternatives considered

- **A council/team framework of its own.** Bespoke dispatch, config, and lifecycle —
  three subsystems where one seam composes. Rejected: it re-earns the accretion cost
  ADR-0001 pays once.
- **A standalone council CLI (the spec's Stage 1).** Kept as the provider-agnostic
  reference, but the integrated home is here — the council's core invariant (independent
  cold context) is already how a Ratchet delegate runs, so the harness is the natural fit.

## Consequences

- New safety rails ride existing seams, not the loop: a nesting-depth guard
  (`AsyncLocal`, counts nesting not fan-out width) stops runaway recursion, and a
  per-delegate iteration budget (an `IAgentObserver` that counts turns and cancels) bounds
  a looping delegate. `CostTally` is lock-guarded for parallel metering.
- The council writes a repo artifact (Decision Record) that a later phase or a human picks
  up — the same "authored, diffable output" posture as handovers.
- Teams and council fan out to N models, so cost is N×; per-member routing lets the bulk
  run on cheap/local drivers with frontier spend concentrated where it matters (the same
  economics as the workflow's cheap-drivers/frontier-gates thesis).
