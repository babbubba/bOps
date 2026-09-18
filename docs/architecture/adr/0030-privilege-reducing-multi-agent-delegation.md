# ADR-0030 — Privilege-reducing multi-agent delegation

Status: Accepted
Accepted: 2026-09-18 by the operator

Governs V1.2 (`agentic/_tasks/2026-09-16-v1.2-multi-agent.md`). Continues ADR-0017 (task
persistence), ADR-0023/0024/0025 (Skills, execution plans, restricted invocation) and ADR-0022
(frozen 1.x contract). It resolves the question ADR-0025 explicitly deferred: durable state and
reconciliation for a run that has side effects.

## Context

V1.1 has two execution paths. `AgentRunner.RunAsync` is a model-driven loop that persists
`TaskState` after every step (ADR-0017). `PrepareSkillAsync` / `ExecutePreparedSkillAsync` run
deterministic Capabilities against an immutable, hash-approved `ExecutionPlan` and are terminal and
non-resumable (ADR-0025). Neither can express "an objective is diagnosed by one role, changed by
another and independently checked by a third, each with less authority than the caller".

V1.2 must add that without weakening what already exists:

- one place authorizes and audits a tool call (rules A5, S9, ADR-0024);
- `Critical` stays unbypassable, approval never waives per-step policy (S3, ADR-0024);
- tool output is data, never instruction (S5) — this now also applies *between agents*;
- verification fails closed (S4);
- approvals are not persisted across restarts (ADR-0022, rejected alternative).

Four choices were put to the operator on 2026-09-18 and are recorded as D-026: fixed pipeline,
human-only approval, side-effect-only journal, and runtime + CLI + API + UI surfaces.

## Decision

### 1. Logical agents in one process

An *agent* is a runtime-created, single-run execution context with an identity, a role, an
**authority envelope**, budgets and a parent. It is not a process, service, thread or plugin. The
runtime assigns `AgentId`; no package or model can supply one (same reasoning as A11).

`AgentIdentity` is additional to `ActorIdentity`, never a replacement: the actor is the operator
on whose authority the objective runs, the agent is which logical role acted. Every audit event of a
delegated run carries both. An `AgentIdentity` can never be an approver (§5).

Roles are generic classes, not products: `AgentRoleKind` = `Discovery`, `Diagnostic`,
`Remediation`, `Verification`. What each role may do is a **role profile**, which is host
configuration (a new optional `delegation` section in `policy.yaml`), not code and not something a
plugin can contribute. A missing or malformed section, or a role without a profile, denies
delegation (S3). `SafeDefault` ships no profiles, so delegation is off until an operator grants it,
exactly like contextual Skill rules in ADR-0025.

### 2. Fixed sequential pipeline

The orchestrator is deterministic runtime code, not a model. It runs:

```text
Discovery -> Diagnostic -> [operator approval of plan hash] -> Remediation -> Verification
```

- **Discovery** — read-only. Gathers `Evidence` through Read tools visible under its envelope.
- **Diagnostic** — read-only. Turns Evidence into `Finding`s and, through an existing
  `ICapability`, may prepare an immutable `ExecutionPlan` (ADR-0025). It may not execute it.
- **Approval** — the existing `ExecutionPlanApproval`, bound to `ExecutionPlanHasher` output, given
  by a human. No plan means the objective ends as a completed diagnosis; no approval ends it as
  rejected. Neither is an error.
- **Remediation** — executes exactly the approved plan through the existing step pipeline
  (`ExecuteStepAsync`: policy, approval, validation, timeout, execution, verification, audit). It
  makes **no model call**: a model cannot alter a plan after approval.
- **Verification** — a distinct identity that gathers its own Read evidence and evaluates the
  capability's declared `VerificationSpec`. Its verdict is deterministic; it makes no model call, so
  no model can declare success. `Inconclusive` and `Refuted` are not success (S4).

The model may reason only inside Discovery and Diagnostic, over a tool view restricted by the
envelope. The set and order of roles cannot be changed by a model, by tool output or by
configuration. Roles cannot request further delegation, so depth is structurally 1 and cycles are
structurally impossible. The contract still records `ParentAgentId` and `Depth` so a later ADR can
lift this deliberately; the runtime rejects any depth other than the fixed one.

Parallel execution is excluded. It needs a conflict and blast-radius ADR of its own.

### 3. Authority envelope: reduction, never grant

