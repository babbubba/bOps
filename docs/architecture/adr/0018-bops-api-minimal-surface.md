# 0018 — `bOps.Api`: a minimal, single-node HTTP surface over the existing runtime

Status: Accepted
Date: 2026-09-15

## Context

V0.9 (agentic/00-project-spec.md) is "`bOps.Api` + Angular UI." Per user decision this session,
V0.9 is scoped to what the roadmap and `06-decisions.md`/`00-project-spec.md` already say: a
**second client of the same local, single-node runtime** (principle 6 — "the CLI is the primary
interface... the web UI is Phase 2 and is a second client of the same runtime, never a privileged
one"), not multi-agent supervision or a remote-agent transport — both are explicitly out of scope
until after V1.0, and the *contract* already accommodates them (`NodeId`, `ActorIdentity.Kind`)
without the *transport* existing (D-001). This session builds `bOps.Api` only; the Angular UI is
deliberately deferred to its own session (raw scope: an entire second application, not reviewable
alongside a backend in one pass).

This requires an ADR because it adds a new host with real design decisions the CLI never had to
make: `bOps.Cli` runs one task per process and blocks on `Console.ReadLine` for approval;
`bOps.Api` must serve many concurrent HTTP clients, return before a task finishes, let a client
observe progress without polling a blocking call, and let a *different* HTTP request supply an
approval decision than the one that started the task.

## Decision

**A new host, `bOps.Api`** (ASP.NET Core Minimal API, ASP.NET Core Web SDK), thin by construction
like `bOps.Cli` (agentic/04-testing-rules.md: "Hosts... test the composition, not the logic —
there should be none") — it wires up exactly the same core components `bOps.Cli` already does
(`AgentRunner`, `IToolRegistry`, `IPolicyEngine`, `IAuditSink`, `ITaskStore`, the provider
packages from V0.5–V0.8) and adds only what an HTTP channel genuinely needs that a console
channel does not.

**Endpoints (MVP surface for this session):**

| | |
|---|---|
| `POST /api/agents/tasks` | Starts a task in the background from `{ "goal": "..." }`. Returns `202 Accepted` with the task id immediately — never blocks on the task finishing. |
| `POST /api/agents/tasks/{id}/resume` | Resumes a stored `Running` task (V0.7) in the background. `404` if no such task is stored. |
| `GET /api/agents/tasks/{id}` | The task's current `TaskState`, read from `ITaskStore`. `404` if never stored. |
| `GET /api/agents/tasks/{id}/events` | Server-Sent Events: a snapshot of `TaskState` every time its step count changes, until the task reaches a terminal status. |
| `GET /api/agents/tasks?status=Running` | Lists resumable tasks (`ITaskStore.ListByStatusAsync`). |
| `GET /api/approvals/pending` | Lists approvals currently awaiting a decision. |
| `POST /api/approvals/{approvalId}/respond` | Resolves one pending approval from `{ "approved": bool, "note": "..." }`. `404` if not pending (already answered, or never existed). |
| `GET /api/tools` | `IToolRegistry.GetAvailableManifests()` — what an operator could ask for right now. |

**A task runs as a detached background operation**, not inside the request that starts or resumes
it (`_ = Task.Run(...)`, not `await`) — `AgentRunner.RunAsync`/`ResumeAsync` already catch every
exception except `OperationCanceledException` and always return a `TaskState` (rule C1), so
nothing about this needs a job queue or a hosted-service framework to stay safe; it needs only
somewhere for a client to *observe* progress after the triggering request has already returned.
That somewhere is `ITaskStore`, which V0.7 already updates after every step — `GET
/api/agents/tasks/{id}/events` **polls the store** (a short fixed interval) rather than requiring
a new in-process event-bus abstraction in `bOps.Runtime`/`bOps.Abstractions`. `AgentRunner` itself
holds no mutable instance state (every constructor dependency is immutable, every per-task variable
is local to `RunAsync`/`ContinueAsync`), so one singleton instance safely serves concurrent
requests without any host-side synchronization.

**Approvals cross the request boundary via a host-local `ApiApprovalProvider : IApprovalProvider`**
(lives in `bOps.Api`, exactly where `ConsoleApprovalProvider` lives in `bOps.Cli` — a channel-
specific implementation of an existing core interface, not a core change). `RequestApprovalAsync`
generates an approval id, stores `(manifest, reason, TaskCompletionSource<ApprovalDecision>)` in
an in-memory `ConcurrentDictionary`, and awaits the `TaskCompletionSource`'s task (linked to the
call's own cancellation token). `POST /api/approvals/{id}/respond` completes that
`TaskCompletionSource` from a different request entirely. Pending approvals are **not** persisted
— a process restart loses them, same as it already loses any other in-flight, non-`Running`-yet
runtime state; the task itself is safe (it is `Running` in `ITaskStore` the moment its plan is
created, per V0.7) but a specific pending approval is not resumable across a restart in this pass.

**No authentication or authorization.** Not named in this version's roadmap line; the plan
(the archived original plan) does not spec one either. `bOps.Api` in this pass is meant to run on a trusted
local network or behind a reverse proxy the operator controls — flagged here explicitly, not
silently skipped, because principle 2 ("every tool declares its own risk... policy engine decides
the mode") already gates every *action* a task can take; what this version leaves open is *who may
ask bOps.Api to start or approve one at all*. Real auth belongs with V1.0's "threat model, package
signing and trust levels, secrets management, hardening" line, where the whole security posture
gets designed together rather than one host bolting on its own scheme first.

**No OpenAPI/Swagger generation, no provider-listing endpoint (`GET /api/providers`).** Both are
named in the archived original plan for the eventual Angular client (OpenAPI-generated TypeScript client,
provider selector in Settings) but have no consumer yet in this session — the Angular UI is
explicitly deferred, so building either now is exactly the "anticipating a later version" the
project's scope discipline warns against. `IChatModelRegistry` also has no enumeration method
today (only `Register`/`Create`); adding one is a `bOps.Abstractions` change with no current
caller, deferred to whichever session actually builds `GET /api/providers`.

## Alternatives considered

- **A real event-bus/notification abstraction in `bOps.Runtime`** (`AgentRunner` publishes a step
  event as it happens, `bOps.Api` subscribes) instead of polling `ITaskStore`. Rejected for this
  pass: it is a real core-contract addition (a new `bOps.Abstractions` type, a new `AgentRunner`
  dependency) for a problem V0.7's persistence already solves adequately at MVP polling latency —
  premature machinery a scope-disciplined V0.9 does not need yet. Worth reconsidering if a future
  version needs true low-latency push (e.g., a live "typing" view of the model's own reasoning
  stream) rather than "did another step complete."
- **`await`ing the task inside `POST /api/agents/tasks`** (a task only "starts" once it finishes).
  Rejected outright — a `bops.Cli`-shaped blocking call is exactly what an HTTP API for an
  agent loop that can run for many steps and need human approval must not be; every browser and
  HTTP client assumes a request eventually returns.
- **A hosted-service/queue (`IHostedService`, `Channel<T>`) for background task execution.**
  Rejected for this pass — `Task.Run` plus `AgentRunner`'s already-total exception handling and
  V0.7's persistence gives the same durability property (a task's progress up to its last
  completed step survives) without a new abstraction; revisit if `bOps.Api` ever needs to survive
  its *own* process being drained mid-task rather than merely surviving `AgentRunner`'s own logic
  throwing.

## Consequences

- `bOps.Api` becomes the third host (after `bOps.Cli`), same composition-root discipline: it names
  every package it loads directly (D-003 — dynamic loading is still V0.10), same as `bOps.Cli`
  does today.
- A pending approval is host-process-local state, not durable — documented above, not hidden. An
  operator restarting `bOps.Api` while an approval is pending loses that specific pending request;
  the underlying task is unaffected (still `Running` in `ITaskStore`) and resumable once the
  restarted process is asked to `resume` it, but the model will be asked to reconsider that step
  fresh rather than the original approval decision ever landing.
- `GET /api/agents/tasks/{id}/events`'s polling interval is a real latency/load tradeoff a future
  session may need to revisit once there is an actual UI consuming it and real usage patterns to
  measure against.
- No authentication is a genuine, stated gap for this version, not an oversight — anyone who can
  reach `bOps.Api`'s port can start and approve tasks. This must be called out to whoever deploys
  it before this goes anywhere it is not already trusted.
