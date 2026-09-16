# ADR-0024 — Executing an `ExecutionPlan` through the existing tool pipeline

Status: Accepted

Continues V1.1 under the consolidated roadmap from ADR-0023, which deliberately deferred two
things: the runtime orchestration that actually runs an `ExecutionPlan`, and the
`ICapability`/`ISkillProvider` execution interfaces. This ADR picks up the first. It
deliberately still does not pick up the second — see "Still deferred," below — because nothing
in this increment requires a Skill to exist yet: an `ExecutionPlan` is already a complete, typed,
hashable value on its own (ADR-0023), constructible directly by a test or, later, by a Skill.

## Context

`AgentRunner.ExecuteStepAsync` (private) already does everything one `ExecutionPlanStep` needs:
resolve the tool, evaluate policy (`IPolicyEngine`), request approval if required
(`IApprovalProvider`), validate arguments, execute under a timeout, verify if non-`Read`
(`IVerifiableTool`), and write every audit event rule S9 requires. This is the **one** place a
tool call is authorized and audited (rule A5) — duplicating that logic in a second component to
"orchestrate Skills" would create two divergent answers to "is this tool call allowed," which is
exactly the kind of drift rule A5 exists to prevent. So the decision here is narrow: reuse it, do
not reimplement it.

## Decision

**`AgentRunner` gains one new public method**, alongside `RunAsync`/`ResumeAsync`:

```csharp
public async Task<SkillReport> ExecuteExecutionPlanAsync(
    Guid taskId, ActorIdentity actor, ExecutionPlan plan, ExecutionPlanApproval? approval,
    CancellationToken ct = default)
```

**Approval is checked once, by hash, before anything runs.** If `approval` is supplied,
`ExecutionPlanHasher.ComputeHash(plan)` must equal `approval.PlanHash`, or execution stops before
the first step with a single `Evidence` entry explaining the refusal. This is ADR-0023's own
invariant made real: a plan that no longer matches its approval is, by construction, unapproved.
`approval` is nullable because a plan built entirely from `Read`-risk steps may need no upfront
approval at all — this does not weaken anything, because...

**...capability-level approval does not replace per-step policy.** Every `ExecutionPlanStep`
still goes through `ExecuteStepAsync` exactly as any model-proposed tool call would: its own
`IPolicyEngine.Evaluate`, its own possible `IApprovalProvider.RequestApprovalAsync`, its own
verification, its own audit trail. An `ExecutionPlanApproval` is "the operator agreed to run this
specific plan," not "every tool call in it is pre-authorized" — collapsing the two would mean a
plan containing one `Critical`-adjacent step could ride in on a single operator glance at the
plan's `Rationale`, which is precisely the shortcut rule A5 and rule S3 exist to forbid.
Consolidating the two into one operator decision (so an operator is not asked twice for the same
effective risk) is real UX work, not attempted here — see "Still deferred."

**Steps execute in `Index` order; a denial, rejection, or unknown tool stops the plan.** Each
step's `PlanStep`/verification outcome becomes one `Evidence` entry
(`EvidenceKind.ExecutedAction`, plus a second `EvidenceKind.Verification` entry when the step was
verified) — the same authorization/outcome data that already reaches the audit log, now also
reaching the caller as structured `Evidence` rather than only as an audit side-effect. Stopping on
the first `PolicyDenied`/`UnknownTool`/`UserRejected` step (mirroring `AgentRunner`'s own
`deviated` check in `ContinueAsync`) is the same "do not keep going after a dead end" posture the
existing loop already has — a Skill executing five steps does not get to skip a denial and
attempt step six anyway.

**`SkillReport.Findings` is always empty from this method.** Turning `Evidence` into `Finding`s
is domain interpretation only a Skill's own logic can do (ADR-0023: "an Inference is not the same
kind of claim as a Fact"); `ExecuteExecutionPlanAsync` produces the Evidence a Skill's logic would
consume to build Findings, not the Findings themselves. Returning an empty list rather than
omitting the field keeps `SkillReport`'s shape whole for a caller that has no Skill logic yet
(exactly what today's tests do), while making unmistakable that no interpretation happened here.

**No `ITaskStore` persistence in this pass.** `RunAsync`/`ResumeAsync` persist after every step
(V0.7, ADR-0017) because a model-driven task can run for many steps over an unbounded time and
must survive a restart. An `ExecutionPlan` is already a bounded, pre-computed sequence — losing
progress mid-plan on a crash is a real gap, but persisting Skill-run state is its own design
question (what "resuming" a partially-run plan even means when steps 1–3 already had side
effects) that deserves its own decision, not an incidental extension of `TaskState`'s existing
shape. Recorded as a real gap below, not silently dropped.

## Still deferred

- **`ICapability`/`ISkillProvider`.** This ADR executes an `ExecutionPlan` handed to it; it says
  nothing about how one gets built. That needs `IToolInvoker` (rule A10's already-named,
  never-yet-implemented host service: read-only tools of the same package only) so a Skill can
  gather diagnostic evidence before committing to a plan — real new infrastructure, not a small
  addition, and still not attempted here.
- **Consolidated capability-level + per-step approval**, per "Decision," above.
- **Persistence/resumability of a Skill run**, per "Decision," above.
- **A sample Skill.** Still blocked on the two items above.

## Alternatives considered

- **A separate `SkillRunner` component, not a method on `AgentRunner`.** Rejected — it would need
  the exact same constructor dependencies (`IToolRegistry`, `IPolicyEngine`, `IApprovalProvider`,
  `IAuditSink`, `TimeProvider`, `ILogger`) `AgentRunner` already has, and the only way to reuse
  `ExecuteStepAsync` without duplicating its ~140 lines of policy/approval/verification/audit
  logic is to keep it in the same class. A second class wrapping the first for no reason beyond
  "orchestration sounds like its own concept" would be an abstraction this task does not need.
- **Let an `ExecutionPlanApproval` waive every step's individual policy check.** Rejected in
  "Decision," above — it is the one alternative that would have made this ADR a security
  regression rather than an addition.
- **Continue past a denied step to whatever comes next in the plan.** Rejected — a Skill's plan
  is not a checklist of independent actions; step 3 failing to authorize usually means step 4's
  premise (built on step 3 having happened) no longer holds. Stopping is the safe default; a
  Skill wanting different behavior can inspect the partial `SkillReport` and decide to build and
  submit a new plan.

## Consequences

- `AgentRunner` gains one new public method and no new constructor dependency — everything
  `ExecuteExecutionPlanAsync` needs, it already had.
- `bOps.Abstractions` is unchanged by this ADR; ADR-0023's types are consumed, not extended.
- No existing `AgentRunner` behavior changes: `RunAsync`/`ResumeAsync`/`ContinueAsync` and every
  test covering them are untouched.
