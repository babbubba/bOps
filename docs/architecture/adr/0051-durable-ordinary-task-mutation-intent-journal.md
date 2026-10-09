# ADR-0051 — Durable mutation intent journal for ordinary tasks, fenced orphan recovery and reconciliation

Status: **Accepted**
Accepted: 2026-10-09 by the operator (D-047), as designed in PR #99, including the documented trade-off for
crash windows C2–C6 (§5.2, §16 R-2): when the original arguments are not available, post-crash verification is not
reconstructed and reconciliation requires an administrator.

Governs finding **F-25** of the V1.3 pre-release stabilization packet
(`agentic/_tasks/2026-10-08-v1.3-pre-release-stabilization.md`, "F-25 / F-26 status";
`agentic/_plans/2026-09-25-v1.3x-reliability-hardening.md` §8 caveat and §15). Design pass F-25A; implementation is
F-25B (`agentic/_tasks/2026-10-09-v1.3-f25-durable-ordinary-mutation-journal.md`).

| ADR | Relationship |
|---|---|
| ADR-0017 (persistent, resumable tasks) | **Preserved** (`ITaskStore` unchanged, resume re-enters the same loop, `TaskState` stays the whole-aggregate JSON row). **Extended**: `tasks.db` gains a second table, the mutation journal, written in the same SQLite file. |
| ADR-0030 (delegation) | **Preserved** unchanged: the delegation `IStepJournal`, `DelegationRunner` reconciliation and `RequiresReconciliation` are not touched. **Extended**: its crash semantics (intent before, outcome after; intent without outcome = *may have happened*; settle by declared verification; `Refuted`/`Inconclusive` → administrator; no automatic retry) are adopted for ordinary tasks, and its `ReconciliationAction` / `StepReconciliation` types are reused. Not amended. |
| ADR-0036 (entitlements) | **Preserved**: entitlement is evaluated before the intent and never persisted; reconciliation verification obtains its own fresh decision. **Extended**: its "record and reconcile such an outcome through the existing timeout/verification and delegation journal semantics" now has an ordinary-task journal to do so. Not amended. |
| ADR-0040 (resume state machine) | **Preserved**: `Running` is never ordinarily resumable; no new `AgentTaskStatus`; resume is an audited atomic transition; execution-attempt fencing. **Amended** explicitly, in §13 below: §3 (new ordered refusal rows), §4.3/§4.4 (journal-aware acquisition; the orphan-recovery design §4.4 deferred to "its own ADR"), §5.2 (two new non-counted synthetic steps), §6 (two new terminal kinds), §9 (view fields, two endpoints), §10 (two lifecycle stages). ADR-0040's text is not edited; it carries an `Amended by: ADR-0051` pointer. |

Also relied on, unchanged: ADR-0022 §6 (approvals are never persisted), ADR-0016 / rule S4 (every executed non-`Read`
call is verified; never default to `Confirmed`), ADR-0038 (history rebuilt from persisted data only), ADR-0039 (terminal
failure containment), ADR-0046/0047/0050 (the call gates before execution), PRE-1 (per-tool bounded timeout).

## 1. Context

### 1.1 The gap, as it is in the code at `9707803`

`AgentRunner.ExecuteStepAsync` (`src/core/bOps.Runtime/AgentRunner.cs:3407`) is the single execution path of every
tool call. Its gates run in this order: tool resolution (ADR-0038), delegated envelope, argument validation, policy
(with the manifest's `RequiresExplicitApproval` escalation), human approval and `IApprovalBoundTool` binding,
entitlement (ADR-0036). Then, at `AgentRunner.cs:3632-3639`:

```csharp
var journal = manifest.Risk != RiskLevel.Read ? delegation?.Journal : null;
if (journal is not null)
{
    await journal.BeginAsync(stepIndex, call.ToolName, call.Arguments);
}
result = await ExecuteWithTimeoutAsync(tool, call, executionContext, ct);
```

followed by post-action verification (`:3704-3718`), `RecordAsync` (`:3725`) and `journal.CompleteAsync` (`:3741-3750`).

So:

```text
delegated   non-Read  -> durable intent before, durable outcome after (ADR-0030 §7)
ordinary    non-Read  -> nothing durable until the loop's next TaskState save
```

For an ordinary task (`TaskOrigin.Ordinary`, the model-driven loop of `RunAsync` / `ExecuteAcquiredResumeAsync`), the
first durable trace of a mutation is the per-step `SaveOwnedAsync` after the step completes (`AgentRunner.cs:1147ff`).
A process death anywhere between the entitlement gate and that save leaves `TaskState.Steps` without the step: the
mutation may have happened, and nothing persisted says so. The `ToolCallAuditEvent` is written by `RecordAsync`, also
after execution, so the audit log does not say so either.

Three in-process paths already lose the same information without a crash:

- **Cancellation** (`DELETE`, host shutdown) during `ExecuteAsync` of an ordinary non-`Read` call: the
  `OperationCanceledException` propagates (`:3670` only handles the delegated case), no step is recorded, and the
  launcher writes `Cancelled` (`AgentTaskLauncher.cs:162-166`). ADR-0040 makes `Cancelled` resumable.
- **Runtime/tool timeout** of a non-`Read` call: recorded as a `Timeout` step and verified, then the loop *continues*
  and replans (`AgentRunner.cs:1130-1133`). The model may propose the same mutation again, automatically if policy is
  `Automatic`, while the first one may still be running (a timeout only stops the runtime waiting).
- **ADR-0039 backstop**: an exception escaping after the tool ran ends the attempt `Failed`/`RuntimeFailure` (resumable)
  with no record of the in-flight call.

### 1.2 Why ADR-0040 cannot simply be relaxed

ADR-0040 (operator amendment D-3) deliberately refuses an ordinary resume of a persisted `Running` task "whether or not
an executor in this host holds it": the absence of a local executor does not prove that no other process (the CLI,
another API process, a stalled executor) is still running a tool call, and fencing protects persisted state only. The
result today is that an orphaned `Running` task is visible ("Interrupted") but has **no recovery path at all**, while a
`Cancelled` or `Failed` task whose last call was a mutation in flight **is** resumable and nothing tells the resumed
model that the mutation may have happened. F-25 must close the second hole without opening the first.

### 1.3 What cannot be promised

There is no transaction spanning a SQLite commit and an external side effect. Whatever the order of writes, there is a
window in which the intent is committed and the effect's existence is unknown. Exactly-once execution is therefore not a
property this runtime can have. The property this ADR provides is:

> **A mutation is never executed twice without someone knowing that it may already have happened** — never
> *unknowingly* executed twice.

and the window "intent committed, effect unknown" is handled fail-closed: it is never read as "did not happen".

## 2. Decision summary

1. Every invocation of a non-`Read` tool in an ordinary task is preceded by a durable, **fenced** intent and followed by
   a durable, **fenced** outcome, in a new SQLite table `task_mutation_journal` in `tasks.db` (§5, §6).
2. The intent is written at exactly one point: after every gate has allowed the call, immediately before
   `ExecuteWithTimeoutAsync` (§3). Nothing that is refused, rejected, mismatched, unoffered, superseded or skipped has an
   intent.
3. The outcome of a call whose result is known is committed **atomically with the step that records it** in
   `TaskState`, so a settled journal entry never exists without the history the model is rebuilt from (§6.3).
4. The journal distinguishes **known not executed**, **known executed** and **unknown** (§4). Unknown is never
   collapsed into failure.
5. An unknown outcome found in-process (timeout, cancellation, attempt-duration interruption) that post-action
   verification does not confirm **ends the execution attempt**; it is never replanned or retried (§7.2).
6. A task with an unsettled entry is not resumable. An **administrator** reconciles it: re-run the declared
   verification (`Confirmed` settles it as done), accept it as already applied, or abandon the task. There is no retry
   action (§9).
7. An orphaned `Running` task stays **not resumable** (ADR-0040 §3 row 3 is preserved). An **administrator-only, fenced
   recovery transition** `(Running, N) → (Failed, N)` makes it terminal. It executes nothing. Its safety does not rest
   on proof that the old executor is dead, but on journal fencing: after the transition, the old executor can neither
   commit a new intent nor settle an old one (§8).
