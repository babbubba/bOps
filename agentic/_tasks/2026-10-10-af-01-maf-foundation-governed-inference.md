# AF-01 — Microsoft Agent Framework foundation and governed model invocation

| | |
|---|---|
| Governing ADR | [ADR-0052](../../docs/architecture/adr/0052-microsoft-agent-framework-bops-boundary.md), Accepted 2026-10-10, D-048 |
| Status | **Completed** |
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

## Implementation evidence (2026-10-10)

### Delivered structure

- Added `src/core/bOps.AgentFramework`, the single isolated MAF integration assembly, and added it to `bOps.slnx`.
- Pinned `Microsoft.Agents.AI` to the exact NuGet range `[1.24.0]`; the resolved version is `1.24.0`.
- Added `IModelInvocationBroker`, `ModelInvocationBroker`, and `ModelInvocationContext` in `bOps.Runtime`. The broker owns the extracted attempt loop, timeout and logical-call budget, retry classification and delay, fallback handshake and pin persistence, attempt audit/records, bounded payloads, actual provider/model metadata, cancellation distinction, delegation correlation, token-accounting callback, and malformed-response callback.
- Reduced `AgentRunner.CallModelAsync` to context construction plus one broker invocation. All existing planning, replanning, step, correction, grounding, evidence-disclosure, and final calls continue to use that wrapper.
- Added `GovernedChatClient`, an implementation of the MAF/Microsoft.Extensions.AI `IChatClient` surface that maps only representable messages, function-call/result content, usage and cancellation. It invokes only `IModelInvocationBroker`, rejects MAF tool registration and unsupported stateful/generation options, and contains no credentials, retry, fallback, compaction, summarization, or tool executor.
- API and CLI reference the isolated integration project at composition time and now construct `ModelInvocationBroker` explicitly. `ExecutionRunnerFactory` still creates an execution-scoped model; its per-attempt adapter still resolves the current credential, preserving `task execution pin -> current credential resolution -> current/fallback candidate`.
- The bridge is deliberately not activated by an agent component in AF-01. AF-02 must create it with the task-scoped `ModelInvocationContext` when it introduces the first MAF component; constructing a context-free or singleton bridge here would be incorrect.

### Tests and guards

- Added direct broker tests for first-attempt success, token accounting, records/audit, and transient retry followed by success.
- Added bridge tests proving request/response/function mapping, cancellation propagation, broker-only invocation, and rejection of MAF-owned tools.
- Added architecture guards for framework-neutral `bOps.Abstractions`, solution/host references to the MAF project, and the narrow production allowlist for direct `IChatModel.CompleteAsync` calls.
- Existing Runtime coverage continues to verify rate-limit/`Retry-After`, remaining-budget refusal, non-retryable authentication/quota/invalid request, attempt timeout versus caller cancellation, logical-call budget, fallback advance/stickiness/pin persistence, context overflow, malformed-response correction, per-attempt audit/records, cumulative task/delegation tokens, and delegation correlation.

### Verification

- `dotnet test tests/bOps.Runtime.Tests/bOps.Runtime.Tests.csproj -c Release --no-restore`: **2050 passed, 0 failed**.
- `dotnet test tests/bOps.Api.Tests/bOps.Api.Tests.csproj -c Release --no-restore`: **501 passed, 0 failed**.
- `dotnet test tests/bOps.Cli.Tests/bOps.Cli.Tests.csproj -c Release --no-restore`: **124 passed, 0 failed**.
- Focused MAF integration tests: **2 passed, 0 failed**. Focused architecture guards: **3 passed, 0 failed**. Focused pre-extraction behavior suites: **72 passed, 0 failed**.
- `dotnet restore bOps.slnx --locked-mode`: **passed**.
- `dotnet build bOps.slnx --configuration Release --no-restore`: **passed with 0 warnings and 0 errors**.
- `dotnet test bOps.slnx --configuration Release --no-build --filter "Category!=LiveModel"`: **all projects passed, 0 failures**; only the existing environment/platform-dependent tests were skipped.
- Lock-file review: MAF dependencies occur only in the new integration project/test and in API/CLI composition consumers plus their test projects. `bOps.Abstractions`, Runtime, provider, and operational-package lock surfaces did not acquire MAF dependencies.

### Deviations and follow-up

- No material MAF API deviation from ADR-0052 was found. The implementation was compiled against the actual `1.24.0` package surface; no experimental-warning suppression was required.
- No persisted `TaskState`, public audit contract, provider wire format, policy, approval, entitlement, execution, mutation-journal, `BoundedHistory`, or EvidenceRead behavior changed.
- AF-02 must preserve the broker/context lifetime when it begins constructing MAF agents. It must not cache provider credentials, enable MAF tools, or add compaction/summarization as part of that composition work.
