# Task store and mutation journal — technical reference

> **Technical reference, not a decision register.** This page holds the exact schema, SQL shapes and code tables that the binding rules in
> [`../current/03-task-state-resume-mutations.md`](../current/03-task-state-resume-mutations.md) (C03-R13 and C03-R15 to C03-R21) refer to. It was
> extracted from ADR-0040 §4.2 and ADR-0051 §5–§9, §11, §14 during the architecture consolidation (DOC-02) because no existing guide describes
> them. If this page and the contract disagree, the contract wins. Verify against the code (`src/core/bOps.Memory/SqliteTaskStore.cs`,
> `src/core/bOps.Runtime/TaskResume.cs`, `src/core/bOps.Runtime/TaskRecovery.cs`) before relying on a detail.

## 1. `tasks` table: execution attempt (ADR-0040 §4.2)

- Additive, idempotent migration in `EnsureSchema`: `tasks.execution_attempt INTEGER NOT NULL DEFAULT 1`, added only when `PRAGMA table_info(tasks)` lacks it;
  a concurrent process that loses the race and gets "duplicate column" re-checks and continues. Existing rows get 1 (matching their JSON default). Nothing is
  rewritten, dropped, recreated or renamed; `state_json` stays the source of the whole state. Downgrading to an older binary is unsupported.
- `TryTransitionAsync` is one statement: `UPDATE tasks SET status, execution_attempt, updated_at_utc, state_json WHERE id = $id AND status = $expectedStatus AND
  execution_attempt = $expectedAttempt`; one affected row is success. SQLite serializes writers on the database file, so the compare and the write cannot interleave.
- `TryCreateAsync` is `INSERT … ON CONFLICT(id) DO NOTHING`. `SaveAsync` is `INSERT … ON CONFLICT(id) DO UPDATE SET … WHERE tasks.execution_attempt <=
  excluded.execution_attempt`; a save with a lower attempt writes nothing and throws `TaskExecutionSupersededException`.

## 2. `task_mutation_journal` table (ADR-0051 §6.2)

Created in `SqliteTaskStore.EnsureSchema` (additive; nothing in `tasks` changes). Enum values are stored by name, like `tasks.status`. Every journal write runs on a connection
with `PRAGMA synchronous=FULL`; the table inherits the file's owner-only permissions.

```sql
CREATE TABLE IF NOT EXISTS task_mutation_journal (
    task_id                      TEXT    NOT NULL,
    execution_attempt            INTEGER NOT NULL,
    step_index                   INTEGER NOT NULL,
    sequence                     INTEGER NOT NULL,
    tool_name                    TEXT    NOT NULL,
    arguments_hash               TEXT    NOT NULL,
    risk                         TEXT    NOT NULL,
    verification_tool_name       TEXT    NULL,
    plan_revision                INTEGER NULL,
    planned_step_index           INTEGER NULL,
    intent_at_utc                TEXT    NOT NULL,
    outcome_kind                 TEXT    NULL,
    outcome_tool_outcome         TEXT    NULL,
    outcome_failure_kind         TEXT    NULL,
    outcome_verification         TEXT    NULL,
    outcome_at_utc               TEXT    NULL,
    state                        TEXT    NOT NULL,
    reconciliation_json          TEXT    NULL,   -- StepReconciliation, NULL until reconciled
    history_recorded_in_attempt  INTEGER NULL,
    PRIMARY KEY (task_id, execution_attempt, step_index),
    UNIQUE (task_id, sequence)
);
CREATE INDEX IF NOT EXISTS ix_task_mutation_journal_state ON task_mutation_journal(task_id, state);
```

`ArgumentsHash` is the lowercase hex SHA-256 over the canonical arguments with every `ToolParameter.Sensitive` value replaced by the redaction marker.

### Writes (each one statement or one `BEGIN IMMEDIATE` transaction; never load/check/save in process)

