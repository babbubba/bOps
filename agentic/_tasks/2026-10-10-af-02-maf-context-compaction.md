# AF-02 — MAF context mapping, model profiles and compaction

| | |
|---|---|
| Governing ADR | [ADR-0052](../../docs/architecture/adr/0052-microsoft-agent-framework-bops-boundary.md), D-048 |
| Status | **Implemented locally — validation complete; not pushed or merged** |
| Depends on | AF-01 complete |
| Blocks | AF-03, AF-05 |
| Effort | **medium** |
| Primary risk | losing bOps execution/evidence semantics while compacting history |

## Objective

Make Microsoft Agent Framework the model-facing historical-context implementation while leaving persisted task/evidence state unchanged.

At completion:

- bOps history maps losslessly to MAF message primitives;
- current execution authority is explicitly protected from historical compaction;
- context capacity is model/profile aware;
- deterministic MAF compaction is active;
- `BoundedHistory` remains only as temporary migration/regression reference, not the primary architecture;
- no summarization inference is enabled yet.

## Inputs

Read:

- ADR-0052 §§6–11;
- `src/core/bOps.Runtime/BoundedHistory.cs`;
- `src/core/bOps.Runtime/AgentRunner.cs` locations using `BoundedHistory.Build`;
- `src/core/bOps.Runtime/AgentRunnerOptions.cs`;
- `src/core/bOps.Runtime/EvidenceRead.cs`;
- ADR-0014 HARDEN-8 amendment;
- ADR-0042 evidence grounding/limitations;
- ADR-0046 exact step-scoped tool routing;
- AF-01 broker/MAF integration code.

## MAF message mapper

Implement one mapper in the isolated MAF integration assembly.

It must support the bOps `ChatTurn` shapes actually used in production:

- user;
- system where applicable to mapped historical turns;
- assistant text;
- assistant with one tool call;
- assistant with multiple tool calls;
- tool result matched by call id;
- existing “not executed” result turns produced for extra first-call-wins responses.

Required invariants:

1. original ordering is preserved;
2. model tool call IDs are stable;
3. arguments round-trip structurally;
4. multiple calls emitted in one assistant message remain grouped;
5. call + result relationships remain valid atomic MAF message groups;
6. Unicode/surrogate content round-trips;
7. mapping never changes executed/unexecuted meaning.

Create direct mapper round-trip/semantic tests. Do not rely only on an end-to-end model fixture.

## Protected current context

Refactor context construction into two conceptual sets:

### Protected/non-compactable

- standing security/system instructions;
- current goal/user input needed for the call;
- current plan revision;
- current planned step;
- `ExpectedTool`;
- correction state;
- EvidenceGrounding;
- EvidenceLimitations;
- mutation-reconciliation notices;
- any runtime-authored control state required for the current logical call.

### Historical/compactable

Prior conversation and tool-result history not required as current authority.

Do not ask MAF to infer which group is authoritative. bOps classifies it before compaction.

If an existing HARDEN-8 construct mixes current authority and old history in one generated block, split the construction rather than marking the entire block compactable.

## ModelContextProfile

Introduce a runtime/host-owned profile, not an `Abstractions` SDK type.

Minimum semantics:

- provider id;
- requested model id;
- max context tokens, nullable/unknown;
- reserved output tokens, nullable only if a safe configured fallback is explicit;
- source/authority metadata sufficient for telemetry/debugging.

Resolution rules:

- exact configured provider+model profile wins;
- do not infer from provider family;
- `openrouter/free` is unknown unless a conservative guaranteed profile is explicitly configured;
- each pinned fallback candidate can resolve its own profile;
- when fallback advances, the next projection uses the new candidate profile;
- invalid values fail configuration validation; do not clamp.

Do not implement network discovery of model metadata in this task unless an existing provider already exposes trustworthy static metadata through a reviewed contract.

## History budget

Calculate a model-facing history budget after reserving room for non-history request material.

The budget logic must account separately for:

- max context;
- reserved output;
- system prompt;
- exact offered tool schema(s);
- protected runtime context;
- safety reserve;
- compactable history.

Store threshold/reserve values in configuration with validation. Do not hard-code MAF sample percentages as architecture.

Unknown model context capacity must not be replaced with an optimistic guessed value.

## Deterministic MAF compaction

Enable MAF context compaction using deterministic strategies first:

1. tool-result compaction;
2. sliding-window compaction;
3. truncation only as final backstop.

Do **not** enable LLM summarization in AF-02.

