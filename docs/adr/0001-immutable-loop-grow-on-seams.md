# 1. The agent loop is immutable; all growth happens on seams

- Status: Accepted
- Landed: v0 (established); honoured in control flow through v0.11 — but `Agent.cs`
  itself was edited in v0.9 (gate check), v0.10 and v0.11 (telemetry spans). See
  "Consequences" for the honest accounting.
- Supersedes: —

## Context

Ratchet is a learning artifact: the whole point is to understand the agent loop at
the wire level, not to compete with a full IDE agent. But a learning artifact that
stays frozen is a toy, and one that grows by accretion becomes the very thing it was
trying to demystify — a loop nobody can read because twelve features are threaded
through it.

The tension: we want the project to grow toward a real agent (Roslyn, MCP,
sub-agents, workflows, routing, telemetry) *without* the central `while` loop in
`Agent.RunTurnAsync` becoming the dumping ground for all of it.

## Decision

`Agent.RunTurnAsync` — the call/append/dispatch-tools/repeat loop — is treated as
**immutable surface**. Every new capability must land on an existing extension point
(`ILlmClient`, `ITool`, `IAgentObserver`, `ISessionStore`, later `IToolGate`,
`IWorkflowObserver`, `IRunStore`, `ITextSearchableStore`) rather than by adding a
branch to the loop. If a feature appears to *need* a loop edit, that is a signal the
seam is missing — add the seam, not the branch.

"Read `Core/Agent.cs` first — the entire idea is one `while`" is a promise the README
makes to every reader, and the architecture is organised to keep that promise true.

## Alternatives considered

- **Edit the loop per feature.** The path of least resistance for each individual
  feature, and the reason most agent codebases have an unreadable core. Rejected: it
  trades a one-time seam cost for permanent comprehension cost.
- **A plugin/middleware pipeline around the loop.** More formal extensibility, but it
  buys generality the project does not need and obscures the loop behind indirection —
  the opposite of the pedagogical goal.

## Consequences

- Across v0.1–v0.11 — streaming, sessions, branch trees, handover, provider
  abstraction, Roslyn/MCP/sub-agents/skills, workflow orchestration, model routing,
  and OpenTelemetry — the loop's *control flow* never changed. The file did:
  `git log -- src/Ratchet.Core/Agent.cs` shows the v0.9 gate check (which added the
  `IToolGate` seam — the move this ADR allows), and v0.10–v0.11 telemetry spans
  wrapping the turn/tool calls in place. The spans are the closest the project has
  come to breaking this rule: instrumentation rather than behaviour, but in-loop
  edits all the same. Categorical "never edited" claims were retired from the docs
  in favour of this accounting.
- The seam set is itself the design record. New seams are added deliberately and
  rarely; each one (e.g. `IToolGate` in v0.9) is a small, reviewable event.
- Cost: occasionally a feature is slightly more work because it must be expressed
  through a seam instead of inline. This is the intended tax, paid once, for a core
  that stays legible.
