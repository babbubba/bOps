# Handoff — V0.9 (`bOps.Api`) complete; Angular UI deferred

Written at the end of the session that implemented V0.9's backend on top of the completed V0.8
provider work. Everything below is exact, not a summary — follow it literally to resume.

## Scope decision made this session — read before doing anything else

V0.9 in the roadmap is "`bOps.Api` + Angular UI." The operator explicitly scoped this session to
**`bOps.Api` only** — the Angular UI is deliberately deferred to its own session, not started, not
scaffolded, nothing under `web/` exists. Two things were raised and resolved before writing any
code, both worth restating so a future session doesn't reopen them without new information:

1. **Whether multi-agent supervision or a remote-agent transport belong in V0.9.** They do not.
   `agentic/00-project-spec.md` already lists both as explicitly out of scope until after V1.0;
   the operator confirmed V0.9 stays within the existing roadmap's scope (a second client of the
   same local, single-node runtime — principle 6) rather than opening a roadmap discussion to pull
   either forward. If a future session is asked to build fleet/remote features, that is a real
   roadmap change requiring an explicit decision and edits to `agentic/00-project-spec.md` and
   `06-decisions.md` — not something to infer from a UI or API request.
2. **How much of V0.9 to build in one session.** Full "API + entire Angular 21 + NgRx SignalStore
   app" was judged too large for one reviewable pass. This session built `bOps.Api` to a genuinely
   complete MVP surface (ADR-0018) with real test coverage; the Angular UI is next session's task,
   explicitly, not an oversight.

## State right now

**`dotnet build bOps.slnx` builds clean end to end — 0 warnings, 0 errors.** 38 projects now, up
from 34 at the end of V0.8 (`bOps.Api`, `bOps.Api.Tests`).

**`dotnet test bOps.slnx --filter "Category!=LiveModel"`: every suite passes**, including the new
`bOps.Api.Tests` (8 end-to-end integration tests against a real, running `bOps.Api` host via
`WebApplicationFactory<Program>` — a deterministic fake `IChatModel`, no live provider). Same
pre-existing skips as every prior handoff.

**Manually smoke-tested against a live `dotnet run`** (not just `dotnet build`): started the real
host on a bound port, `GET /api/tools` returned real tool manifests, `POST /api/agents/tasks`
with an empty goal returned the expected `400`. Process and its stray `tasks.db` were cleaned up
afterward — verified via `git status` that nothing was left behind.

**⚠️ Security finding carried forward from V0.7, still not resolved: the real OpenRouter API key
in `src/core/bOps.Cli/appsettings.json` is committed to git history** (commit `7ac2901`). This
session's own `bOps.Api/appsettings.json` was written from scratch with `"ApiKey": ""`, per rule
S6, and was never populated with anything else — not implicated in the existing finding, which
remains open and unrelated to this session's work.

## What this session did

Implemented V0.9's backend per the scope decision above: **`bOps.Api`, a second, non-privileged
HTTP client of the same local runtime `bOps.Cli` already drives.** ADR-0018
(`docs/architecture/adr/0018-bops-api-minimal-surface.md`) records the full design and the
alternatives rejected — read it before extending this host.

### The endpoint surface

`POST /api/agents/tasks`, `POST /api/agents/tasks/{id}/resume`, `GET /api/agents/tasks/{id}`,
`GET /api/agents/tasks/{id}/events` (SSE), `GET /api/agents/tasks?status=`, `GET
/api/approvals/pending`, `POST /api/approvals/{id}/respond`, `GET /api/tools`. Full behavior is in
ADR-0018; the short version: a task **starts detached** (`202 Accepted` with its id immediately,
never blocking the request on the task finishing), progress is **observed by polling
`ITaskStore`** (V0.7's own persistence, not a new event-bus abstraction), and an approval **crosses
the request boundary** via a new host-local `ApiApprovalProvider` — a `TaskCompletionSource`-backed
queue completed by a *different* HTTP request than the one that raised it, exactly as
`ConsoleApprovalProvider`'s own doc comment already anticipated back in V0.3.

### A real bug this session's own testing caught and fixed: `AgentRunner.RunAsync`'s task id

`bOps.Cli` never needed to know a task's id before the task finished — it prints the id only in
the final transcript. `bOps.Api` fundamentally does: `POST /api/agents/tasks` must return the id
*before* the task has run at all, so a client can poll or open an SSE stream for it. The original
V0.7 signature, `RunAsync(string goal, ActorIdentity actor, CancellationToken ct = default)`,
generates its own `Guid.NewGuid()` internally with no way to inject one — so the id
`AgentTaskLauncher` handed back to an HTTP client was never the id `AgentRunner` actually persisted
under. This was caught by this session's own integration tests (every `GET` of a just-started
task returned `404` forever — diagnosed by checking the fake model's own call count directly,
which proved the task *was* running to completion, just under a different id than the client was
ever told). Fixed by adding an optional `Guid? taskId = null` parameter (after `actor`, before
`ct` — `CancellationToken` must stay last per CA1068) that `AgentTaskLauncher.Start` now passes
through; `RunAsync`'s behavior for every existing caller (`bOps.Cli`, every `bOps.Runtime.Tests`
test) is unchanged since the parameter defaults to generating a fresh id exactly as before.

### A real bug this session's own testing caught and fixed: `SqliteTaskStore` had no busy timeout