Prefer `CompactionProvider.CompactAsync` or the current supported ad-hoc MAF compaction surface so bOps does not need to adopt `AgentSession`/`ChatClientAgent` for this packet.

Remember that current MAF ad-hoc token counts may be estimated rather than exact. Record both estimated projection metrics and provider-reported usage when available.

## Persistence invariant

Projection is ephemeral.

AF-02 must not rewrite:

- `TaskState.Steps`;
- raw `ToolCallResult.Output`;
- persisted `Observation`;
- plans;
- EvidenceFacts;
- verification state;
- mutation journal;
- audit history.

A resume rebuilds candidate context from persisted bOps state.

## Legacy HARDEN-8

During AF-02 keep enough legacy code/tests to compare semantics and provide a temporary fail-safe while the central context path changes.

Do not create a public permanent “choose any context engine” abstraction merely for this migration.

The target at AF-05 is removal of obsolete custom model-facing compaction code once real regression proves it unnecessary.

## Telemetry

Add context metrics/spans for:

- profile known/unknown and profile source;
- historical groups/messages before and after;
- estimated tokens/characters before and after;
- protected-context estimate;
- tool-schema estimate;
- selected deterministic strategies;
- groups/results removed;
- provider-reported prompt tokens after the call;
- estimate delta where both values exist;
- context-overflow retries.

Never put raw context/tool result text in telemetry attributes.

## Tests

Required focused cases:

- tool call + tool result are never split;
- multiple tool calls and their results group correctly;
- current plan/expected tool remains intact after aggressive compaction;
- EvidenceGrounding/Limitations remain intact;
- mutation reconciliation notice survives;
- persisted state byte/JSON-equivalent before and after projection;
- resume reconstructs authoritative candidate history from persisted state;
- profile resolution changes on fallback candidate;
- unknown router profile remains unknown;
- fixed/protected context larger than capacity fails safely rather than truncating authority;
- deterministic compaction cannot widen `AvailableTools`;
- context overflow still uses bounded recovery, not infinite retry.

Retain existing HARDEN-8/Evidence control-plane tests until AF-04/AF-05 deliberately amend them.

## Non-goals

Do not:

- enable LLM summarization;
- change EvidenceRead exhaustion semantics yet;
- remove raw evidence;
- replace TaskState with MAF sessions;
- use Harness;
- expose MAF tools;
- add long-term semantic memory;
- delete `BoundedHistory` before AF-05.

## Validation

Run focused Runtime/AgentFramework tests, then:

```bash
dotnet build bOps.slnx --configuration Release
dotnet test bOps.slnx --configuration Release --no-build --filter "Category!=LiveModel"
```

## Definition of Done

- production model-facing historical context uses the MAF context layer;
- protected bOps authority context is outside generic compaction decisions;
- model context profile/budget is explicit and validated;
- deterministic compaction is tested;
- raw persistence/evidence is unchanged;
- tool surface is unchanged;
- no summarizer model call exists yet;
- full non-live regression is green.

## Stop conditions

Stop if MAF compaction cannot preserve bOps tool-call/result relationships, if the only solution requires MAF types in `bOps.Abstractions`, or if a proposed optimization would delete/alter authoritative persisted evidence.

## Implementation evidence (2026-10-11)

### Delivered structure

- Implementation commit: `25774e5` (`feat: add MAF context compaction`).
- Added the strict `MafChatTurnMapper` inside `bOps.AgentFramework`. It round-trips the production bOps roles, null/empty/Unicode text, structurally equivalent JSON arguments, multi-call assistant messages and call-id-matched result turns.
- Added runtime-owned `ModelContextRequest`, profile/options, projection result/statistics and the narrow `IModelContextProjector` boundary. No MAF type entered `bOps.Abstractions` or `bOps.Runtime`.
- `AgentRunner` now classifies current authority before projection: system/security rules, goal, current plan and step routing, correction state, EvidenceGrounding, EvidenceLimitations, reconciliation notices and logical-call continuations remain outside compactable historical turns.
- `MafContextProjector` uses the MAF 1.24.0 ad-hoc `CompactionProvider.CompactAsync` path with `ToolResultCompactionStrategy`, `SlidingWindowCompactionStrategy`, then `TruncationCompactionStrategy`. No summarization strategy or summarizer inference exists in AF-02.
- The tool-result formatter emits only runtime-authored tool name/status metadata. It never copies raw result data into assistant text, so prompt injection inside a historical tool result is either retained as structured `Tool` data or omitted by deterministic compaction.
- `ModelInvocationBroker` projects immediately before each actual provider attempt. After fallback advances, the next loop reads the new descriptor and resolves/projects with that exact candidate profile; retry, fallback pinning, audit and accounting decisions are otherwise unchanged.
- CLI, API singleton composition and API execution-scoped composition activate the projector. `BoundedHistory` remains the conservative request when no exact profile exists and remains available for AF-05 comparison/removal.

