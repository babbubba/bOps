# ADR-0040 — Resume as a persisted state-machine transition: execution attempts, budgets, atomic transitions, lifecycle audit

Status: Accepted (2026-09-30, operator decision, with the amendments recorded below)

Amends ADR-0017 (persistent, resumable tasks) and ADR-0018 (`bOps.Api` minimal surface). Their text is not edited;
each carries an `Amended by: ADR-0040` pointer. Carries the ADR-0022-style note for additive `bOps.Abstractions`
members. Governs HARDEN-3 of the V1.3.x reliability train (`agentic/_plans/2026-09-25-v1.3x-reliability-hardening.md`
§8, findings F-03 backend, F-04, F-05 guard, F-06 backend, F-07; defects C-04, C-05 backend, C-06, C-07, C-08, C-09,
C-10, C-11, C-12). Builds on ADR-0039 (HARDEN-2) terminal-failure semantics and does not change them.

**Operator amendments at acceptance.** D-3: a persisted `Running` task is **never** resumable by an ordinary resume,
whether or not an executor in this host holds it — the absence of a local executor does not prove that no other
process (the CLI, another API process, an old executor) is still running a tool call, and fencing cannot undo an
external side effect; no "force orphan resume" is introduced (the proposal's §4.4 caveat is removed, not accepted).
The atomic transition is an **additive capability interface** (`ITaskTransitionStore`), not a default member of
`ITaskStore`, and no store may emulate it with load/check/save; a store without it makes resume fail closed. The
task-level counter is named `ExecutionAttempt`, never `Attempt`, to stay distinct from ADR-0039's `ModelAttempt`.
D-9: no goal/prompt text heuristic decides whether a task is delegated; origin is explicit persisted metadata, and a
legacy row whose origin cannot be proven is `Unknown` and refused (`resume_origin_unknown`). D-7: the resume `202` is
returned only after the atomic transition **and** the launcher's admission of the new execution attempt; an
admission failure after the transition terminally contains that attempt (`Failed`, never left `Running`) and is not
a `202`. D-4 and D-5 accepted with clarifications (lifetime caps always win; legacy accounting is derived once and
materialized by the first authoritative write). D-10 (resume note) is withdrawn — see §11. D-1, D-2, D-6 and D-8
accepted as proposed, with the renames above.

## Context

ADR-0017 designed resume for one case: a task a crash left `Running`, continued by re-entering the same loop at the
next step index. ADR-0018 exposed it as `POST /api/agents/tasks/{id}/resume`, "`404` if no such task is stored". Read
at `1eb6f33` (HARDEN-2 merged), the code does more and less than that, and every defect below is reproducible by
reading it:

1. **Any status is accepted (C-08).** `AgentsEndpoints.cs:65-80` loads the task and hands it to
   `AgentTaskLauncher.TryResumeAsync` without looking at its status: `Completed`, `PolicyBlocked`, a role task and a
   task executing right now are all "resumed".
2. **202 before anything is persisted (C-04, C-05 backend).** `TryResumeAsync` (`AgentTaskLauncher.cs:57-65`) only
   starts a detached `runner.ResumeAsync`; the endpoint returns 202 immediately. `TryStartAsync` persists `Running`
   before its 202 (`AgentTaskLauncher.cs:37-40`); resume does not. `Running` is first written by the resumed loop at
   `AgentRunner.cs:426`, after the first complete step (one model call — 32 s in the incident). Until then every
   reader, including the SSE loop that stops at the first non-`Running` snapshot (`AgentsEndpoints.cs:147`), sees
   the old terminal status.
3. **Zero-plan tasks: 202, then a throw (C-07).** A task whose initial plan call failed is persisted `Failed` with
   `Plans.Count == 0` (`AgentRunner.cs:183-187`). `ResumeAsync` throws `InvalidOperationException` for it
   (`AgentRunner.cs:219-222`) inside the detached task, after the 202. The ADR-0039 backstop leaves the terminal row
   unchanged and only logs.
4. **`MaxStepsReached` resumes into zero work (C-06).** `ResumeAsync` enters the loop at `startStepIndex:
   steps.Count` and the loop runs `while stepIndex < options.MaxSteps` (`AgentRunner.cs:242-243, 259`). A task with
   15 steps and `MaxSteps = 15` performs no iteration and is saved `MaxStepsReached` again. `MaxSteps` is therefore an
   undocumented **lifetime** cap measured in step indices.
5. **Synthetic failure steps consume that cap (C-10).** `BuildFailed` (`AgentRunner.cs:2729-2735`) and the ADR-0039
   backstop (`AgentRunner.cs:1279-1280`) each append a step. A task that failed after 7 tool steps resumes with at
   most `15 − 8` steps; every failed resume shrinks it further.
6. **Tokens reset (C-09).** `ResumeAsync` passes `totalTokens: 0` (`AgentRunner.cs:242`): each resume gets a fresh
   `MaxTotalTokens` budget, so the token budget is bypassed by resuming. Replans are *not* reset
   (`replanCount = plans.Count − 1`), so the replan cap is a lifetime cap, also undocumented.
7. **No lifecycle audit (C-09).** Nothing records that an operator resumed a task, when, or with what budget left.
   ADR-0039 §9 added only the narrow `TaskExecutionFaultAuditEvent` and explicitly left the lifecycle record here.
8. **Role tasks resumable outside their envelope (C-11).** `DelegationRunner` runs the Discovery and Diagnostic roles
   through `AgentRunner.RunDelegatedAsync` with a fresh task id (`DelegationRunner.cs:850`), persisted in the same
   `ITaskStore`. Nothing in `TaskState` marks them, and the delegation store does not record role task ids. The task
   endpoint resumes them with `delegation: null` (`AgentRunner.cs:243`): the full tool view, no envelope, no
   correlation. The only legitimate path for a role is `POST /api/delegations/{id}/resume`, which restarts an
   interrupted role as a *new* role task (`DelegationRunner.cs:132-176`) and never calls `AgentRunner.ResumeAsync`.
9. **Duplicate and concurrent resumes (C-08).** Within one host, `_running.ContainsKey` plus `TryAdd` stops a second
   executor, but answers it with a body-less 503. Across processes (the CLI's `bops resume`, `Program.cs:276-286`,
   and the API share `tasks.db`) nothing stops two executors, and each overwrites the other's `SaveAsync`
   (`SqliteTaskStore.SaveAsync` is an unconditional upsert).
10. **Orphaned `Running` is indistinguishable from executing (C-12).** A persisted `Running` means both "a launcher
    here is executing it" and "the process died". The API does not expose which.
11. **Stale writers can overwrite newer state.** The launcher's cancellation path (`AgentTaskLauncher.cs:98-99`) and
    the ADR-0039 backstop (`AgentRunner.cs:1256-1284`) read then write without a precondition; ADR-0039 §9 recorded
    that "no compare-and-set store capability is added here (HARDEN-3)".

Unchanged and relied on: approvals are never persisted and a resumed task must obtain a new approval from a currently
authenticated approver (ADR-0022 §6); policy, entitlement and verification run on every step exactly as in a first
attempt; history reconstruction is the ADR-0038 one (`RebuildHistory` + `AddToolCallTurns`, including
`UnexecutedToolCalls`), independent of any provider payload.

## Decision

### 1. Task execution attempt (additive; not a new status)

- `TaskState.ExecutionAttempt` (`int`, 1-based). A task's initial execution is execution attempt **1**; the first
  accepted resume makes it **2**, and so on. Every accepted resume increments it by exactly one, in the same atomic
  write that sets `Running` (§4). A refused or lost resume never increments it. Rows written before this ADR have no
  such property and load as execution attempt 1.
- `PlanStep.ExecutionAttempt` (`int?`): the execution attempt that produced the step. `null` in rows written before
  this ADR, read as 1. `PlanStep.Index` stays globally monotonic across attempts (it is the position in `Steps`).
- Terminology: `ExecutionAttempt` ≠ ADR-0039's `ModelAttempt` (the retry index of one logical model call). No domain
  member is named plain `Attempt`. An API view may expose the persisted name (`executionAttempt`).
- No `AgentTaskStatus` value is added. "Interrupted" is a view label (§9), never a persisted status.

### 2. Additive task fields

| Member | Type | Meaning | Rows written before this ADR |
|---|---|---|---|
| `TaskState.ExecutionAttempt` | `int` | Current execution attempt (§1). | 1 |
| `TaskState.Accounting` | `TaskAccounting?` | Lifetime counters (§5): `TokensUsed` (`long`), `LifetimeSteps` (`int`), `LifetimeReplans` (`int`). Written by every runtime save from this ADR on. | `null`; derived per §5.4 |
| `TaskState.Origin` | `TaskOrigin` | `Unknown` (0), `Ordinary` (1), `Delegated` (2). Set by the runtime only; no request DTO can set it (§8). | `Unknown` |
| `TaskState.DelegationId` | `Guid?` | The delegated run, for `Delegated`. | `null` |
| `TaskState.DelegationRole` | `AgentRoleKind?` | The role, for `Delegated`. | `null` |
| `TaskState.TerminalReason` | `TaskTerminalReason?` | Why the latest execution attempt ended: `Kind` (§6) and, for a model failure, the ADR-0039 `ModelFailureKind`. `null` while `Running`. | `null` |
| `TaskState.ResumedAtUtc` | `DateTimeOffset?` | When the current execution attempt was acquired by a resume. | `null` |
| `TaskState.ResumedBy` | `ActorIdentity?` | Who acquired it. | `null` |
| `PlanStep.ExecutionAttempt` | `int?` | §1. | `null` (= 1) |

All are init-only properties with defaults (no constructor change, binary-compatible); nullable ones are omitted from
JSON when `null`. `TaskAccounting` and `TaskTerminalReason` are records; `TaskOrigin` and `TaskTerminalKind` are enums
with explicit values. The terminal reason carries no free text: the operator-facing failure text stays where ADR-0039
put it (the synthetic failure step), so no provider text is duplicated.

### 3. Resumability — one runtime function

`bOps.Runtime` gains one pure function, `TaskResumePolicy.Evaluate(TaskState task, AgentRunnerOptions options)`,
returning a `TaskResumeDecision` (`Resumable`, and a `TaskResumeRefusal` with a stable `Code` and an operator
`Message` when refused). `AgentRunner.EvaluateResume` applies it with the runner's options. The API, the CLI and the
task view all use it; no host re-implements a status check. Checks run in this order, the first match refusing:

| # | Persisted state | Resumable | Refusal code |
|---|---|---|---|
| 1 | `Origin == Delegated` (any status) | **No** | `task_delegated` — "Resume the delegation run." |
| 2 | `Origin == Unknown` (any status; every pre-ADR row) | **No** | `resume_origin_unknown` |
| 3 | `Running` (whether or not an executor in this host holds it) | **No** | `task_running` |
| 4 | `Completed` | **No** | `task_completed` |
| 5 | `PolicyBlocked` | **No** | `task_policy_blocked` (rule C4 dead end) |
| 6 | any status not listed as resumable below | **No** | `task_status_not_resumable` (fail closed for a future status) |
| 7 | `MaxTotalTokens` set and `TokensUsed ≥ MaxTotalTokens` (the value configured **now**) | **No** | `token_budget_exhausted` |
| 8 | `LifetimeSteps ≥ MaxLifetimeSteps` | **No** | `lifetime_steps_exhausted` |
| 9 | `ReplanLimitReached` and `LifetimeReplans ≥ MaxLifetimeReplans` | **No** | `lifetime_replans_exhausted` |

Resumable when none of the above refuses: `Failed` (any `ModelFailureKind`, a runtime failure, or no plan at all —
§7), `Cancelled`, `MaxStepsReached`, `ReplanLimitReached`, `BudgetExceeded`. Rows 7–9 apply to every resumable
status, so **an accepted resume always has at least one step and one token of headroom and can make progress**; a
resume that could not is refused, never accepted as a zero-work success. `BudgetExceeded` becomes resumable only
when an administrator has configured a `MaxTotalTokens` above the tokens already used; the consumed tokens remain.

### 4. The transition: an atomic persisted state change, then admission, then 202

#### 4.1 Store capability (additive interface)

`bOps.Abstractions` gains a capability interface, implemented by a store **in addition to** `ITaskStore`:

```csharp
public interface ITaskTransitionStore
{
    Task<bool> TryCreateAsync(TaskState task, CancellationToken ct = default);
    Task<bool> TryTransitionAsync(TaskState task, AgentTaskStatus expectedStatus, int expectedExecutionAttempt,
        CancellationToken ct = default);
}
```

- `TryCreateAsync` inserts the task only if no row with its id exists.
- `TryTransitionAsync` atomically — at the persistence level, across threads **and processes** sharing the store —
  replaces the row for `task.Id` with `task` if and only if the persisted status is `expectedStatus` **and** the
  persisted execution attempt is `expectedExecutionAttempt`; otherwise it changes nothing and returns `false`.
  `task.ExecutionAttempt` must be `expectedExecutionAttempt` (a write by the owning attempt) or
  `expectedExecutionAttempt + 1` (an acquisition); anything else is an argument error.
- A store must never implement it as load, check in process, save. A store that does not implement the interface
  makes resume refuse with `transition_unsupported` (fail closed). `ITaskStore` is unchanged, so third-party stores
  keep compiling and keep working for fresh tasks.
- `SqliteTaskStore.SaveAsync` additionally gains an attempt fence: a save whose `ExecutionAttempt` is lower than the
  persisted row's writes nothing and throws the new `TaskExecutionSupersededException` (in `bOps.Abstractions`).

#### 4.2 SQLite implementation and migration

- **Schema migration (additive):** `tasks` gains `execution_attempt INTEGER NOT NULL DEFAULT 1`. Applied in
  `EnsureSchema`, idempotently: added only when `PRAGMA table_info(tasks)` lacks it; a concurrent process that loses
  the race and gets "duplicate column" re-checks and continues. Existing rows get 1, matching their JSON default.
  Nothing is rewritten, dropped, recreated or renamed; `state_json` stays the source of the whole state. Downgrading
  to an older binary is not supported, as for any schema addition.
- `TryTransitionAsync` is one statement: `UPDATE tasks SET status, execution_attempt, updated_at_utc, state_json
  WHERE id = $id AND status = $expectedStatus AND execution_attempt = $expectedAttempt`; one affected row is success.
  SQLite serializes writers on the database file, so the compare and the write cannot interleave with another writer.
- `TryCreateAsync` is `INSERT … ON CONFLICT(id) DO NOTHING`. `SaveAsync` becomes `INSERT … ON CONFLICT(id) DO UPDATE
  SET … WHERE tasks.execution_attempt <= excluded.execution_attempt`.

#### 4.3 The resume sequence (API)

1. Load the authoritative task; none → **404** (unchanged).
2. `TaskResumePolicy.Evaluate`; refused → **409** `{ code, message }`, audited `ResumeRejected` (§10).
3. The store lacks `ITaskTransitionStore` → **501** `{ code: "transition_unsupported", message }`, audited.
4. Build the next snapshot from the loaded one: `Status = Running`, `ExecutionAttempt + 1`, `ResumedAtUtc`,
   `ResumedBy`, `TerminalReason = null`, `Accounting` = the task's (materialized once for a row without it, §5.4).
   `TryTransitionAsync(next, loaded.Status, loaded.ExecutionAttempt)`; `false` → **409** `{ code: "resume_conflict" }`
   (another resume or writer got there first), audited `ResumeRejected`. Nothing was written by this request.
5. Audited `ResumeAccepted`. Submit the acquired snapshot to the launcher.
6. The launcher admits it (a free execution slot and a registration keyed by task id **and** execution attempt) →
   **202** with the accepted transition (§9). The detached execution starts from the persisted snapshot.
7. The launcher does not admit it → the runtime transitions `(Running, N+1)` → `Failed` with
   `TerminalReason.Kind = NotAdmitted` and a synthetic `Execution not started` step, audits `ExecutionTerminal`, and
   the request answers **503** `{ code: "executor_unavailable", message }` with `Retry-After: 5`. Never `202`, never
   left `Running`. A later resume of that `Failed` task is an ordinary resume.

After a 202, a reader of the task (or of `/events`) observes `Running` for the accepted execution attempt, or a
terminal state written by that same execution attempt — never the stale pre-resume terminal state. Whatever the
detached execution then does, including throwing before its first step, the transition has already happened and the
ADR-0039 backstop moves it to a truthful terminal state (§4.5).

The CLI (`bops resume <id>`) runs steps 1–5 and then the execution attempt in-process, with the same refusal codes
and messages (printed, non-zero exit). It has no launcher and no admission step.

#### 4.4 Ownership and fencing

- Exactly one resume can win a given `(status, execution attempt)`; every other concurrent resume — same process or
  another one sharing the store — loses the transition and gets 409. **Two concurrent resumes: one 202, one 409**, one
  new execution attempt, no duplicate detached execution. This does not rely on the launcher's in-memory registry.
- The execution attempt is a **fencing token**. With a store that implements `ITaskTransitionStore`, every write by an
  executing attempt is `TryTransitionAsync(state, Running, itsAttempt)` — the attempt still owns the row only while it
  is `(Running, itsAttempt)`. The first write of a fresh execution attempt 1 is that transition (the API launcher
  created the row) or, when no row exists (CLI, delegated role), `TryCreateAsync`. A write that fails is refused as
  **superseded**: the executor stops at that point, writes nothing more to the task, and audits
  `ExecutionSuperseded` (§10). This covers the runner's per-step and terminal saves, the zero-plan re-plan, the
  launcher's cancellation write, the ADR-0039 backstop and the admission-failure containment — no terminal path is
  unfenced.
- Fencing protects **persisted state** only. It cannot undo an external side effect of a tool call already in flight.
  That is why a `Running` task is never ordinarily resumable (§3 row 3): an orphaned-looking task may still be executing
  in another process. No lease, heartbeat or forced orphan resume is introduced; an explicit orphan-recovery design
  (lease/heartbeat, staleness threshold, operator authorization or equivalent ownership proof) is future work with its
  own ADR.
- With a store that does not implement the capability, resume is refused (`transition_unsupported`); only execution
  attempt 1 can then exist, so the pre-ADR unconditional saves keep their meaning for fresh tasks.

#### 4.5 Post-execution writes

- **Launcher cancellation** (`DELETE`, host shutdown): after the executor exits, re-read and transition
  `(Running, thatAttempt)` → `Cancelled` (`TerminalReason.Kind = Cancelled`), then `ExecutionTerminal`. A newer attempt
  or any terminal state is never overwritten. Before the cancellation propagates, the runner persists (fenced) its
  in-memory accounting, so tokens already spent by the cancelled attempt are not lost.
- **ADR-0039 backstop** (`ContainEscapedFailureAsync`): transitions only `(Running, thatAttempt)` → `Failed`
  (`RuntimeFailure`). It still writes `TaskExecutionFaultAuditEvent` exactly as ADR-0039 §9 says, plus
  `ExecutionTerminal`.

### 5. Budgets: per execution attempt versus lifetime

#### 5.1 Semantics

| Budget | Scope | Option | Default |
|---|---|---|---|
| Steps | **Per execution attempt** | `Agent:MaxSteps` (existing key, semantics change) | 15 (unchanged) |
| Steps | **Lifetime** | `Agent:MaxLifetimeSteps` (new) | **60** |
| Replans | **Per execution attempt** | `Agent:MaxReplans` (existing key, semantics change) | 3 (unchanged) |
| Replans | **Lifetime** | `Agent:MaxLifetimeReplans` (new) | **12** |
| Tokens | **Lifetime, never reset** | `Agent:MaxTotalTokens` (existing) | unset (unchanged) |

- An execution attempt runs at most `min(MaxSteps, MaxLifetimeSteps − LifetimeSteps)` executable steps; the lifetime
  cap always wins. If the lifetime remainder is what stops it, the status is `MaxStepsReached` with
  `TerminalReason.Kind = LifetimeStepLimit`; otherwise `StepLimit`. Replans likewise (`ReplanLimit` /
  `LifetimeReplanLimit`, status `ReplanLimitReached`). The loop bound is the explicit per-attempt counter, never
  `Steps.Count`.
- Tokens accumulate in `Accounting.TokensUsed` across attempts; the existing `> MaxTotalTokens` checks compare the
  cumulative value. A resume never adds token headroom; only the configured cap can.
- `AgentRunnerOptions.Validate` rejects `MaxSteps < 1`, `MaxLifetimeSteps < MaxSteps`, `MaxReplans < 0`,
  `MaxLifetimeReplans < MaxReplans`, naming the keys. Because a lifetime cap is never below its per-attempt cap, **a
  single execution attempt behaves exactly as today**.
- Delegated roles keep their envelope budgets and deadline (ADR-0030 §6), unchanged; a role task has a single attempt.

#### 5.2 What counts

- **Lifetime steps** count executable steps: a step that executed or proposed a tool call, and a final-response step.
  **Synthetic failure steps** (`Model protocol failure`, `Unexpected runtime failure`, `Execution not started`) are
  recorded in `Steps` but never counted against a step budget (C-10).
- **Lifetime replans** count accepted plan revisions after revision 0. A re-plan on zero-plan resume (§7) creates
  revision 0 and is not a replan.
- **Tokens** are prompt + completion tokens of every model response that reported usage, plan and replan calls
  included, as today.

#### 5.3 Where they are persisted

`TaskState.Accounting` is updated in memory as the loop runs and written with every save, so a crash loses at most the
step in flight (as ADR-0017 already accepts for steps); a cancellation loses nothing already counted (§4.5).

#### 5.4 Rows without accounting (deterministic derivation, materialized once)

For a row without `Accounting` the runtime derives, deterministically and from persisted history only:
`TokensUsed` = the sum of reported usage over every `ModelCallRecord` in `Plans` and `Steps` (the two lists are
disjoint, so nothing is counted twice; a record without usage counts 0, so the value is a lower bound for rows older
than recorded model calls); `LifetimeSteps` = steps that are not synthetic failure steps (in pre-ADR rows: no
`ToolCall` and a runtime failure description); `LifetimeReplans` = `max(0, Plans.Count − 1)`. The derived record is
written by the first successful authoritative write of this ADR's code to that row (the resume transition, a fenced
cancellation or backstop write) and is never recomputed afterwards. The view (§9) shows the same derived values for a
row not yet materialized. Every pre-ADR row is also `Origin = Unknown` (§8.2) and so is not ordinarily resumable; the
derivation exists so that the numbers shown and any authoritative write agree.