8. Nothing happens at process startup (§10).
9. The capability is an additive store interface `ITaskMutationJournalStore : ITaskTransitionStore`; a task created on a
   store without it has mutations disabled for its whole life (§11). Pre-F-25 tasks have no journal and are refused
   (§12).

## 3. D1 — What is journaled, and the exact boundary

### 3.1 Scope

| Invocation | Journaled? | Why |
|---|---|---|
| Ordinary task (`Origin = Ordinary`, model-driven loop), tool `Risk > Read`, all gates passed | **Yes** — intent and outcome | The F-25 subject. |
| Ordinary task, `Risk == Read` | No | No side effect; an interrupted read is repeated (§12.4). |
| Runtime-mandated post-action verification (`ExecuteVerificationToolAsync`) | No | It is a `Read` by registration (rule B3); its result is recorded *in* the mutation's outcome. |
| Reconciliation verification (§9.3) | No | Same `Read` path; its result is recorded as a reconciliation. |
| Delegated task (`Origin = Delegated`) | No (ordinary journal) | Unchanged ADR-0030: the delegation `IStepJournal` when the run is stored. Never both journals. |
| `ExecuteExecutionPlanAsync` / Skill paths without a task (`ExecuteExecutionPlanCoreAsync`, evidence calls) | No | Not a task, not resumable (ADR-0025); outside F-25. Recorded as residual R-1 (§16). |
| EvidenceRead / control-plane operations | No | Not tools. |

### 3.2 Gate-by-gate (`ExecuteStepAsync` order)

| Pipeline point | Intent? | Recorded as |
|---|---|---|
| Unknown / unoffered tool name (ADR-0038, ADR-0046 step view) | No | Unchanged rejected step. |
| ADR-0050 semantic mismatch (the loop records it without calling `ExecuteStepAsync`, `AgentRunner.cs:1005-1023`) | No | Unchanged. |
| Argument validation failure (ADR-0038 malformed, manifest schema, ADR-0047 correction) | No | Unchanged validation step. |
| **Journal-mode gate** (new, §11): task has `MutationJournalMode = MutationsDisabled` | No | Runtime `Forbidden` (precedent: envelope refusal), before policy, so no human is asked to approve something that cannot run. |
| Policy `Forbidden` (including undefined mode) | No | Unchanged. |
| Policy `Approval` → human rejects | No | Unchanged `UserRejected`. |
| `IApprovalBoundTool.BindApprovalAsync` fails | No | Unchanged. Binding is the package's approval-recording hook, documented as "failure prevents approved execution"; it is not the mutation. |
| Entitlement denied / unavailable / invalid (ADR-0036) | No | Unchanged. |
| **All gates allowed** | **Intent** (§6.1) | — |
| Intent write returns `false` (fenced) | — | Executor superseded (ADR-0040 §4.4): stop, write nothing more, audit. Tool not invoked. |
| Intent write throws | — | Tool not invoked. The attempt ends `Failed` / `RuntimeFailure` through the ADR-0039 containment. A commit whose acknowledgement was lost leaves a `Pending` entry, handled as unknown (conservative, never "not executed"). |
| Cancellation or attempt-duration expiry observed after the intent, before invocation | Intent + outcome `NotInvoked` | The only post-intent outcome that proves "known not executed": the runtime itself did not call the tool. |
| `ExecuteWithTimeoutAsync` invoked | Intent + outcome per §4 | — |

**The boundary** is therefore: *after* `EvaluateEntitlementAsync` returned allowed and the telemetry activity started,
*at* the line where `delegation?.Journal` is chosen today (`AgentRunner.cs:3635`), *before* `ExecuteWithTimeoutAsync`.
The ordinary journal is selected there as `manifest.Risk != RiskLevel.Read ? (delegation?.Journal ?? ordinaryMutation)
: null`, where `ordinaryMutation` is a new internal scope that only the ordinary model-driven loop supplies. For an
ordinary task that scope is never `null`: a task that cannot journal has `MutationsDisabled` and never reaches this
line with a non-`Read` call (§11).

## 4. D4 — Outcomes and the three kinds of knowledge

### 4.1 Outcome kinds (new `MutationOutcomeKind`; explicit persisted values)

| Kind | Meaning | Known? |
|---|---|---|
| `NotInvoked = 0` | After the intent, the runtime did not call the tool (cancellation or attempt-duration expiry observed before invocation). | **Known not executed** |
| `Returned = 1` | The invocation ended and the runtime observed its end: the tool returned a result (`Success` or `Failure`) or threw (contained as `Failure`/`Internal`, rule C1). The tool's `ToolOutcome`, `ToolFailureKind` and the post-action `VerificationStatus` are recorded. | **Known executed** |
| `TimedOut = 2` | The runtime stopped waiting (PRE-1 effective timeout), or the tool itself returned `ToolOutcome.Timeout`. | Unknown |
| `Cancelled = 3` | The task was cancelled while the tool was invoked. | Unknown |
| `Interrupted = 4` | The attempt-duration budget interrupted the invocation (HARDEN-8). | Unknown |
| *(no outcome)* | Intent committed, nothing after it is durable (process death, lost write). | Unknown |

"Known executed" means the invocation is known to have run to its end and what it reported is recorded. It does **not**
claim that the effect succeeded: a `Returned` outcome with `ToolOutcome.Failure` may have partially applied (rule S7),
which is exactly why its verification is recorded beside it. It is settled because the model, the operator and the
journal all have the same complete record of it — any later call is an informed new mutation with its own intent,
policy and approval. A `Failure` is never relabelled "failed, safe to retry".

**Settled iff** the runtime observed the invocation end and the tool did not report a timeout, or proved the tool was
never invoked, or verification confirmed the effect. `ToolOutcome.Timeout` returned by a tool counts as unknown, because
a tool's own timeout says only that it stopped waiting for something.

### 4.2 Role of `VerificationSpec` / `VerificationStatus`

- Every executed non-`Read` call is still verified exactly as today (ADR-0016), whatever its outcome kind, and the
  status is stored on the outcome.
- For an **unknown** outcome kind, `Confirmed` settles the entry as **known executed** (the effect is observed).
  `Refuted` and `Inconclusive` do **not** settle it: `Refuted` after a timeout or cancellation proves only that the
  effect is not observed *now*; the external operation may still complete (R7). Neither becomes "not executed".
- For `Returned`, verification does not change settlement; it is information (and drives ADR-0040/C8 replanning as
  today: `Refuted` deviates).

### 4.3 Journal state (new `TaskMutationState`; persisted, runtime-computed, explicit values)

| State | When | Knowledge | Blocks resume | Reconcilable |
|---|---|---|---|---|
| `Pending = 0` | Intent, no outcome. | Unknown | Yes | Yes (task not `Running`) |
| `Settled = 1` | `NotInvoked`; `Returned`; unknown kind with `Confirmed`. | Known not executed / known executed | No | No |
| `Ambiguous = 2` | Unknown kind, verification not `Confirmed`. | Unknown | Yes | Yes |
| `Escalated = 3` | A reconciliation verification ran and did not confirm (`ReconciliationAction.EscalatedToOperator`). | Unknown | Yes | Yes (verify again, accept, abandon) |
| `ReconciledDone = 4` | `VerifiedDone` (reconciliation verification `Confirmed`) or `OperatorAcceptedDone`. | `VerifiedDone`: known executed. `OperatorAcceptedDone`: **still unknown**, *treated as executed* by an administrator's decision. | No | No |
| `Abandoned = 5` | `OperatorAbandoned`. | Unknown | **Yes, permanently** (`task_abandoned`) | No |

"Unsettled" = `Pending`, `Ambiguous` or `Escalated`. The classification is one pure runtime function
(`MutationJournalPolicy`); stores persist the state they are given and use it only in predicates.

## 5. D2, D3 — Contents and identity

### 5.1 Identity

