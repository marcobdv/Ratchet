# 8. Telemetry on BCL diagnostics in Core; the OTel SDK only in the CLI

- Status: Accepted
- Landed: v0.11
- Relates to: [0001](0001-immutable-loop-grow-on-seams.md), [0002](0002-core-stays-dependency-free.md)

## Context

Observability is genuinely cross-cutting: the spans worth recording are the model
call, the turn, the tool execution, the gate, the run, and the phase — and those live
in `Ratchet.Core` and `Ratchet.Workflow`, not at the edges. That collides head-on with
two standing rules: the loop is immutable (ADR-0001) and Core takes no dependencies
(ADR-0002). The naive implementation — add the OpenTelemetry SDK to Core and wrap the
loop in instrumentation — violates both at once.

## Decision

Split *instrumentation* from *configuration*, along the BCL boundary:

- **Instrument in Core on the vendor-neutral BCL primitives.** `RatchetTelemetry`
  uses one `System.Diagnostics.ActivitySource` and one
  `System.Diagnostics.Metrics.Meter` — both shipped in the BCL. No OpenTelemetry
  package is referenced by Core. When nothing is listening, an `ActivitySource` that
  has no listeners does no work, so the instrumentation is **zero-cost by default**.
- **Configure only in the CLI.** `Ratchet.Cli` is the sole project that takes the
  OpenTelemetry SDK and wires exporters — `RATCHET_OTEL=console|otlp`, OTLP endpoint
  via the standard env var. Same instrument-in-Core / configure-at-the-root seam as
  every other dependency (ADR-0002).
- **Follow the GenAI semantic conventions** (`gen_ai.*`) so any OTel backend
  (Jaeger/Tempo/Grafana) renders it natively, and place each span where the relevant
  fact is known: the chat span in the LLM client (where the model name lives), the
  turn/tool/gate spans in the loop, the run/phase spans in the scheduler.
- **Spans go on the seams as `using` blocks, not as loop edits.** The loop body still
  reads as one `while` (ADR-0001).

## Alternatives considered

- **OpenTelemetry SDK referenced directly in Core.** The straightforward way to
  instrument. Rejected: it breaks the dependency-free rule, and forces every Core
  consumer (tests, embedding hosts) to inherit the exporter stack.
- **A logging/metrics abstraction interface (`ITelemetry`) injected through Core.**
  Would honour the dependency rule, but it reinvents what the BCL `ActivitySource`/
  `Meter` *already are* — a vendor-neutral diagnostics API with a built-in
  zero-listener fast path. Adding our own seam over it is pure ceremony.
- **Instrument only at the CLI edges.** No Core dependency, but you lose the spans that
  matter (per-model-call tokens, per-tool duration, gate denials) because those facts
  only exist deep in the loop and the scheduler.

## Consequences

- Traces nest into a real tree — `workflow.run → classify / phase → agent.turn →
  chat {model}` / `execute_tool {name}` / `gate {kind}` — with provider, model, token
  usage, finish reason, and gate-denied attributes; metrics cover token usage, model
  and tool durations, tool-call counts, and gate denials.
- Core keeps its no-dependency rule *even for observability* — the sharpest possible
  test of ADR-0002, and it held.
- Turning telemetry off isn't a config flag with overhead; with no listener the
  instrumentation genuinely costs nothing.
- Cost: the BCL diagnostics API is lower-level and more verbose than calling an SDK
  directly, and the GenAI conventions must be tracked by hand (they're still evolving —
  e.g. `gen_ai.provider.name` over the deprecated `gen_ai.system`). Accepted as the
  price of keeping the dependency boundary intact.
