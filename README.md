# Ratchet v0

A deliberately tiny .NET 9 coding agent — a "pi-plain" port. Four tools, one
loop, a hand-rolled Anthropic client. The point is **understanding the agent
loop at the wire level**, not competing with Claude Code.

> This is the learning atom from `01. Projects/Ideas/Ratchet.md`. v0.1–v0.5 kept it
> stripped (no MCP, no Roslyn, no sub-agents). **v0.6 grows along the seams the README
> always promised**: the `ILlmClient` seam now rides `Microsoft.Extensions.AI.IChatClient`
> (provider-agnostic), and `ITool` gains Roslyn, MCP, sub-agents/advisors, and skills — each
> in its own project so Core stays dependency-free. The loop, tree, and handover are untouched.

## Run it

```powershell
setx ANTHROPIC_API_KEY "sk-ant-..."   # once, then open a new terminal
cd D:\Repos\Ratchet
dotnet run --project src/Ratchet.Cli
```

Optional: drop an `AGENTS.md` in the working directory and it's prepended to the
system prompt. Override the model with `RATCHET_MODEL` (defaults to
`claude-sonnet-4-6` — swap when a newer one ships). Pick the shell the `bash`
tool drives with `RATCHET_SHELL` — `bash`, `cmd`, or `pwsh` (PowerShell 7+).
Unset, it defaults to cmd on Windows and bash elsewhere.

**Not tied to Anthropic.** Pick the backend with `RATCHET_PROVIDER` — Ratchet talks to
any of them (and every other config, tools, sessions, workflows, work the same):

| `RATCHET_PROVIDER` | endpoint | key env | notes |
|---|---|---|---|
| `anthropic` *(default)* | Anthropic Messages API | `ANTHROPIC_API_KEY` | via `AnthropicChatClient` |
| `anthropic-native` | Anthropic Messages API | `ANTHROPIC_API_KEY` | hand-rolled wire `ILlmClient` |
| `openrouter` | `openrouter.ai/api/v1` | `OPENROUTER_API_KEY` (or `RATCHET_API_KEY`) | **one key, hundreds of models** |
| `openai` | `api.openai.com/v1` | `OPENAI_API_KEY` (or `RATCHET_API_KEY`) | |
| `groq` | `api.groq.com/openai/v1` | `GROQ_API_KEY` (or `RATCHET_API_KEY`) | |
| `local` / `ollama` | `RATCHET_LOCAL_BASE_URL` (default Ollama) | `RATCHET_LOCAL_API_KEY` (optional) | LM Studio / vLLM / llama.cpp |
| *anything else* | `RATCHET_BASE_URL` | `RATCHET_API_KEY` | any OpenAI-compatible endpoint |

Everything except Anthropic flows through one hand-rolled OpenAI-compatible client
(`Llm/OpenAiChatClient.cs`). For non-Anthropic providers set `RATCHET_MODEL` to that
backend's model id (e.g. OpenRouter `anthropic/claude-sonnet-4` or `openai/gpt-4o`;
Groq `llama-3.3-70b-versatile`). `RATCHET_BASE_URL` overrides the endpoint for any
OpenAI-compatible provider (proxies, gateways). OpenRouter attribution headers come from
`RATCHET_OPENROUTER_REFERER` / `RATCHET_OPENROUTER_TITLE`. Example:

```powershell
$env:RATCHET_PROVIDER = "openrouter"
$env:OPENROUTER_API_KEY = "sk-or-..."
$env:RATCHET_MODEL = "anthropic/claude-sonnet-4"   # or openai/gpt-4o, google/gemini-2.5-pro, …
dotnet run --project src/Ratchet.Cli
```

The same provider names work for a workflow's per-tier `models:` block, so one workflow
can mix backends (a cheap `local` driver with an `openrouter` frontier judge).

The extra tools light up from the working directory: a `.mcp.json` connects MCP
servers, `.ratchet/skills/<name>/SKILL.md` (or `.claude/skills/…`) registers skills,
and the Roslyn + `explore`/advisor tools are always available. `ratchet --roslyn-check`
runs the Roslyn tools against the current solution with no API key (a self-test).