A mutation is identified by **`(TaskId, ExecutionAttempt, StepIndex)`** — the primary key — plus a per-task monotonic
`Sequence` (1-based, assigned inside the intent statement) for ordering and display.

- `StepIndex` alone is **not** unique across attempts: a crash loses the unsaved step, and the next attempt resumes at
  `Steps.Count`, re-using that index. `ExecutionAttempt` disambiguates; it is the ADR-0040 fencing token, incremented
  only by an accepted resume.
- Within one attempt, one loop iteration executes at most one tool call (first-call-wins, ADR-0038), and every
  iteration — including an ADR-0047 correction of the same planned step and a replan — gets a new `StepIndex`.
- The same tool repeated in a plan, a superseded or skipped planned step, a conditional step (ADR-0050): each executed
  call has its own `StepIndex`; unexecuted ones have no entry.
- Plan revision and planned-step index are **context, not identity**: they are stored so the plan panel (PRE-4) can
  place the entry, never used to match calls.

### 5.2 Intent contents (minimal)

| Field | Why |
|---|---|
| `TaskId`, `ExecutionAttempt`, `StepIndex`, `Sequence` | Identity and order. |
| `ToolName` (canonical registered name) | What was about to run; operator display; duplicate guard (§9.6). |
| `ArgumentsHash` | Lowercase hex SHA-256 (`DelegationHasher.ComputeArgumentsHash`) over the canonical arguments **with every `ToolParameter.Sensitive` value replaced by the redaction marker** (`ToolArguments.Redact`). A fingerprint for matching the persisted call (§9.3) and the duplicate guard; hashing redacted values means a low-entropy secret cannot be recovered from the hash by guessing. (The delegation journal hashes raw arguments; that is unchanged.) |
| `Risk` | Display. |
| `VerificationToolName` (the manifest's `VerificationSpec.VerifyToolName` at intent time) | Reconciliation verification refuses to use a different verifier than the one declared when the call ran. |
| `PlanRevision`, `PlannedStepIndex` (nullable) | PRE-4 placement only. |
| `IntentAtUtc` | Display and audit. |

**Not persisted:** raw or redacted arguments, the `VerificationSpec.ArgumentsFrom` projection, credentials, tool output
or error text, model/provider material, approval decisions or approver, entitlement decisions or bindings.

Reconciliation verification needs arguments: `IVerifiableTool.EvaluateVerificationAsync` receives the **full original
arguments**, not only the `ArgumentsFrom` subset, so a projection would not be enough and full arguments in the journal
would break P8. The design therefore takes arguments only from where ADR-0017 already persists them — the task's own
recorded step for that call — and treats their absence as "verification unavailable" (§9.3). This is the deliberate
price of P8: a process death *before* the step was recorded (crash windows C2–C6) always needs an administrator.

### 5.3 Outcome contents

`Kind`, `ToolOutcome?` and `ToolFailureKind?` (for `Returned`), `Verification` (`VerificationStatus?`), `AtUtc`. No
message, observation or output.

### 5.4 Reconciliation contents

Reuses ADR-0030's `StepReconciliation` (`Action`, `Verification`, `ResolvedBy`, `AtUtc`) and `ReconciliationAction`
(`VerifiedDone`, `OperatorAcceptedDone`, `EscalatedToOperator`, `OperatorAbandoned`) unchanged — same meaning, same
values. Plus `HistoryRecordedInAttempt` (`int?`): the attempt whose acquisition appended the runtime-authored history
step for a `ReconciledDone` entry (§9.5). An administrator's note goes only to the audit event, bounded.

## 6. D6, D13, D15 — Storage, atomicity and fencing

### 6.1 Storage model: **B, a separate SQLite table in `tasks.db`** (chosen)

| | A: `TaskState.MutationJournal` (JSON) | **B: `task_mutation_journal` table** | C: generalize `IStepJournal` | D: combination |
|---|---|---|---|---|
| Atomic + fenced intent | Yes, via `TryTransitionAsync` — but rewrites the whole task JSON per intent | **Yes, one conditional `INSERT … SELECT … FROM tasks WHERE status/attempt`** | Delegation journal is a whole-run save; ordinary fencing would still be needed | — |
| Fencing of reconciliation and concurrent operators (R5) | **No**: two reconciliations both CAS on the unchanged `(status, attempt)` and the second silently overwrites the first; needs a row version ADR-0040 rejected | **Yes, per-entry CAS** on the entry's state | — | — |
| Any writer can drop it | **Yes**: every `TaskState` writer (backstop, cancellation, launcher, view-derived writes) must carry it forward; one omission erases evidence | **No**: no `TaskState` write touches the table | — | — |
| Queryability (`unsettled` predicates, view) | JSON scan | **Columns + index** | — | — |
| Old rows / old readers | JSON property ignored by old binary | Table ignored by old binary | — | — |
| Public `Abstractions` | `TaskState` property + types | Capability interface + types | `IStepJournal` is internal; making it public widens ADR-0030's contract | Union of both costs |
| Delegation reuse | Types | **Types and semantics** (§5.4) | Code | — |
| Third-party `ITaskStore` | Silent loss if a store maps columns | **Explicit capability; fail closed without it** | — | — |
| Crash testing | Whole-state writes only | Each journal write is an injectable point | — | — |

B is chosen. The delegation journal stays where it is (`IDelegationStore`, ADR-0030); ordinary tasks reuse its
vocabulary, not its storage, because the ordinary journal's writes must be fenced against the **task row** in the same
statement, which only a table in the task database can do.

### 6.2 Schema (additive, created in `SqliteTaskStore.EnsureSchema`)

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

Enum values are stored by name, like `tasks.status`. Nothing in `tasks` is changed, dropped or rewritten. Every
journal write runs on a connection with `PRAGMA synchronous=FULL`, so a commit that returned survives power loss, not
only process death. The table inherits the file's owner-only permissions.

### 6.3 Writes (each a single statement or one `BEGIN IMMEDIATE` transaction; never load/check/save in process)

| Write | Precondition, checked in the same statement/transaction | Effect |
|---|---|---|
| **Intent** | `tasks` row is `(Running, N)` and the key is free | Insert entry `Pending`, `sequence = MAX+1`. `false` when the row is not `(Running, N)` (superseded). |
| **Outcome** | Entry exists, `outcome_kind IS NULL`, `reconciliation_json IS NULL`, **and** `tasks` row is `(Running, N)` | Set outcome and state. Every outcome kind is written with the step that records it (§7.3), so the **same transaction** always replaces the task row with the attempt's state including that step (`UPDATE tasks … WHERE status='Running' AND execution_attempt=N`, exactly one row). Either both commit or neither. |
| **Reconcile** | `tasks` row is `(S, N)` as read, `S ≠ Running`; every named entry is in its expected unsettled state | Set reconciliation and new state on each named entry; nothing else. One entry failing its precondition rolls the whole call back (`false`). |
| **Acquire** (resume) | `tasks` row is `(S, N)`; **no** entry of the task is `Pending`, `Ambiguous`, `Escalated` or `Abandoned`; every named entry is `ReconciledDone` with `history_recorded_in_attempt IS NULL` | Replace the task row with `(Running, N+1)` (the acquired state, carrying the history steps of §9.5); set `history_recorded_in_attempt = N+1` on the named entries. |
| **Recovery** | `tasks` row is `(Running, N)` | Existing `TryTransitionAsync` to `(Failed, N)`; the journal is not touched. |

Defence in depth: `SqliteTaskStore.TryTransitionAsync`, when called for an acquisition (`task.ExecutionAttempt =
expected + 1`), also enforces the "no blocking entry" predicate, so a runtime path that forgot to use **Acquire** still
cannot resume past an unsettled mutation.

### 6.4 Fencing and the late writer (D15, P7)

Every executor write to the journal requires the task row to be `(Running, N)` of that executor, in the same statement
— at least ADR-0040's fencing. Consequences:

- **Old executor, new owner.** For attempt 2 to own the task, attempt 1 must have been recovered (`(Running,1) →
  (Failed,1)`), each of its unsettled entries reconciled (`ReconciledDone`) — Acquire refuses otherwise — and a resume
  accepted. A late **outcome** from attempt 1 fails both preconditions (task row is not `(Running,1)`; the entry has a
  reconciliation). It changes nothing; attempt 1 audits `OutcomeNotCommitted` with its outcome kind and verification
  status (evidence kept in the audit log), then stops as superseded.
- **Old executor, task recovered but not yet reconciled.** The late outcome is refused just the same (task row not
  `(Running,1)`). It is **deliberately not accepted** even though it is truthful: a `Returned` outcome would settle the
  entry while its step can no longer be written to the fenced task row, so the next resume would rebuild a history
  without the mutation — the original F-25 hazard. The entry stays unsettled; the audit event carries what the late
  writer saw; the administrator decides.
- **Old executor, late intent.** After recovery its intent statement fails; the tool is not invoked. An approval it held
  is wasted, never transferred (P5).
- Nothing a stale attempt writes can authorize, settle or replay a mutation; nothing it fails to write erases the
  intent that says the effect may have happened.

## 7. D5, D21 — Crash matrix and in-process behaviour

### 7.1 The pipeline after F-25 (ordinary, non-`Read`)

```text
G   gates (resolution, validation, journal-mode gate, policy (+duplicate guard), approval/binding, entitlement)
I   intent commit (fenced)                       -> Pending
P   pre-invocation check (cancel / attempt budget)  -> NotInvoked + step, atomic; stop
E   ExecuteWithTimeoutAsync
V   post-action verification (always)
O   outcome + step, one atomic fenced commit      -> Settled | Ambiguous
T   Ambiguous => attempt ends (terminal write)    ; Settled => loop continues as today
```

### 7.2 Crash windows

"Restart sees" assumes the task row was last written `Running` by this attempt (a crash leaves it so); "resume" always
means after an administrator recovery (§8) and, where required, reconciliation (§9).

| # | Window | Durable | What may have happened externally | Restart sees | Retry safe? | Verification mandatory? | Operator reconciliation? |
|---|---|---|---|---|---|---|---|
| C0 | before the intent commit starts | Task row of the last save; no entry for this call | Nothing (tool not invoked) | Orphaned `Running`, no unsettled entry | Nothing to retry: after recovery, resume is allowed; a new proposal of the call gets its own intent and fresh policy/approval | No | Recovery only |
| C1 | during the intent commit | SQLite atomicity: either nothing (= C0) or the committed entry (= C2) | Nothing | Either case; the runtime cannot tell which | Not decidable by the runtime → treated as C2 | — | As C2 |
| C2 | intent committed, before `ExecuteAsync` | `Pending` entry; no step | Nothing in fact; unknowable to the runtime | Orphaned `Running`, `Pending` entry | **No** (unknown) | Attempted on reconcile; arguments are not persisted for this call, so it is **unavailable → `Inconclusive` → `Escalated`** | **Yes** (accept as done / abandon) |
| C3 | during `ExecuteAsync` | `Pending` | Partial or complete effect, or none | Same | **No** | Same as C2 | **Yes** |
| C4 | `ExecuteAsync` returned, before outcome commit | `Pending` | Effect may be complete | Same | **No** | Same | **Yes** |
| C5 | during post-action verification | `Pending` | Same | Same | **No** | Same | **Yes** |
| C6 | after verification, before the outcome commit | `Pending` (the in-memory `Confirmed` is lost) | Same | Same | **No** | Same | **Yes** |
| C7 | after the atomic outcome+step commit, before the next task write (terminal write or the loop's next save) | Outcome **and** step | Known | `Settled`: orphaned `Running`, step in history. `Ambiguous`: orphaned `Running`, `Ambiguous` entry with its step | `Settled`: not a retry — the call is in history; any repeat is a new informed mutation. `Ambiguous`: **No** | `Settled`: no. `Ambiguous`: **yes, available** (the step holds the arguments) | `Settled`: recovery only. `Ambiguous`: verify; if not `Confirmed`, accept/abandon |
| C8 | after outcome and the following task write | Outcome, step, and the terminal state (`Failed`/`MutationOutcomeUnknown`, `Cancelled`, `BudgetExceeded`) or the next `Running` save | Known | Terminal task (no recovery needed) or `Running` orphan | As C7 | As C7 | As C7, without recovery when terminal |

No window is read as "did not happen". There is no window in which a settled outcome exists without the step that
records it (§6.3), so C7's "outcome durable, history not" state of the delegation design does not exist here.

### 7.3 In-process outcomes (no crash)

| Event | Outcome written | Verification | Then |
|---|---|---|---|
| Tool returns `Success`/`Failure` or throws | `Returned` + step, atomic | Always, recorded | `Settled`; loop continues exactly as today (C8 replan on `Refuted`, etc.). |
| PRE-1 runtime timeout, or tool returns `Timeout` | `TimedOut` + step, atomic | Always (own bounded token) | `Confirmed` → `Settled`, loop continues (and replans as today on a timeout). Otherwise → `Ambiguous`; the attempt **ends** `Failed` with `TaskTerminalKind.MutationOutcomeUnknown`; no replan, no further step. **Timeout is never a retry.** |
| Attempt-duration budget interrupts the invocation (HARDEN-8) | `Interrupted` + step, atomic | Always (already today) | `Confirmed` → `Settled`; otherwise `Ambiguous`. The attempt ends `BudgetExceeded` / `AttemptDurationBudget` as today. |
| Cancellation (`DELETE`, host shutdown) while invoked | `Cancelled` + a step recording "cancelled while running; outcome unknown", atomic, written with `CancellationToken.None`; the step is added to the run's history first so the attempt's later progress save keeps it | Not run (the task's token is cancelled), as ADR-0030 does | `Ambiguous`; the cancellation propagates; the launcher writes `Cancelled` as today. |
| Cancellation or attempt expiry after the intent, before invocation | `NotInvoked` + step, atomic | None (nothing executed) | `Settled` (known not executed); the stop propagates as today. |
| Exception escaping after invocation, outside the tool (audit/store failure, runtime defect) | None (the outcome commit did not happen) | — | `Pending`; ADR-0039 backstop ends the attempt `Failed`/`RuntimeFailure`; resume refused until reconciled. |
| Outcome commit refused (superseded) | None | — | Stop; audit `OutcomeNotCommitted`; entry stays as it is. |

The behaviour change is narrow and deliberate: today a non-`Read` timeout whose verification is not `Confirmed` replans
and lets the model try again; after F-25 it ends the attempt. That is the only way to guarantee P3 for a call that may
still be running, consistent with ADR-0030 (ambiguous → reconciliation) and HARDEN-8 (an interrupted side-effecting
action ends the attempt).

## 8. D8, D9 — Representing ambiguity, and the orphaned `Running` task

### 8.1 Representation: existing status + typed terminal reason + journal projection (Options B + C)

- **No new `AgentTaskStatus`** (plan §16 and ADR-0040 forbid it, and none is needed).
- **`TaskTerminalKind`** gains `MutationOutcomeUnknown = 14` (an in-process unknown ended the attempt) and
  `ExecutionInterrupted = 15` (an administrator recovered an orphaned `Running` attempt). Additive, explicit values.
- **The journal is authoritative** for resumability. A task's status and terminal reason describe how its attempt
  ended; whether a mutation may have happened is read from the journal. A `Cancelled` task can therefore be
  non-resumable (`mutation_outcome_unknown`) without any new status.
- **`TaskState.MutationJournalMode`** (`TaskMutationJournalMode`: `Absent = 0`, `Journaled = 1`, `MutationsDisabled =
  2`), set by the runtime when it creates an ordinary task and never changed afterwards. `Absent` is what every row
  written before F-25 deserializes to. Delegated tasks keep `Absent` (irrelevant: origin refuses them first).

Option A (new status) is rejected for the reasons above. Option C alone (projection, no terminal kind) would leave an
in-process unknown labelled `Failed`/`RuntimeFailure` or similar — a lie about why the attempt ended.

### 8.2 Orphaned `Running`: alternatives

| Alternative | Verdict |
|---|---|
| 1. Keep `Running` non-resumable; add a separate administrator recovery action | **Chosen**, combined with 3. |
| 2. Persistent ownership lease / heartbeat / epoch | Rejected for F-25. A lease is a liveness heuristic, not proof: a paused process (VM suspend, debugger, GC stall, a 12-minute PRE-1 tool) outlives any threshold, and a lease cannot stop its in-flight external effect. It would add a heartbeat write path to every executor. With fenced intents (below) it adds no safety; it may later add *convenience* (suggesting recovery), under its own ADR. |
| 3. Operator-confirmed recovery with CAS and fencing | **Chosen.** |
| 4. Startup containment (mark own orphans on boot) | Rejected: a booting process cannot know whether the CLI or another API process owns the task; it would be the forbidden automatic transition (§10). |
| 5. Resume `Running && !executing` | Rejected (ADR-0040 D-3; `executing == false` is not proof). |

### 8.3 The recovery transition

`POST /api/agents/tasks/{id}/recover` (administrator; CLI `bops recover`), body `{ "executionAttempt": N }` — the
attempt the administrator was shown. Ordered checks, first match refusing:

| # | Condition | Refusal | HTTP |
|---|---|---|---|
| 1 | no such task | — | 404 |
| 2 | `Origin = Delegated` | `task_delegated` | 409 |
| 3 | `Origin = Unknown` | `task_origin_unknown` | 409 |
| 4 | status ≠ `Running` | `task_not_running` | 409 |
| 5 | an execution attempt of the task is registered in **this** host's launcher | `task_executing_here` (use `DELETE` to cancel it) | 409 |
| 6 | `MutationJournalMode = Absent` (legacy) | `mutation_journal_absent` | 409 |
| 7 | store lacks `ITaskTransitionStore` | `transition_unsupported` | 501 |
| 8 | `Journaled` and store lacks `ITaskMutationJournalStore` | `mutation_journal_unsupported` | 501 |
| 9 | persisted attempt ≠ requested `executionAttempt` | `recovery_conflict` | 409 |
| 10 | `TryTransitionAsync((Failed, N) + synthetic "Execution interrupted" step + TerminalReason ExecutionInterrupted, Running, N)` returns `false` | `recovery_conflict` | 409 |

Success: **200** with the task view. It writes the task row only, executes no tool, runs no verification, asks no
approval, and changes no journal entry. Audited `TaskLifecycleAuditEvent` `RecoveryAccepted` (or `RecoveryRejected`
with the code), plus one `TaskMutationAuditEvent` `AmbiguityDiscovered` per unsettled entry found after the transition.

**Why this is safe without proving the old executor dead.** Suppose an executor of attempt N is still alive somewhere.

1. Any mutation it can still perform has a **committed intent** (P1: it invokes a tool only after the fenced intent
   committed). That intent is in the journal as `Pending`, so the recovered task is not resumable until it is
   reconciled, and no reconciliation action re-executes it.
2. It cannot **begin** another mutation: its next intent requires `(Running, N)`, which no longer exists.
3. It cannot **settle** anything or overwrite the task: every write it makes is fenced on `(Running, N)` (§6.4).
4. Recovery itself performs no action on the managed system.

So the worst case is: the old executor completes the one invocation it had already intended, after recovery. That
invocation is recorded as possibly applied; the runtime never repeats it; an identical new call requires a human
approval that names it (§9.6). That is "never unknowingly twice". ADR-0040 D-3 is not weakened: `Running` stays
non-resumable, `executing == false` is not used as proof (row 5 only *refuses* when the local host is executing), and
recovery is not a resume.

## 9. D7, D11, D12 — Reconciliation, resume and approvals

### 9.1 Who and what

Reconciliation is an **administrator** decision (API `AdministratorPolicy`; CLI as the local OS user, exactly like
`bops delegate reconcile`), by a human identity (the runtime refuses an agent or `RuntimeSystem` identity, as
`DelegationRunner.ReconcileAsync` does). Allowed only when the task is `Ordinary`, `Journaled`, the store has the
journal capability, the task is **not** `Running` (recover first) and at least one entry is unsettled.

`POST /api/agents/tasks/{id}/reconcile`, body `{ "action": "verify" | "acceptDone" | "abandon", "note"?: string }`
(note ≤ 500 characters, audit only). Ordered checks, first match refusing (each refusal audited `ReconcileRejected`):

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
| 11 | a per-entry CAS of the **Reconcile** write fails | `reconcile_conflict` | 409 |

The only actions:

| Action | Applies to | Effect | Executes |
|---|---|---|---|
| `verify` | each unsettled entry, in `Sequence` order | Re-runs the entry's declared verification (§9.3). `Confirmed` → `ReconciledDone` (`VerifiedDone`); otherwise → `Escalated` (`EscalatedToOperator`, the status recorded). | Only the declared `Read` verification tool |
| `acceptDone` | every unsettled entry | `ReconciledDone` (`OperatorAcceptedDone`, the last verification status kept). The runtime treats it as executed; its knowledge stays "unknown" in the view. | Nothing |
| `abandon` | every unsettled entry | `Abandoned` (`OperatorAbandoned`); the task is permanently refused `task_abandoned`. What it was meant to do is a new task with new approvals. | Nothing |

**No retry action exists**, and no "accept as not executed": the runtime cannot verify a negative claim, and a
"not executed" mark would turn an unknown into a licence to execute the same mutation again. An administrator who knows
it did not happen abandons the task and starts a new one (ADR-0030 §7: "a retry is a new objective").

### 9.2 Ordinary tasks adopt ADR-0030's rule — with one difference

`Confirmed` settles as done; `Refuted` and `Inconclusive` escalate to the administrator; nothing is retried. The
difference: ADR-0030 runs the verification automatically during the run's resume; here it runs only on the explicit
`verify` action, because an ordinary resume must stay a fast atomic transition (ADR-0040 §4.3) and verification may take
up to the verifier's PRE-1 timeout. `verify` may be repeated on `Escalated` entries (a long operation may complete later,
R7); it can only ever produce `VerifiedDone` or keep `Escalated`, so repetition is never less safe.

### 9.3 Reconciliation verification, exactly

For an entry `(N, s)`:

1. Find the persisted step with `ExecutionAttempt == N`, `Index == s`, a `ToolCall` whose `ToolName` equals the entry's
   and whose redacted-canonical arguments hash equals `ArgumentsHash`. None → `Inconclusive` (`arguments_unavailable`).
   This is the case for C2–C6.
2. Resolve the tool in the **current** registry; it must be registered, implement `IVerifiableTool`, and declare a
   `VerificationSpec` whose `VerifyToolName` equals the entry's `VerificationToolName`. Otherwise `Inconclusive`
   (`verification_unavailable`).
3. Run the same code path as post-action verification (`ExecuteVerificationToolAsync` + `EvaluateVerificationAsync`):
   argument projection and validation, fresh entitlement (ADR-0036), the verifier's bounded timeout, exceptions
   contained as `Inconclusive`. No policy, no approval (runtime-mandated `Read`, as today).
4. The verifier failing, timing out or being denied is `Inconclusive` (R10). Never `Confirmed` by default (S4).

Each entry's result is committed with the **Reconcile** write (per-entry CAS). Two concurrent `verify` calls may both
read the system (harmless, a `Read`); only one commits per entry; the other answers **409 `reconcile_conflict`**.

### 9.4 Resume rules (amending ADR-0040 §3)

`TaskResumePolicy.Evaluate` gains a journal input (availability + entries) and these rows, inserted **after** ADR-0040
row 6 and **before** row 7:

| # | Condition | Refusal code | HTTP |
|---|---|---|---|
| 6a | `MutationJournalMode = Absent` | `mutation_journal_absent` | 409 |
| 6b | `Journaled` and the store lacks `ITaskMutationJournalStore` | `mutation_journal_unsupported` | 501 |
| 6c | the journal could not be read | `mutation_journal_unavailable` | 503 |
| 6d | any entry `Abandoned` | `task_abandoned` | 409 |
| 6e | any entry `Pending`, `Ambiguous` or `Escalated` | `mutation_outcome_unknown` (message names count, tools, attempts) | 409 |

`MutationsDisabled` tasks pass 6a–6e on any store (no mutation can have begun) and keep ADR-0040's resumability,
including on a third-party store with `ITaskTransitionStore` only. The atomic **Acquire** (§6.3) re-checks 6d/6e inside
the transition, so a concurrent change between evaluation and acquisition cannot slip past it.

### 9.5 History after reconciliation (ADR-0038)

The acquisition that starts the next attempt appends, for every `ReconciledDone` entry not yet recorded, one
runtime-authored synthetic step `Mutation reconciled` (no `ToolCall`, not counted in any budget) whose observation
states the tool, the attempt and step, the arguments fingerprint (first 12 hex characters), and either "its declared
verification confirmed the effect" or "an administrator accepted it as already applied (verification: …)", and that it
is not repeated automatically. These steps are persisted in the acquired `TaskState` in the same transaction that marks
`history_recorded_in_attempt`, so every later rebuild of the history — live or resumed — is identical (ADR-0038; this is
persisted history, not the per-resume note ADR-0040 §11 withdrew). The acquisition is the only writer of a non-`Running`
task row, which keeps ADR-0040's invariant that a terminal `(status, attempt)` row changes only by a transition.

### 9.6 Approvals and policy after restart (D11)

- Approvals are never persisted (ADR-0022 §6). An approval obtained by attempt N for a call whose intent did not commit
  is void; one whose intent committed was consumed by that intent. Recovery, reconciliation and resume grant no
  execution authority, and none of them is an approval.
- Every call of the resumed attempt runs every gate again: `Forbidden` stays forbidden (no intent ever); `Approval` asks
  a currently authenticated approver; `Automatic` stays automatic — **except**:
- **Duplicate guard.** A non-`Read` call whose `(ToolName, ArgumentsHash)` equals that of a `ReconciledDone` entry of
  the same task is escalated from `Automatic` to `Approval`, with a reason that says it is identical to a mutation
  reconciled as already applied (verified or administrator-accepted). The escalation happens in the policy step, beside
  the existing `RequiresExplicitApproval` escalation; it never relaxes a decision. It applies to `ReconciledDone`
  entries only, because those are the mutations the model knows of only through a runtime summary.

## 10. D10 — Process startup

Startup does **nothing new**: no recovery, no verification, no reconciliation, no resume, no write. Detection,
classification and surfacing happen **on read** — the task view (§14) computes `executing`, `recoverable`, the
journal's unsettled count and the refusal codes whenever a client reads a task. The only path from "process restarted" to "a tool
runs again" passes through three explicit, audited human actions: recover (administrator), reconcile if needed
(administrator), resume (operator) — and then the gates of every new call.

## 11. D14 — Store capability and third-party stores

```csharp
public interface ITaskMutationJournalStore : ITaskTransitionStore
{
    Task<bool> TryRecordIntentAsync(TaskMutationIntent intent, CancellationToken ct = default);
    Task<bool> TryRecordOutcomeAsync(TaskMutationKey key, TaskMutationOutcome outcome, TaskMutationState state,
        TaskState owningTask, CancellationToken ct = default);
    Task<TaskJournalSnapshot?> LoadWithJournalAsync(Guid taskId, CancellationToken ct = default);
    Task<bool> TryReconcileAsync(Guid taskId, AgentTaskStatus expectedStatus, int expectedExecutionAttempt,
        IReadOnlyList<TaskMutationResolution> resolutions, CancellationToken ct = default);
    Task<bool> TryAcquireAsync(TaskState acquired, AgentTaskStatus expectedStatus, int expectedExecutionAttempt,
        IReadOnlyList<TaskMutationKey> recordedInHistory, CancellationToken ct = default);
}
```

- Additive capability, like ADR-0040's `ITaskTransitionStore`, which it extends (the journal is fenced against the
  task row, so it cannot exist without conditional task transitions). `ITaskStore` is unchanged; every third-party
  store keeps compiling.
- Every method is atomic at the persistence level across threads and processes sharing the store; it must never be
  implemented as load, check in process, save. `LoadWithJournalAsync` reads the task and its entries in one read
  transaction (a consistent snapshot).
- **Fail closed, decided once per task.** When the runtime creates an ordinary task it records
  `MutationJournalMode = Journaled` if the store implements the capability, else `MutationsDisabled`. A
  `MutationsDisabled` task refuses every non-`Read` call at the journal-mode gate (§3.2), for its whole life, even if the
  store later gains the capability — so "no mutation ever began" stays provable and its read-only work stays resumable.
  A `Journaled` task on a store that lacks the capability (a misconfiguration) refuses resume (`mutation_journal_unsupported`)
  and refuses execution of non-`Read` calls. There is no path "journal unavailable → execute anyway".
- `SqliteTaskStore` implements it. Test doubles that execute non-`Read` tools must implement it too.

## 12. D16, D17 — Legacy tasks and read-only work

### 12.1 Legacy (`MutationJournalMode = Absent`: every ordinary row written before F-25)

No intent is invented. A legacy row cannot prove that its last attempt did not end inside a mutation (cancellation,
backstop and crash windows all leave no trace), so:

| Legacy row | Resume | Recover | Reconcile |
|---|---|---|---|
| `Failed`, `Cancelled`, `MaxStepsReached`, `ReplanLimitReached`, `BudgetExceeded` | **Refused** `mutation_journal_absent` | n/a | n/a |
| `Running` orphan | Refused `task_running` (unchanged) | **Refused** `mutation_journal_absent` — stays as ADR-0040 left it | n/a |
| Containing completed non-`Read` steps | Refused (as its status row) | as above | n/a |
| `Completed`, `PolicyBlocked`, delegated, unknown origin | Unchanged ADR-0040 refusal (rows 1–5 come first) | Unchanged | n/a |

The operator starts a new task. This is the same trade ADR-0040 made for pre-origin rows: the affected rows are
preview-era tasks, and resuming them is not worth an unprovable assumption.

### 12.2 Read-only work (P6)

`Read` calls never have an entry. A task interrupted during a `Read` (or a model call, or between steps) has no unsettled
entry: after recovery it is resumable under ADR-0040's ordinary rules and the reads are simply repeated. "Read
interrupted" and "mutation outcome unknown" are different states with different codes; the second never arises from
the first.

## 13. ADR-0040 amendments (exact)

| ADR-0040 § | Amendment |
|---|---|
| §3 | Rows 6a–6e of §9.4 inserted between rows 6 and 7. Row 3 (`Running` never resumable) unchanged. |
| §4.3 step 4 | With a store implementing `ITaskMutationJournalStore`, the acquisition is `TryAcquireAsync` (journal predicate + §9.5 history), not `TryTransitionAsync`. `SqliteTaskStore.TryTransitionAsync` enforces the same predicate for any acquisition. |
| §4.4 | "No lease, heartbeat or forced orphan resume is introduced; an explicit orphan-recovery design … is future work with its own ADR": **this ADR is that design**, by operator authorization plus journal fencing. Still no lease, heartbeat or orphan *resume*. |
| §5.2 | `Execution interrupted` (recovery) and `Mutation reconciled` (§9.5) are synthetic steps, never counted against a step budget. |
| §6 | `TaskTerminalKind.MutationOutcomeUnknown = 14`, `ExecutionInterrupted = 15`. |
| §9 | View fields and the `recover` / `reconcile` endpoints of §14; `/events` also emits when the journal summary changes. |
| §10 | `TaskLifecycleStage.RecoveryAccepted = 5`, `RecoveryRejected = 6`. |
| §12 | Restated by §9.6, plus the duplicate guard (an escalation only). |

## 14. D18, D19 — Audit, API, CLI, UI (minimum contract)

### 14.1 Audit

New `TaskMutationAuditEvent` (discriminator `taskMutation`, appended to the hash chain like every event; distinct from
`DelegationJournalAuditEvent`, which keeps its delegation-correlated meaning). Fields: base fields (`TaskId`, `Actor`,
`StepIndex`), `Stage`, `ExecutionAttempt`, `Sequence?`, `Tool`, `ArgumentsHash`, `OutcomeKind?`, `ToolOutcome?`,
`Verification?`, `State?`, `ReconciliationAction?`, `ResolvedBy?`, `ReasonCode?`, `Note?` (administrator note, bounded).
Never arguments, output, error text, observation, credentials, provider material.

| Required observation | Event |
|---|---|
| intent durably committed | `TaskMutationAuditEvent` `IntentCommitted` (after the commit returned) |
| intent not committed (superseded, store failure, mutations disabled at the gate) | `IntentNotCommitted` + `ReasonCode` (`superseded`, `store_failure`, `mutations_disabled`) |
| outcome durably committed | `OutcomeCommitted` (kind, tool outcome, verification, state) |
| outcome refused / late write | `OutcomeNotCommitted` + `ReasonCode` (`superseded`, `store_failure`) — carries the outcome the writer saw |
| ambiguous mutation discovered | `AmbiguityDiscovered` — when an in-process outcome commits `Ambiguous`, and for each unsettled entry after a recovery |
| verification reconciliation attempted / result | `VerificationAttempted` then `Reconciled` (`VerifiedDone` or `EscalatedToOperator`, with status and `ReasonCode` such as `arguments_unavailable`) |
| operator reconciliation | `Reconciled` (`OperatorAcceptedDone` / `OperatorAbandoned`, `ResolvedBy`, `Note`) |
| reconcile refused | `ReconcileRejected` + `ReasonCode` |
| recovery accepted / refused | `TaskLifecycleAuditEvent` `RecoveryAccepted` / `RecoveryRejected` + `RefusalCode` |
| resume refused for a journal reason | existing `ResumeRejected` with the new `RefusalCode`s |

The existing `ToolCallAuditEvent` / `PolicyDecisionAuditEvent` / `ApprovalAuditEvent` are unchanged; the duplicate
guard's escalation appears in the `PolicyDecisionAuditEvent` reason.

### 14.2 API

- **Task view** (`GET` one, list, `/events`), additive: `mutationJournal: { mode, available, unsettledCount, entries: [
  { executionAttempt, stepIndex, sequence, tool, risk, argumentsFingerprint (12 hex), intentAtUtc, plannedStepIndex,
  planRevision, outcome: { kind, toolOutcome, failureKind, verification, atUtc } | null, state, knowledge:
  "KnownNotExecuted" | "KnownExecuted" | "Unknown", reconciliation: { action, verification, resolvedBy, atUtc } | null
  } ] }`, `recoverable`, `recoveryBlockedReason` (`{ code, message }` from the §8.3 policy evaluated without the request
  body), and the existing `resumable` / `resumeBlockedReason` now including §9.4. `executionPlan` (PRE-4) shows a
  planned step whose entry is unsettled as `OutcomeUnknown`, not `Pending`/`Failed`/`Completed`.
- `POST /api/agents/tasks/{id}/recover` — administrator; §8.3 codes; 200 with the view; 400 when `executionAttempt` is
  missing or not positive.
- `POST /api/agents/tasks/{id}/reconcile` — administrator; 200 `{ taskId, results: [ { executionAttempt, stepIndex,
  state, reconciliation } ], unsettledCount, resumable, resumeBlockedReason }`; refusals in the order and with the
  codes of §9.1.
- `POST /api/agents/tasks/{id}/resume` — unchanged contract; new 409/501/503 codes of §9.4.

### 14.3 CLI

`bops recover <task-id> --attempt <N>`; `bops reconcile <task-id> verify|accept|abandon [--note <text>]`; `bops resume`
prints the new refusal codes. Same runtime functions, same codes, non-zero exit on refusal.

### 14.4 UI (spec only; F-25B)

- `Running && !executing`: "Interrupted" (existing) plus, for administrators, **Recover** with a confirmation that says it
  runs nothing, stops any other process from recording progress for this attempt, and that an operation already started
  by that process may still complete and will be shown as "may have been applied".
- Any unsettled entry: a banner on the task — "An operation may or may not have been applied: `<tool>` (attempt N,
  step s). bOps will not repeat it automatically." — with administrator actions **Verify again**, **Accept as already
  applied**, **Abandon task**, and no retry button. The status badge keeps the persisted status with an "outcome
  unknown" qualifier; it is never shown as a plain "Failed".
