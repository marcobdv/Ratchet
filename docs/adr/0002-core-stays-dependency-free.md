# 2. Core stays dependency-free; heavy dependencies live in separate projects

- Status: Accepted
- Landed: v0 (rule), enforced at scale in v0.6
- Relates to: [0001](0001-immutable-loop-grow-on-seams.md), [0008](0008-telemetry-on-bcl-in-core.md)

## Context

The features Ratchet grows into drag in heavyweight dependencies: MSBuildWorkspace
(Roslyn) is large and version-sensitive, the MCP client pulls a protocol stack,
YamlDotNet parses workflow files, Microsoft.Data.Sqlite is a native-interop package,
and the OpenTelemetry SDK is a whole exporter ecosystem. If `Ratchet.Core` references
any of them, every consumer of the loop inherits a transitive dependency graph it may
not want, build times balloon, and the "tiny, readable core" claim quietly becomes
false.

## Decision

`Ratchet.Core` references **nothing outside the BCL**. It defines the seams
(`ILlmClient`, `ITool`, `ISessionStore`, `IToolGate`, …) and the loop; every heavy
dependency lives behind one of those seams in its **own project**:

- `Ratchet.Llm` — provider clients (Anthropic wire, `IChatClient` adapter, OpenAI-compatible wire)
- `Ratchet.Storage.Sqlite` — `ISessionStore` + `ITextSearchableStore` over SQLite/FTS5
- `Ratchet.Tools.Roslyn` — semantic C# tools over MSBuildWorkspace
- `Ratchet.Tools.Mcp` — MCP-backed `ITool`s
- `Ratchet.Workflow` — the orchestrator (+YamlDotNet)

The CLI (`Ratchet.Cli`) is the composition root — the one place that references
everything and wires concrete implementations into the seams.

## Alternatives considered

- **One project, reference what you need.** Simplest to start, and the default for
  small codebases. Rejected: dependencies are transitive, so "Core needs nothing" is
  the only place the boundary can actually be enforced. Once Core references Roslyn,
  every test and every embedding host pays for Roslyn.
- **A `Ratchet.Abstractions` package separate from `Ratchet.Core`.** The common
  enterprise split. Rejected as ceremony: Core *is* the abstractions plus the loop,
  and keeping them together is what makes "read Core first" a complete picture.

## Consequences

- Core can be referenced by anything — a test harness, an embedding host, a different
  front-end — without inheriting Roslyn/Yaml/SQLite/OTel.
- The project layout is self-documenting: each project name announces exactly which
  dependency it quarantines.
- This rule directly shaped ADR-0008: OpenTelemetry instrumentation had to go in Core
  (it's cross-cutting), so it uses the BCL `ActivitySource`/`Meter` primitives and the
  SDK is wired only in the CLI — the dependency-free rule held even for observability.
- Cost: the composition root in `Cli/Program.cs` carries real wiring complexity (every
  provider, store, gate, and exporter is resolved there). That concentration is
  deliberate — one busy file instead of dependencies smeared across the tree.
