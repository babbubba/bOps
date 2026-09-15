# ADR-0001 — A project-owned `IChatModel`, not `Microsoft.Extensions.AI`

Status: Accepted

**Backfill note.** This ADR was written at V0.9.1 to record a decision actually made and acted
on since V0.1 — `agentic/05-workflow.md` has always listed it as one of "the first ADRs to
exist," but no file existed until now. Nothing here changes any code or reopens the decision;
it documents what `bOps.Abstractions/Model.cs` has looked like from the start.

## Context

By V0.1, .NET already had an emerging ecosystem abstraction for chat models
(`Microsoft.Extensions.AI`). Depending on it directly in `bOps.Abstractions` would have saved
writing `IChatModel`, `ChatTurn`, `ModelRequest`/`ModelResponse` from scratch. But
`bOps.Abstractions` has one absolute rule that predates any specific model contract decision
(`agentic/05-workflow.md`, "Things to never do without being asked"): it stays dependency-free,
permanently. `agentic/01-architecture-rules.md` rule A12 also keeps it on `0.x` specifically so
the contract can still move — a project-owned type can move on the project's own schedule; a
third-party abstraction's shape moves on that library's schedule instead.

## Decision

`IChatModel`, `ChatTurn`, `ModelToolCall`, `ModelRequest`, `ModelResponse`, `ChatModelDescriptor`
and `ModelUsage` are all defined in `bOps.Abstractions` itself, owned entirely by this project.
Provider packages (`bOps.Packages.Providers.*`) translate to and from whatever SDK or raw HTTP
call each provider actually needs; a breaking change in a vendor library, or in
`Microsoft.Extensions.AI` itself, touches exactly one adapter, never the runtime or the
contract every package is built against.

## Alternatives considered

- **Depend on `Microsoft.Extensions.AI` directly in `bOps.Abstractions`.** Rejected: it is a
  dependency on the contract that stays dependency-free by rule, and it would couple this
  project's audit/replay/policy requirements (round-trippable arguments per rule B1, an audit
  event shape that records provider/model/token usage per rule B8) to whatever that library's
  maintainers decide those shapes should look like, on their timeline, not this project's.
- **Depend on it only inside provider packages, not the core contract.** Considered more
  seriously — packages may take third-party dependencies freely (rule A7). Rejected because the
  actual OpenAI-compatible providers (D-007's tool-calling requirements: `tool_call_id`, multiple
  calls per turn) needed a contract shape this project controlled from day one, and mixing "some
  providers use the abstraction, some use raw HTTP" would have made `OpenAiCompatibleChatModel`'s
  job of presenting one consistent `IChatModel` to the runtime harder, not easier.

## Consequences

Every provider package (`OpenAiCompatibleChatModel` shared by OpenRouter/Ollama/llama.cpp/OpenAI/
DeepSeek, and the native `AnthropicChatModel`) implements the same project-owned `IChatModel`
directly. `bOps.Abstractions` has zero external dependencies, as it must (rule A12, D-012).