- Each entry shows its knowledge in words: "not executed", "executed (tool reported …, verification …)", "unknown —
  may have been applied", "accepted as applied by <administrator>". EN/IT, existing layout rules.

## 15. D23 — Guarantees

| # | Property | Enforced by |
|---|---|---|
| P1 | A non-`Read` operation of an ordinary task never begins unless its intent is durably recorded. | §3.2 boundary; fenced intent commit returns before invocation; `MutationsDisabled` gate |
| P2 | A durable intent without a settled outcome is never treated as proof that the operation did not happen. | §4 classification; no outcome = `Pending` = unknown |
| P3 | An ambiguous mutation is never automatically re-executed. | §7.3 attempt ends; §9.4 resume refused; §9.1 no retry action; §9.6 duplicate guard |
| P4 | A mutation proven (or accepted as) already applied is never re-executed by resume, recovery or reconciliation; an identical new call needs a human approval naming it. | §9.1, §9.5, §9.6 |
| P5 | Restart/resume never reuses an old human approval for new execution. | ADR-0022 §6; §6.4 late intent; §9.6 |
| P6 | Read-only interrupted work remains separately recoverable. | §3.1, §12.2 |
| P7 | Late writes from stale execution attempts cannot authorize, settle or replay a mutation, and cannot erase its intent. | §6.3 preconditions, §6.4 |
| P8 | Raw mutation arguments and tool output are not persisted in the intent journal. | §5.2–§5.4 (hash over redacted canonical arguments only) |
| P9 | ADR-0040's `Running`/executor safety is not weakened implicitly. | §8.3 (recovery is not resume, `executing` is never proof), §13 explicit amendments |
| P10 | No automatic resume, recovery or reconciliation occurs on process startup. | §10 |
| P11 | Every journal write is atomic and fenced against the task row across processes. | §6.3, §11 |
| P12 | A settled outcome is never durable without the history step that records it. | §6.3 atomic outcome + step |
| P13 | Recovery and reconciliation never invoke a non-`Read` tool. | §8.3, §9.1, §9.3 |
| P14 | Without a usable journal no mutation runs. | §11 |
| P15 | Pre-F-25 rows are never assumed safe. | §12.1 |

