# 10. A stop-reason policy at the loop boundary

- Status: Accepted
- Landed: v0.12

## Context

`LlmResponse.StopReason` was transported through the `ILlmClient` seam but never
*interpreted*: the loop treated every non-`tool_use` stop as clean completion. Three
bugs from the 2026-07 review shared this single root cause:

1. A `max_tokens` stop that cut the response off mid-tool-call left `tool_use`
   blocks in the transcript with no matching `tool_result` — the Messages API
   rejects every subsequent request on that path, permanently poisoning the
   session (recoverable only by hand with `/rewind`).
2. A handover doc truncated by `max_tokens` was saved silently — and on `/compact`
   became the fresh session's *only* working memory, violating ADR-0004's core
   promise that loss is authored, never silent.
3. `ChatClientLlm` synthesized the stop reason from "were there tool calls?",
   masking truncation entirely on the default provider path (fixed at the wire
   layer alongside this ADR).

## Decision

The loop owns exactly one transcript-validity invariant that only it can maintain:
**every `tool_use` block it appends is answered before the turn ends.** When a
response stops for any reason other than `tool_use` but still carries tool-use
blocks, the loop closes them with error tool-results
(`[not executed: the response was interrupted (stop_reason=…)]`) so the transcript
stays valid and the model sees the interruption next turn.

Consumers of a completion interpret the stop reason at their own boundary:
`HandoverGenerator` refuses to return a `max_tokens`-truncated (or empty) document
rather than saving a silently lossy one.

Cancellation is part of the same policy: `OperationCanceledException` from a tool
propagates out of the turn promptly instead of being converted into a fake tool
failure and followed by another model call on a dead token.

## Alternatives considered

- **Strip the orphaned tool_use blocks from the assistant message.** Keeps the
  transcript valid but rewrites what the model actually said — the record stops
  being trustworthy, and the model loses the signal that its call was cut off.
- **Handle it in the CLI / stores.** The invariant is created by the loop appending
  the assistant message, so enforcement anywhere else is a convention every
  composition root must remember (the same reasoning as ADR-0009's gate placement).

## Consequences

- This is a deliberate loop edit under ADR-0001's exception clause: the invariant
  cannot live behind a seam, because the seams only see messages after the loop has
  already appended them.
- `max_tokens` mid-tool-call now degrades gracefully: the turn ends, the transcript
  is valid, and the next prompt (or the model itself) can retry the work.
- The synthesized results are honest: marked `is_error` with the stop reason named,
  so a transcript reader can distinguish "tool failed" from "tool never ran".
