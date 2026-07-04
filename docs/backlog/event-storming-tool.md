# Backlog: `storm` — a DDD event-storming tool in the delegation family

- Status: **Proposed** (not scheduled)
- Origin: session discussion 2026-07-03 — "can we have a DDD event storming with the
  council tool, or would that be a separate tool?"
- Governing prior art: [ADR-0011](../adr/0011-delegation-family-one-seam.md) (sub-agent /
  team / council are one seam at three altitudes), `src/Ratchet.Core/Council.cs`

## Problem

Event storming is a domain-discovery workshop: participants silently post domain events,
then iteratively build a shared board — timeline, commands, actors, policies, and
eventually aggregate and bounded-context candidates. Nothing in Ratchet runs that shape
today. The council is the nearest tool but was deliberately built for a different job:
it **organizes disagreement into a decision for a human**; storming **constructs a domain
model as a shared artifact**.

## Why the council can't just do it

The overlap is real but stops after round one:

- **Round 1 is a perfect fit.** Storming's silent-posting phase (everyone writes events
  before anyone speaks) is exactly the council's anchoring guard: cold, parallel, locked
  dispatch. Same primitive, different prompt and roster.
- **Every later round breaks council invariant #1.** Storming participants *must* see the
  shared board and react to it — spot missing events, dispute the timeline, propose
  boundaries. The council structurally forbids any member seeing another's output; that
  invariant is the tool's identity (ADR-0011), not a flag to flip.
- **The synthesizer's job differs in kind.** The clerk organizes and is barred from
  constructing. A storming *facilitator* is constructive — dedupes events, enforces the
  timeline, applies proposals to the board each round — and runs every round, not once.
- **The artifact is wrong.** A Decision Record is decision-shaped. Storming emits a
  model: event timeline, command → aggregate → event → policy grammar, context candidates.

So: a **sibling protocol, not a council mode and not a layer on top of it**. Calling
`CouncilTool` per round would fight the fixed clerk prompt and record format at every step.

## Proposed shape

A `StormTool` (`ITool`, Core) alongside `CouncilTool` — the delegation family's fourth
member, at the council's altitude but under a different protocol:

1. **Round 1 — silent posting (cold, parallel, locked).** Reuse
   `SubAgents.DispatchParallelAsync` verbatim. Personas are domain-lens voices (defaults
   TBD, e.g. process/customer/operations/integration) or project-defined agents via the
   same roster resolution as the ad-hoc council. Prompt: enumerate domain events, past
   tense, one per line, with a one-line note each.
2. **Merge.** The facilitator dedupes and orders into **board v1** (the timeline).
3. **Rounds 2..N — board rounds (warm, shared).** Each round, every persona sees the
   current board and proposes deltas: missing/wrong events, commands + actors, policies
   ("whenever X → Y"), cluster/boundary suggestions. Parallel within a round; the
   facilitator applies deltas between rounds. Round count bounded by config (default 2–3),
   plus the existing `Delegation` nesting guard and per-delegate iteration budget.
4. **Hotspots, not resolutions (the council DNA worth keeping).** Where personas
   conflict — event naming, ownership, boundary placement — the facilitator *marks* a
   hotspot; it never adjudicates. Organizing disagreement stays; deciding stays human.
5. **Artifact.** `.ratchet/storm/<id>.md`: the timeline, the grammar table, candidate
   aggregates/bounded contexts, and the hotspot list. Same authored-diffable-output
   posture as handovers and Decision Records.

**Composition, not nesting:** each hotspot is precisely a "decision with no prior art" —
the council's entry criterion. The intended flow is `storm` (discover, surface hotspots)
→ `council` per hotspot (deliberate) → human (decide). Storm upstream of council.

**Definition format:** per ADR-0011, one format serves the family — `members` +
`mode: storm` in the same agent-definition frontmatter, plus an ad-hoc built-in like the
ad-hoc council. Per-member `model:` routing works unchanged.

**Estimated reuse (~60%):** `DelegateTool` personas, roster resolution
(`CouncilPersonas.Roster` pattern), parallel dispatch, `Delegation.Enter()`, cost tally,
`.ratchet/<dir>` record writing. New: the round loop, the facilitator prompt + board
state, the board artifact writer, default personas.

## Cheapest next step (do this before building)

Convene the existing ad-hoc council with a storming-shaped `decision` ("map the domain
events of X; argue the pivotal events and where the context boundaries fall"), optionally
with domain-expert agents in `.ratchet/agents`. Yields N independent event inventories
plus a Brief showing where they disagree about the domain — a one-round approximation
that tells us whether the multi-round board loop earns its keep.

## Open questions

- Default persona roster: generic lenses vs. requiring project-defined domain experts?
- Round convergence: fixed count, or stop when a round produces no accepted deltas?
- Board representation the personas consume: markdown table vs. a compact DSL (affects
  delta-application reliability).
- Rendering: the board is a natural fit for the future `mermaid` fence handler seam in
  `MarkdownAnsiRenderer` (timeline / context map as a diagram) — out of scope here, noted
  for the mermaid project.
- Does this need its own ADR when implemented? Likely yes — a short one recording "second
  protocol at the council altitude; invariant relaxed *by design* after round 1."

## Acceptance sketch

- `storm` runs R rounds over M members and writes the board artifact with ≥1 hotspot
  section (possibly empty) — unit-tested with fake `ILlmClient`s like `CouncilTests`.
- Council invariants untouched: `CouncilTool` and its tests unchanged.
- A hotspot line is copy-pasteable as a `council` `decision` input.
