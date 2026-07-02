# Architecture Decision Records

This directory records the *contested* architectural decisions in Ratchet — the
places where there was a real fork in the road and we deliberately took one path
over another. The README's `>` version blockquotes say **what** each version did;
these ADRs say **why it was done that way and not the obvious other way**.

Not every version has an ADR. v0.1 (streaming), v0.2 (sessions), v0.4 (SQLite),
and v0.7 (sharper tools) were implementations of a settled idea — they rejected
no meaningful alternative, so they live in the README changelog, not here.

## Format

Each ADR is a short [Nygard-style](https://cognitect.com/blog/2011/11/15/documenting-architecture-decisions)
record: **Context · Decision · Alternatives considered · Consequences**. They are
immutable once accepted — a decision that gets reversed gets a *new* ADR that
supersedes the old one, rather than an edit. That way the record of "we used to
think X" survives.

## Index

| # | Decision | Landed | Status |
|---|---|---|---|
| [0001](0001-immutable-loop-grow-on-seams.md) | The agent loop is immutable; all growth happens on seams | v0 / ongoing | Accepted |
| [0002](0002-core-stays-dependency-free.md) | Core stays dependency-free; heavy deps live in separate projects | v0 / v0.6 | Accepted |
| [0003](0003-session-as-branch-tree.md) | A session is a branch tree (HEAD over a DAG), the git model | v0.3 | Accepted |
| [0004](0004-handover-not-compaction.md) | Retrieval-backed handover, never silent in-place compaction | v0.5 | Accepted |
| [0005](0005-provider-agnostic-keep-native-wire.md) | Provider-agnostic via `IChatClient`, but keep the hand-rolled Anthropic wire | v0.6 / v0.9 | Accepted |
| [0006](0006-deterministic-orchestrator.md) | Deterministic orchestrator; LLM judgment only at the classifier and judge gates | v0.8 | Accepted |
| [0007](0007-two-layer-routing-not-a-router.md) | Two-layer model routing (predictive + reactive), not a dedicated router | v0.10 | Accepted |
| [0008](0008-telemetry-on-bcl-in-core.md) | Telemetry on BCL diagnostics in Core; the OTel SDK only in the CLI | v0.11 | Accepted |
| [0009](0009-readonly-subagents-by-structure.md) | YOLO by default, but delegated sub-agents are scoped read-only by *structure* | v0.9 / v0.11 | Accepted |
| [0010](0010-stop-reason-policy-at-the-loop-boundary.md) | A stop-reason policy at the loop boundary (orphaned tool_use is closed, truncation refuses loudly) | v0.12 | Accepted |

## The through-line

Read top to bottom, the ADRs tell one story: **keep the irreducible core small and
unchanging, and push every elaboration to a seam.** ADR-0001 and ADR-0002 establish
that discipline; ADRs 0003–0009 are each a worked example of honouring it while
adding something substantial (persistence, long-context survival, provider choice,
orchestration, routing, observability, safety). No *feature* has required editing
`Agent.RunTurnAsync`; the one deliberate exception is ADR-0010, a *correctness
invariant* (answer every tool_use) that can only live in the loop that creates it —
taken knowingly, under ADR-0001's "add the seam, not the branch" escape hatch, and
recorded here precisely because the rule matters.