## 16. Residuals, stated plainly

- **R-1** `ExecuteExecutionPlanAsync` / Skill runs without a task are not journaled (not resumable, outside F-25). A
  crash during one leaves only the audit of earlier steps. Proposed as a follow-up finding, not F-25 scope.
- **R-2** Crash windows C2–C6 always require an administrator (arguments are not persisted, P8). This is the accepted
  cost; it can be revisited only by an ADR that changes P8.
- **R-3** An executor alive in another process when its task is recovered can complete the one invocation it had already
  intended. That invocation is recorded as possibly applied and never repeated by the runtime.
- **R-4** `OperatorAcceptedDone` is an administrator's assertion, not knowledge; the view says so.
- **R-5** Downgrading to a pre-F-25 binary is unsupported (as for ADR-0040): an old binary ignores the journal and could
  resume a task with an unsettled entry. Release notes must state it.
- **R-6** The duplicate guard matches exact `(tool, redacted-arguments hash)`; a semantically equal call with different
  arguments is not caught. It is defence in depth, not the mechanism P3 rests on (the attempt end and the resume refusal
  are).

## 17. Design review against required failure modes

| # | Failure mode | Deterministic, fail-closed result |
|---|---|---|
| R1 | Crash immediately before `ExecuteAsync` | C2: `Pending`; after recovery resume refused `mutation_outcome_unknown`; `verify` → `arguments_unavailable` → `Escalated`; administrator accepts or abandons; tool count stays 0. Never "not executed". |
| R2 | Side effect occurs but the tool never returns | PRE-1 timeout → `TimedOut` + step; verification; not `Confirmed` → `Ambiguous`, attempt ends `MutationOutcomeUnknown`. If the process dies instead: C3. Either way no retry. |
| R3 | Effect occurs, verification confirms, process dies before journal completion | C6: in-memory `Confirmed` lost, `Pending`; `verify` cannot run (no step) → `Escalated`; administrator decides. Never re-executed. |
| R4 | Old execution writes after a new recovery attempt | Intent and outcome writes require `(Running, N)`; refused; audited `IntentNotCommitted`/`OutcomeNotCommitted`; executor stops superseded (§6.4). |
| R5 | Two concurrent recovery requests | Both evaluate; one `TryTransitionAsync((Failed,N), Running, N)` wins (200); the other gets `false` → 409 `recovery_conflict`. Concurrent reconciliations: per-entry CAS, one 200, the other 409 `reconcile_conflict`. |
| R6 | Task contains a superseded planned mutation | A superseded or skipped planned step never reached `ExecuteStepAsync` → no entry. An executed call of an earlier revision has its own entry; its `PlanRevision` is context only. |
| R7 | Timeout cancellation arrives while the external operation continues | `TimedOut`; verification may be `Refuted` now → `Ambiguous` (not "not executed"); attempt ends; `verify` later may `Confirm` → `VerifiedDone`. |
| R8 | Legacy row has no journal | `Absent` → resume `mutation_journal_absent`, recovery refused; no intent invented. |
| R9 | Store cannot provide durable/fenced journal semantics | New tasks `MutationsDisabled` (non-`Read` refused before approval); `Journaled` task on such a store refuses resume/recovery/execution (`mutation_journal_unsupported`). |
| R10 | Verification tool fails or times out | Post-action: `Inconclusive` → unknown kinds stay `Ambiguous`. Reconciliation: `Inconclusive` → `Escalated`. Never `Confirmed` by default. |

