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

In-session commands: `/sessions` lists saved sessions, `/resume <id>` continues
one, `/new` starts fresh, `/help` shows them. Sessions auto-save to
`.ratchet/sessions/` after each turn; `ratchet -c` reopens the most recent.
(Gitignore `.ratchet/` in real projects.)

## The whole thing in six files

| File | What it is |
|---|---|
| `Core/Agent.cs` | **The loop.** Read it first — the entire idea is one `while`. |
| `Core/Conversation.cs` | The transcript + the four wire content-block shapes. |
| `Core/ITool.cs` | The extension seam + registry. |
| `Core/Tools.cs` | read / write / edit / bash. |
| `Core/Sessions.cs` | Persist & resume the transcript (the agent's memory). |
| `Llm/AnthropicClient.cs` | Wire-level Messages API — builds JSON & consumes the SSE stream by hand. |

`Cli/Program.cs` is just wiring + a console observer + the REPL.

## Why it's split into three projects

So it grows toward the full Ratchet without a rewrite. Each seam is where a
doc'd feature plugs in:

- **`ILlmClient`** → provider-agnostic later (OpenAI, Copilot-as-provider).
- **`ITool`** → Roslyn semantic-navigation tool, MCP-backed tools, all land here.
- **`IAgentObserver`** → audit logging / TUI / ACP streaming hang off this.
- **`BashTool` + `ShellSpec`** → shell is swappable (bash/cmd/pwsh) today; the
  ConPTY upgrade replaces the `Process` plumbing inside this one class.
- **`ISessionStore`** → persistence is a seam too; a SQLite or cloud store drops
  in behind it without touching the loop.

## What it deliberately does NOT do

No context compaction, no sub-agents, no permission gates (YOLO bash, like pi).
Each omission is a known next step, not an oversight. Add them one at a time —
that's the curriculum.

> **v0.1 — streaming.** Responses stream over SSE: assistant text appears
> token-by-token, and tool-call arguments are reassembled from `input_json_delta`
> fragments. See `Llm/AnthropicClient.ConsumeStreamAsync`.
>
> **v0.2 — sessions.** Conversations auto-save to `.ratchet/sessions/` after each
> turn (one JSON file each, same shape as the API wire format). `/sessions`,
> `/resume <id>`, and `ratchet -c` bring them back. Resume is linear; pi's
> tree-branching (`/branch`, rewind) is the next rung.

## Namespacing

`CodeStack.Ratchet.*` namespaces, company-prefix-free assembly names
(`Ratchet.Core.dll`), matching the convention in the Forgewright repo.
