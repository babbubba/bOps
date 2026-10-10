# AF-01 — Microsoft Agent Framework foundation and governed model invocation

| | |
|---|---|
| Governing ADR | [ADR-0052](../../docs/architecture/adr/0052-microsoft-agent-framework-bops-boundary.md), Accepted 2026-10-10, D-048 |
| Status | **Planned** |
| Depends on | ADR-0052/docs branch merged |
| Blocks | AF-02, AF-03 |
| Effort | **medium** |
| Primary risk | changing model retry/fallback/audit semantics while extracting them |

## Objective

Create the minimum stable MAF foundation **without changing agent behavior**.

At the end of AF-01:

1. MAF exists in one isolated implementation project with an exact pinned package version;
2. the current behavior of `AgentRunner.CallModelAsync` has been extracted into one bOps-owned governed model-invocation component;
3. all ordinary planning/replanning/step/final model calls still behave as before;
4. an MAF `IChatClient` bridge can invoke a bOps model only through that governed component;
5. no MAF type enters `bOps.Abstractions`.

AF-01 is plumbing. It does **not** enable MAF compaction or summarization yet.

## Existing code to preserve

Read before changing code:

- `src/core/bOps.Runtime/AgentRunner.cs` — locate `CallModelAsync`; it currently owns retries, model-attempt timeout, logical-call budget, failure classification, audit/model-call recording, cumulative token accounting and fallback interaction.
- `src/core/bOps.Hosting/FallbackChatModel.cs` — execution-scoped ordered/sticky fallback.
- `src/core/bOps.Api/ExecutionRunnerFactory.cs` — execution-scoped model construction and credential refresh.
- `src/core/bOps.Api/Program.cs` and `src/core/bOps.Cli/Program.cs` — composition roots.
- `src/core/bOps.Abstractions/Model.cs` — project-owned model contract; **do not replace it**.
- ADR-0039 and ADR-0045 — authoritative retry/fallback behavior.

## Required project structure

Add one isolated project under `src/core` or an equivalent integration folder chosen consistently with the solution layout, preferably:

`src/core/bOps.AgentFramework/bOps.AgentFramework.csproj`

The exact name may differ if repository naming rules make another name more consistent. Do not create multiple MAF integration projects in this packet.

References:

```text
bOps.AgentFramework -> bOps.Runtime + bOps.Abstractions
API/CLI             -> bOps.AgentFramework at composition time
bOps.Runtime         -> must not reference Microsoft.Agents.AI directly
bOps.Abstractions    -> unchanged dependency surface
```

Pin the exact reviewed `Microsoft.Agents.AI` package version. If a version newer than ADR-0052's research baseline 1.24.0 is used, record the exact version and verify that the required compaction/`IChatClient` APIs still exist before coding further.

Any experimental analyzer suppression must be local to the integration project and documented in `docs/architecture/suppressions.md`; do not disable experimental diagnostics solution-wide.

## Governed model invocation extraction

Introduce a runtime-owned service conceptually named `IModelInvocationBroker`. Naming may vary only if an existing runtime naming convention clearly fits better.

The broker must own the behavior currently concentrated in `AgentRunner.CallModelAsync`:

- one provider adapter invocation per model attempt;
- runtime retry classification;
- `ModelCallMaxAttempts`;
- `ModelCallAttemptTimeout`;
- `ModelCallBudget`;
- bounded retry delay and `Retry-After`;
- provider fallback handshake through `IFallbackChatModelControl`;
- one `ModelCallAuditEvent` per attempt;
- one `ModelCallRecord` per attempt;
- request/response body bounding;
- actual provider/model metadata;
- cumulative task token accounting;
- cancellation semantics;
- malformed-response callback behavior where currently required.

Do **not** make a new public `bOps.Abstractions` interface for this packet. The broker is runtime/integration infrastructure.

The broker must accept enough execution context to preserve the exact existing audit fields, including task id, step index, actor and delegation correlation where applicable.

## AgentRunner migration

Replace internal calls to the old `CallModelAsync` implementation with the broker.

Do this as a behavior-preserving refactor:

- initial plan;
- replan;
- normal step;
- final/evidence disclosure/grounding calls that use the common path.

When complete, `AgentRunner` must not contain a second retry/fallback implementation.

A compatibility private wrapper named `CallModelAsync` may temporarily delegate to the broker during the same packet if that makes the diff safer, but it must contain no retry/fallback logic itself.

## MAF IChatClient bridge

In the MAF integration project, implement an adapter from MAF `IChatClient` semantics to the bOps broker.

For AF-01 the bridge is not allowed to invent tool execution or context compaction. It exists only so later MAF components can issue a governed inference.

The bridge must:

- translate MAF messages/options needed by its caller into a bOps `ModelRequest`;
- call the broker, never `IChatModel.CompleteAsync` directly;
- translate `ModelResponse` back to the MAF response shape required by the supported API;
- preserve cancellation;
- not retain API keys;
- not implement its own retries;
- not select providers independently.

If the current MAF interface requires data that bOps cannot represent losslessly, stop and document the exact mismatch. Do not widen `bOps.Abstractions` speculatively.

## Architecture guards

Add architecture tests that fail if:

1. `bOps.Abstractions.csproj` references `Microsoft.Agents.AI` or `Microsoft.Extensions.AI`;
2. a MAF namespace appears in public `bOps.Abstractions` source;
3. the MAF integration project is missing from `bOps.slnx`;
4. production code outside the governed broker/provider-adapter boundary begins calling `IChatModel.CompleteAsync` directly.

The fourth test may need an explicit allowlist for provider implementations and test doubles. Keep that allowlist narrow and source-path based.

## Tests

Test first around the broker extraction.

At minimum preserve/add tests for:

- successful single-attempt call;
- transient failure then success;
- rate limit + allowed `Retry-After`;
- `Retry-After` too long for remaining budget;
- authentication/quota/invalid-request non-retry;
- attempt timeout distinct from caller cancellation;
- logical-call budget exhaustion;
- fallback candidate advance and sticky candidate;
- fallback pin persistence callback;
- context overflow classification unchanged;
- malformed-response callback behavior unchanged;
- every failed and successful attempt audited;
- every attempt recorded in task model-call records;
- cumulative token accounting unchanged;
- delegation audit correlation unchanged;
- MAF bridge cannot cause an unaudited direct model call.

Run existing `ModelCallFailureContainmentTests`, `ModelCallRecordingTests`, `FallbackHandshakeTests`, provider wire E2E fixtures and all Runtime/API tests affected by constructor changes.

## Non-goals

Do not in AF-01:

- enable MAF compaction;
- add `SummarizationCompactionStrategy`;
- change `BoundedHistory`;
- change EvidenceRead;
- replace `IChatModel`;
- change provider wire formats;
- adopt Harness;
- change TaskState persistence;
- change policy/approval/tool execution;
- add vector memory.

## Validation

Required before marking complete:

```bash
dotnet restore bOps.slnx --locked-mode
dotnet build bOps.slnx --configuration Release --no-restore
dotnet test bOps.slnx --configuration Release --no-build --filter "Category!=LiveModel"
```

Also run focused broker/integration tests independently so failures are attributable.

If adding the NuGet dependency changes lock files, verify only the expected projects gained MAF transitive dependencies.

## Definition of Done

AF-01 is complete only when:

- MAF package is isolated and version-pinned;
- `bOps.Abstractions` public/dependency surface is unchanged;
- all production task inference goes through one governed broker;
- retry/fallback/audit/token behavior is equivalent to the pre-extraction baseline;
- MAF has a tested `IChatClient` bridge over that broker;
- no MAF tool/context feature is active yet;
- Release build has zero warnings/errors;
- full non-live suite is green, subject only to separately recorded pre-existing known failures.

## Stop conditions

Stop and report rather than inventing a design if:

- current MAF `IChatClient` cannot be adapted without exposing MAF types in `bOps.Abstractions`;
- the extraction would require changing persisted `TaskState` or audit contract shapes;
- an MAF package upgrade materially changes the APIs researched by ADR-0052;
- a test reveals existing retry/fallback behavior is ambiguous rather than simply refactorable.