### 6. Terminal reason

Every terminal save carries `TaskTerminalReason`. `TaskTerminalKind`: `Completed`, `StepLimit`, `LifetimeStepLimit`,
`TokenBudget`, `DelegationBudget`, `ReplanLimit`, `LifetimeReplanLimit`, `PolicyBlocked`, `ModelFailure` (with the
ADR-0039 `ModelFailureKind`), `EmptyResponse`, `RuntimeFailure` (a non-model failure contained by the runner or the
backstop), `Cancelled`, `NotAdmitted` (§4.3 step 7). Existing synthetic steps keep their descriptions and
observations, so current readers keep working.

### 7. Zero-plan resume

A resumed task with no plan re-enters planning as the new execution attempt (the same `CreatePlanAsync`, revision 0)
instead of throwing. A planning failure then ends that attempt `Failed` through the normal, fenced path, never an
escaped exception, and never after a 202 that left the task unchanged.

### 8. Task origin

#### 8.1 Explicit marker

The runtime sets `Origin` when it creates a task: the API launcher's initial row and `AgentRunner.RunAsync` write
`Ordinary`; `AgentRunner.RunDelegatedAsync` writes `Delegated` with `DelegationId` and `DelegationRole` from its first
save. No request can set it. `TaskResumePolicy` refuses `Delegated` (`task_delegated`) before any other check, and so
does `AgentRunner.ResumeAsync`, so no host can resume a role without its envelope. The only resume path for a role
stays `POST /api/delegations/{id}/resume` (ADR-0030 §7), unchanged; delegated resume support is HARDEN-11's.

