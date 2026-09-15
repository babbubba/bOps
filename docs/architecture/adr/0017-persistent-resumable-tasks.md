# 0017 — Persistent, resumable tasks via `ITaskStore` (SQLite)

Status: Accepted
Date: 2026-09-15

## Context

V0.7 (agentic/00-project-spec.md) is "Persistent, resumable tasks (SQLite)". Today
`AgentRunner.RunAsync` holds a task's entire state — `Steps`, `Plans`, the model conversation
history — only in local variables. If the process is killed (crash, restart, an operator's own
Ctrl+C), that state is gone: there is no way to tell, after the fact, that a task was even
`Running`, let alone to continue it from its last completed step.

`piano-bops.md` §10 sketches this as an EF Core `bOpsDbContext` over SQLite. `07-plan-
corrections.md` does not flag that section as wrong, but D-003 (dynamic package loading) and
D-010 (real targets, no unnecessary weight) both push against adding EF Core's machinery to the
core for what is, structurally, "save one record, load it back, list a few."

This requires an ADR because it alters `bOps.Abstractions` (a new `ITaskStore` contract and a
new `AgentTaskStatus` transition is exercised for the first time), changes the execution
topology (`AgentRunner` gains an explicit persist/resume path instead of being purely
in-memory), and adds a dependency to the core (`Microsoft.Data.Sqlite` in the new `bOps.Memory`
project).

## Decision

- `ITaskStore` is declared in `bOps.Abstractions` (dependency-free, like `IAuditSink` and
  `IChatModel`): `SaveAsync(TaskState)`, `LoadAsync(Guid)`, `ListByStatusAsync(AgentTaskStatus)`.
- `bOps.Memory` (already named as a core component in agentic/00-project-spec.md, previously
  unimplemented) provides `SqliteTaskStore : ITaskStore`, backed directly by
  `Microsoft.Data.Sqlite` — one table, `TaskState` serialized whole as a JSON column via the
  same `System.Text.Json` source-generation pattern `bOps.Audit` already uses for
  `AuditJsonContext`. No EF Core: there is exactly one aggregate (`TaskState`) with no relational
  queries beyond "by id" and "by status," so an ORM buys nothing here that a parameterized
  `INSERT OR REPLACE` and a JSON column do not already give us, at a fraction of the dependency
  weight.
- `AgentRunner` takes `ITaskStore` as a required constructor dependency (it is core
  infrastructure the loop always has, like `IAuditSink` — not an optional feature flag) and:
  - saves the task with `AgentTaskStatus.Running` after the initial plan and after every step,
    so a crash mid-task leaves the last completed step durable, never a half-written one;
  - saves the final `TaskState` once more when the loop reaches a terminal status.
- `AgentRunner.ResumeAsync(TaskState task, ActorIdentity actor, CancellationToken ct)` resumes a
  task previously left `Running`: it rebuilds the in-memory conversation history from
  `task.Steps` (each step's tool call and observation re-wrapped exactly as `RunAsync` would have
  produced them the first time) and the current plan from `task.Plans[^1]`, then continues the
  same loop `RunAsync` uses from the next step index — it is not a separate implementation of the
  loop, only a different entry point into it.
- `bops resume <task-id>` is added to the CLI as the first real subcommand; `bops "<goal>"`
  keeps working unchanged (the existing usage message already documents no subcommands, so
  Program.cs now checks `args[0] == "resume"` before falling back to "everything is the goal").

## Alternatives considered

- **EF Core over SQLite**, matching `piano-bops.md` literally. Rejected: an object-relational
  mapper for a single serialized-blob table is exactly the kind of weight D-003 already argued
  against adding to the core, and it would be the first EF Core dependency in the repository for
  no relational benefit.
- **JSON-lines file, like `bOps.Audit`.** Rejected: audit is append-only by design (rule S9 — a
  denial or a past decision is never revised). A task's row is mutated every step — "the last
  known state of task X" — which an append-only log answers only by replaying every line for
  that task id on every resume. SQLite's `UPDATE` is the right primitive for the field's actual
  reformulation.
- **`ITaskStore` inside `bOps.Memory` instead of `bOps.Abstractions`.** Rejected: `AgentRunner`
  (in `bOps.Runtime`) needs to call it, and `bOps.Runtime` must not depend on `bOps.Memory`
  specifically — the same reasoning that already put `IAuditSink` in `bOps.Abstractions` instead
  of `bOps.Audit`.
- **Optional `ITaskStore?` on `AgentRunner`, defaulting to a no-op.** Rejected: a `null`-checked
  persistence path is a backwards-compatibility shim for a runtime that has no other consumer to
  stay compatible with yet (agentic/02-coding-standards.md forbids exactly this). Every host
  wires a real store, the same way every host already wires a real `IAuditSink`.

## Consequences

- `bOps.Memory` becomes a real core project with a real dependency (`Microsoft.Data.Sqlite`),
  the first the core has taken beyond `Microsoft.Extensions.*`. It stays out of
  `bOps.Abstractions`, which remains dependency-free per `05-workflow.md`.
- `AgentRunner`'s constructor grows a required parameter; every existing call site (the CLI host,
  and every `AgentRunnerTests` test) must supply an `ITaskStore` — an in-memory fake for tests,
  mirroring the existing `TestDoubles.cs` pattern for `IAuditSink`.
- A resumed task's rebuilt history is an approximation of the original in-memory conversation:
  it reconstructs exactly what `RunAsync` would have appended to `history` for each persisted
  step (the same `WrapToolOutput` formatting), but a plan's `Rationale` and step descriptions are
  themselves already part of `TaskState`, so nothing about the plan is lost — only the literal
  model-call objects (never persisted; they are not part of `TaskState` today) are gone, which
  matters only for their token-accounting contribution to a resumed run's own new budget, not to
  correctness.
- `bOps.Architecture.Tests` (rule A1) gains `bOps.Memory` as a fourth scanned core assembly —
  it must never contain a literal package/tool/provider string either, exactly like `bOps.Runtime`,
  `bOps.Policy` and `bOps.Audit`.
