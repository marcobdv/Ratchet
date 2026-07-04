# 12. REPL turn routing — the interactive exception, taken deliberately

- Status: Accepted
- Relates to: [0007](0007-two-layer-routing-not-a-router.md) (two-layer routing, not a router)
- Design doc: [../model-routing.md](../model-routing.md)

## Context

ADR-0007 rejected a per-request predictive router for workflows because coding has
ground truth: a gate goes red, the driver promotes — *reacting* to "did it pass" beats
*predicting* "will it be hard". The design doc reserved exactly one case where
prediction wins: **interactive, latency-bound use**, where the failed-cheap-then-retry
round trip is a user-visible pause and there is no gate to produce the failure signal.

The REPL is that case. It ran every turn — "fix this typo" and "redesign the storage
layer" alike — on one session model, switched only by hand (`/model`). The goal:
the agent selects the model per task presented.

## Decision

**Per-turn predictive routing in the REPL only, opt-in (`RATCHET_ROUTE=auto`), riding
the reserved exception — with the reactive layer explicitly left out.**

- **One cheap classify call per human turn** (`TurnRouter`, Core) picks a route from a
  **readable route table**: `.ratchet/routing.json` when present, else a built-in
  Anthropic ladder (quick/haiku → standard/sonnet → deep/opus, default standard). The
  classifier runs on the table's cheapest rung unless the table names one.
- **The route drives one turn.** The session client, `/model` choice, compaction and
  handover machinery are untouched; sub-agents and advisors keep their own wiring.
- **Every decision is visible and logged**: printed with the model and one-sentence
  reason, appended to `.ratchet/route-log.jsonl` — the same falsifiability posture as
  `--routing-stats` (is the cheap rung actually carrying the load?).
- **The human outranks the router.** `/model <id>` pins and pauses routing;
  `/model auto` resumes. A pin is never silently overridden.
- **Failure lands on the default route, never on an error**: unparseable classifier
  output, an unknown route name, or a dead classifier all fall back to the table's
  default (the middle rung — interactively, wrong-expensive wastes money but
  wrong-cheap wastes the human's time; the workflow classifier's size-*up* fallback
  would overspend here).
- **No reactive layer in the REPL.** Promotion needs a ground-truth failure signal;
  a conversation has none (the human's dissatisfaction is not machine-readable). If a
  turn lands on a too-weak model the human says so — `/model` *is* the REPL's reactive
  layer, and it feeds the same log.

## Alternatives considered

- **Reuse the workflow's two-layer routing.** Its predictive key `(phase, work_type)`
  needs a classifier + phase structure a REPL turn doesn't have, and its reactive
  layer needs gates. The shape doesn't transfer; only the philosophy does (readable
  config, logged decisions, fallback-not-failure).
- **Heuristics instead of a classify call** (length, keywords). Free, but wrong in
  both directions ("quick question:" prefixing a deep task), and un-tunable without
  becoming a rule engine. The classify call is one cheap-model roundtrip and its
  reasoning is loggable.
- **Route inside `ILlmClient`** (a routing decorator). Invisible from the loop and the
  transcript — exactly the black-box ADR-0007 refused. Routing lives where it can be
  printed.

## Consequences

- The REPL gains a per-turn latency cost (one cheap classify call) only when opted in;
  default behavior is byte-identical.
- The route table is config, diffable, and per-repo (`.ratchet/routing.json`), so a
  local-first user routes quick→local-small / deep→frontier with a three-line file.
- `route-log.jsonl` accumulates the evidence to retune the table — the same
  observe-then-tune loop as ADR-0007's, just with the human as the gate.
- A turn's cost now varies by route; the per-call token line (already printed) is the
  visibility.