`AuthorityEnvelope` is an immutable value with exact, non-wildcard members, matching the exact-match
posture of ADR-0025:

- allowed Skill and Capability names, and allowed tool names for Read evidence;
- maximum `RiskLevel` and maximum blast radius;
- allowed targets and environments;
- an optional maintenance window (UTC interval);
- budgets: steps, tokens, an absolute UTC deadline;
- the delegation depth and the originating `ActorIdentity`.

A child envelope is `parent ∩ role profile ∩ request`, computed per dimension: set intersection,
minimum risk, minimum blast radius, interval intersection, minimum of each budget against the
parent's *remaining* amount, earlier deadline. **An empty intersection in any dimension is a denial**
(`DelegationDenied`, audited with the dimension and reason), never a fallback to a default or to the
parent's value. The root envelope of an objective is derived from the operator request and the
role profile in the same way.

**Enforcement.** The envelope is checked in `ExecuteStepAsync`, before `IPolicyEngine.Evaluate`, and
can only deny. The policy engine remains the sole source of `Automatic`/`Approval`; the effective
decision is the more restrictive of the two. The same envelope fields are also placed in the
contextual `PolicyContext` (as with Skill id, target and environment in ADR-0025) so policy rules and
audit can see them. `IPolicyEngine` semantics are unchanged. A tool call that bypasses the envelope
because someone forgot to pass it must fail: a delegated step without an envelope is rejected, not
run unrestricted.

### 4. Inter-agent data is structured, never instruction

The only things that cross from one role to the next are `Evidence`, `Finding`, `ExecutionPlan` and
`VerificationReport` values. There is no free-text channel and no role can address another. Any text
inside those values reaches a later role's context only as delimited tool-result data (S5), and the
invariant that every `Finding` cites recorded `Evidence` is kept. `Evidence` gains an optional
provenance (`AgentId`, role, delegation id) so the chain from observation to verdict is auditable.

### 5. Separation of duties

- **Approvals are human only.** `IApprovalProvider` is never invoked on behalf of an agent, an
  `ApprovalDecision` whose actor is an agent identity is rejected, and the API approval route
  authenticates a human principal. Agents can only *request* approval.
- **Distinct identities.** Diagnostic, Remediation and Verification receive three different
  `AgentId`s. A run whose Verification identity equals another role's is invalid.
- **Independent evidence.** Verification does not trust Remediation's `ToolCallResult`. It receives
  the approved plan hash and the expected end state and reads the system itself. The approval
  boundary and the executor are already separate objects; this closes the last shared one.

### 6. Budgets, deadlines and cancellation

The deadline and budgets of a child are reserved from the parent's remaining amount before the role
starts and reconciled when it ends; consumption is persisted (§7). Restarting a role never resets its
budget, and a maximum number of resumes bounds crash loops. One cancellation token tree covers run,
role and step. Cancelling a side-effecting step whose outcome is not known is journaled as unknown
(§7), not as failed. Budget exhaustion, deadline expiry and cancellation are distinct terminal states,
each audited.

### 7. Durable state and resume

A new `IDelegationStore` contract in `bOps.Abstractions` persists a `DelegationRun` aggregate:
objective, actor, status, per-role identity/envelope hash/status/budget consumed, approved plan hash,
and a **step journal**. `bOps.Memory` implements it over the same SQLite approach as ADR-0017
(whole aggregate as JSON, one row per run, transactional replace). `TaskState` and `ITaskStore` are
unchanged.

Only steps with side effects are journaled, in two durable writes: an **intent** entry (step index,
tool, argument hash) committed *before* execution and an **outcome** entry after. Read-only roles are
not journaled per step; their results are stored when the role completes, and an interrupted read-only
role restarts from its beginning against its remaining budget.

Resume rules, all fail-closed:

| State found | Action |
|---|---|
| Role completed, next role not started | Continue with the next role. |
| Read-only role interrupted | Restart that role. |
| Plan prepared, approval not held | Require a fresh human approval of the same hash. Approvals are never persisted across restarts. |
| Step with outcome `Succeeded` | Never re-executed. |
| Step with intent but no outcome, or outcome `Cancelled`/`Timeout` | **Ambiguous.** Run the step's declared `VerificationSpec`. `Confirmed` records the step as done by reconciliation. `Refuted` or `Inconclusive` stops the run as `RequiresReconciliation`. |
| `RequiresReconciliation` | Waits for an operator: accept the step as done (audited, administrator-only) or abandon the run. There is no automatic retry; a retry is a new objective with a new plan and approval. |

