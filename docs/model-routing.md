# Model selection / routing — follow-up feature

> **Status:** **implemented** in `src/Ratchet.Workflow` (v0.10), additive over
> [`workflow-orchestration.md`](./workflow-orchestration.md). The design text below is
> the rationale of record and is preserved unchanged; see **Implementation notes** at
> the end for the code map and how the open forks were resolved.

## TL;DR

The instinct to add **model routing** is right, but the answer isn't a separate
router component. The orchestration design *already contains* routing, in the
form best suited to a coding agent:

- **Predictive layer** — pick the starting tier from `(phase, work_type)`, the
  same key already used for skills. One judgment (the intake classifier), already
  made, already logged.
- **Reactive layer** — a cascade: cheap driver runs, and an *empirical* signal
  (recurring error, advisor `max_consults`, a red gate) promotes the tier. This
  is the advisor + escalation machinery already in the base doc.

A dedicated difficulty-router would duplicate the classifier on the predictive
side and the cascade on the reactive side, while being less reliable and *more*
expensive here. So: **don't add a predictive router. Make the tier a function of
`(phase, work_type)`, and let the cascade correct it.**

## You already have both halves of routing

**Predictive — `(phase, work_type)`.** Phase alone is too coarse to pick a model
for the same reason it's too coarse to pick skills: "implement" for a
field-removal is not "implement" for a new feature. The key that fixed skill
selection fixes model selection. Reuse it.

**Reactive — the cascade.** "Try cheap, escalate on failure," fired by facts
rather than predictions: the driver hits a recurring error or a gate goes red,
and the tier is promoted. This is exactly `consult_advisor` + the escalation
ceiling, read as a model-selection mechanism.

A standalone router sits between these two and adds nothing they don't already
cover — at the cost of a second opaque decision.

## Why not a predictive difficulty-router (for *this* domain)

Predictive routers (RouteLLM-style classifiers, difficulty cascades) are real
and good — in the regime they're built for. A coding agent is the other regime,
on three axes:

1. **Verifiability.** Predictive routing earns its keep when you *can't cheaply
   verify* output, so you must *guess* difficulty up front. Coding has ground
   truth one `dotnet test` away. When you can check the result, **reacting to
   "did it pass" beats predicting "will it be hard"** — the prediction is a
   guess, the test is a fact. Predictive routers shine in chat / Q&A /
   classification-at-scale (no verifier); coding is the opposite.
2. **Cost — ironic, given cost is the whole motivation.** A per-turn router pays
   an inference tax on *every* request to decide the route. The classifier pays
   it *once per task*. The cascade pays *only when actually stuck*. The reactive
   design is both cheaper and more reliable here.
3. **Diffability.** A tier keyed by config is readable: you can see "what model
   does a bugfix's implement phase start on" in the file. A learned router is a
   black box — you can't diff *why* it picked. That's the same reason the project
   chose handover over compaction: decisions should show up in a diff. A router
   quietly breaks that.

## The one case where prediction genuinely wins

Hard **latency** pressure where you can't eat the failed-cheap-attempt-then-retry
round trip. In interactive use, a wrong cheap attempt costs a user-visible pause
before the test catches it, which starts to favour predicting correctly the first
time. For **batch / unattended (dark-factory) runs — the actual target — the
round trip is free**, so the cascade wins outright. Revisit only if an
interactive, latency-bound use case appears.

## Resolution: tier as a function of `(phase, work_type)`

Model tier resolves by a fallback chain, most specific first:

```
work_type[phase].model  →  spine[phase].driver  →  defaults.driver
```

So a `work_type` may override the starting tier per phase, exactly the way it
already overrides skills — and if it says nothing, it inherits the phase default,
then the global default. Nothing is forced to specify a model.

## Reactive layer: the cascade + a promotion ladder

The base doc's escalation ceiling becomes a model promotion step. On the trigger
(recurring error / `max_consults` hit / red gate, up to `max_loops`), promote the
*driver* one rung along a ladder instead of retrying at the same tier or
consulting again:

```
driver_cheap  →  driver_mid  →  driver_strong
```

Promote the driver; don't just consult harder. Consulting harder doesn't fix a
driver that can't satisfy the check (the advisor inherits the same stuck premises
— see the base doc's advisor section). A stronger *driver* changes the work.

## The feedback loop (the real payoff of having both layers)

The cascade's **escalation rate is the training signal for the predictive
defaults.** If `bugfix · implement` escalates 80% of the time, the cheap default
for that key was wrong — retune it upward. If a key never escalates, try a
cheaper default. The reactive layer *teaches* the predictive layer, and because
both are config + logged telemetry, the retune is a readable diff, not a retrain.

This is why the two-layer design beats a single learned router even on the
router's own turf: you get adaptation **without** giving up diffability.

## Schema delta

Additive over `workflow-orchestration.md`. Illustrative; placeholder names.

```yaml
models:
  driver_cheap:    { provider: local,     model: qwen3-coder-30b }
  driver_mid:      { provider: local,     model: devstral-small-2-24b }
  driver_strong:   { provider: anthropic, model: claude-sonnet-4-6 }
  advisor_frontier:{ provider: anthropic, model: claude-opus-4-8 }

defaults:
  driver: driver_cheap
  driver_ladder: [driver_cheap, driver_mid, driver_strong]   # promotion order
  record_promotions: true         # emit per-(phase, work_type) promotion telemetry

work_types:
  bugfix:
    phases: [implement, verify, review]
    skills:
      implement: [debugging, regression-test, conventions]
    models:                        # NEW: optional per-phase tier override
      implement: driver_mid        # bugfix-implement starts a rung up (learned from telemetry)

  trivial:
    phases: [implement, verify]
    models:
      implement: driver_cheap      # explicit: stay cheap, never auto-promote on a trivial task
```