#### 8.2 Rows written before the marker (fail closed)

A row without the field loads as `Unknown`. The only persisted evidence that could identify a pre-ADR role task — the
audit events that carry a `Delegation` correlation for its id — can at most prove `Delegated`, never `Ordinary`, and
absence of such evidence is not proof. No goal, prompt or other text heuristic is used. An `Unknown` task is refused
`resume_origin_unknown`. This deliberately gives up ordinary resume of pre-ADR tasks rather than reopening the
delegation bypass.

### 9. API view and HTTP contract (amends ADR-0018)

- `POST /api/agents/tasks/{id}/resume`: **202** `{ taskId, status, executionAttempt, executing, resumable,
  resumeBlockedReason }` describing the accepted transition (`Running`, the new execution attempt, `executing: true`,
  `resumable: false`, reason `task_running`) — `taskId` stays for existing clients; **404** unknown id; **409**
  `{ code, message }` for every refusal of §3 and for `resume_conflict`; **501** `{ code: "transition_unsupported" }`;
  **503** `{ code: "executor_unavailable", message }` with `Retry-After: 5` only when the launcher did not admit the
  acquired attempt (§4.3 step 7). 409, not 503, for a task that is running or was resumed by someone else.
- `POST /api/agents/tasks`: its capacity 503 gains the same body and `Retry-After` (was body-less).
- `DELETE /api/agents/tasks/{id}`: unchanged (cancels a task executing in this host, 404 otherwise). The cancellation
  is audited as `ExecutionTerminal` / `Cancelled`.