The store is node-local, like the audit sink (A6). Delegation start is idempotent on a caller-supplied
key, consistent with the V1.0 task-start behavior.

### 8. Audit

New events: delegation requested, envelope reduced, delegation denied, role started, role completed,
budget consumed, journal intent, journal outcome, reconciliation, run terminal outcome. Existing
`ToolCallAuditEvent`, `PolicyDecisionAuditEvent`, `ApprovalAuditEvent` and `SkillRunAuditEvent` gain
an optional `Delegation` correlation block (delegation id, agent id, role, parent, envelope hash) that
is `null` for non-delegated runs. This is an additive audit-schema change. Envelope contents are
audited as a hash plus reduced dimensions; arguments and tool output keep their existing redaction
(S6) and never reach telemetry (rule D).

### 9. Surfaces

- **Runtime:** a `DelegationRunner` in `bOps.Runtime` with start, resume, cancel and reconcile.
- **CLI:** `bops delegate`, `bops delegate resume|cancel|reconcile|status`.
- **API:** authenticated endpoints to start, list, read, cancel, resume and reconcile a delegation.
  Approval reuses the existing human approval path. Start/cancel/resume need the operator role,
  reconcile needs the administrator role.
- **UI:** a Delegations view showing roles in order, reduced authority, evidence and findings, the
  plan hash and approval prompt, verification verdict and any ambiguity awaiting reconciliation.
  Existing rendering rules for untrusted output apply.

### 10. Contracts and versioning

All new contracts are additive, product-neutral and dependency-free in `bOps.Abstractions`, which moves
to `1.2.0-preview.1`. No member of the frozen 1.0 surface is removed or changed. Round-trip
serialization tests are required for every new type (A2). The core names no package, tool or provider
(A1); role profiles are data.

## Non-goals

Parallel agents; agent-to-agent conversation; approval by any agent, including auto-approval for low
risk; wildcards in envelopes; model-driven selection or ordering of roles; a model in Remediation or
Verification; a per-role model or provider choice (one `IChatModel` serves a run; revisit by ADR);
roles contributed by plugins; multi-node delegation, Control Plane or remote agents; commercial
Skills; guarantees against a malicious in-process package (S8 still applies).

## Alternatives considered

- **Model-driven delegation graph.** Rejected: non-deterministic, hard to test, and it lets poisoned
  tool output steer which role runs and with what objective (S5).
- **Roles without a model (Skills only).** Rejected: it gives up open-ended diagnosis and would not be
  multi-agent in any useful sense. Determinism is kept where it protects the most (Remediation,
  Verification).
- **Agents can approve low-risk actions.** Rejected for V1.2: it creates a second approval channel and
  forces a trust model for approving roles. It can be reconsidered by ADR once separation of duties
  has been proven in the field.
- **Enforce the envelope only inside `IPolicyEngine`.** Rejected: the reduce-only guarantee would
  depend on every engine implementation, and it would change policy semantics. Enforcing before
  policy in the single execution path keeps A5 and makes the guarantee independent of the engine.
- **Journal every step of every role.** Rejected: large state and many transitions to crash-test for
  no safety gain, because read-only work can be repeated harmlessly.
- **Resume only at role boundaries.** Rejected: it cannot answer what happened when a crash interrupts
  a remediation, and V1.2's stated goal is resuming without repeating side effects.
- **Automatic retry of an ambiguous step.** Rejected: an unverified retry of a non-idempotent action is
  precisely the failure this design exists to prevent.
- **Separate processes or services per agent.** Rejected: out of scope and it would import remote
  execution concerns (V1.4) into V1.2.

## Consequences

- The runtime gains one orchestrator and one store; the tool authorization path stays single.
- The delegation surface is denied by default and needs explicit operator configuration.
- Every role and ambiguity is visible in audit and in the API/UI.
- Crash recovery is the most demanding part to test: a crash test is required for every durable state
  transition.
- Operators must re-approve after a restart. This is deliberate and matches ADR-0022.
- V1.2 adds an HTTP and UI surface, so the API authorization matrix and threat model are extended.
- Known limits, stated plainly: the state store is protected by file permissions only, not
  tamper-evident like the audit chain; envelopes restrict tool calls that go through the runtime and do
  nothing against code that does not (S8).
