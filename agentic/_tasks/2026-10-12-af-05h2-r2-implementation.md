# AF-05H2-R2 — early-final continuation after corrected validation failures

Candidate branch: `fix/af-05h2-r2-early-final-recovery`, worktree `C:\source\repos\babbubba\bOps-af05h2`.
Code base: `3d2afb7fdddd5b06347ed0dc0722877230006789` (AF-05H2-R1). No BENCH-00 file, collector, prompt, provider, model,
policy or packaging change. No live benchmark and no real LLM call were made.

## Verified root cause

BENCH-01 on R1 (B01 Completed 16,557 tokens; B02 Failed 171,255; B03 Failed 35,899). The structure of the two failed task
states, read locally from the private BENCH-01 archive and never copied, is:

| Task | Plan rev. 0 | History before the early final | Pending | Replan |
|---|---|---|---|---|
| B02 | 7 read steps, semantic contract | steps 3, 4, 5 at planned index 3 (`system.events`): `SemanticMismatch` + `Failure/Validation`, `ArgumentValidationFailure` + `Failure/Validation`, `Matched` + `Success`; all others `Matched` + `Success` | cursor 6, `storage.health` | one call, `Failure/MalformedResponse`, `length` |
| B03 | 3 read steps, semantic contract | step 1 at planned index 1: `SemanticMismatch` + `Failure/Validation`; step 2 `Matched` + `Success` | cursor 2, `system.info` | one call, `Failure/MalformedResponse`, `length` |

There was no plan revision between the failures and the early final. The checkpoint condition held, the position fold was
coherent with no spent correction, and the pending steps were unconditional reads. The only clause that refused was the
history check in R1 `EarlyFinalRecovery.SafePosition`:

```csharp
prefix.Any(step => step.ToolCall is not null
    && (step.Result is not { Outcome: ToolOutcome.Success } || manifestOf(step.ToolCall.ToolName) is not { Risk: RiskLevel.Read }))
```

The runtime records a call it rejects for arguments (semantic mismatch: never executed; manifest validation failure) as a tool
step with `Failure/Validation`. A history with such a step therefore always refused, even after the matched success at the same
planned index. Synthetic B02/B03 reproductions failed on `3d2afb7` with `Failed` and now complete.

`LifetimeReplans = 1` with a single plan in both tasks is not a defect: R1 reserves every early-final replan attempt against the
budget before the paid call. It is the budget counter, not the number of accepted revisions.

## Decision

The continuation proof is now `EarlyFinalRecovery.Evaluate`, returning a persisted `EarlyFinalRecoveryDecision`. Earlier tool
calls, in every revision, are classified in order:

1. no result → `UnknownToolOutcome`;
2. `Denied` or `Authorization` → `AuthorizationDenied`;
3. `Timeout` → `ToolTimeout`;
4. a tool that is not a registered read-only tool (any outcome, including a validation failure of a mutating tool, or an
   unknown tool) → `NonReadOnlyHistory`;
5. refuted verification → `VerificationRefuted`;
6. `Success` → fine;
7. a `Failure/Validation` the runtime classified `SemanticMismatch` or `ArgumentValidationFailure` for the planned tool of the
   **current** revision, followed by a `Matched`, successful, non-refuted call of the same tool at the **same planned index** of
   the same revision → resolved and listed in `ResolvedValidationStepIndexes`;
8. anything else, including a validation failure corrected only in a later revision → `UnresolvedToolFailure`.

The other preconditions are unchanged from R1, each with a typed reason: semantic contract, latest early-final trigger, received
malformed checkpoint, coherent fold, unresolved step, no pending correction, unconditional pending step, registered read-only
pending tool, offered in the current view. The continuation still reuses the original cursor through the existing runtime
record and fold; the model must call the pending tool, which goes through validation, step authority, policy, entitlement,
approval and the mutation journal. No step is invented and none is skipped or repeated.

Bounded cost and anti-loop:

- A continuation of a revision must be strictly further along than any earlier continuation of that revision
  (`RepeatedWithoutProgress`), whatever the replan's finish reason.
- Once an early-final replan of a revision stopped at `length` without a plan and was continued, a later early final of that
  revision is not sent to the model again (`PaidReplanSkipped`). Nothing is reserved for a call never made. With progress the
  proof continues; without it the attempt fails at no model cost. R1 instead paid a further 4,113-token replan per repeat
  until the replan budget ran out.

Persistence and audit:

- `PlanStep.EarlyFinalRecoveryDecision` (optional, omitted when null) on the early-final step, written only together with what it
  decided: the continuation record or the failure step. Steps written before R2 have none and fold as before.
- `TaskLifecycleStage.EarlyFinalPlanContinuationRefused = 8` with a stable snake_case `RefusalCode`; acceptance keeps
  `EarlyFinalPlanContinued`. The failure step text names the reason.
- `ReplanBreakdown` (runtime-internal, derived from state only) separates the charged budget counter, accepted revisions,
  early-final attempts with a paid call, deterministic continuations and skipped paid replans. `TaskAccounting.LifetimeReplans`
  documentation now states what it charges.

Model-call consumers: token accounting (`DeriveAccounting`), the API projection and SQLite already read
`EarlyFinalReplanCalls`. The web UI did not: the step "?" details and the last provider reason now list both collections
through `stepModelCalls`. Audit events are written per call by the broker and were already complete. The BENCH collector
captures raw state and is out of scope.

