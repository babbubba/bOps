# Handoff — V0.7 (Persistent, resumable tasks) complete

Written at the end of the session that implemented V0.7 on top of the completed V0.6 Docker work.
Everything below is exact, not a summary — follow it literally to resume.

## State right now

**`dotnet build bOps.slnx` builds clean end to end — 0 warnings, 0 errors.** 30 projects now, up
from 28 at the end of V0.6 (`bOps.Memory`, `bOps.Memory.Tests`).

**`dotnet test bOps.slnx --filter "Category!=LiveModel"`: every suite passes** — including the new
`bOps.Memory.Tests` (5, against a real SQLite file, not a fake) and `bOps.Runtime.Tests` (now 72,
up from 66: the persistence/resume behavior gets its own file,
`AgentRunnerPersistenceTests.cs`). Same pre-existing skips as every prior handoff (Linux-only
conformance tests, one symlink test).

**⚠️ Security finding from this session, not yet resolved: `src/core/bOps.Cli/appsettings.json`'s
real OpenRouter API key (`sk-or-v1-57af8...`) is committed to git history**, in commit `7ac2901`
("Aggiorna configurazioni e struttura soluzione") — not made by any agent session on record. This
violates rule S6 (`ApiKey` must always be `""` in the repo) and is worse than an uncommitted
working-tree change: the key is recoverable from history even after the file is fixed forward. The
operator needs to (1) revoke/rotate the key at OpenRouter now, (2) decide how to handle the
history (rewrite, or accept it as burned and rely only on rotation), (3) move the real key to an
environment variable or `dotnet user-secrets` going forward. No agent session should commit this
file's `ModelProvider.ApiKey` field as anything but `""`.

## What this session did

Implemented V0.7 per `agentic/00-project-spec.md`'s roadmap: **"Persistent, resumable tasks
(SQLite)."** ADR-0017 (`docs/architecture/adr/0017-persistent-resumable-tasks.md`) records the
design and the alternatives rejected (EF Core, an append-only file, an optional/nullable store).

### `ITaskStore` (`bOps.Abstractions/Memory.cs`)

`SaveAsync(TaskState)`, `LoadAsync(Guid)`, `ListByStatusAsync(AgentTaskStatus)`. Dependency-free,
node-local, same reasoning as `IAuditSink` living in `bOps.Abstractions` rather than in the
project that implements it.

### `bOps.Memory` — the core's first real dependency beyond `Microsoft.Extensions.*`

`SqliteTaskStore : ITaskStore`, one table (`tasks`: `id`, `status`, `updated_at_utc`,
`state_json`), the whole `TaskState` serialized as a JSON column via a source-generated
`MemoryJsonContext`, same pattern `bOps.Audit`'s `AuditJsonContext` already uses. No EF Core — a
parameterized `INSERT ... ON CONFLICT DO UPDATE` and a JSON column are the entire feature. Added
to `bOps.Architecture.Tests`' `CoreAssemblyMarkers` (rule A1): scanned for forbidden literals like
every other core assembly.

`Microsoft.Data.Sqlite` 10.0.8 still floors its transitive `SQLitePCLRaw.*` dependencies at
2.1.11, which trips `NU1903` (a real, high-severity advisory, `GHSA-2m69-gcr7-jv3q`) as an error
under this repo's warnings-as-errors. Fixed with a direct `PackageReference` to
`SQLitePCLRaw.bundle_e_sqlite3` 3.0.3 in `bOps.Memory.csproj`, overriding the floor — documented
inline in the `.csproj`, not yet added to `docs/architecture/suppressions.md` (it is a version
override, not a suppressed analyzer rule, so it may not belong there verbatim — worth a second
look next session).

### `AgentRunner` — persist-as-you-go, plus `ResumeAsync`

`ITaskStore` is now a required constructor dependency (not optional/nullable — every host wires a
real one, exactly like `IAuditSink`). The step loop (`RunAsync`'s body, extracted into a shared
`ContinueAsync`) now:

- saves the task as `AgentTaskStatus.Running` right after the initial plan, and again after every
  step — so a crash between two steps loses at most the step in flight;
- saves the final `TaskState` through every terminal return path, via one `FinishAsync` helper
  every exit now funnels through instead of returning `Build(...)` directly.

`ResumeAsync(TaskState task, ActorIdentity actor, CancellationToken ct)` is the new entry point:
rebuilds the model-facing `history` from `task.Steps` (`RebuildHistory` — same
`ChatTurn.FromAssistantToolCalls`/`FromToolResult` shape `ContinueAsync` would have produced the
first time), takes `task.Plans[^1]` as the current plan, and continues the same loop from
`task.Steps.Count` — not a second implementation of the agent loop, a second entry point into the
one that already existed. Throws `InvalidOperationException` if the task has no recorded plan
(cannot happen for a task `RunAsync` itself produced, since planning always runs first — this
guards a caller handing back something malformed).

### CLI: `bops resume <task-id>`

The first real subcommand. `bops "<goal>"` is unchanged; `args[0] == "resume"` is checked first
(`Program.cs`). `SqliteTaskStore` is constructed at `Memory:FilePath` (default `tasks.db`, same
directory-config pattern as `Audit:FilePath`/`Policy:FilePath`). `bops resume <task-id>` for an
id with nothing stored prints an error and exits 1 before ever touching the model provider.