- **Task view** (`GET /api/agents/tasks/{id}`, the list, `/events`): every existing property unchanged (model bodies
  still stripped), plus the §2 fields (with `accounting` shown effective per §5.4), `executing` (the launcher of this
  host holds an execution attempt of the task), `resumable` and `resumeBlockedReason` (`{ code, message }` or `null`),
  both from `TaskResumePolicy`. `Running && !executing` is what a client may label "Interrupted"; it is not resumable
  (§3 row 3) and no status is added.
- **`/events`**: a snapshot is emitted when the step count, the status **or the execution attempt** changes. Because the
  transition is persisted before the 202, a stream opened after a resume starts on the new attempt and does not end on
  the stale pre-resume snapshot. It still ends at a terminal status.
- No UI change is made by this ADR (HARDEN-4).

### 10. Lifecycle audit

New `TaskLifecycleAuditEvent` (discriminator `taskLifecycle`, appended; the hash-chain format is unchanged), distinct
from ADR-0039's `TaskExecutionFaultAuditEvent`, which keeps its meaning:

| Stage | Written by | When |
|---|---|---|
| `ExecutionStarted` | runner | An execution attempt begins: attempt 1 of a fresh task (also a role task, with its `Delegation` correlation) or an admitted resumed attempt. |
| `ResumeAccepted` | runtime resume transition | The atomic transition of §4.3 step 4 succeeded. |
| `ResumeRejected` | runtime resume transition | A resume of a stored task was refused (§3), lost the transition, or the store lacks the capability. Not for 404. |
| `ExecutionTerminal` | runner, launcher cancellation, backstop, admission containment | Every terminal write of an execution attempt. |
| `ExecutionSuperseded` | runner | An executor was fenced (§4.4). |

