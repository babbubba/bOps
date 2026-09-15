# ADR-0005 — One shared OpenAI-compatible adapter; Anthropic gets the only native one

Status: Accepted

**Backfill note.** Written at V0.9.1 to record a decision made at V0.1 (the shared adapter) and
extended at V0.8 (Anthropic's native adapter, plus OpenAI and DeepSeek joining the shared one).
`agentic/05-workflow.md` has listed this ADR as owed since the project's first commit.

## Context

`piano-bops.md` §3.1 already observed that OpenRouter, Ollama and llama.cpp all speak the same
OpenAI Chat Completions wire format, but its own layout put a shared implementation
(`OpenAiCompatibleChatModel`) somewhere it structurally could not live: not in
`bOps.Abstractions` (which must stay dependency-free and provider-agnostic), and not duplicated
into each of the three packages (rule A1 forbids the core from naming providers, and copy-paste
across three packages is the exact kind of defect `agentic/07-plan-corrections.md` catalogues
elsewhere). At V0.8, OpenAI and DeepSeek needed the same choice made again, while Anthropic's
Messages API is not Chat-Completions-shaped at all — no shared adapter could honestly cover it.

## Decision

`bOps.Packages.Providers.OpenAiCompatible` is a plain shared library (rule A8's pattern, applied
to providers instead of operating systems): `OpenAiCompatibleChatModel` implements `IChatModel`
once, including the JSON-schema-in-prompt fallback strategy for providers/models without
reliable native tool-calling (`piano-bops.md` §3.1.1). `bOps.Packages.Providers.OpenRouter`,
`.Ollama`, `.LlamaCpp`, `.OpenAi` and `.DeepSeek` are each a thin `IModelProviderPackage`
(~15–20 lines) that constructs it with that provider's base URL and defaults. `bOps.Packages.
Providers.Anthropic` implements `IChatModel` natively against the Messages API instead of
forcing it through the shared adapter.

## Alternatives considered

- **One adapter per provider, even where the wire format is identical.** Rejected: five
  near-identical copies of request/response mapping is exactly the duplication A8's shared-
  library pattern exists to avoid, and a fix to one (e.g., a JSON-schema-fallback bug) would need
  finding and repeating in every copy.
- **One universal adapter covering both Chat Completions and Messages shapes.** Rejected: the
  two APIs differ enough (message roles, tool-call representation, streaming events) that a
  single abstraction covering both would leak provider-specific branches into what's supposed to
  be the *shared* code — worse than two adapters, not better.
- **Put the shared adapter in `bOps.Abstractions`.** Rejected: it has an HTTP dependency and
  provider-specific defaults; `bOps.Abstractions` stays dependency-free permanently.

## Consequences

Adding a sixth OpenAI-compatible provider is a ~20-line package, not a new adapter. Anthropic's
native adapter is the one place native `tool_use`/`tool_result` blocks are actually handled, kept
deliberately separate rather than forced into the shared shape.