```powershell
$env:RATCHET_SHELL = "pwsh"            # this session only
setx RATCHET_SHELL "pwsh"             # persistent (new terminal to take effect)
```

Try: *"create hello.txt with a haiku about warehouses, then read it back"*.

In-session commands: `/sessions`, `/resume <id>`, `/new`, `/tree` (show the
branch tree), `/rewind [n]` (move HEAD back n turns), `/goto <node>` (jump to a
branch tip), `/handover` (write a handover doc), `/handovers` (list them),
`/compact` (fold this session into a handover and continue fresh), `/help`.
Sessions auto-save to `.ratchet/sessions/` after each turn; `ratchet -c`
reopens the most recent. (Gitignore `.ratchet/` in real projects.)

More tools are always on besides the four primitives: `update_plan` (an explicit,
revisable task checklist), `run_tests` (runs the suite — `dotnet test` by default,
override with `RATCHET_TEST_CMD` — and returns a parsed pass/fail summary), and
`git_status` / `git_diff` (read-only — staging/committing stay behind the
not-yet-built permission gate). The `edit` tool requires you to have read a file
first and that its match be unique (or pass `replace_all`).

Two more env knobs: `RATCHET_CONTEXT_LIMIT` sets an input-token threshold past which
Ratchet auto-compacts (see v0.7); `RATCHET_PTY=1` opts the `bash` tool into a Windows
ConPTY pseudo-console (a real TTY) instead of redirected pipes.

### Workflows (phased orchestration)

Instead of one undifferentiated agent, run a task through a fixed sequence of phases
(`research → plan → implement → verify → review`), each with its own role, tool subset,
model tier, and skills — cheap models do the bulk, frontier spend is concentrated at
gates:

```powershell
ratchet --workflow workflows/ratchet-dev.yaml "add a --version flag to the CLI"
```

The orchestrator is **deterministic** (the spine, the gates, loop-back, escalation);
LLM judgment shows up only at the intake classifier (which sizes the task into a
`work_type`) and at judge gates. Floors (`verify`, `review`) always run, and a final
`land` phase branches + commits the reviewed change (`git_commit`, governed by the
permission gate). Phase-to-phase context uses the v0.5 handover + `recall` machinery.
Local/OpenAI-compatible driver tiers point at any `/v1` endpoint via
`RATCHET_LOCAL_BASE_URL` (e.g. Ollama).

