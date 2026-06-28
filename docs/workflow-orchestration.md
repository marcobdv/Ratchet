# Workflow orchestration — design goal

> **Status:** **implemented** in `src/Ratchet.Workflow` (v0.8). This document
> remains the rationale of record — it captures decisions **and why** on purpose,
> so they aren't silently re-litigated. The *Open forks* at the bottom were decided
> deliberately during implementation; see **Implementation notes** at the very end
> for how each was resolved and where the code lives. The original design text below
> is preserved unchanged.

## Goal

Put a **workflow orchestrator** on top of Ratchet's agent loop. Today the loop
runs one undifferentiated agent per turn. The goal is to run a task through a
small, fixed sequence of *phases* (research → plan → implement → verify →
review), where each phase has its own role, its own tool subset, its own model,
and its own skills — and where cheap models do the bulk of the work while
frontier models are spent only where reasoning density is high.

The loop itself does not change. This rides on the existing seams.

## Guiding principle

**Structure the control flow, not the thinking.**

Hard-code the things whose entire value is the *guarantee* — the gates ("no PR
on red", "no code before a spec"). A workflow that *usually* runs the tests is
worth far less than one that *always* does. Use soft, editable text (prompts,
skills) for the *cognition inside* a phase — how to design, how to debug, what a
spec should say. Forcing judgment into code branches makes it worse; freezing a
control flow you haven't validated is premature.

They compose: the deterministic spine guarantees you *reach* the review phase;
the (editable) review prompt and skills shape *how* you review. Both, not either.

## Architecture overview

Three layers, coarse to fine, driven by **one** LLM judgment at intake:

1. **Intake classifier (judgment).** One LLM call sizes the task → a `work_type`.
   This single decision drives everything downstream. It is *recorded on the run*.
2. **Spine + work_type → phases (deterministic).** A fixed, ordered, maximal
   phase sequence. The `work_type` selects which *skippable* phases drop out.
   Never reorder, never invent — selection is by omission from a fixed menu.
3. **(phase, work_type) → skills (deterministic), then load policy.** Each
   running phase resolves its eligible skill set from config; the set is loaded
   wholesale if small, or via on-demand `load_skill` if large.

Orchestrator = **scheduler + judge, split.** A real product owner both
*schedules* (mechanical) and *accepts against criteria* (judgment). Keep those
as different things in code:

- The **scheduler** is dumb, deterministic C#. Reads the workflow, runs the
  phase, evaluates the gate, routes to the next phase. No LLM. The guarantees
  live here.
- A **judge** is a sub-agent the scheduler invokes *at a gate*, only when the
  gate needs judgment an exit code can't express ("is this spec unambiguous?").
  It returns pass / fail / loop-back. A gate, not the driver.

So the orchestrator stays deterministic; LLM judgment shows up in exactly two
bounded places: the intake classifier, and judge-gates.

## The pieces

### Fixed maximal spine, skippable phases

The full ordered spine always exists in config: e.g. `research → plan →
implement → verify → review` (confirm the real phase names against the harness).
A `work_type` runs an *ordered subset* of it. Phases drop by omission; the order
is never the model's to choose.

- **Floor phases cannot be skipped.** `verify` and `review` are floors. Whatever
  the classifier downgrades, the gate that proves "done" stays mandatory.
  Skipping verify is precisely how a "trivial" change ships a regression.
- The model's discretion is bounded to the *skippable* set, where being wrong is
  cheap.

### Intake classifier — one judgment, recorded

One LLM call at the top returns the `work_type` (and thereby the phase subset).
Keep the taxonomy **small** (e.g. `trivial`, `small_change`, `feature`,
`bugfix`) — three or four to start, not fifteen. It is a *hint that sets
defaults*, not a lock.

The classification is the **highest-leverage judgment in the pipeline** — it's
the one call that can silently lower quality by skipping a phase that was
actually needed. Therefore:

- **Record it on the run** (choice + reasoning), so a "trivial" misjudgment that
  ships a bug is diffable after the fact — you can see the *skip* was wrong, not
  the implementation.
- A misclassification should **degrade gracefully** (slightly-wrong skill set,
  phase still runs), never hard-fail.

### Skill resolution — `(phase, work_type)` then load by pool size

Phase alone is too coarse a key: "implement" for a field-removal wants different
context than "implement" for a new feature. The eligible skill set is a function
of **both** phase and work_type. The field-removal task simply doesn't list the
entity-modelling skills as *eligible* — the driver never sees fields it's there
to delete, because config never made them eligible, not because the model chose
well under pressure.

Within the resolved eligible set:

- **≤ threshold (≈4 skills): load all.** A non-decision can't introduce variance.
  Given parallel agents and a one-shot-acceptance target, every LLM routing call
  is a place nondeterminism leaks back in — don't pay it unless the pool forces
  you to.
- **Larger pool: progressive disclosure.** Inject names + one-line descriptions
  for that phase; give the driver a `load_skill(name)` tool that reads the body
  on demand. Same "metadata in prompt, body via tool" mechanism as AGENTS.md and
  `recall`.
- **Embeddings / semantic retrieval: not yet.** Only if a *single* phase's pool
  grows to dozens. At ~31 skills bucketed by `(phase, work_type)` you're nowhere
  near it. (At ~80k skills the literature says the skill *body* is the decisive
  routing signal and metadata isn't enough — that's the opposite regime; don't
  import its conclusions.)

Phase-bucketing also *contains* the known failure mode of skill selection (bad
description → wrong skill): a bad description can only mis-fire *within* a phase,
never across the whole registry. That containment is an architectural benefit,
not just convenience.

### Model tiers

Phases reference a **named tier**, not a hardcoded model, so swapping the backing
model is a one-line change (these churn fast). Cheap local drivers do the
high-volume mechanical phases; frontier spend is concentrated where it pays.

- `driver` — does the work (cheap/local by default: Qwen3-Coder, Devstral, etc.).
- `advisor` — consulted *during* a phase (frontier). See below.
- judge model — runs *at a gate* (frontier, fresh context). See below.

### Advisor — a driver capability, **not** a gate

Modelled on the Claude Code advisor tool: a cheap executor drives the task
end-to-end and *consults* a stronger advisor mid-work — before committing to an
approach, when an error recurs, before declaring done. The advisor reads the
shared conversation and returns a short plan / course-correction; the driver
applies it and continues.

This is **not** the same as a gate, and the difference is load-bearing:

- **Timing is the driver's judgment.** A confidently-wrong driver won't call for
  help. So the advisor gives **no guarantee** and **does not replace gates**.
- **Context distinguishes the two frontier touchpoints:**
  - *Advisor* = during work, **shared context**, driver-invoked. Good for "am I
    on track given everything I've done?" — but it *inherits the driver's
    accumulated premises*, so it's blind to a wrong framing.
  - *Judge gate* = after work, **fresh context**, deterministic trigger. The
    fresh context is the point: it catches the framing error the advisor
    structurally cannot.

Design rules for the advisor:

- It's **per-phase config** (parallel to `driver`/`tools`), implemented as a
  `consult_advisor` tool the driver may call. `null` on a phase = no advisor.
- **Loop ceiling is mandatory** (`max_consults`). The driver can otherwise
  consult repeatedly on the same problem, getting slightly different advice each
  time and making no progress. On hitting the ceiling, escalate the *driver*
  (bump to a stronger tier) or kick to a fresh-context gate — don't consult again.
- **Advisor-before-first-write** is the one lever to recover determinism from a
  judgment-timed tool: require a consult before the first state-changing action
  on non-trivial work. Gate this rule on `work_type` (trivial → skip it) using
  the classifier output, which avoids the documented over-calling on tasks with
  a straightforward first step.
- **Surface conflicts.** When a `consult_advisor` recommendation is followed by a
  red verify gate, log the contradiction on the run rather than silently
  overriding. High-value signal when eval'ing whether the advisor pays for itself.

**Portability constraint:** the Anthropic advisor *tool* is API-only (not
Bedrock/Vertex/Foundry) and only attaches when the advisor model is at least as
capable as the main model in a recognised pairing. With a **local** driver you
cannot use the tool — you **reimplement the pattern**: a `consult_advisor` tool
that forwards the transcript to a frontier model and returns text. The pattern
ports; the tool doesn't. Upside: you control the timing rules, which the real
tool doesn't expose.

### Gates

Per phase transition. Two kinds:

- `command` — deterministic. Run a command, route on exit code (e.g.
  `dotnet test`, `pass_on: exit_zero`). The cheapest, strongest judge you have;
  prefer it wherever an exit code can answer the question. `verify` uses this and
  needs **no model at all**.
- `judge` — an LLM reviewer for judgment an exit code can't express. Mark
  `fresh_context: true` to get the fresh-eyes property (distinct from the
  shared-context advisor). Frontier model. `on_fail: loop` with a ceiling.

Frontier judgment goes only to gates where the check is genuinely a judgment
call (is this plan sound, is this diff mergeable), never where a test already
answers it.

### Escalation — skip is a recoverable default, not a lock

A phase must be able to fail *back up* the spine. If `implement` discovers the
"trivial" colour change actually touches a shared token in twelve places, it
needs to say "this wasn't trivial — re-enter `plan`", and the scheduler
re-enters it. Without this, a misjudged skip at intake silently produces worse
work with no recovery — the same failure shape as a bad compaction. The
classifier *biases* the pipeline; it doesn't imprison it.

### Context handoff between phases — the actually hard part

"The orchestrator collects results and passes them to the next phase" hides
everything. Full transcript across five phases blows the context window; a naive
summary drops the one detail the next phase needed. **This is exactly what v0.5
handover + recall already solve:** phase N hands phase N+1 a working-set doc
(authored, not silent) plus `recall` into the cold transcript. Treat the handoff
as load-bearing and the orchestration as comparatively trivial glue on top.

## YAML schema (illustrative)

Declarative only. The moment a condition needs real logic — computed values,
compound loop-exit criteria, data transforms between phases — that is **C# the
scheduler runs**, not YAML it interprets. Declarative config, escape to code.
The instant the YAML grows `if/else` and `${{ }}` you've reinvented a
programming language with no type system and no debugger.

Skill and phase names below are **placeholders** — swap for the real workflow and
vlc skill names.

```yaml
version: 1
name: sfms-dev-workflow

# Intake: ONE llm call -> one work_type, which drives both the phase subset and
# the per-phase skills. Recorded so the skip decision is auditable.
classifier:
  output: work_type          # must be a key under work_types
  record: true               # persist choice + reasoning on the run

# Named tiers so a phase references a tier, not a model. Swap models in one place.
models:
  driver_cheap:     { provider: local,     model: qwen3-coder-30b }
  driver_mid:       { provider: local,     model: devstral-small-2-24b }
  advisor_frontier: { provider: anthropic, model: claude-opus-4-8 }

defaults:
  driver: driver_cheap
  advisor:                   # driver MAY consult during the phase; null = none
    model: advisor_frontier
    consult_when: >
      Before your first file-writing action on a non-trivial change, when the
      same error recurs twice, and before declaring the phase done.
    max_consults: 3          # loop ceiling

# eligible set <= threshold -> inject all; else progressive disclosure.
skill_loading:
  threshold: 4
  strategy_above: progressive   # load_all | progressive

# Fixed, ordered, maximal. Structure only — no skills here.
spine:
  - id: research
    skippable: true
    role: "Investigate the codebase; surface unknowns and impact."
    driver: driver_cheap
    advisor: { inherit: defaults }
    tools: [read, recall, bash, consult_advisor]
    gate: { type: none }

  - id: plan
    skippable: true
    role: "Produce the change plan / spec."
    driver: driver_mid
    advisor: { inherit: defaults }
    tools: [read, recall, consult_advisor]
    gate: { type: judge, agent: spec-review, fresh_context: true,
            model: advisor_frontier, on_fail: loop, max_loops: 3 }

  - id: implement
    skippable: false
    role: "Make the change."
    driver: driver_cheap
    advisor: { inherit: defaults }
    tools: [read, write, edit, bash, load_skill, consult_advisor]
    gate: { type: none }
    escalation: [plan, research]   # may re-enter these if work proves bigger

  - id: verify
    skippable: false               # FLOOR — the guarantee
    role: "Prove it: build + tests green."
    driver: driver_cheap
    advisor: null                  # tests are the judge; no model needed
    tools: [bash, read]
    gate: { type: command, run: "dotnet test", pass_on: exit_zero }

  - id: review
    skippable: false               # FLOOR
    role: "Check conventions and diff quality."
    driver: driver_cheap
    advisor: null
    tools: [read, recall]
    gate: { type: judge, agent: code-review, fresh_context: true,
            model: advisor_frontier, on_fail: loop, max_loops: 3 }

# The overlay the classifier selects. Each block is the WHOLE story of one
# work_type: which phases run (ordered subset of spine, MUST include floors) and
# the eligible skills per phase. Skills live here, not on the spine, so one block
# reads as a complete diffable unit.
work_types:

  trivial:            # e.g. theme colour A -> B
    phases: [implement, verify]
    skills:
      implement: [conventions]

  small_change:       # e.g. add a remark field to orders
    phases: [plan, implement, verify, review]
    skills:
      plan:      [entity-modelling, migration-spec]
      implement: [entity-modelling, migration, conventions]
      review:    [conventions]

  feature:
    phases: [research, plan, implement, verify, review]
    skills:
      research:  [codebase-survey]
      plan:      [solution-design, spec-authoring]
      implement: [solution-design, conventions]
      review:    [conventions]

  bugfix:
    phases: [implement, verify, review]
    skills:
      implement: [debugging, regression-test, conventions]
      review:    [conventions]
```

## Loader validation rules

So the config can't lie:

1. Each `work_types[*].phases` must be an **ordered subset** of `spine` ids and
   must include **every floor** (non-skippable) phase.
2. Every skill name must resolve against the real skill registry — a typo'd skill
   **fails loading**, never loads silently as nothing.
3. Every `driver`/`advisor`/judge `model` must resolve to a `models` tier.
4. Advisor pairing sanity: where the platform requires advisor ≥ driver, validate
   it (or note the reimplemented-tool path has no such restriction).

## Mapping to existing Ratchet seams

This is an elaboration, not a rewrite. The agent loop (`Agent.RunTurnAsync`) is
untouched.

- **`Agent`** — one `Agent` instance per phase, constructed with that phase's
  system prompt, tool subset, and model. (`Agent` is already just a holder for
  `(llm, tools, systemPrompt, observer)`.)
- **`ILlmClient`** — the seam for driver vs advisor vs judge models. The advisor
  consult and the judge gate are each "one completion" through it. (A second
  `ILlmClient` impl for local/OpenAI-compatible endpoints is a prerequisite rung.)
- **`ITool`** — `load_skill`, `consult_advisor`, and `recall` all land here.
- **`ISessionStore` + `Handover`/`RecallTool`** — the phase-to-phase handoff.
- **`IAgentObserver`** — where the run recording (classification, skips,
  consults, gate outcomes, advisor/result conflicts) is emitted.

The scheduler is new C# above all of this; it is plain control flow.

## Build sequence — start here

**Do not write the YAML first.** Designing the schema before you've felt the
constraints means guessing wrong and calcifying it.

1. **Two phases, hardcoded.** `implement → verify` with a green-tests gate
   between, no YAML, no classifier. Feel where the *context handoff* hurts and
   what a gate actually has to express.
2. **Add the loop-back.** Make `verify` failure re-enter `implement`. Add the
   ceiling. This is the smallest real orchestration.
3. **Extract the spine to config** once you know what it must say. Add `research`,
   `plan`, `review`.
4. **Add the intake classifier** and the `work_type → phases` overlay.
5. **Add per-phase model tiers**, then the **advisor** capability, then **skill
   resolution**. These are independent and can land in any order.

Verify (the machine-checked "done") is the first thing worth making
deterministic — the dark-factory / unattended ambition only works if "done" is
checked, not vibed.

## Open forks — decide deliberately, don't guess

1. **Does the advisor get a *stop signal*, or only advise?** If the advisor can
   halt the driver (not just course-correct), it edges back toward being a gate.
   Handle deliberately; don't let it become a gate by accident.
2. **Does escalation re-run the classifier, or just re-enter the phase?**
   Re-running re-sizes the whole task; re-entering keeps the original sizing.
3. **Is the skip decision currently recorded/inspectable in the harness?** If
   it's happening inside a prompt with no trace, making it declarative/logged is
   the highest-value first move (so a bad skip is diffable).
4. **Is `work_type` the right single key for phase selection,** or should "depth"
   (which phases) be a separate classifier output from "kind" (which skills)? A
   `feature` can be small or large.
5. **Where does this live** relative to the vlc skills and the existing harness —
   in Ratchet, or in the harness repo with Ratchet as the engine?

## What exists today (baseline)

v0.5: streaming agent loop, sessions as a branch tree, file + SQLite stores, and
retrieval-backed handover (`/handover`, `ratchet --handover <id>`, `recall`).
See `README.md`. Nothing in this document is built yet.

## Implementation notes (v0.8 — `src/Ratchet.Workflow`)

Built on the existing seams; `Agent.RunTurnAsync` is untouched. Map of the design to
the code:

| Design piece | Code |
|---|---|
| Validated config (spine, tiers, gates, work_types) | `WorkflowConfig.cs` |
| YAML loader + the 4 "can't lie" rules | `WorkflowLoader.cs` |
| Intake classifier (one call, recorded, graceful fallback) | `Classifier.cs` |
| Scheduler (deterministic spine, gates, loop-back, escalation, handoff) | `WorkflowScheduler.cs` |
| Command + judge gates | `Gates.cs` |
| Advisor (reimplemented tool), before-first-write guard, escalation tool | `PhaseTools.cs` |
| Run recording (classification, skips, consults, gate outcomes, conflicts) | `WorkflowRun.cs` |
| Local/OpenAI-compatible driver tier | `Llm/OpenAiChatClient.cs` |
| Entry point `ratchet --workflow <file> "<task>"` + console trace | `Cli/Program.cs` |
| A concrete workflow | `workflows/ratchet-dev.yaml` |

Phase-to-phase handoff is exactly v0.5: after each phase the scheduler authors a
handover doc (`HandoverGenerator`), persists the phase transcript as a session, and
hands the next phase the accumulated working-set docs plus a `recall` tool over the
prior session. The judge gate's fresh-context input *is* that authored doc.

**Open forks — how they were decided:**

1. **Advisor stop signal?** No. The advisor only advises (course-corrects); it cannot
   halt the driver. Keeping it strictly non-halting is what keeps it distinct from a
   gate. Halting power stays with gates and the escalation lever.
2. **Escalation re-runs the classifier, or re-enters the phase?** Re-enters the phase
   (keeps the original sizing). `request_escalation` is bounded to the phase's
   configured targets; the scheduler splices a pulled-in skipped phase back at its
   spine-ordered position. A global escalation ceiling prevents thrash.
3. **Is the skip decision recorded?** Yes — `classifier.record` persists choice +
   reasoning on the run (`WorkflowRun`), so a bad skip is diffable after the fact.
4. **Is `work_type` the right single key?** Kept as a single key for now (matches the
   schema). Splitting "depth" (phases) from "kind" (skills) is noted as the next move
   if a `feature` proving small/large becomes a real pain point.
5. **Where does it live?** In Ratchet, as a Core-only project (`Ratchet.Workflow`,
   +YamlDotNet) with the CLI as the composition root — Ratchet is the engine.

**One deliberate deviation from the illustrative YAML:** the sample showed `trivial`
running `[implement, verify]`, but the prose also makes `review` a floor and rule 1
requires every floor to run. The loader enforces the *rule* (floors = `verify` +
`review` run in every work_type); `workflows/ratchet-dev.yaml` includes both. The
merge-readiness gate stays mandatory — skipping it is how a "trivial" change ships a
regression, which is the exact failure the floors exist to prevent.

The control flow is covered by a deterministic test harness (a scripted `ILlmClient`
plus a real command gate) exercising classify → phases → judge loop-back →
command-gate loop-back → escalation, alongside the four loader-validation rules.

**v0.9 additions.** Five things the design called for but v0.8 left open:

- **Permission gate** (`Core/ToolGate.cs`, an `IToolGate` in `Agent.ExecuteToolAsync`).
  The design left "no PR on red" to gates but left the *tool*-level gate unbuilt; it now
  exists and every workflow phase agent is constructed with it. A `land` phase (git
  branch + commit via new mutating `git_commit`/`git_create_branch` tools) finally lets a
  run ship its work rather than leave an uncommitted diff.
- **Run records are persisted** (`IRunStore`/`FileRunStore`, `.ratchet/runs/<id>.json`).
  The design wanted "a bad skip is diffable after the fact"; in v0.8 the record was
  in-memory only. Now it's durable and inspectable (`--runs`/`--run`).
- **Per-tier cost** (`MeteredLlmClient` → `CostTally` on the run). The design's whole
  cheap-driver/frontier-gate economics were unmeasurable; a metering wrapper on each tier
  makes them legible without touching any call site.
- **Resumable runs.** The snapshot doubles as a checkpoint (loop state + handoff + cost);
  an interrupted run continues from the last completed phase via `--workflow-resume`,
  without re-classifying — the unattended-run prerequisite the design kept gesturing at.

These are verified by a second deterministic harness (gate allow/deny in the loop, run
store roundtrip, cost tally across a full run, interrupt → checkpoint → resume, and a
real `git_commit` both succeeding and being blocked by a deny gate).

**Note on `pass_on`.** The illustrative gate YAML above shows `pass_on: exit_zero`, but the
implementation only ever passes a command gate on exit 0, so `pass_on` was a no-op and has
been dropped from the schema (the loader no longer reads it). A command gate = "exit 0 is
pass"; if a non-zero expected code is ever needed, that's a real feature to add, not a label.
