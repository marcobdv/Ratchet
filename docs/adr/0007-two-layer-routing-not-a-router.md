# 7. Two-layer model routing (predictive + reactive), not a dedicated router

- Status: Accepted
- Landed: v0.10
- Relates to: [0006](0006-deterministic-orchestrator.md)
- Design doc: [../model-routing.md](../model-routing.md)

## Context

"Use the right model for the job" usually becomes a **dedicated router**: a
classifier (often itself a learned model) that, per request, predicts difficulty and
picks a model. It adds a per-turn inference tax, it's a black box that's hard to debug
("why did it pick the expensive one?"), and crucially it predicts *will this be hard*
when, for coding, you can cheaply *observe* whether the work actually succeeded.

The realisation: the orchestrator (ADR-0006) already contained the skeleton of
routing. The classifier already chose a `work_type`; phases already had model tiers;
gates already produced a pass/fail ground-truth signal. A separate router would
duplicate machinery that was already there.

## Decision

No separate router. Routing is **two layers riding the existing scheduler**:

- **Predictive.** A phase's starting tier resolves most-specific-first:
  `work_type[phase].model → spine[phase].driver → defaults.driver`. This is the *same*
  `(phase, work_type)` key the classifier already chose and skills already key on — no
  second classifier, no per-turn router, no extra inference tax. A bugfix's `implement`
  can start a rung up because telemetry says bugfixes rarely land on the cheap tier.
- **Reactive.** A `defaults.driver_ladder` turns the existing bounded loop-back into a
  *promotion*: when a gate goes red and a phase re-runs, its driver climbs one rung of
  the ladder rather than retrying at the same tier. Rationale: a stronger driver
  changes the work; consulting the advisor harder does not. Bounded by the same
  `max_loops` and the ladder top. A `work_type` opts out with `promote: false` (a
  trivial task that keeps failing is a misclassification, not a too-weak model).
- **Escalation does *not* promote.** It stays the distinct fresh-context re-frame from
  ADR-0006 — a different lever, kept separate.
- **Feedback loop.** Promotions are recorded per `(work_type, phase)`; `ratchet
  --routing-stats` aggregates the escalation rate across runs, so a wrong cheap default
  shows up as a high rate and is retuned with a **one-line config diff** — adaptation
  without a learned black box.

## Alternatives considered

- **A dedicated (possibly learned) router predicting difficulty per request.**
  The standard approach. Rejected on its own turf: coding has ground truth a
  `dotnet test` away, so *reacting* to "did it pass" beats *predicting* "will it be
  hard", costs no extra inference, and stays diffable.
- **Reactive only (always start cheap, climb on failure).** Simpler, but wastes a
  guaranteed-to-fail cheap attempt on classes of work known up front to need a stronger
  model. The predictive layer encodes that prior; the reactive layer handles the rest.
- **Promote on escalation too.** Conflates two distinct responses to trouble — "same
  task, stronger model" (promotion) vs "re-frame with fresh context" (escalation).
  Keeping them separate keeps each legible.

## Consequences

- Routing is config, not code: tiers, the ladder, and per-`work_type` overrides all
  live in the workflow YAML and are tuned by editing it.
- The whole "cheap drivers, frontier gates" thesis is *falsifiable* — per-tier cost
  (v0.9) plus routing-stats (v0.10) answer "are the cheap defaults actually carrying
  the load, and where are they wrong?"
- Rides the existing scheduler; the loop is untouched (ADR-0001).
- Cost: the policy is spread across the YAML (tiers, ladder, overrides) and only pays
  off once there's enough run history to tune against — it is explicitly a feedback
  loop, not a one-shot optimum.
