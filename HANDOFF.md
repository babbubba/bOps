# Handoff — V0.8 (Anthropic, OpenAI and DeepSeek provider packages) complete

Written at the end of the session that implemented V0.8 on top of the completed V0.7 persistence
work. Everything below is exact, not a summary — follow it literally to resume.

## State right now

**`dotnet build bOps.slnx` builds clean end to end — 0 warnings, 0 errors.** 34 projects now, up
from 30 at the end of V0.7 (`bOps.Packages.Providers.OpenAi`, `bOps.Packages.Providers.DeepSeek`,
`bOps.Packages.Providers.Anthropic`, `bOps.Packages.Providers.Anthropic.Tests`).

**`dotnet test bOps.slnx --filter "Category!=LiveModel"`: every suite passes**, including the new
`bOps.Packages.Providers.Anthropic.Tests` (8 contract tests against a recorded/fake HTTP handler,
no live call). Same pre-existing skips as every prior handoff.

**⚠️ Security finding carried forward, still not resolved: the real OpenRouter API key in
`src/core/bOps.Cli/appsettings.json` is committed to git history** (commit `7ac2901`). See the
V0.7 handoff (preserved in git history at that commit) for full detail. No agent session should
commit this file's `ModelProvider.ApiKey` as anything but `""`; this session did not touch it.

## What this session did

Implemented V0.8 per `agentic/00-project-spec.md`'s roadmap: **"Anthropic, OpenAI and DeepSeek
provider packages."** This is Phase 2's opening version — Phase 1 (the CLI roadmap, V0.1–V0.7) is
now complete.

### OpenAI and DeepSeek — thin wrappers over the shared adapter (ADR-0005)

`bOps.Packages.Providers.OpenAi` and `bOps.Packages.Providers.DeepSeek` are two-file packages,
identical in shape to `OpenRouterProviderPackage`/`OllamaProviderPackage`/`LlamaCppProviderPackage`:
`SupportedProviderIds`, and `Create` delegates straight to the existing
`OpenAiCompatibleChatModel` — both providers speak the same OpenAI Chat Completions schema that
adapter already handles, so there was nothing new to write beyond the package boundary itself
(`PackageId`/`IModelProviderPackage` registration). No new logic, no new tests — same precedent as
every other thin wrapper package, which is why none of those have dedicated test files either.

### Anthropic — the one native adapter (`bOps.Packages.Providers.Anthropic`)

`AnthropicChatModel : IChatModel` talks to the Messages API directly (`POST {BaseUrl}/v1/messages`,
`x-api-key` + `anthropic-version: 2023-06-01` headers) — real new translation logic, not a wrapper,
because Anthropic's wire shape differs enough from OpenAI's that sharing `OpenAiCompatibleChatModel`
would mean bending that adapter around a second protocol rather than writing a second adapter:

- **System prompt is a top-level `system` string**, never a message — `ModelRequest.History` never
  carries a `ChatRole.System` turn in practice (the runtime keeps `SystemPrompt` and `History`
  separate already), so `MapRole` throws if one ever did, rather than silently mis-translating it.
- **Content is a tagged-union block array**, not a flat string: `text`, `tool_use` (native tool
  calls — arguments arrive as a real JSON object, `input`, not a JSON-encoded string like OpenAI's
  `arguments`), and `tool_result` (Anthropic has no `"tool"` role; a tool's result is a
  `tool_result` block inside a `"user"` message).
- **Consecutive same-role turns are merged into one message.** The Messages API rejects two
  consecutive messages with the same role, but the runtime's own loop (rule D-007) can append
  several consecutive `ChatRole.Tool` turns back to back — the executed call's result, then one
  "not executed" turn per tool call the runtime did not run. `BuildMessages` merges these into a
  single `"user"` message with multiple `tool_result` blocks; this is the one piece of translation
  logic that has no OpenAI-adapter equivalent, because OpenAI's flat message list has no such
  same-role-adjacency restriction.
- **`max_tokens` is required on every Anthropic request; `ChatModelOptions` has no such field.**
  `bOps.Abstractions` stays provider-detail-free by design — adding an Anthropic-specific field
  there would need its own ADR for a single provider's requirement. Fixed at a constant
  (`MaxTokens = 4096` in `AnthropicChatModel`), not configurable yet — flagged in code, not hidden.
- **The `SupportsNativeToolCalling` fallback (plan §3.1.1) is implemented too**, mirroring
  `OpenAiCompatibleChatModel`'s JSON-in-prompt strategy exactly (tool list embedded in the system
  prompt as text, one retry on malformed JSON) — every other provider honors this option, so
  Anthropic does too rather than silently ignoring a documented `ChatModelOptions` field.

### Tests