**Not run against a live model this session** (same appsettings.json constraint as every prior
handoff — the file's `ApiKey` is `""` in the repo and must stay that way). The CLI's
argument-parsing paths (`bops` with no args, `bops resume <not-a-guid>`, `bops resume
<unknown-guid>` reaching the same "Missing 'ModelProvider' configuration section" failure every
other CLI path reaches without a configured key) were smoke-tested directly; no `tasks.db` or
`audit.jsonl` was created by these runs (verified via `git status` — both writes happen after the
`ModelProvider` config check, which fails first).

### Tests

`bOps.Memory.Tests` (real SQLite file, per-test temp path, `SqliteConnection.ClearAllPools()` in
`Dispose` — Microsoft.Data.Sqlite pools connections by default, which keeps the file locked past
`using var connection`'s own `Dispose`): round-trip, unknown id → `null`, a second save overwrites
in place, `ListByStatusAsync` filters correctly, state survives reopening the store against the
same file (proves durability, not just an in-process cache).

`AgentRunnerPersistenceTests.cs` (`bOps.Runtime.Tests`): a step's `Running` snapshot is saved
after every step and the terminal state once more at the end; a task cancelled mid-loop (via a
`CancelAfterCallChatModel` test double that cancels a shared token deterministically after a
chosen model call, rather than a timing-based `CancelAfter`) is left `Running` in the store with
exactly its completed steps, never returned as a finished `TaskState`; `ResumeAsync` continues
from the next step without repeating the persisted one, and the model's next request already
carries the completed step as history; `ResumeAsync` on a task with no recorded plan throws.

## Design choices worth knowing before extending this further

- **`AgentRunner`'s constructor grew a required parameter.** Every test that builds one goes
  through a single `CreateRunner` factory per test file — the fix for `AgentRunnerTests.cs` was
  one line (add `ITaskStore? taskStore = null` defaulting to a new `InMemoryTaskStore`), not a
  67-test rewrite.
- **`InMemoryTaskStore` (`TestDoubles.cs`) also records every save, in order** (`.Saves`), used by
  the persistence tests to assert *when* the runtime persists, not only that a final load
  round-trips.
- **A resumed task's rebuilt history is not byte-identical to the original run's in-memory
  `history`** — it is reconstructed from `TaskState.Steps`, which never captured the raw
  `ModelResponse`/token-usage objects (those were never part of the contract). `ResumeAsync`
  starts `totalTokens` at 0 for its own new budget — a resumed run gets its own fresh
  `MaxTotalTokens` allowance, deliberately, not a continuation of the original run's spent budget.
  Correctness of *what already happened* is unaffected; only a resumed run's own token accounting
  restarts.
- **`ITaskStore` is required, never optional/nullable, on `AgentRunner`.** Considered and rejected
  in ADR-0017 — a null-checked persistence path would be exactly the kind of
  backwards-compatibility shim `agentic/02-coding-standards.md` forbids for a runtime that has no
  other consumer to stay compatible with.

## What V0.7 deliberately does NOT have yet

- **No `bops task list` / "show me what's resumable" CLI command.** `ITaskStore.ListByStatusAsync`
  exists and is tested, but nothing in the CLI calls it yet — an operator resuming a task today
  needs to already know its id (from the printed transcript, or the audit log). Worth a small
  follow-up, not part of this version's roadmap line as stated.
- **No automatic resume on startup.** A `Running` task left behind by a crash sits there until an
  operator explicitly runs `bops resume <id>` — no "resume everything still Running" sweep.
  Reasonable for a single-operator CLI; would need real thought (which task, whose approval) before
  a multi-operator surface (V0.9's API) could do this unattended.
- **No SQLite schema migration story.** One table, no version column. Fine for a 0.x SDK
  (D-012) with a single shape so far; will need one before V1.0 if `TaskState`'s shape ever
  changes in a way that breaks deserializing an old row.
- **`bOps.AppHost` still has not been run this session** — carried forward, unrelated to V0.7.
- **No dynamic plugin loading** — V0.10, unrelated.
- **No CLI command to run `AuditChainVerifier` on demand** — carried forward again, still small,
  still not done.

## Next steps

V0.7 is done: every task is durable as it runs, and `bops resume <task-id>` genuinely continues
from the next unfinished step rather than restarting the goal, verified against a real SQLite file
and a deterministic fake model, not just asserted.

**Before any further roadmap work**, resolve the security finding above — the committed API key.
This blocks nothing about V0.7's own correctness, but it is a live credential in git history and
should not wait for the next scheduled session.

**V0.8 — "Anthropic, OpenAI and DeepSeek provider packages" — has not been started.** Per the
scope-discipline rule, the next session should begin by reading `agentic/00-project-spec.md`'s
roadmap entry for V0.8 and `06-decisions.md` for any settled provider-package decisions before
writing code. Phase 1 (the CLI roadmap) is now complete through V0.7; V0.8 opens Phase 2.

Two smaller, non-urgent items carried forward again from every prior handoff:

1. The six pre-existing ADRs `agentic/05-workflow.md` lists as "the first ADRs to exist" (0001,
   0002, 0005, 0006, 0011, 0012) are still unwritten.
2. No CLI subcommand runs `AuditChainVerifier`. Small, real, not done.
