# ADR-0038 — Provider wire contract: tool-name aliasing, valid tool-call history, and native-tool-free planning

Status: Accepted (2026-09-25, operator decision, with the amendments recorded below)

Amends ADR-0014 (what the plan and replan calls send). Carries the ADR-0022 note for three additive
`bOps.Abstractions` members (`ModelToolCall.ToolNameError`, `ModelToolCall.ArgumentsError`,
`PlanStep.UnexecutedToolCalls`). Governs HARDEN-1 of the V1.3.x reliability train
(`agentic/_plans/2026-09-25-v1.3x-reliability-hardening.md`, findings C-01, C-02, C-13, C-14, H-5).

## Context

A real task failed with `HTTP 400` from an OpenRouter upstream: *"Function at index 0 has an invalid
name: "fs.size". Only a-z, A-Z, 0-9, underscores, and dashes are allowed."* bOps sends canonical tool
names verbatim as native function names. Canonical names are lowercase dotted (`fs.size`,
`system.crashes`) and some already contain `_` and `-` (`fs.delete_tree`, `linux.dpkg-status`). The OpenAI
function-name contract is `^[a-zA-Z0-9_-]{1,64}$`; whether a given upstream enforces it depends on which
backend a router picks, so the same request succeeds or fails non-deterministically.

Reading the adapters and the runtime for this ADR found four further wire-contract defects on the same
path:

1. **The failing call was the replan call.** `CreatePlanAsync` and `ReplanAsync` pass
   `ToolViewFor(delegation)` (all ~100 tools, `tool_choice: auto`) even though ADR-0014 already states the
   plan call is "no tool-calling". A model that answers a replan with a tool call produces an unusable
   reply, and it is the plan call that carried the ~47 KB tool surface into the rejected request.
2. **Invalid multi-call history.** `AgentRunner.ContinueAsync` records `[primaryCall]` in the assistant
   turn but adds a tool-result turn for every unexecuted call, whose `tool_call_id` never appears in the
   preceding assistant turn (`AgentRunner.cs:336-343`). Strict providers reject orphaned tool results.
3. **Malformed argument JSON becomes `{}`.** `OpenAiCompatibleChatModel.ParseArguments` returns
   `ToolArguments.Empty` on `JsonException` and on any non-object JSON, so a tool whose parameters are all
   optional runs with defaults the model never requested.
4. **Explicit `null` members.** The OpenAI-compatible request DTOs serialize `content`, `tool_calls` and
   `tool_call_id` as `null` on every message that does not carry them (hypothesis H-5).

## Decision

### 1. Wire-safe tool-name aliasing lives at the provider boundary, as a closed mapping

Canonical names never change. They stay the identity of a tool in `bOps.Runtime`, `bOps.Policy`,
`bOps.Audit`, `PlanStep`, plans, the UI and logs. An **alias** exists only inside one provider adapter, for
the duration of one request, and is invisible to everything above `IChatModel`.

For each request an adapter builds one `ToolWireNames` map over the **union** of the offered tools and the
tool names that appear in the history's assistant turns, so a call recorded in an earlier step is aliased
consistently even when the current request no longer offers that tool. The mapping is a pure function of
that set of canonical names. Names are processed in ordinal order and no randomness, hash code or process
state takes part:

1. A name that already matches `^[A-Za-z0-9_-]{1,64}$` is its own alias (an explicit identity mapping in
   the map).
2. Any other name gets the **readable alias**: every character outside `[A-Za-z0-9_-]` becomes `_`
   (`fs.size` becomes `fs_size`).
3. If a readable alias equals any other name's alias in the set, every non-verbatim name in that collision
   group uses the **injective alias** instead: characters in `[A-Za-z0-9-]` are kept; every other character,
   including `_` and `.`, is written as `_` plus the two uppercase hex digits of each UTF-8 byte of that
   character (`a.b` becomes `a_2Eb`, `fs.delete_tree` becomes `fs_2Edelete_5Ftree`). This is a reversible
   escape, so two distinct non-verbatim names can never receive the same injective alias. A name that is
   already wire-safe keeps itself, so `a.b` and `a_b` in one request become `a_2Eb` and `a_b`.
4. The finished map is verified: every alias matches the regex and is at most 64 characters, and all
   aliases are distinct. Otherwise building the request **fails closed** with a `ModelProtocolException` that
   names the canonical names involved; nothing is sent. This covers the residual pathological cases (an
   over-long or empty name, or a tool literally named `a_2Eb` colliding with an injective alias).

**Reverse mapping is exact and closed (amendment 1).** A tool-call name in a provider response is looked up
**exactly** (ordinal) in that request's alias map. A hit is replaced by the canonical name **before** the
`ModelToolCall` leaves the adapter, so the runtime, policy and audit only ever see canonical names. A name
that is **not** in the map, including a string that happens to equal a real canonical tool name, is **never**
passed through as a tool name. The adapter returns a `ModelToolCall` whose `ToolNameError` is set and the
runtime rejects it as `UnknownTool` without resolving or executing anything. A model cannot bypass the alias
contract by inventing a canonical-looking name. Wire-safe canonical names remain valid only because they
were put in the request's map as identity mappings.