| Write | Precondition, checked in the same statement/transaction | Effect |
|---|---|---|
| Intent | `tasks` row is `(Running, N)` and the key is free | Insert the entry `Pending` with `sequence = MAX+1`; `false` when the row is not `(Running, N)` (superseded). |
| Outcome | Entry exists, `outcome_kind IS NULL`, `reconciliation_json IS NULL` **and** the task row is `(Running, N)` | Set outcome and state; the same transaction replaces the task row with the attempt's state including the step (`UPDATE tasks … WHERE status='Running' AND execution_attempt=N`, exactly one row). |
| Reconcile | Task row is `(S, N)` as read with `S ≠ Running`; every named entry is in its expected unsettled state | Set reconciliation and the new state on each named entry; one failing precondition rolls the whole call back (`false`). |
| Acquire (resume) | Task row is `(S, N)`; no entry is `Pending`, `Ambiguous`, `Escalated` or `Abandoned`; every named entry is `ReconciledDone` with `history_recorded_in_attempt IS NULL` | Replace the task row with `(Running, N+1)` (the acquired state carrying the history steps) and set `history_recorded_in_attempt = N+1` on the named entries. |
| Recovery | Task row is `(Running, N)` | Existing `TryTransitionAsync` to `(Failed, N)`; the journal is not touched. |

## 3. Crash windows (ADR-0051 §7.2)

"Restart sees" assumes the task row was last written `Running` by this attempt; "resume" always means after an administrator recovery and, where required, reconciliation.

| # | Window | Durable | Restart sees | Retry safe? | Verification available after restart? | Operator reconciliation? |
|---|---|---|---|---|---|---|
| C0 | before the intent commit starts | task row of the last save; no entry | orphaned `Running`, no unsettled entry | nothing to retry; a new proposal gets its own intent and fresh policy/approval | no | recovery only |
| C1 | during the intent commit | atomic: nothing (= C0) or the committed entry (= C2) | either; undecidable → treated as C2 | not decidable | — | as C2 |
| C2 | intent committed, before `ExecuteAsync` | `Pending` entry; no step | orphaned `Running`, `Pending` entry | **no** (unknown) | unavailable (`arguments_unavailable` → `Escalated`) | **yes** |
| C3 | during `ExecuteAsync` | `Pending` | same | **no** | as C2 | **yes** |
| C4 | `ExecuteAsync` returned, before the outcome commit | `Pending` | same | **no** | as C2 | **yes** |
| C5 | during post-action verification | `Pending` | same | **no** | as C2 | **yes** |
| C6 | after verification, before the outcome commit | `Pending` (the in-memory `Confirmed` is lost) | same | **no** | as C2 | **yes** |
| C7 | after the atomic outcome+step commit, before the next task write | outcome **and** step | `Settled`: orphaned `Running`, step in history; `Ambiguous`: orphaned `Running`, `Ambiguous` entry with its step | `Settled`: not a retry (the call is in history; a repeat is a new informed mutation); `Ambiguous`: **no** | `Settled`: no; `Ambiguous`: **yes** (the step holds the arguments) | `Settled`: recovery only; `Ambiguous`: verify, then accept/abandon |
| C8 | after outcome and the following task write | outcome, step, and the terminal state (`Failed`/`MutationOutcomeUnknown`, `Cancelled`, `BudgetExceeded`) or the next `Running` save | terminal task or `Running` orphan | as C7 | as C7 | as C7, without recovery when terminal |

## 4. Refusal codes

### Recovery — `POST /api/agents/tasks/{id}/recover` (body `{ "executionAttempt": N }`; administrator; ordered, first match refuses)

| # | Condition | Refusal | HTTP |
|---|---|---|---|
| 1 | no such task | — | 404 |
| 2 | `Origin = Delegated` | `task_delegated` | 409 |
| 3 | `Origin = Unknown` | `task_origin_unknown` | 409 |
| 4 | status ≠ `Running` | `task_not_running` | 409 |
| 5 | an execution attempt of the task is registered in this host's launcher | `task_executing_here` | 409 |
| 6 | `MutationJournalMode = Absent` (legacy) | `mutation_journal_absent` | 409 |
| 7 | store lacks `ITaskTransitionStore` | `transition_unsupported` | 501 |
| 8 | `Journaled` and store lacks `ITaskMutationJournalStore` | `mutation_journal_unsupported` | 501 |
| 9 | persisted attempt ≠ requested `executionAttempt` | `recovery_conflict` | 409 |
| 10 | `TryTransitionAsync((Failed, N) + synthetic "Execution interrupted" step + TerminalReason ExecutionInterrupted, Running, N)` returns `false` | `recovery_conflict` | 409 |

`executionAttempt` missing or not positive is `400`. Success is `200` with the task view.

