# 3. A session is a branch tree (HEAD over a DAG), not a linear transcript

- Status: Accepted
- Landed: v0.3
- Relates to: [0004](0004-handover-not-compaction.md)

## Context

After v0.2 a session was a flat, append-only list of message nodes. That makes
"undo" and "try a different approach from three turns back" impossible without
destroying history: to rewind you would truncate the list, throwing away the branch
you are leaving. But exploring alternatives is exactly what agent work needs — a tool
call went wrong, a plan was a dead end, you want to back up and fork.

## Decision

Model a session as a **tree of message nodes with a HEAD pointer** — the git model.
Each node points at its parent; HEAD marks the current tip. Rewinding moves HEAD back
*whole turns*; continuing from a rewound HEAD **forks a new branch** while the old
line is preserved intact. `/tree` visualises the structure, `/goto <node>` jumps
between branch tips, `/rewind [n]` walks HEAD back n turns.

Rewind is deliberately **turn-level, not message-level**, so HEAD always lands on a
valid conversation boundary (a complete user→assistant→tools cycle), never in the
middle of an unresolved tool call.

## Alternatives considered

- **Linear transcript with truncation for undo.** Simple, and what v0.2 had.
  Rejected: undo is destructive, and you can never compare two approaches because only
  one survives.
- **Snapshot/checkpoint copies of the whole transcript.** Easy to reason about but
  O(n) storage per branch and no shared history — divergent branches duplicate their
  common prefix. The DAG shares the prefix for free.
- **Message-level rewind.** More granular, but lets HEAD rest inside a half-finished
  turn, which the loop would then have to defend against on every resume. Turn-level
  rewind makes invalid states unrepresentable.

## Consequences

- Persistence became *append-only*: a turn adds new nodes, it never rewrites existing
  ones. This is what later let `SqliteSessionStore` (v0.4) insert just the new rows per
  turn and walk parent chains with recursive CTEs, instead of rewriting a file.
- The tree is the substrate ADR-0004 builds on: `recall` searches the prior session's
  full node tree, and nothing is ever destroyed — old context is demoted, not deleted,
  precisely because the DAG keeps every branch.
- Cost: the store and the resume path are more complex than a list would be (parent
  pointers, HEAD management, branch tips). The capability — non-destructive
  exploration — is judged worth it for an agent.