The JSON-schema fallback path (`SupportsNativeToolCalling = false`) has no wire names, but the same closed
rule holds: a tool name in the model's JSON reply is valid only if it is exactly the canonical name of a tool
offered in that request.

**Where the code lives (amendment 4).** One `internal` source file, `Wire/ToolWireNames.cs`, owned by
`bOps.Packages.Providers.OpenAiCompatible` (the shared adapter of ADR-0005) and linked into
`bOps.Packages.Providers.Anthropic` with a `<Compile Include>` item. Both adapters therefore consume exactly
one implementation. No new public API, no new project or package (there is no shared provider project that
would be a better home), no change to `bOps.Abstractions` for aliasing, no lockfile change. The runtime and
the other core assemblies contain no aliasing code and name no provider (CLAUDE.md rule 1). Every future
adapter that exposes native tool calling must build its tool definitions, its history tool calls and its
response names through this same map.

### 2. Null omission

`OpenAiJsonContext` gets `DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull`, and so does the
Anthropic context (its request DTOs emit `null` for the unused members of a content block). The requests
therefore omit `content`, `tool_calls`, `tool_call_id`, `tools`, `tool_choice` and the unused block members
whenever they are semantically absent. This is per spec: OpenAI `content` is required only when the message
has no `tool_calls`. The setting is scoped to those two serializer contexts, so no other JSON in the product
changes.

### 3. Valid, truthful, persisted history when the model emits several tool calls (amendment 3)

The runtime still **executes one tool call per step** (D-007; unchanged). What changes is the record of the
model's turn:

- The assistant turn records **every** call the model emitted, in order.
- The first call is executed and its observation is its tool result.
- Every other call receives a tool-result turn with the existing, truthful text *"Not executed: only one
  tool call is executed per step. Ask again next step if still needed."*, wrapped as tool output like any
  other observation. It is never represented as a success.

Each `tool_call_id` in a tool-result turn therefore refers to a call in the immediately preceding assistant
turn.

**Persistence.** `PlanStep` gains one additive, optional `init` property `UnexecutedToolCalls`
(`IReadOnlyList<ModelToolCall>?`, default `null`): the calls the model emitted in the same turn after
`ToolCall` and that bOps deliberately did not execute, in emission order. `ToolCall` stays the executed
call. The persisted `PlanStep` therefore contains everything needed to rebuild the model's turn (the calls
`ToolCall` then `UnexecutedToolCalls`, the executed result, and the fixed "not executed" text for the rest)
without reading any provider-specific `ResponseJson`. `RebuildHistory` for a resumed task builds exactly the
same turns `ContinueAsync` builds live. The property is provider-neutral, deterministic, serialized in the
task store, and absent (`null`) in every row written before this change, which therefore deserialize
unchanged. It is an `init` property, not a constructor parameter, so existing binaries and call sites keep
working. Unexecuted calls are never executed, never audited as tool calls and never invent a result; they are
history, not actions.

### 4. Malformed argument JSON is a validation failure, never `{}` (amendment 2)

`ModelToolCall` gains additive, optional `init` properties `ArgumentsError` and `ToolNameError`
(`string?`, default `null`); they are the smallest provider-neutral way to carry "this call as received
cannot be honoured" through `ModelResponse` (ADR-0022 note: additive, binary-compatible).

For a provider whose arguments travel as a payload (the OpenAI schema's JSON-encoded string):

| Provider payload | Result |
|---|---|
| a JSON object, e.g. `"{}"` or `"{\"a\":1}"` | valid arguments (`"{}"` is a valid empty object) |
| the member is absent or JSON `null` (no payload) | an explicit empty argument object; the runtime's required-parameter validation still applies |
| `""` or whitespace only | **malformed**: `ArgumentsError` set |
| syntactically invalid JSON, e.g. `"{"` | **malformed**: `ArgumentsError` set |
| valid JSON that is not an object (array, string, number, `null` literal text) | **malformed**: `ArgumentsError` set |

A payload that is present but blank is never treated as "no arguments". A malformed call is returned with
`Arguments = ToolArguments.Empty` (a placeholder, never executed) and an `ArgumentsError` drawn from a fixed
vocabulary that contains no argument content and no request data. Before policy evaluation and before any
execution, the runtime treats a non-null `ArgumentsError` as a validation failure: it records the step with
`ToolCallResult.Failure(...)` and the model receives that as the observation, audited and recorded exactly
like the existing type-validation failures. Policy is not evaluated as if valid arguments had been
supplied. The same rule applies to the JSON-schema fallback path, where a non-object `arguments` value is
currently coerced to `{}`: it is treated as an unparseable reply and takes the existing single retry.

When such a call is echoed back in history, the arguments are sent as `{}` (Anthropic requires an object,
the OpenAI schema a string); the accompanying tool result states what was wrong.

### 5. Plan and replan calls carry no native tools (amends ADR-0014)

ADR-0014 specified the plan call as "a `ModelRequest` built specifically to elicit a JSON plan (no
tool-calling)". The implementation instead offered every tool with `tool_choice: auto`. This ADR makes the
code match the decision and closes the gap by construction:

