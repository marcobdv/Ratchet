# 9. YOLO by default, but delegated sub-agents are scoped read-only by *structure*

- Status: Accepted
- Landed: v0.9 (`IToolGate`), completed in v0.11 (read-only `explore`)
- Relates to: [0001](0001-immutable-loop-grow-on-seams.md)

## Context

Ratchet is "pi-plain YOLO" on purpose: the top-level agent, which a human drives
directly and watches, runs without permission prompts. That's a fine default for an
operator-in-the-loop tool. But a **delegated** sub-agent — the `explore` investigator
that the main agent spawns to go read the codebase — is *not* driven by a human
turn-by-turn. Giving it the same unrestricted toolset means a prompt-injected file or
a confused model could have an "investigator" delete files or run mutating shell
commands, with no operator watching that inner loop.

The user's framing was exact: **"YOLO by design, but agents should be scoped to their
role."**

## Decision

Two parts.

1. **A permission seam, off by default** (`IToolGate`, v0.9). `Agent.ExecuteToolAsync`
   consults a gate *before* a tool runs; a denial returns to the model as an error
   result, not a crash, so the guarantee lives in the loop. The default `AllowAllGate`
   preserves YOLO; `RATCHET_GATE=prompt|deny` turns gating on for mutating tools.

2. **Delegated sub-agents are scoped read-only by *structure*, not by instruction**
   (v0.11). The `explore` sub-agent runs under a deny-by-default `ReadOnlyGate` *and*
   is handed only read-only tools — `read` plus a real read-only `search` (regex +
   glob, scoped under the working dir, no shell). It **cannot** mutate even if a
   prompt tells it to, because the capability isn't in its hands and the gate denies
   the rest. The constraint is enforced in the loop, not requested in the prompt.

The choice that matters: when v0.11 removed `explore`'s shell, the alternative was a
**command denylist** (allow `bash`, block `rm`/`sed -i`/`find -delete`/…). We rejected
it and built a read-only `SearchTool` instead — read-only-by-construction beats
read-only-by-heuristic.

## Alternatives considered

- **Give sub-agents the full toolset and trust the role prompt.** "You are a read-only
  investigator" in the system prompt. Rejected: a prompt is a request, not a guarantee;
  injection or model error defeats it, and there's no operator watching the inner loop.
- **Keep the shell but filter dangerous commands (denylist).** Looks safer, isn't:
  shells have unbounded ways to mutate (`find -delete`, `sed -i`, `> file`, `python -c`,
  redirected output), so any denylist is a porous heuristic an adversarial prompt
  routes around. Structural read-only has no such gap.
- **Make the gate on by default.** Would protect everyone, but it breaks the pi-plain
  YOLO identity for the operator-driven top-level agent, who *is* the supervision.
  Default off for the human-driven loop, structurally locked for the delegated loop —
  the distinction the user drew.

## Consequences

- Safety is *structural where it's unsupervised* and *opt-in where a human supervises*.
  The top-level operator keeps frictionless YOLO; the inner investigator literally
  lacks the capability to do harm.
- `SearchTool` (read-only regex + filename search, working-dir-scoped, with skip-dirs,
  a file-size cap, and a regex timeout) is now a first-class tool, not just a sub-agent
  fixture — a net capability gain that fell out of choosing structure over denylist.
- The gate is a clean seam (ADR-0001): the same `IToolGate` governs the REPL agent and
  every workflow phase, and a faulting gate degrades to an error result rather than
  killing the turn.
- Cost: `explore` can no longer shell out for an ad-hoc command mid-investigation — it
  is confined to read + structured search. That is the point, and it is the right
  default for an unsupervised delegate.
