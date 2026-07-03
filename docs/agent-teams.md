# Agent teams & council mode

Ratchet's delegation family is one seam — the nested `Agent` behind `DelegateTool` — at
three altitudes. All three are defined the same way and grow on the `ITool` / `ILlmClient`
seams; the loop is untouched (see [ADR-0011](adr/0011-delegation-family-one-seam.md)).

| Tier | What it is | Defined by |
|---|---|---|
| **Sub-agent** | one delegated agent, its own context/tools/model/gate | an agent file (no `members`) |
| **Team** | N members dispatched **in parallel**, then optionally merged by a lead | an agent file with `members:` |
| **Council** | independent cold personas → clerk organizes → human decides | an agent file with `members:` + `mode: council` |

## Defining an agent

Drop a Markdown file with YAML frontmatter under `.claude/agents/` or `.ratchet/agents/`
(workspace or `~`). This is the Claude Code subagent format, so a repo's existing Claude
subagents load unchanged.

```markdown
---
name: code-reviewer
description: Reviews a diff for correctness, convention, and regression risk.
tools: read, search, git_diff        # omit → default read-only set (read, search, recall)
model: opus                          # omit / "inherit" → the top-level model; alias or id
---
You are a meticulous code reviewer. Read the diff, then report findings as a prioritized
list: correctness bugs first, then convention and scope. Cite file:line.
```

Each becomes a named tool (`code-reviewer`) the top-level agent can call. It runs in its
**own fresh context** — pass it all the context it needs; it does not see your conversation.
Tools are resolved by name from the host's set; an all-read-only tool list runs under a
deny-by-default `ReadOnlyGate` (scoped by structure, [ADR-0009](adr/0009-readonly-subagents-by-structure.md)).
`model:` accepts `sonnet` / `opus` / `haiku` aliases or any backend model id, resolved
through the provider seam — so different agents can run on different models (and, when your
local inference is up, different local models).

## Teams

A team is an agent whose frontmatter lists `members`. It dispatches the task to every member
**in parallel** (each cold), then — if it has a `model` — runs one lead synthesis pass that
merges their outputs; without a lead it returns the members' labelled outputs verbatim.

```markdown
---
name: review-board
description: Runs backend, security, and performance reviewers in parallel and merges them.
members: code-reviewer, security_advisor, performance_advisor
model: opus            # the lead that synthesizes; omit to return raw member outputs
---
Merge the members' reviews into one prioritized, de-duplicated list. Reconcile
disagreements explicitly; flag anything only one reviewer caught.
```

Members resolve to other agents you've defined, the built-in delegates (`explore`,
`*_advisor`), or the built-in council personas.

## Council mode

A council is a team with `mode: council`. It is a deliberation harness for architectural
decisions that have **no reference implementation to copy** — where the scarce skill is
direction-setting, and single-agent "consider multiple viewpoints" prompting fails because
one context window can't genuinely disagree with itself.

```markdown
---
name: arch-council
description: Deliberate an architectural decision with no prior art.
members: architect, skeptic, developer, domain
mode: council
model: opus            # the clerk that organizes (does not decide)
---
```

What happens on a call (input: a `decision` with its context):

1. **Dispatch** the personas in parallel, each **cold to the others** — independence by
   construction, not by prompt.
2. **Lock** their perspectives, then a **clerk** (the `model`) organizes them into an
   **Analysis Brief** — `Consensus` / `Contradictions` / `Partial coverage` /
   `Unique insights` / `Blind spots`. The clerk *organizes; it does not decide*.
3. **Drop a Decision Record template** to `.ratchet/council/council-<id>.md`: blank
   Decision / Rationale / Ruled out / Risks accepted / Open questions for the human, above
   the Brief and the locked perspectives.

The human writes the Decision Record; the council never decides. Two invariants protect
independence, both structural: the clerk runs only after all perspectives are locked, and
personas never see the Brief. This is Phase 1 (the clerk, no recommendation). Phase 2 will
add an advisory recommendation rendered *after* the contradictions, so the human forms a
view before seeing the machine's — the same anchoring guard that keeps the personas honest,
turned to protect the human.

### Ad-hoc councils

You don't need a definition file to convene one. A built-in **`council`** tool is always
available; name the roster in the call itself:

```
you> Use the council tool on: should sync be a modular monolith or microservices?
     members: backend-architect, infra-skeptic, domain
```

The `members` are resolved the same way as a defined council — your agents first, then the
built-in personas — so you can assemble a roster on the spot. Omit `members` and it uses the
default four personas. Use a **defined** council (a file) when the roster is stable and you
want it named and reusable; use the ad-hoc `council` tool for a one-off deliberation.
(If you define your own agent named `council`, it takes precedence and the built-in steps aside.)

### Built-in personas

`architect` (structure/boundaries/maintainability), `skeptic` (simplicity/hidden
costs/prior failures), `developer` (implementation reality/PR shape/legibility), and
`domain` (semantics/naming/cross-product alignment) work out of the box. Override any by
defining an agent of the same name — e.g. to pin each persona to a **different model** (the
"Council of Reeds" multi-model path), which the provider seam makes a config change, not code.

## Safety

Nested delegation is bounded on the seams, not the loop: a depth guard (max nesting 3)
refuses a delegate that keeps spawning delegates, and each delegate has an iteration budget
(a turn cap enforced through the observer). Teams and councils fan out to N models, so a run
costs N× — concentrate frontier spend on the members/clerk that need it and let the rest run
cheap.