The decision is recorded as an amendment of ADR-0046 (it changes `bOps.Abstractions` and the audit schema additively).

## Files

| File | Change |
|---|---|
| `docs/architecture/adr/0046-…-replanning.md` | AF-05H2 amendment |
| `src/core/bOps.Abstractions/TaskState.cs` | `EarlyFinalRecoveryReason`, `EarlyFinalRecoveryDecision`, optional step field |
| `src/core/bOps.Abstractions/Audit.cs` | `EarlyFinalPlanContinuationRefused` lifecycle stage |
| `src/core/bOps.Abstractions/TaskLifecycle.cs` | `LifetimeReplans` semantics documented |
| `src/core/bOps.Runtime/EarlyFinalRecovery.cs` | Typed evaluation, resolved validation failures, progress rule, paid-replan skip |
| `src/core/bOps.Runtime/AgentRunner.cs` | Skip repeated output-limited replan; persist decision; audit refusal |
| `src/core/bOps.Runtime/TaskResume.cs` | `ReplanBreakdown` |
| `tests/bOps.Runtime.Tests/EarlyFinalValidationRecoveryTests.cs` | B02/B03 reproductions, non-recoverable classes, decision table, resume |
| `tests/bOps.Runtime.Tests/EarlyFinalReplanTests.cs` | No-progress tests updated to the bounded-cost rule |
| `tests/bOps.Memory.Tests/SqliteTaskStoreTests.cs` | Decision round trip and pre-R2 compatibility |
| `tests/bOps.Audit.Tests/JsonLinesAuditSinkTests.cs` | New stage through the real hash chain |
| `tests/bOps.Api.Tests/TaskModelCallsEndpointsTests.cs` | Decision exposed by the API |
| `web/bops-ui/src/app/…` | `stepModelCalls`; dashboard and provider reason read replan calls; specs |

## Tests

New runtime coverage (`EarlyFinalValidationRecoveryTests`, 15 cases): B02 and B03 reproduced with the live classification
sequence (red on `3d2afb7`, green now) with request/response/usage/finish reason, audit, accounting, projection and breakdown
checks; a second early final after progress continues without a second paid replan; uncorrected validation at the cursor,
validation corrected only in a later revision, an environment failure, a policy denial, a timeout and a successful side effect
are refused with their reason and blocking step; a decision table over every class, including validation failures of a mutating
tool and an unknown tool; refusal codes; resume at the malformed checkpoint, after the continuation record and after the pending
tool (B02, equal replan cap), and after a skipped-replan continuation, with no repeated replan, tool or record and unchanged
tokens. R1's unknown-mutation-outcome resume refusal, provider-exception, cancellation, budget, approval, entitlement and
authority tests still pass unchanged.

Windows (`dotnet test`, Debug):

- `bOps.Runtime.Tests`: **2137 passed** (full project; 56 early-final tests).
- `bOps.Architecture.Tests`: **175 passed**.
- `bOps.Memory.Tests`: **101 passed**.
- `bOps.Audit.Tests`: **26 passed**.
- `bOps.Api.Tests`: **511 of 512 passed** in the full run; the failure, `PrerequisiteReadinessIntegrationTests.WhenACheckIsLaterRegistered_TheNextRealResultTransitionsNormally_AndRecoveryIsOneInformation`, is in prerequisite readiness (untouched) and its class passed 18/18 in three isolated reruns: a timing-dependent test under full-suite load, not a regression of this change.
- `bOps.AgentFramework.Tests`: **98 passed**.
- `bOps.Policy.Tests`: **308 passed**.
- `web/bops-ui` Karma (ChromeHeadless): **489 passed**.
- Builds warning-free under `TreatWarningsAsErrors`.

Linux (WSL Ubuntu-24.04, .NET SDK 10.0.401, Release, from a `git archive` of the staged tree, identical to the commit): `bOps.Runtime.Tests` **2137 passed**, `bOps.Memory.Tests` **101 passed**, `bOps.Audit.Tests` **26 passed**.

## Residual risks

- Unit and synthetic tests prove the decision, not the live outcome. The gate is a new BENCH-01 1×3 run by the operator.
- After a continuation the model can still finalize early again. Without progress the attempt now fails without another paid
  replan, so a model that never calls the pending tool still ends `Failed`.
- A validation failure corrected only after an accepted replan, in a later revision, stays unresolved by design. BENCH-01 had
  no such revision.
- Skipping a repeated output-limited replan removes the model's chance to shorten the plan in that revision. That is intended,
  because the previous replan could not fit the output limit.
- A tool-originated `Validation` failure of a read-only tool is treated like a runtime rejection when a later matched success
  resolves it. A read-only tool has no side effect, so this widens nothing.
- B02's 171,255 tokens are mostly EvidenceRead and context cost (AF-EVID-01), not addressed here.

## Operator command: BENCH-01 1×3

First with `-PreflightOnly`, then without it, using the same private snapshot directory as BENCH-00:

```powershell
& 'C:\source\repos\babbubba\bOps\scripts\windows\run-live-benchmark.ps1' -TargetRepoRoot 'C:\source\repos\babbubba\bOps-af05h2' -ConfigSourceRoot 'C:\source\repos\babbubba\bOps' -ConfigSnapshotPath '<directory snapshot BENCH-00>' -RunsPerScenario 1
```