Every run is recorded to `.ratchet/runs/<id>.json` — the classification + reasoning, the
event trace, and **per-tier token cost** (so "are the cheap drivers actually carrying the
load?" is answerable). Inspect with `ratchet --runs` / `ratchet --run <id>`. An interrupted
run checkpoints after each phase and continues with `ratchet --workflow-resume <id>`. The
full design and rationale is `docs/workflow-orchestration.md`.

Storage backend is swappable via `RATCHET_STORE`: unset (default) writes one
JSON file per session under `.ratchet/sessions/`; `sqlite` uses a single
`.ratchet/ratchet.db` and inserts only new nodes per turn (no full rewrite).
Both implement the same `ISessionStore` seam.

Resume cold from a handover with `ratchet --handover <session-id>`: a fresh
session that carries the handover doc as its working set and gains a `recall`
tool to page detail back out of the prior session.

## The core files

| File | What it is |
|---|---|
| `Core/Agent.cs` | **The loop.** Read it first — the entire idea is one `while`. |
| `Core/Conversation.cs` | The transcript + the four wire content-block shapes. |
| `Core/ITool.cs` | The extension seam + registry. |
| `Core/Tools.cs` | read / write / edit / bash. |
| `Core/Sessions.cs` | Session **tree** (HEAD over a DAG) + the JSON-file store. |
| `Core/Handover.cs` | Handover doc, prompt template, file store, generator. |
| `Core/RecallTool.cs` | Retrieval back into a prior session's cold-stored nodes. |
| `Core/SubAgents.cs` | `DelegateTool` — a tool that runs a nested `Agent`. The `explore` sub-agent + advisors. |
| `Core/Skills.cs` | SKILL.md discovery + the `load_skill` tool (progressive disclosure). |
| `Llm/AnthropicClient.cs` | Wire-level Messages API — builds JSON & consumes the SSE stream by hand (`anthropic-native`). |
| `Llm/AnthropicChatClient.cs` | The same wire client exposed as a `Microsoft.Extensions.AI.IChatClient`. |
| `Llm/ChatClientLlm.cs` | `ILlmClient` over any `IChatClient` — this is the "adopt IChatClient" seam. |
| `Tools.Roslyn/` | Semantic C#: diagnostics, find-symbol/references, outline, rename (MSBuildWorkspace). |
| `Tools.Mcp/` | Connects MCP servers from `.mcp.json`; each server tool becomes an `ITool`. |
| `Core/PlanTool.cs` | `update_plan` — an explicit, re-sent task checklist (planning). |
| `Core/TestTool.cs` | `run_tests` — runs the suite and returns a parsed pass/fail summary. |
| `Core/GitTools.cs` | `git_status` / `git_diff` — read-only repo awareness. |
| `Core/FileAccessLog.cs` | The read-before-write guard shared by read/write/edit. |
| `Core/WindowsPty.cs` | Opt-in ConPTY pseudo-console runner for `bash` (`RATCHET_PTY=1`). |

`Cli/Program.cs` is wiring + a console observer + the REPL.
`Storage.Sqlite/SqliteSessionStore.cs` is the optional SQLite backend.

## Why it's split into projects

So it grows toward the full Ratchet without a rewrite. Each seam is where a
doc'd feature plugs in:

- **`ILlmClient`** → provider-agnostic later (OpenAI, Copilot-as-provider). Also
  what the handover generator calls — summarising is just another completion.
- **`ITool`** → Roslyn navigation, MCP-backed tools, and `recall` all land here.
- **`IAgentObserver`** → audit logging / TUI / ACP streaming hang off this.
- **`BashTool` + `ShellSpec`** → shell is swappable (bash/cmd/pwsh) today; the
  ConPTY upgrade replaces the `Process` plumbing inside this one class.
- **`ISessionStore`** → persistence is a seam: `FileSessionStore` (JSON files)
  and `SqliteSessionStore` (one DB, incremental inserts, recursive-CTE path
  walks) both sit behind it. Pick with `RATCHET_STORE`; the loop never changes.
  Core stays dependency-free — the SQLite adapter is a separate project.

## What it deliberately does NOT do

Still no **silent in-place compaction**: the long-session answer remains *handover*,
now also auto-triggered when context crosses `RATCHET_CONTEXT_LIMIT` (v0.7) — a
self-authored summary, never a quiet lossy truncation. The **permission gate** that was
the conspicuous gap now exists (v0.9, `IToolGate`) but is **off by default** — Ratchet
stays pi-plain YOLO unless you set `RATCHET_GATE=prompt` (or `deny`), at which point
mutating tools (`bash`, `write`, `edit`, `git_commit`, `git_create_branch`, the Roslyn
rename) require approval while read-only tools always pass. The `explore` sub-agent is
still read-only by prompt, not by a gate.

> **v0.1 — streaming.** Responses stream over SSE: assistant text appears
> token-by-token, and tool-call arguments are reassembled from `input_json_delta`
> fragments. See `Llm/AnthropicClient.ConsumeStreamAsync`.
>
> **v0.2 — sessions.** Conversations auto-save to `.ratchet/sessions/` after each
> turn (one JSON file each, same shape as the API wire format). `/sessions`,
> `/resume <id>`, and `ratchet -c` bring them back.
>
> **v0.3 — branch tree.** A session is a *tree* of message nodes with a HEAD
> pointer (the git model). `/rewind [n]` moves HEAD back whole turns; continuing
> forks a new branch while the old line is preserved. `/tree` visualises it,
> `/goto <node>` jumps between branch tips. Rewind is turn-level so HEAD always
> lands on a valid boundary.
>
> **v0.4 — SQLite store.** `SqliteSessionStore` (separate project, keeps Core
> dependency-free) implements `ISessionStore` over one `.ratchet/ratchet.db`.
> Nodes are append-only, so each turn inserts just the new rows instead of
> rewriting a whole file; recursive CTEs walk the parent chain. Opt in with
> `RATCHET_STORE=sqlite`.
>
> **v0.5 — handover (instead of compaction).** Long sessions are handled by
> *retrieval-backed handover*, not in-place summarisation. `/handover` has the
> model author a structured doc (goal · state · decisions · next steps · gotchas ·
> pointers) saved as editable Markdown under `.ratchet/handovers/`. `ratchet
> --handover <id>` then starts a **fresh** session with that doc injected as its
> working set, plus a `recall` tool that searches the prior session's full tree
> for detail the summary left out. Nothing is destroyed — old context is demoted
> to cold storage, and loss is *authored*, not silent. The generator rides on
> `ILlmClient`, `recall` rides on `ITool` + `ISessionStore`; the loop is untouched.
> When unattended runs eventually need it, compaction returns as an auto-triggered
> self-handover on the same machinery. Next rungs: a second `ILlmClient`; SQLite
> FTS behind `recall`.
>
> **v0.6 — IChatClient + the deferred elaborations.** The `ILlmClient` seam now has a
> second implementation, `ChatClientLlm`, backed by any `Microsoft.Extensions.AI`
> `IChatClient` — so Ratchet is provider-agnostic and MCP tools (which are `AITool`s)
> drop straight in. Anthropic flows through it via `AnthropicChatClient` (the same
> hand-rolled wire code, now speaking `IChatClient`); `RATCHET_PROVIDER=anthropic-native`
> still selects the original wire `ILlmClient`. On the `ITool` seam: **Roslyn**
> (`Tools.Roslyn`, semantic C# via MSBuildWorkspace), **MCP** (`Tools.Mcp`, `.mcp.json`),
> **sub-agents + advisors** (`Core/SubAgents.cs`, a tool that runs a nested `Agent`), and
> **skills** (`Core/Skills.cs`, SKILL.md progressive disclosure). Each heavy dependency sits
> in its own project; Core stays dependency-free. The loop, tree, and handover never changed —
> every addition landed on a seam, exactly as the growth path promised.
>
> **v0.7 — sharper tools, caching, and self-compaction.** A batch of elaborations, each on
> an existing seam, none touching the loop:
> - **Prompt caching.** Both Anthropic clients now stamp `cache_control` breakpoints on the
>   (stable) system prompt and tool specs and on the transcript tail, so unchanged prefixes are
>   read from cache instead of re-billed each turn. Lives entirely in `Llm/CacheControl.cs` +
>   the request builders.
> - **Auto self-compaction.** Set `RATCHET_CONTEXT_LIMIT` and, once a turn's input context
>   crosses it, Ratchet authors a handover and continues in a fresh session seeded with that doc
>   plus a `recall` tool over the archive — the v0.5 machinery, now self-triggered. `/compact`
>   does it on demand. "When unattended runs need it, compaction returns as an auto-triggered
>   self-handover," exactly as v0.5 promised.
> - **FTS-backed recall.** The SQLite store implements a new `ITextSearchableStore` seam (FTS5),
>   so `recall` searches in the database instead of loading the whole tree; the file store still
>   falls back to the in-memory scan. The seam is additive — no store is forced to implement it.
> - **A planning tool** (`update_plan`), a **test runner** (`run_tests`, parsed summary), and
>   **read-only git** (`git_status`/`git_diff`) — all plain `ITool`s.
> - **Edit guard.** `edit` now requires a prior read (shared `FileAccessLog`) and a unique match
>   (or `replace_all`) — no more blind, ambiguous edits.
> - **ConPTY shell.** An opt-in (`RATCHET_PTY=1`) Windows pseudo-console runner for `bash`
>   (`Core/WindowsPty.cs`, pure BCL P/Invoke). It gives the child a real TTY; because one-shot
>   *capture* through a pty is finicky (VT framing, render timing, nested consoles), it's opt-in
>   and the redirected-`Process` path stays the default. On a spawn failure it falls back to that
>   path; once a command has actually run under the pty it never re-runs it, so a side-effecting
>   command can't execute twice.
>
> Permission gates are still the conspicuous gap — the next rung, and the reason git here is
> read-only.
>
> **v0.8 — workflow orchestration.** A phased orchestrator on top of the loop
> (`src/Ratchet.Workflow`, +YamlDotNet; Core stays dependency-free). One LLM call at
> intake sizes the task into a `work_type`; a fixed, ordered spine then runs the selected
> phase subset, each phase its own `Agent` with its own role/tools/model-tier/skills. The
> scheduler is deterministic — it runs phases, evaluates **gates** (command gates route on
> an exit code; judge gates spend a frontier model on judgment an exit code can't express),
> and routes: pass advances, fail **loops back** (bounded), and a phase that proves bigger
> than its sizing **escalates** back up the spine. Floors (`verify`, `review`) always run.
> The phase-to-phase handoff *is* v0.5: an authored handover doc + `recall` into the prior
> phase's transcript. A second `ILlmClient` (`OpenAiChatClient`) lets cheap `local` driver
> tiers run on any OpenAI-compatible endpoint while frontier judges stay hosted. The intake
> classification, every gate outcome, each advisor consult, and advisor↔gate conflicts are
> recorded on the run, so a bad skip is diffable after the fact. Run it with
> `ratchet --workflow <file.yaml> "<task>"`; the design and the resolved open forks are in
> `docs/workflow-orchestration.md`. The agent loop, tree, and handover never changed — the
> orchestrator is plain control flow above the seams.
>
> **v0.9 — gates, run records, cost, resume, land.** Five elaborations that make the
> orchestrator trustworthy and close the "produces uncommitted diffs" gap:
> - **Permission gate** (`Core/ToolGate.cs`). An `IToolGate` consulted in
>   `Agent.ExecuteToolAsync` *before* a tool runs — a denial returns to the model as an
>   error result, not a crash, so the guarantee lives in the loop. Default `AllowAllGate`
>   (unchanged YOLO); `RATCHET_GATE=prompt|deny` turns it on for mutating tools. The REPL
>   agent and every workflow phase share it.
> - **Git write + a `land` phase.** `git_commit` / `git_create_branch` (mutating, so the
>   gate governs them) let the workflow actually *ship*: a terminal `land` phase branches
>   and commits the reviewed change instead of leaving a diff on the floor.
> - **Persisted run records** (`IRunStore` / `FileRunStore`). The classification, event
>   trace, and cost are written to `.ratchet/runs/<id>.json` — so "a bad skip is diffable
>   after the fact" is finally true, not a console line that scrolled away. `--runs` / `--run`.
> - **Per-tier cost accounting.** A `MeteredLlmClient` wraps every tier, so driver /
>   classifier / judge / advisor / handover tokens are tallied by tier with no call site
>   changes — making the "cheap drivers, frontier gates" economics measurable instead of
>   asserted.
> - **Resumable runs.** The scheduler checkpoints before each phase; a run interrupted by a
>   transient failure continues from the last good phase with `--workflow-resume <id>`,
>   without re-classifying or re-running completed phases — the prerequisite for unattended
>   runs. Each rides an existing seam; the loop is still untouched.
> - **Provider-agnostic.** `RATCHET_PROVIDER` now selects Anthropic, **OpenRouter** (one key,
>   hundreds of models), OpenAI, Groq, a local server, or any OpenAI-compatible endpoint via
>   `RATCHET_BASE_URL` — all through the existing `ILlmClient` seam (`OpenAiChatClient` for the
>   non-Anthropic wire). The same provider names work per-tier in a workflow, so one run can
>   mix a cheap local driver with an OpenRouter frontier judge. See the provider table above.

## Namespacing

`CodeStack.Ratchet.*` namespaces, company-prefix-free assembly names
(`Ratchet.Core.dll`), matching the convention in the Forgewright repo.
