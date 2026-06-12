# Ratchet v0

A deliberately tiny .NET 9 coding agent — a "pi-plain" port. Four tools, one
loop, a hand-rolled Anthropic client. The point is **understanding the agent
loop at the wire level**, not competing with Claude Code.

> This is the learning atom from `01. Projects/Ideas/Ratchet.md`, stripped to v0:
> no ACP, no MCP, no Roslyn, no plugin compatibility. Those are deliberate later
> elaborations — see *Growth path* below.

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

```powershell
$env:RATCHET_SHELL = "pwsh"            # this session only
setx RATCHET_SHELL "pwsh"             # persistent (new terminal to take effect)
```

Try: *"create hello.txt with a haiku about warehouses, then read it back"*.

In-session commands: `/sessions`, `/resume <id>`, `/new`, `/tree` (show the
branch tree), `/rewind [n]` (move HEAD back n turns), `/goto <node>` (jump to a
branch tip), `/handover` (write a handover doc), `/handovers` (list them),
`/help`. Sessions auto-save to `.ratchet/sessions/` after each turn; `ratchet -c`
reopens the most recent. (Gitignore `.ratchet/` in real projects.)

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
| `Llm/AnthropicClient.cs` | Wire-level Messages API — builds JSON & consumes the SSE stream by hand. |

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

No **in-place context compaction**, no sub-agents, no permission gates (YOLO bash,
like pi). The long-session answer here is handover (v0.5), not silent
summarisation. Each omission is a known next step, not an oversight — add them one
at a time; that's the curriculum.

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

## Namespacing

`CodeStack.Ratchet.*` namespaces, company-prefix-free assembly names
(`Ratchet.Core.dll`), matching the convention in the Forgewright repo.