### Configuration semantics

Profiles bind under `Agent:Context:Profiles`; lookup is exact provider id + requested model id. No provider-family inference exists, and `openrouter/free` remains unknown unless explicitly configured. Unknown capacity uses the existing bounded projection instead of guessing a token window.

```json
{
  "Agent": {
    "Context": {
      "SafetyReserveTokens": 1024,
      "MinimumPreservedToolGroups": 2,
      "MinimumPreservedTurns": 1,
      "Profiles": [
        {
          "Provider": "configured-provider-id",
          "Model": "exact-model-id",
          "MaxContextTokens": 128000,
          "ReservedOutputTokens": 8192
        }
      ]
    }
  }
}
```

Invalid, duplicate or impossible profiles fail `AgentRunnerOptions.Validate`; values are never clamped. The computed history allowance is `max context - reserved output - system/protected estimate - exact tool-schema estimate - safety reserve`. If protected/fixed material alone cannot fit, projection throws a classifiable `ContextOverflow` without truncating authority or calling the provider.

### Persistence, tools and telemetry

- Projection is ephemeral. No `TaskState`, `PlanStep`, raw `ToolCallResult.Output`, `Observation`, plan, evidence fact, verification state, mutation journal or audit schema was changed.
- Offered tools are passed through unchanged. The MAF layer neither registers nor executes operational tools and cannot widen `AvailableTools`.
- `bops.model_context` activities and context histograms record provider/model, known/unknown profile source, configured capacities, protected/tool/history estimates, message/character counts, selected deterministic strategies, compacted/dropped counts, provider prompt tokens and estimate delta. No prompt, argument or tool-result content is emitted.

### Tests and guards

- Direct mapper tests cover role/order, null and empty content, Unicode, nested JSON arguments, multiple calls and matched results.
- Deterministic compaction tests cover atomic multi-call/result groups, preserved executed/unexecuted meaning, prompt injection in raw results, sliding-window and truncation backstops, protected authority/reconciliation, unchanged raw candidate history and unchanged tool surface.
- Profile tests cover invalid/duplicate configuration, exact candidate-specific selection, `openrouter/free` remaining unknown, protected-context overflow, deterministic repeatability and fallback re-projection from candidate A to candidate B.
- Existing HARDEN-8, EvidenceRead, evidence grounding/limitations, resume, provider failure, fallback and architecture suites remain in place and green.

### Verification

- `dotnet test tests/bOps.AgentFramework.Tests/bOps.AgentFramework.Tests.csproj --configuration Release --no-restore`: **11 passed, 0 failed**.
- `dotnet build bOps.slnx --configuration Release --no-restore`: **passed with 0 warnings and 0 errors**.
- `dotnet test bOps.slnx --configuration Release --no-build --filter "Category!=LiveModel"`: **all projects passed, 0 failures**; existing platform/environment-dependent tests were skipped.
- Representative totals: Runtime **2050 passed**; API **501 passed**; CLI **124 passed**; architecture **169 passed**; AgentFramework **11 passed**.

### Reviewed MAF surface and deviations

- The implementation was compiled and tested against the pinned `Microsoft.Agents.AI` **1.24.0** package. Its compaction index forms atomic tool-call/result groups and the three required deterministic strategies are available.
- `CompactionMessageIndex.Create` is not public in the compiled 1.24.0 surface even though it appears in generated API XML. AF-02 therefore uses the supported public ad-hoc `CompactionProvider.CompactAsync` entry point, as allowed by this packet.
- MAF's experimental diagnostic `MAAI001` is suppressed only in the isolated `bOps.AgentFramework` project, with an explanatory project comment. No experimental MAF surface leaks across that boundary.
- MAF compaction needs a user-turn anchor to index bOps's assistant/tool-only historical slice. AF-02 supplies a fixed runtime-authored marker solely to the in-memory compaction call and removes it before provider projection; the actual goal remains separately protected and unchanged.
- No stop condition was reached: MAF preserved atomic relationships; no persisted evidence or `bOps.Abstractions` public shape changed; provider routing/fallback semantics remained runtime-owned.