## 18. Alternatives considered

- **Journal inside `TaskState` (A).** Rejected: no per-entry CAS for concurrent reconciliations, every `TaskState`
  writer could drop it, whole-state rewrite per intent (§6.1).
- **Generalize the delegation `IStepJournal` (C).** Rejected: its writes are fenced against the delegation run, not the
  task row; its `StepOutcomeKind.Failed` would label a possibly-applied call "failed"; it would widen ADR-0030's
  internal contract. Its *types* for reconciliation are reused.
- **Persist the full or `ArgumentsFrom` arguments in the intent** so C2–C6 can be verified automatically. Rejected for
  P8 and because `EvaluateVerificationAsync` reads full arguments (R-2).
- **Record the outcome before verification** (two outcome writes). Rejected: a settled outcome would exist without its
  step; the resumed model would not know the mutation happened.
- **Let the loop continue after an unconfirmed mutation timeout** (block only resume). Rejected: the model may re-propose
  the mutation (automatically under `Automatic` policy) while the first may still be running; the duplicate guard
  matches exact arguments only.
- **Accept a late outcome from a recovered attempt.** Rejected (§6.4): it would settle an entry whose step cannot be
  written.
- **Lease / heartbeat ownership; startup containment; resume of `Running && !executing`.** Rejected (§8.2).
- **New `AgentTaskStatus` (e.g. `RequiresReconciliation`).** Rejected (plan §16, ADR-0040).
- **Run reconciliation verification inside resume, as ADR-0030 does.** Rejected for ordinary tasks (§9.2).
- **An "accept as not executed" or "retry" reconciliation action.** Rejected (§9.1).