Resolution and validation:

- Tier resolves `work_type[phase].model → spine[phase].driver → defaults.driver`.
- A loader rule (extends the base doc's rules): every `models:` value in a
  `work_type` must resolve to a `models` tier, and every laddered tier must exist.
- Optional per-key cap: a `trivial` task may set a ceiling so the cascade can't
  promote it past `driver_cheap` (a trivial task that keeps failing is a
  misclassification — escalate the *work_type*, not the model).

## Build sequence (later rung)

1. **Flat defaults first.** Ship the base orchestration with one `driver` per
   phase (as the base doc already describes). No per-`work_type` model overrides.
2. **Add escalation telemetry.** Log escalation rate per `(phase, work_type)`.
   Change nothing else. Just watch.
3. **Add per-`(phase, work_type)` overrides**, tuned by what step 2 showed.
4. **Iterate the defaults** from ongoing telemetry. This is the feedback loop;
   it never "finishes," but each change is a one-line diff.

Do **not** start here. The defaults have to be wrong in an observable way before
you know which keys deserve an override — same discipline as not writing the YAML
before feeling the plumbing.

## Open forks

1. **Promote the driver, or escalate to a fresh-context gate first?** On a stuck
   phase, a stronger driver and a fresh-eyes re-frame are different fixes (the
   advisor/gate context distinction from the base doc). Which fires first?
2. **Per-key promotion cap** — should every `work_type`/phase be promotable, or do
   some (e.g. `trivial`) cap out and escalate the classification instead?
3. **Telemetry granularity** — escalation rate by `(phase, work_type)` is the
   minimum; is per-skill or per-repo signal worth the extra logging?
4. **When (if ever) the latency exception applies** — is there a planned
   interactive, latency-bound mode, or is the target wholly batch/unattended?

## Note: provider-level routing is orthogonal

This document is about *capability* tiers (which model for which work). Provider
routing — failover, or cheapest-host-for-a-given-model (OpenRouter-style) — is a
different concern that lives *inside* a `models` tier's resolution and composes
with everything above. It is not a substitute for, nor in tension with, the
two-layer selection here.

*(Provider routing shipped separately in v0.9.1 — `RATCHET_PROVIDER` selects Anthropic,
OpenRouter, OpenAI, Groq, a local server, or any OpenAI-compatible endpoint, and a tier's
`provider:` chooses per-tier. Orthogonal, exactly as noted here.)*

## Implementation notes (v0.10 — `src/Ratchet.Workflow`)

Additive over the base orchestration; no new component, no per-turn router — both layers
reuse machinery that already existed.

| Design piece | Code |
|---|---|
| Predictive resolution `work_type[phase].model → spine[phase].driver → defaults.driver` | `WorkflowConfig.StartingTier` |
| Promotion ladder step | `WorkflowConfig.PromoteTier` over `defaults.driver_ladder` |
| Per-phase override + per-work_type cap | `WorkTypeSpec.Models` / `WorkTypeSpec.Promote` |
| Reactive promotion on a red gate | `WorkflowScheduler` — promote the re-running phase's `currentTier` |
| Escalation telemetry | `run.Promotion(...)` events; `ratchet --routing-stats` aggregates per `(work_type, phase)` |
| Loader rules | every ladder rung + every `work_type.models` value resolves to a tier; every runnable phase's starting tier is **on** the ladder (so promotion can climb) |

The telemetry flag is `record_promotions` (`WorkflowConfig.RecordPromotions`) — it gates the
recording of promotion events, which is what `--routing-stats` aggregates; it was renamed from
the design's `record_escalations` because the code has a *separate*, always-recorded
`run.Escalation` (the request-escalation re-frame), and the old name straddled both.

The reactive step rides the **existing** loop-back: when a gate goes red and the scheduler
re-runs a phase (whether `on_fail: loop` or `on_fail: <earlier phase>`), it promotes that
phase's driver one rung before the re-run — bounded by the same `max_loops` and the ladder
top. The promoted tier is part of the run checkpoint, so a resumed run continues at the
tier it had climbed to. Cost already attributes per tier (the metered client), so a
promotion shows up in the run's cost breakdown for free.

**Open forks — resolved:**

1. **Promote the driver, or re-frame first?** Promote the driver on the red-gate loop (the
   cheap, in-place fix). The fresh-context *re-frame* stays a distinct path — the judge
   gate's `fresh_context` and the `request_escalation` tool (escalation does **not**
   promote). So both fixes exist and don't collide: gate-fail climbs the ladder; an
   explicit escalation re-enters an earlier phase without changing tier.
2. **Per-key cap.** A `work_type` sets `promote: false` to opt out of the cascade entirely
   (e.g. `trivial` — a trivial task that keeps failing is a misclassification, not a
   too-weak model). Clean boolean, no per-phase ceiling schema needed.
3. **Telemetry granularity.** Shipped the minimum — per `(work_type, phase)` via
   `--routing-stats`. Per-skill/per-repo is deferred until a real retune needs it.
4. **Latency exception.** Not applicable: the target is batch/unattended, where the
   failed-cheap-then-retry round trip is free, so the cascade wins outright (as argued).

Intended verification (a deterministic harness on a scripted `ILlmClient`): loader
rejects bad ladder / override tiers, a per-`work_type` override sets the starting tier,
a red gate promotes the driver one rung and logs the promotion, the promoted tier
survives a resume, and `promote: false` loops without climbing. **Partially in-tree**
(v0.12 scaffold, `WorkflowLoaderTests`): the loader ladder/override rules, starting-tier
resolution, and `PromoteTier`'s climb-and-stick behaviour are covered; the runtime
pieces (promotion on a red gate, survival across resume) are still to come.
