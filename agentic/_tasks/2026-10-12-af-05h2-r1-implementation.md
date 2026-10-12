# AF-05H2-R1 — implementation and offline validation

Candidate branch: `fix/af-05h2-replan-recovery`, worktree `C:\source\repos\babbubba\bOps-af05h2`.
Code base: `07e422a4c9c65bd617cafaf209d2352657f1c130` (AF-05H H2+H4).
The plan and task are copied byte-for-byte from documentation commit `dfb1644035117799bd98e71be316997dc4bb524a`.
No other documentation-branch changes were imported.

## Root cause and recovery criterion

H2 correctly rejected the premature final and retained the unresolved cursor. The next stage retried malformed replan JSON
once, including output-limited replies at the same limit, then threw `MalformedResponse`. No persisted transition allowed
the original plan to become executable again. Previously, only accepted revisions consumed the replan budget.

`EarlyFinalWithUnresolvedPlan` is now distinct from other C8 deviations. A received, unparseable `length` reply is not retried
at the same limit. Other received malformed replies retain the single existing corrective retry. Provider exceptions,
including provider-thrown `MalformedResponse`, never become received malformed plans and never authorize fallback.

Automatic recovery requires a semantic-contract plan, a coherent derived position before the early-final trigger, no spent
argument/semantic correction, an unconditional pending read-only tool that is still available in the existing tool view,
and certain successful read-only execution history. No new step or argument is invented. The runner offers the original
pending tool at the original cursor, then uses its existing validation, authority, policy, entitlement, approval and journal
path. A repeated early final stays non-final; only resolved plan coverage permits normal completion.

Early-final replan attempts reserve attempt/lifetime budget before calling the model. Token, duration, delegation,
cancellation and step budgets continue to apply. A completed reserved replan may be recovered at an equal replan cap
without another paid attempt; a lowered cap below consumption forbids continuation. Repeated failures without progress
exhaust the existing budgets. Valid replans, including valid empty replans, retain their normal acceptance path.

## Persistence and audit

The original early-final `ModelCalls` stay separate. `EarlyFinalReplanCalls` checkpoints append received malformed replan
records, usage and original bounded provider payloads; `EarlyFinalReplanFailed` marks only a finished malformed attempt.
Each checkpoint saves those records and accounting together under existing ownership fencing. Calls on accepted plans,
checkpoint fields and synthetic terminal steps are disjoint, so tokens are counted once.

A distinct runtime recovery step records `EarlyFinalRecoveryCursor` without completing a planned step. The persisted fold
clears only an early-final replan obligation at the identical cursor, never another C8 obligation. A lifecycle audit event
`EarlyFinalPlanContinued` identifies the recovery record; original model-call audits are untouched. Resume revalidates tool
safety and the mutation journal admission remains mandatory. A complete malformed checkpoint resumes without regenerating
the replan; an incomplete checkpoint may start another bounded, paid attempt after explicit operator recovery/resume.
Cancellation is propagated and completed calls remain durable. The API strips payload bodies from both call collections;
SQLite keeps the original records. Downstream analysis must include `EarlyFinalReplanCalls` when enumerating model calls.

## Files

| File | Change |
|---|---|
| `agentic/_plans/2026-10-11-maf-knowledge-convergence.md` | Exact documentation import |
| `agentic/_tasks/2026-10-12-af-05h2-replan-truncation-recovery.md` | Exact documentation import |
| `agentic/_tasks/2026-10-12-af-05h2-r1-implementation.md` | This validation report |
| `src/core/bOps.Abstractions/Audit.cs` | Additive recovery lifecycle stage |
| `src/core/bOps.Abstractions/Model.cs` | Finish-reason contract documentation |
| `src/core/bOps.Abstractions/TaskLifecycle.cs` | Replan-accounting documentation |
| `src/core/bOps.Abstractions/TaskState.cs` | Optional recovery/checkpoint fields |
| `src/core/bOps.Api/TaskStateView.cs` | Preserve API payload omission for checkpoints |
| `src/core/bOps.Runtime/AgentRunner.cs` | Bounded replan, durable checkpoints and recovery |
| `src/core/bOps.Runtime/EarlyFinalRecovery.cs` | Conservative continuation proof |
| `src/core/bOps.Runtime/PlannedStepPosition.cs` | Persisted recovery fold |
| `src/core/bOps.Runtime/ReplanTrigger.cs` | Explicit origin classification |
| `src/core/bOps.Runtime/TaskResume.cs` | Checkpoint token accounting and synthetic-step classification |
| `tests/bOps.Api.Tests/TaskModelCallsEndpointsTests.cs` | Checkpoint API payload test |
| `tests/bOps.Audit.Tests/JsonLinesAuditSinkTests.cs` | Real chained audit serialization |
| `tests/bOps.Memory.Tests/SqliteTaskStoreTests.cs` | Real SQLite/source-generated round trip |
| `tests/bOps.Runtime.Tests/EarlyFinalReplanTests.cs` | Recovery, guards, budgets, checkpoints and resume |
| `tests/bOps.Runtime.Tests/PlannedStepValidationRetryTests.cs` | Complete correction fixture without an unrelated early final |