`bOps.Packages.Providers.Anthropic.Tests` — 8 contract tests against a `StubHttpMessageHandler`
(records every request, replays canned responses; agentic/04-testing-rules.md's "contract tests
against recorded HTTP fixtures" for provider packages, no live call): a text-only response parses
as final; the system prompt is sent separately from messages with the right auth headers and
endpoint; a `tool_use` block parses into a `ModelToolCall` with real (not JSON-string) arguments;
a `ToolManifest` translates into Anthropic's `input_schema` shape; consecutive `ChatRole.Tool`
turns merge into one `user` message with multiple `tool_result` blocks (the one Anthropic-specific
translation rule); the fallback path parses a JSON-in-text reply; a non-success HTTP status and a
malformed response body both throw `ModelProtocolException`.

**No live-model smoke test this session** — same `appsettings.json` constraint as every prior
handoff (`ApiKey` stays `""` in the repo). `dotnet build` on `bOps.Cli` with the three new
provider packages wired in was verified to compile and link cleanly; actually resolving
`"Anthropic"`/`"OpenAI"`/`"DeepSeek"` through `ChatModelRegistry` at runtime was not exercised
end-to-end (would need real credentials, out of scope for this session per the standing
constraint).

## Design choices worth knowing before extending this further

- **OpenAI's `Provider` id is `"OpenAI"`, not `"OpenAi"`** — matches the casing convention the
  roadmap line and `06-decisions.md`'s table already use elsewhere (`"OpenRouter"`, `"Ollama"`);
  the C# type/namespace names use `OpenAi` (Pascal-cased two-letter acronym, the repo's existing
  convention — see `OpenAiCompatibleChatModel` itself) — the two are deliberately different
  strings for different purposes (wire-protocol id vs. .NET identifier), not an inconsistency.
- **`bOps.Packages.Providers.Anthropic` does not depend on `bOps.Packages.Providers.OpenAiCompatible`**
  at all — only on `bOps.Abstractions`, same dependency shape as the shared adapter package itself.
  There was nothing to share; duplicating the small `BuildParameterSchema`/`MapJsonSchemaType`
  helpers (~20 lines) was simpler and clearer than introducing a cross-package dependency or a
  third shared-helpers package for two nearly-identical private methods.
- **`ContentBlockDto` is one flat DTO for all three Anthropic content-block types** (`text`,
  `tool_use`, `tool_result`), every field but `type` optional, rather than three separate records —
  `System.Text.Json` source generation has no clean polymorphic-by-sibling-field support as simple
  as this for a wire format this small.

## What V0.8 deliberately does NOT have yet

- **No live-model smoke test against a real Anthropic/OpenAI/DeepSeek endpoint.** Blocked on the
  same `appsettings.json` constraint as every prior handoff, unrelated to this version's own work.
- **`AnthropicChatModel.MaxTokens` is a hardcoded constant**, not sourced from configuration. Would
  need a `ChatModelOptions` change (its own ADR, since that type is in `bOps.Abstractions`) to make
  configurable per-provider.
- **No streaming.** Still deferred to Phase 2's `IStreamingChatModel : IChatModel` per D-007 —
  unrelated to which providers exist, not started for any provider yet.
- **No prompt caching, extended thinking, or other Anthropic-specific request options.** Not named
  in this version's roadmap line; the adapter implements exactly the request/response shape the
  existing `IChatModel` contract needs, nothing Anthropic-specific beyond that.
- **`bOps.AppHost` still has not been run this session** — carried forward, unrelated to V0.8.
- **No dynamic plugin loading** — V0.10, unrelated.
- **No CLI command to run `AuditChainVerifier` on demand** — carried forward again, still small,
  still not done.

## Next steps

V0.8 is done: OpenAI and DeepSeek are real, working provider packages (thin by construction, since
the shared adapter already covers their wire protocol), and Anthropic is a genuinely new native
adapter with its own translation logic and its own test coverage — verified against recorded HTTP
fixtures, not just asserted to compile.

**Before any further roadmap work**, the committed API key finding from V0.7 is still open. See
the state section above.

**V0.9 — "`bOps.Api` + Angular UI" — has not been started.** Per the scope-discipline rule, the
next session should begin by reading `agentic/00-project-spec.md`'s roadmap entry for V0.9 and
`06-decisions.md`/`01-architecture-rules.md` for anything already settled about the API/UI split
before writing code. This is a substantially larger version than V0.1–V0.8 (a new host, a new
client, likely new contract surface for exposing tasks/approvals over HTTP) — worth explicitly
confirming scope with the operator before starting, per the standing "stop and ask" rule for
anything that looks like it might pull in later-roadmap work.

Two smaller, non-urgent items carried forward again from every prior handoff:

1. The six pre-existing ADRs `agentic/05-workflow.md` lists as "the first ADRs to exist" (0001,
   0002, 0005, 0006, 0011, 0012) are still unwritten. ADR-0005 in particular ("one shared
   OpenAI-compatible adapter, with Anthropic as the only native adapter") is now the decision this
   very version implements — still worth writing up formally, since three more provider packages
   just leaned on it without its own record existing yet.
2. No CLI subcommand runs `AuditChainVerifier`. Small, real, not done.
