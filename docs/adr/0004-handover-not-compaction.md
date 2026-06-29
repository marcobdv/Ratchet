# 4. Retrieval-backed handover, never silent in-place compaction

- Status: Accepted
- Landed: v0.5 (manual), auto-triggered in v0.7
- Relates to: [0003](0003-session-as-branch-tree.md), [0006](0006-deterministic-orchestrator.md)

## Context

Conversations outgrow the context window. The industry-default answer is **in-place
compaction**: silently summarise the older turns and replace them with the summary.
It works, but it has two properties Ratchet rejects — the loss is *lossy* (detail the
summariser judged unimportant is gone) and it is *silent* (the user never authored or
even saw the boundary where information was dropped). When the agent later behaves as
if it forgot something, there is no diffable record of what was discarded or why.

## Decision

Handle long sessions with **retrieval-backed handover** instead. `/handover` has the
model author a *structured* document — goal · state · decisions · next steps ·
gotchas · pointers — saved as **editable Markdown** under `.ratchet/handovers/`.
`ratchet --handover <id>` then starts a **fresh** session with that doc injected as
its working set (the same mechanism as `AGENTS.md`), plus a `recall` tool that
searches the prior session's full node tree for any detail the summary left out.

The old context is **demoted to cold storage, never destroyed** (it lives in the v0.3
tree). Loss becomes *authored* — the human can read and edit the handover — and
*recoverable* — `recall` pages the original detail back in on demand. When unattended
runs need it, the same machinery runs as an **auto-triggered self-handover** once a
turn's input crosses `RATCHET_CONTEXT_LIMIT` (v0.7); `/compact` does it on demand.
This is compaction-shaped, but it folds into a handover rather than truncating.

## Alternatives considered

- **Silent in-place compaction.** Standard, cheap, and lossy-without-a-record.
  Rejected on the two grounds above: unauthored loss and no recovery path.
- **Just use a bigger context window.** Punts the problem one model generation and
  pays for it on every turn; does nothing for genuinely long-running work.
- **Full-transcript RAG with no authored summary.** Recall without a handover doc
  means the fresh session starts cold with no working set — the model has to
  re-discover the task shape from search hits. The authored doc is the cheap, legible
  spine; `recall` is the safety net under it.

## Consequences

- Long-session survival rides entirely on existing seams: the generator is one
  `ILlmClient` completion, `recall` is an `ITool` over `ISessionStore`. The loop never
  changed (ADR-0001).
- Recall scaled along its own seam: v0.7 added `ITextSearchableStore` (SQLite FTS5) so
  `recall` searches the DB instead of loading the whole tree; the file store keeps the
  in-memory scan as a fallback. The seam is additive — no store is forced to implement it.
- This decision is reused wholesale by the orchestrator (ADR-0006): phase-to-phase
  context handoff *is* an authored handover plus `recall` into the prior phase, not a
  shared mutable scratchpad.
- Cost: a handover is an explicit step with its own LLM call, heavier than a silent
  truncation. The payoff — authored, editable, recoverable context boundaries — is the
  whole thesis, and the README lists "no silent in-place compaction" as a deliberate
  non-goal.