- `CreatePlanAsync` and `ReplanAsync` (including their parse retries) build the `ModelRequest` with an
  **empty** `AvailableTools`, so adapters send no `tools` and no `tool_choice`, and the reply cannot be a
  native tool call.
- So that plans can still name `PlannedStep.ExpectedTool`, the planning instructions carry a compact text
  catalog of the tools the task may use (the same `ToolViewFor(delegation)` view, so a delegated task sees
  only its envelope), one line per tool in ordinal order: canonical name, risk level and the first sentence
  of the description, each line bounded in length. Parameter schemas are not included. The catalog is prompt
  text, not an executable surface.
- Step calls are unchanged: they offer native tools, aliased under Decision 1.

Nothing in ADR-0014's replan triggers, audit rules or plan schema changes. Context compaction and replan
digests remain HARDEN-8.

## Alternatives considered

- **Rename the canonical tools, or replace dots globally.** Rejected. Names are the identity used by
  policy, manifests, plugins, approvals and audit; changing them is a public contract change for a
  transport limitation, and plugin manifests would have to change too.
- **A naive `.` → `_` replacement with no collision handling.** Rejected: `a.b` and `a_b` collide and one
  tool could execute in place of another. Real names such as `fs.delete_tree` already contain `_`.
- **Fail the request on any collision, with no resolution.** Rejected as the only strategy: a single
  plugin tool that collides would make every model call fail. The chosen scheme resolves realistic
  collisions deterministically and fails closed only in the residual cases.
- **Hash-suffixed aliases.** Rejected: unreadable, and any truncation makes collision handling
  probabilistic. The hex escape is exact.
- **Aliasing in `bOps.Runtime`.** Rejected: wire restrictions are a per-provider fact and the core must not
  know providers.
- **Put the helper in `bOps.Abstractions`, a new provider-core package, or duplicate it per adapter.**
  Rejected: a permanent public type in a 1.0 security-boundary assembly (ADR-0022) or a new package for an
  internal concern, or two copies of collision logic that can drift. One linked internal file has none of
  those costs.
- **Pass an unmapped response name through and let the runtime's exact lookup decide.** Rejected by the
  operator: it lets a model bypass the closed alias contract with a canonical-looking name.
- **Treat a blank argument payload as "no arguments".** Rejected by the operator: a payload that is present
  but empty is malformed output and is not silently repaired.
- **Omit unexecuted calls from history, on resume or live.** Rejected by the operator: history must keep
  what the model actually emitted. Recording all calls and persisting the unexecuted ones on `PlanStep`
  keeps live and resumed history identical.
- **Reconstruct unexecuted calls from the stored `ResponseJson`.** Rejected: provider-specific, capped and
  possibly absent.
- **Execute all calls in a multi-call turn.** Rejected: out of scope and unsafe (D-007).
- **Throw `ModelProtocolException` on malformed arguments or an unknown name.** Rejected: it would fail the
  whole task for an error the model can correct on its next turn, and failure semantics belong to HARDEN-2.
- **Keep offering tools on plan calls but set `tool_choice: none`.** Rejected: the tool surface still costs
  ~47 KB and still exposes names on the wire.

## Consequences

- No request built by either adapter contains a function name outside `^[a-zA-Z0-9_-]{1,64}$`; the incident
  request class becomes impossible by construction.
- Policy, approval, verification, audit, plans and the UI keep canonical names. Aliases never cross
  `IChatModel`.
- Three additive optional properties are added and no constructor signature changes. Existing adapters,
  tests and stored tasks that do not set them behave as before; old task rows deserialize with the new
  properties `null` and need no migration.
- An unknown wire name or a malformed argument object is a recorded, audited, model-visible failed step that
  consumes a step, like any other rejected or invalid call. It is never repaired and never executed.
- Plan and replan requests are much smaller (the ~47 KB tool schema is replaced by a short text catalog) and
  can no longer return a native tool call.
- Live and resumed history are identical, including deliberately unexecuted calls.
- This ADR does not change failure classification, retry or timeout behaviour (HARDEN-2), tool parameter
  constraints (HARDEN-6), or context compaction (HARDEN-8).
- The deterministic strict fake provider test double introduced with HARDEN-1 becomes the required gate for
  any future change to an adapter's request shape.