## 19. Consequences

- A crash, cancellation or timeout during a mutation can no longer make an unknown outcome look like a safe retry.
  Orphaned `Running` tasks gain an audited, fenced, administrator-only recovery path, without lease machinery and without
  weakening ADR-0040 D-3.
- Behavioural changes: a non-`Read` timeout or cancellation that verification does not confirm ends the attempt and
  blocks resume until an administrator reconciles; a duplicate of a reconciled mutation needs approval; pre-F-25 ordinary
  tasks are no longer resumable; a third-party store without the capability can no longer run non-`Read` tools in new
  tasks.
- Public surface grows, additively: `bOps.Abstractions` — `TaskMutationJournalMode`, `TaskState.MutationJournalMode`,
  `ITaskMutationJournalStore`, `TaskMutationIntent`, `TaskMutationOutcome`, `TaskMutationKey`,
  `TaskMutationJournalEntry`, `TaskMutationResolution`, `TaskJournalSnapshot`, `MutationOutcomeKind`,
  `TaskMutationState`, `TaskMutationAuditEvent`, `TaskMutationAuditStage`, two `TaskTerminalKind` and two
  `TaskLifecycleStage` values; reused unchanged: `ReconciliationAction`, `StepReconciliation`. `bOps.Runtime` —
  `MutationJournalPolicy`, `TaskRecoveryPolicy` and its decision/refusal types, the journal-aware
  `TaskResumePolicy.Evaluate` overload, `AgentRunner` recover/reconcile members, new refusal codes. `bOps.Api` — two
  endpoints and view fields. `tasks.db` gains one table on first open by the new binary.