### Reconciliation — `POST /api/agents/tasks/{id}/reconcile` (body `{ "action": "verify" | "acceptDone" | "abandon", "note"?: string }`, note ≤ 500 characters, audit only)

| # | Condition | Refusal | HTTP |
|---|---|---|---|
| 1 | unknown action, oversized note | — | 400 |
| 2 | no such task | — | 404 |
| 3 | `Origin = Delegated` | `task_delegated` | 409 |
| 4 | `Origin = Unknown` | `task_origin_unknown` | 409 |
| 5 | `MutationJournalMode = Absent` | `mutation_journal_absent` | 409 |
| 6 | `Journaled` and store lacks `ITaskMutationJournalStore` | `mutation_journal_unsupported` | 501 |
| 7 | journal cannot be read | `mutation_journal_unavailable` | 503 |
| 8 | status `Running` (recover first) | `task_running` | 409 |
| 9 | no `Pending`, `Ambiguous` or `Escalated` entry (includes every `MutationsDisabled` task) | `nothing_to_reconcile` | 409 |
| 10 | the identity is not a human | `reconcile_not_human` | 409 |
| 11 | a per-entry compare-and-set of the Reconcile write fails | `reconcile_conflict` | 409 |

| Action | Applies to | Effect | Executes |
|---|---|---|---|
| `verify` | each unsettled entry, in `Sequence` order | re-runs the entry's declared verification; `Confirmed` → `ReconciledDone` (`VerifiedDone`), otherwise `Escalated` (`EscalatedToOperator`) | only the declared `Read` verification tool |
| `acceptDone` | every unsettled entry | `ReconciledDone` (`OperatorAcceptedDone`, last verification status kept); treated as executed, still unknown in the view | nothing |
| `abandon` | every unsettled entry | `Abandoned` (`OperatorAbandoned`); the task is permanently refused `task_abandoned` | nothing |

Reconciliation verification inconclusive reason codes: `arguments_unavailable` (no persisted step with equal attempt, index, tool name and redacted-arguments hash) and
`verification_unavailable` (tool not registered, not `IVerifiableTool`, or declaring another `VerifyToolName`).

### Resume rows added to the ordered resume policy (inserted after row 6 and before row 7)

| # | Condition | Refusal code | HTTP |
|---|---|---|---|
| 6a | `MutationJournalMode = Absent` | `mutation_journal_absent` | 409 |
| 6b | `Journaled` and the store lacks `ITaskMutationJournalStore` | `mutation_journal_unsupported` | 501 |
| 6c | the journal could not be read | `mutation_journal_unavailable` | 503 |
| 6d | any entry `Abandoned` | `task_abandoned` | 409 |
| 6e | any entry `Pending`, `Ambiguous` or `Escalated` | `mutation_outcome_unknown` | 409 |

## 5. Task view additions (ADR-0051 §14.2)

`mutationJournal: { mode, available, unsettledCount, entries: [ { executionAttempt, stepIndex, sequence, tool, risk, argumentsFingerprint (12 hex), intentAtUtc, plannedStepIndex,
planRevision, outcome: { kind, toolOutcome, failureKind, verification, atUtc } | null, state, knowledge: "KnownNotExecuted" | "KnownExecuted" | "Unknown", reconciliation: { action,
verification, resolvedBy, atUtc } | null } ] }`, `recoverable`, `recoveryBlockedReason` (`{ code, message }`) and the existing `resumable` / `resumeBlockedReason`. `executionPlan` shows a
planned step whose entry is unsettled as `OutcomeUnknown`. The reconcile response is `{ taskId, results: [ { executionAttempt, stepIndex, state, reconciliation } ], unsettledCount,
resumable, resumeBlockedReason }`.

## 6. Audit stages

`TaskMutationAuditEvent` stages: `IntentCommitted`, `IntentNotCommitted` (reason codes `superseded`, `store_failure`, `mutations_disabled`), `OutcomeCommitted`, `OutcomeNotCommitted`
(`superseded`, `store_failure`), `AmbiguityDiscovered`, `VerificationAttempted`, `Reconciled`, `ReconcileRejected`. `TaskLifecycleStage.RecoveryAccepted = 5` and
`RecoveryRejected = 6` carry the recovery outcome. `TaskTerminalKind.MutationOutcomeUnknown = 14` and `ExecutionInterrupted = 15`.