Every resume request for a stored task produces exactly one `ResumeAccepted` or `ResumeRejected`. Fields: the base
ones (`TaskId`, `Actor`, `StepIndex` = the task's current step count, `Delegation` for role tasks), `Stage`,
`ExecutionAttempt` (always), `Origin`, `PriorStatus` (resume stages), `Status` (resulting or current),
`TerminalKind` (terminal), `RefusalCode` (rejection), and a budget snapshot: `TokensUsed`, `LifetimeSteps`,
`LifetimeReplans`, `MaxTotalTokens`, `MaxSteps`, `MaxLifetimeSteps`, `MaxReplans`, `MaxLifetimeReplans`. It never
carries the goal, prompts, model or tool payloads, refusal message text, credentials or stack traces.

### 11. Resume note — withdrawn

The proposal added one runtime-authored user turn on every resume. ADR-0038 requires the history rebuilt on resume to be
identical to the live one, and its tests prove it; an extra turn would break that property. No note is added; the
model sees the reconstructed history exactly. Telling the model about an earlier attempt belongs with context
management (HARDEN-8).

### 12. Policy, approval and verification on resume

Unchanged and restated: every resumed step goes through policy, entitlement, approval and verification exactly as a
first-attempt step. No approval is carried across attempts or processes (ADR-0022 §6); a step that needs approval asks
a currently authenticated approver again.

## Alternatives considered

- **Resume an orphaned `Running` task when no local executor holds it.** Rejected at acceptance: the absence of a
  local executor does not prove that no other process is executing the task, and fencing cannot undo an external side
  effect of an in-flight tool call.
- **A default `ITaskStore.TryTransitionAsync` member.** Rejected at acceptance in favour of an additive capability
  interface: no default can be atomic, and a throwing default hides the capability check at the call site.
- **Keep `MaxSteps` a lifetime cap and refuse every `MaxStepsReached` resume.** Honest, but makes the most common
  "needs a few more steps" case impossible. `MaxLifetimeSteps = MaxSteps` still configures exactly this.
- **Give each resume a fresh token budget.** Rejected: it is the C-09 bypass; resume is not a way to raise a cap.
- **A row-version column as the transition precondition.** Viable, but the execution attempt already is the ownership
  token that fences writers; a separate version adds a column and a second concept for no extra guarantee.
- **Precondition via `json_extract` over `state_json`, no new column.** Depends on the JSON1 build of SQLite and cannot
  be indexed; the additive column is explicit and simple.
- **Identify legacy role tasks by the runtime-authored role-goal prefix.** Rejected at acceptance: no text heuristic
  may be a security boundary; unknown origin fails closed instead.
- **Scan the audit log to identify legacy role tasks.** Could prove `Delegated` but never `Ordinary`, and makes
  eligibility depend on an unbounded, possibly rotated log. Not needed once unknown origin fails closed.
- **Mark "Interrupted" as a new `AgentTaskStatus`.** Forbidden by the plan (§16).
- **Resume a role task with its stored envelope from the task endpoint.** Rejected: delegation authority and
  continuation are ADR-0030's; HARDEN-11 owns delegation operability.

## Consequences

- Resume either makes real progress from a persisted, audited transition or is refused with a code and reason; a 202
  is always backed by a persisted `Running` of a new execution attempt that a launcher has admitted (C-04, C-06, C-07,
  C-08, C-10 closed; C-05's backend cause closed without UI change).
- Tokens can no longer be reset by resuming (C-09); every resume and refusal is audited with a budget snapshot.
- Role tasks can no longer be resumed outside their envelope (C-11). Pre-ADR tasks are no longer ordinarily resumable
  (unknown origin).
- Orphaned `Running` is distinguishable in the API (C-12), still without a new status, and still not recoverable:
  orphan recovery needs its own ownership design.
- Public surface grows (additive): the `TaskState` and `PlanStep` members of §2, `TaskAccounting`,
  `TaskTerminalReason`, `TaskTerminalKind`, `TaskOrigin`, `ITaskTransitionStore`, `TaskExecutionSupersededException`,
  `TaskLifecycleAuditEvent` and `TaskLifecycleStage`; in `bOps.Runtime` the new `AgentRunnerOptions` properties,
  `TaskResumePolicy`, `TaskResumeDecision`, `TaskResumeRefusal`, `TaskResumeRefusedException`, `TaskResumeAcquisition`
  and the `AgentRunner` resume members. Behavioural changes: `Agent:MaxSteps`/`Agent:MaxReplans` become per execution
  attempt; `AgentRunner.ResumeAsync` refuses non-resumable tasks and requires the task to be stored; `POST /resume`
  returns 409/501/503 with bodies where it returned 202/503-without-body; `SqliteTaskStore.SaveAsync` refuses a
  superseded execution attempt. The resume 202 keeps `taskId`, so existing clients keep working.
- `tasks.db` gains one column on first open by the new binary; old rows load with defaults.

## Decisions (as accepted)

| # | Decision | Accepted |
|---|---|---|
| D-1 | Execution attempt | `TaskState.ExecutionAttempt` (initial execution = 1, first accepted resume = 2; legacy rows load as 1) and `PlanStep.ExecutionAttempt`; incremented only by an accepted resume, atomically with `Running`. Distinct from `ModelAttempt`. **Renamed.** |
| D-2 | Resumability | The ordered table of §3, one runtime function, deterministic refusal codes; lifetime and token caps apply to every resumable status so no accepted resume is zero-work. **Amended** (Running, unknown origin). |
| D-3 | Transition and ownership | Additive `ITaskTransitionStore` with a true conditional write; no load/check/save emulation, fail closed without it; execution-attempt fencing on every executor, cancellation, backstop and containment write; additive `execution_attempt` column; `Running` never ordinarily resumable; no orphan resume. **Amended.** |
| D-4 | Lifetime defaults | `MaxLifetimeSteps = 60`, `MaxLifetimeReplans = 12`, validated ≥ per-attempt caps; the lifetime cap always wins; synthetic failure steps never count. **Clarified.** |
| D-5 | Accounting shape | Nested nullable `TaskState.Accounting` (`null` only for pre-ADR rows), derived deterministically and materialized by the first authoritative write (§5.4). **Clarified.** |
| D-6 | Synthetic failure steps | Recorded, never counted against step budgets. As proposed. |
| D-7 | HTTP | 202 only after the transition and launcher admission, describing the accepted attempt; 409 `{ code, message }` for state/ownership conflicts; 501 `transition_unsupported`; 503 `executor_unavailable` + `Retry-After: 5` only for a non-admitted attempt, which is contained `Failed`; start's 503 gains a body; `DELETE` unchanged; view fields of §9; `/events` also emits on status/attempt change. **Amended.** |
| D-8 | Lifecycle audit | `TaskLifecycleAuditEvent` with stages `ExecutionStarted`, `ResumeAccepted`, `ResumeRejected`, `ExecutionTerminal`, `ExecutionSuperseded`; every stage carries `ExecutionAttempt`; no payloads. **Renamed.** |
| D-9 | Task origin | Explicit persisted `Origin` (`Ordinary`/`Delegated`, `Unknown` for legacy); no text heuristic; `Delegated` and `Unknown` refused by ordinary resume. **Amended.** |
| D-10 | Resume note | Withdrawn (conflicts with ADR-0038 identical history); deferred to HARDEN-8. **Withdrawn.** |