`bOps.Cli` was the only consumer of `ITaskStore` through V0.7 and V0.8 — one process, one task in
flight, never two connections touching the same SQLite file at once. `bOps.Api` is the first
consumer with genuine concurrent access: a detached background write (the task's own progress)
and an HTTP-triggered read (a client polling `GET /api/agents/tasks/{id}`) can hit the same file
at the same moment. SQLite's default journal mode blocks a reader behind an in-progress writer and,
with no `busy_timeout` set, fails immediately with `SQLITE_BUSY` rather than waiting briefly — this
surfaced during this session's own testing as requests to a just-started task intermittently
failing. Fixed in `SqliteTaskStore`: every connection now sets `PRAGMA busy_timeout=5000;`
immediately after opening, and the database itself is switched to `journal_mode=WAL` once (a
durable, once-per-file setting) in `EnsureSchema`, which lets a reader and a writer coexist far
more gracefully than the default rollback-journal mode. This is a `bOps.Memory` change, not a
`bOps.Api`-only one — it benefits `bOps.Cli`'s own `bops resume` too, though `bOps.Cli` never hit
the bug since it has no concurrent access pattern to trigger it.

### Tests

`bOps.Api.Tests` (`WebApplicationFactory<Program>`, a real host per test via `TestAppFactory`,
isolated temp directory per instance for its audit log/task store/policy file, `IChatModel` and
`IPolicyEngine` substitutable before the first request): a missing `goal` returns `400`; a task
started, polled, and observed completing through real HTTP; an unknown task id returns `404` from
both `GET` and `resume`; a task seeded directly into `ITaskStore` as `Running` resumes to
completion through the resume endpoint; a full approval round-trip — task blocks, `GET
/api/approvals/pending` shows it (with the correct task id, recovered via `ApiApprovalProvider`'s
`AsyncLocal<Guid?>`, not persisted state), a separate `POST .../respond` unblocks it, the task
completes, the approval list empties; responding to an unknown approval id returns `404`; `GET
/api/tools` returns the real registered manifests.

**No live-model smoke test** — same `appsettings.json` constraint as every prior handoff.

## Design choices worth knowing before extending this further

- **Every type in `bOps.Api` is `internal`** (CA1515 — this is an application, not a library),
  including the `Program` marker class; `bOps.Api.Tests` sees them via a project-level
  `InternalsVisibleTo`. This is new — no prior host in this repository needed it, since `bOps.Cli`
  has never had an integration-test project driving it through its own composition root.
- **`ApiApprovalProvider.CurrentTaskId` is a `static AsyncLocal<Guid?>`**, set by
  `AgentTaskLauncher` for the duration of a task's `RunAsync`/`ResumeAsync` call and read inside
  `RequestApprovalAsync`, which is nested many calls deep inside `AgentRunner` and has no task id
  parameter to work with (the `IApprovalProvider` interface predates a multi-task host). This
  avoids touching `IApprovalProvider`'s contract — a `bOps.Abstractions` change every other
  provider (`ConsoleApprovalProvider`, tests) would also need to absorb — for something genuinely
  local to how *this one host* recovers context it needs for its own UI, not part of what the
  interface promises callers in general.
- **A pending approval is host-process-local, not durable** (ADR-0018) — a restart mid-approval
  loses that specific pending request, though the underlying task is unaffected and resumable.
  Stated explicitly in the ADR, not a silent gap.
- **No authentication in this version** (ADR-0018) — anyone who can reach `bOps.Api`'s port can
  start and approve tasks. Real auth is explicitly deferred to V1.0's hardening line, where the
  whole security posture gets designed together.

## What V0.9 deliberately does NOT have yet

- **The Angular UI does not exist.** Nothing under a `web/` directory, no scaffold, no
  `angular.json`. This is the explicit scope decision from the top of this document, not an
  oversight — next session's task.
- **No OpenAPI/Swagger generation.** Named in `piano-bops.md` for the eventual Angular client
  (generated TypeScript client) but has no consumer yet; deferred to whichever session actually
  builds the UI (ADR-0018).
- **No `GET /api/providers`.** `IChatModelRegistry` has no enumeration method today (only
  `Register`/`Create`) — adding one is a `bOps.Abstractions` change with no current caller.
- **No authentication or authorization** — see above, explicitly deferred to V1.0.
- **`GET /api/agents/tasks/{id}/events`'s 500ms poll interval is an unmeasured MVP default** —
  ADR-0018 explicitly defers tuning it to a session with a real UI and real usage to measure
  against.
- **`bOps.AppHost` still has not been run this session** — carried forward, unrelated to V0.9.
- **No dynamic plugin loading** — V0.10, unrelated.
- **No CLI command to run `AuditChainVerifier` on demand** — carried forward again, still small,
  still not done.

## Next steps

`bOps.Api`'s MVP surface is done and genuinely tested end to end — not just "compiles," but a real
host handling a real task through start → poll → complete, and a full approval round-trip through
two separate HTTP requests, plus resume. Two real bugs this session's own tests caught (the task-id
mismatch, the missing SQLite busy timeout) are fixed, not merely worked around.

**Before any further roadmap work**, the committed API key finding from V0.7 is still open —
carried forward again.

**Next: the Angular UI**, the deferred half of V0.9. Per the scope-discipline rule, that session
should begin by reading `piano-bops.md` §17 (the only place the intended Angular structure —
standalone components, `@ngrx/signals` SignalStore, `httpResource()`/`resource()` for cacheable
GETs — is actually described) and this session's ADR-0018 for the exact endpoint contract it will
consume, then confirm with the operator whether OpenAPI-generated-client tooling should be set up
first or whether a hand-written client is acceptable for an initial pass.

Two smaller, non-urgent items carried forward again from every prior handoff:

1. The six pre-existing ADRs `agentic/05-workflow.md` lists as "the first ADRs to exist" (0001,
   0002, 0005, 0006, 0011, 0012) are still unwritten.
2. No CLI subcommand runs `AuditChainVerifier`. Small, real, not done.
