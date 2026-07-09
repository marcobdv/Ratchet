# 13. MCP serve exposes coarse delegation tools, not the toolset

- Status: Accepted
- Relates to: [0009](0009-readonly-subagents-by-structure.md) (scoping by structure),
  [0011](0011-delegation-family-one-seam.md) (the delegation family)

## Context

The goal: call Ratchet headless from Claude Code as an MCP server, so a frontier
orchestrator on a subscription does the thinking and Ratchet burns local/cheap tokens
executing plans. Ratchet already *consumes* MCP (`McpToolset`, `.mcp.json` → `ITool`);
serving is the same seam pointed outward. The contested question was **what to expose**.

## Decision

**Three coarse delegation tools — `ratchet_implement`, `ratchet_task`, `ratchet_run` —
not Ratchet's tool inventory.** The caller has its own read/edit/bash; exporting ours
would make Ratchet a file server with extra steps and put the *caller's* tokens back in
the loop for every step — defeating the entire point. What the caller lacks is
"run this to completion on another model and report back": the delegation family's
contract (ADR-0011), served over a wire.

Supporting decisions, each the cheap end of a real fork:

- **`McpServe` adapts `ITool` → MCP tool** (the exact inverse of `McpToolset`), so the
  server host is transport-agnostic, testable over in-memory pipes, and any future tool
  can be served by handing it over. The delegation tools are ordinary `ITool`s.
- **stdout is claimed before anything prints.** The raw stdio streams are captured at
  process start and `Console.Out` is redirected to stderr, so the entire existing
  composition root — banners, tool logs, observers, gate messages — keeps working
  unchanged and visibly (stderr shows in the client's MCP logs), while the protocol
  channel stays byte-clean. No per-call-site sweep, no second code path.
- **Progress notifications are load-bearing, not cosmetic.** An implementation call runs
  minutes to hours; MCP clients time tool calls out. Every agent tool call / workflow
  phase event forwards as a progress notification (plus a 15s heartbeat), which is what
  resets the caller's clock. `IProgressTool` is the seam; plain `ITool`s get the
  heartbeat only.
- **Calls are serialized** (one repo, one pair of hands) and each call persists its
  session/run exactly like the REPL — `--resume`, `--run`, and `recall` work on
  delegated work afterwards. A failure returns an MCP error *result* (the calling model
  adapts), never a protocol fault (the session survives).
- **A serving process skips its own `.mcp.json` entry** (`skipSelfServe`): the project's
  config legitimately lists `ratchet --mcp-serve` for Claude Code, and connecting to it
  from inside would spawn serving children with no base case.
- **`RATCHET_MCP_WORKFLOW`** upgrades `ratchet_implement` from one agent turn to the
  phased orchestrator — headless is where gates matter most (no human is the gate), and
  it is the dark-factory regime ADR-0006/0007 were designed for.

## Alternatives considered

- **Expose the full toolset** (read/edit/bash/roslyn… as MCP tools). Maximum flexibility,
  but the orchestrator would drive every step with its own (subscription) tokens and its
  own context — the delegation, and the economics, disappear.
- **A bespoke RPC/HTTP API.** More control (streaming, jobs), but a new protocol for
  exactly one client when MCP is the lingua franca both sides already speak.
- **Async job pattern** (`implement` returns a run id; the caller polls). More robust to
  client timeouts, but calling models poll unreliably; progress notifications keep the
  synchronous call alive and the run record already covers the crash case. Can be added
  later without breaking the sync surface.

## Consequences

- Claude Code (or any MCP client) delegates with one tool call; Ratchet's model choice
  rides the server entry's `env` (`RATCHET_PROVIDER`/`RATCHET_MODEL`), so
  "subscription orchestrates, local implements" is a config file, not a feature flag.
- Headless mutating work runs under whatever `RATCHET_GATE` the server entry sets — `off`
  by default, consistent with the CLI; the gate mode is stated in the tool description so
  the calling model knows the contract.
- The serve process is long-lived; per-tier cost and run records accumulate in the repo's
  `.ratchet/` as usual, so the "did the cheap models carry it?" question stays answerable.