- Crash testing grows by one injection point per journal write (F-25B test matrix).

## 20. Decisions (accepted 2026-10-09, D-047)

| # | Decision |
|---|---|
| D-1 | Journal every ordinary-task invocation with `Risk > Read` at the single boundary after entitlement and before `ExecuteWithTimeoutAsync`; nothing refused or unexecuted has an intent. |
| D-2 | Storage B: `task_mutation_journal` in `tasks.db`, every write fenced on the task row in one statement/transaction; synchronous=FULL. |
| D-3 | Identity `(TaskId, ExecutionAttempt, StepIndex)` + per-task `Sequence`; plan revision/planned index are context. |
| D-4 | Intent stores tool, redacted-canonical arguments hash, risk, declared verifier, plan context, time — never arguments or output. |
| D-5 | Outcome kinds `NotInvoked`/`Returned`/`TimedOut`/`Cancelled`/`Interrupted`; states `Pending`/`Settled`/`Ambiguous`/`Escalated`/`ReconciledDone`/`Abandoned`; settled outcome committed atomically with its step. |
| D-6 | In-process unknown not `Confirmed` ends the attempt (`MutationOutcomeUnknown`, or the existing kind for cancel/attempt budget); timeout is never a retry. |
| D-7 | No new status; `TaskTerminalKind` +2; the journal is authoritative for resumability; refusal rows 6a–6e. |
| D-8 | Orphaned `Running`: administrator-only fenced recovery `(Running,N) → (Failed,N)/ExecutionInterrupted`; executes nothing; no lease, no orphan resume; refused when executing in this host. |
| D-9 | Reconciliation by an administrator: `verify` (declared verification using the task's persisted call), `acceptDone`, `abandon`; no retry, no "not executed". |
| D-10 | Acquisition appends `Mutation reconciled` history steps atomically; duplicate guard escalates identical calls to approval. |
| D-11 | `ITaskMutationJournalStore : ITaskTransitionStore`; tasks created without it are `MutationsDisabled` for life. |
| D-12 | Legacy (`Absent`) ordinary tasks: not resumable, not recoverable. |
| D-13 | `TaskMutationAuditEvent` + two lifecycle stages; no payloads. |
| D-14 | API `recover` / `reconcile` (administrator), view fields; CLI equivalents; UI states. |
| D-15 | Nothing at startup. |

## 21. Implementation evidence (F-25B, 2026-10-10)

F-25B implements the accepted decisions without changing them: the journal is SQLite-fenced with atomic outcome plus
history persistence; recovery/reconciliation and journal-aware resume are available through API, CLI and the EN/IT
dashboard. Local deterministic crash/behavior/property coverage is green (150 tests, including the 96-case
no-double-execution property). Windows Release build is 0 warnings/0 errors and Angular production/headless/i18n
gates are green. M1–M24 mutation capture and CI remain required before completion. WSL Ubuntu is present but lacks
`dotnet`; Linux execution is retained as CI evidence.
