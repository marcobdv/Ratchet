# 5. Provider-agnostic via `IChatClient`, but keep the hand-rolled Anthropic wire

- Status: Accepted
- Landed: v0.6 (`IChatClient`), broadened to OpenAI-compatible providers in v0.8/v0.9
- Relates to: [0002](0002-core-stays-dependency-free.md)

## Context

v0 spoke to exactly one backend through a hand-written Anthropic Messages client —
JSON built by hand, SSE consumed by hand. That hand-rolled wire is *pedagogically the
point*: it's how you actually see what a turn is on the network. But tying the whole
agent to one vendor is a real limitation, and the user explicitly wanted broader
choice ("I don't want to be tied to only Anthropic") — OpenRouter, OpenAI, Groq, local
models via Ollama/LM Studio/vLLM.

The tension: going provider-agnostic usually means deleting the bespoke client in
favour of an SDK abstraction. That would throw away the wire-level artifact.

## Decision

Keep **both**, behind the existing `ILlmClient` seam:

1. `ChatClientLlm` adapts `ILlmClient` onto any `Microsoft.Extensions.AI.IChatClient`,
   making Ratchet provider-agnostic and letting MCP tools (which are `AITool`s) drop
   straight in.
2. The hand-rolled Anthropic wire is **retained**, now also exposed as an
   `IChatClient` (`AnthropicChatClient`) so it flows through the same adapter — while
   `RATCHET_PROVIDER=anthropic-native` still selects the *original* bespoke
   `ILlmClient` for anyone who wants to read the wire path.
3. Everything non-Anthropic flows through **one** hand-rolled OpenAI-compatible client
   (`OpenAiChatClient`). `RATCHET_PROVIDER` selects anthropic / anthropic-native /
   openrouter / openai / groq / local / generic; `RATCHET_BASE_URL` covers any other
   `/v1` endpoint.

The wire artifact survives *and* the agent runs on hundreds of models through one key.

## Alternatives considered

- **Adopt one vendor SDK exclusively.** Less code, but it deletes the hand-rolled wire
  (the learning value) and still locks you to whatever that SDK abstracts well.
- **`IChatClient` everywhere, drop the bespoke clients entirely.** Cleanest single
  abstraction. Rejected because `anthropic-native` is a deliberate teaching path, and
  because a thin hand-rolled OpenAI client we control is easier to debug at the wire
  than a general SDK when a provider misbehaves (e.g. null content with tool calls,
  synthetic tool-call ids — both real fixes in `OpenAiChatClient`).
- **A client per provider.** N bespoke clients is N maintenance burdens. One
  OpenAI-compatible client covers the entire OpenAI-shaped ecosystem; only Anthropic,
  which predates and differs from that shape, gets its own.

## Consequences

- One key (e.g. OpenRouter) reaches hundreds of models; a workflow's per-tier
  `models:` block can mix backends — a cheap `local` driver with an OpenRouter frontier
  judge — because the provider selection is per-`ILlmClient`, not global.
- Anthropic-specific features (prompt caching via `cache_control`, full cache-token
  accounting) live in the Anthropic clients without leaking into the generic path.
- Per ADR-0002, all of this is in `Ratchet.Llm`; Core still sees only `ILlmClient`.
- Cost: two-and-a-bit client implementations to maintain, and provider quirks surface
  as targeted fixes in the OpenAI-compatible client. Accepted as the price of keeping
  the wire-level artifact while being genuinely vendor-neutral.