## Validation on Windows

- Runtime targeted filter: **383 passed**, including **40 early-final tests**. Covered normal final/replan, malformed/length
  first reply and retry, cursor progression, anti-loop, token/duration/replan budgets, provider failures, cancellation,
  checkpoint and post-tool resume, policy/entitlement/approval, conditional/unavailable/mutative safety, plan authority,
  unknown mutation outcome, existing journal durability/no-double-execution, delegation budgets and replan warnings.
- SQLite task-store and transition filters: **23 passed**.
- JSON-lines audit filter: **13 passed**, including the new stage through the real hash chain.
- Task model-call API endpoint filter: **5 passed** with fake models.
- `dotnet build src/core/bOps.Api/bOps.Api.csproj --no-restore --verbosity quiet`: **passed, zero warnings/errors**.
  The fresh worktree required explicit restores of the two existing Windows helper projects, which the API builds through
  MSBuild targets outside its ordinary restore graph. No project/packaging changes were made.
- `git diff --check`: clean; both imported documents byte-verified against `dfb1644`.

Runtime filter used:

```powershell
dotnet test tests/bOps.Runtime.Tests/bOps.Runtime.Tests.csproj --no-restore --filter 'FullyQualifiedName~EarlyFinalReplanTests|FullyQualifiedName~AgentRunnerTests|FullyQualifiedName~AgentRunnerPersistenceTests|FullyQualifiedName~SemanticPlannedStepTests|FullyQualifiedName~PlannedStepValidationRetryTests|FullyQualifiedName~ConditionalFollowUpTests|FullyQualifiedName~ResumeStateMachineTests|FullyQualifiedName~ModelCallRecordingTests|FullyQualifiedName~EntitlementExecutionTests|FullyQualifiedName~OrdinaryMutationJournal|FullyQualifiedName~OrdinaryMutationNoDoubleExecutionTests|FullyQualifiedName~DelegationRunnerBudgetTests|FullyQualifiedName~ReplanThresholdMessageTests' --verbosity quiet
```

## Remaining gates and scope

No live benchmark, real LLM call, full suite, push, merge or PR #100 change was performed. H1/H3/H4/H5, BENCH-00 and
AF-EVID-01 are unchanged. Recovery deliberately refuses legacy plans, activated conditional pending steps and histories
containing any mutation or unsuccessful tool execution, even if a broader continuation might be provable.
Existing payload limits and unreported usage cannot be reconstructed. A process killed before a received response reaches
its checkpoint retains the existing model audit durability guarantees; this is not an atomic transaction with the provider.

Linux validation is still required: the available Ubuntu WSL distribution has no `dotnet`. Existing CI includes Windows
and Ubuntu, but this local-only candidate has not been pushed and no CI run was triggered. The live BENCH-01 gate remains
with the operator, including the separately planned collector policy-hash fix and frozen configuration comparison.

The original checkout stays on `feat/bench-00-capture`, SHA `946c85a5f571a699085c76a9f1e40e061a51b40c`, with only its
pre-existing `src/core/bOps.Api/appsettings.json` edit, preserved. Its final SHA-256 is
`C33FB3ABC8ACEA4ED980E345D07A49260B33302F9669ED271AB5A2DE8BE6FB60`.

## Operator command: BENCH-01 1×3

Use the existing collector in the original checkout against the candidate code. Replace the snapshot placeholder with the
same private snapshot directory used for BENCH-00; first run the same command with `-PreflightOnly`, then without it.

```powershell
& 'C:\source\repos\babbubba\bOps\scripts\windows\run-live-benchmark.ps1' -TargetRepoRoot 'C:\source\repos\babbubba\bOps-af05h2' -ConfigSourceRoot 'C:\source\repos\babbubba\bOps' -ConfigSnapshotPath '<directory snapshot BENCH-00>' -RunsPerScenario 1
```

The candidate intentionally does not import the collector or the original checkout's personal configuration changes.
