# Delegation (V1.2)

How bOps takes one objective through four roles, each with less authority than the operator who asked. The decisions are
ADR-0030 and ADR-0031; this page says what the runtime does today and where it stops. Operators configure it in
[`policy.yaml`](delegation-policy.md); clients drive it through the [CLI](#surfaces) and the [HTTP API](delegations-api.md).

## The idea

A delegated run is not a swarm and not a conversation between models. It is one deterministic pipeline in one process. The
runtime, not a model, decides which role runs next, what each is allowed to touch, and when a person must decide. A model
reasons only in Discovery and Diagnostic; nothing after the plan is approved involves a model.

```text
Discovery -> Diagnostic -> [a person approves the plan by its hash] -> Remediation -> Verification
```

| Role | Does | Model | Can change the system |
|---|---|---|---|
| Discovery | Reads the system through Read tools and records evidence | yes | no |
| Diagnostic | Turns evidence into findings; may prepare an immutable plan through an existing Capability, never runs it | yes | no |
| Remediation | Executes exactly the approved plan through the normal step pipeline (policy, approval, timeout, verification, audit) | no | yes, within its envelope |
| Verification | Reads the system itself and evaluates the change's declared verification | no | no |

No plan means the run ends as a diagnosis (`DiagnosisCompleted`). A plan that is not approved ends as `Rejected`. Neither is an
error. Roles cannot start other roles, so the depth is exactly one and cycles cannot occur.

## Authority only shrinks

Each role runs under an **authority envelope**: the Skills and Capabilities it may prepare, the tools it may call, the highest risk
and blast radius, the targets and environments, an optional maintenance window, and budgets of steps, tokens and time. Every
member is an exact name; there are no wildcards.

The envelope of a role is the intersection of three things: what the operator's request asks for, what the role's profile in
`policy.yaml` grants, and what is left of the parent's authority. It is computed per dimension (set intersection, the lowest
ceiling, the earliest deadline, the smaller budget) and can only be smaller than each. An empty result in a dimension the role needs is a
denial, audited with the dimension and the reason, never a fallback to something wider.

Some dimensions do not apply to a role and are forced to nothing: Discovery and Verification have no Skills or Capabilities,
Remediation and Verification have no token budget, the read-only roles are held to `Read`. Which is required, optional or not
applicable for each role is the table in ADR-0031.

The envelope is checked in the runtime before the policy engine, and can only deny. The policy engine remains the only source of
`Automatic` or `Approval`, and the more restrictive of the two wins. `Critical` is forbidden whatever any envelope says.

## Separation of duties

- **Only a human approves.** The plan and every step that requires approval are decided by a person. A decision made by an agent, by
  the runtime or in the name of one of the run's agents is refused and audited as a refusal.
- **Three identities.** Diagnostic, Remediation and Verification each have their own agent id. A run in which two share one is
  invalid.
- **Independent verification.** Verification is given only the approved plan. It does not trust what Remediation reported: it reads the
  system through its own reduced authority, with no model call. `Refuted` and `Inconclusive` are not success, and the worst verdict
  across steps wins.
- **Data between roles is structured.** Only evidence, findings, the plan and the verification report cross from one role to the next,
  each piece marked with the delegation, agent and role that produced it. Any text in them reaches a later role as delimited
  data, never as instruction.

## Budgets, deadlines and cancellation

A role's budget is reserved from what is left of the run's before it starts and settled when it ends; restarting a role never
resets it. A run has a deadline and a maximum number of resumes. Running out of budget, passing the deadline and being cancelled are
different end states, each audited. Cancelling a step whose outcome is not known is recorded as unknown, not as failed.

## Durability, resume and reconciliation

A run is stored (one SQLite file, `delegations.db` by default, `Delegation:FilePath`) at every transition. A step with side effects is
journaled twice: its intent before it runs and its outcome after. So after a crash:

| Found | What happens |
|---|---|
| A role completed | The run carries on with the next. |
| A read-only role was interrupted | It starts again against what the run has left. |
| A plan was prepared but not approved | A person is asked again, for the same hash. Approvals are never persisted. |
| A step is recorded as done | It is never executed again. |
| A step has an intent but no outcome | Its own declared verification decides. Confirmed: it is recorded as done. Anything else: the run stops as `RequiresReconciliation`. |
| `RequiresReconciliation` | An administrator accepts the step as done (the run can then be resumed) or abandons the run. It is never retried automatically. |

Starting the same objective again with the same idempotency key returns the run the first one created.

## Audit

Every event of a delegated run carries the delegation id, the agent, its role and the hash of its envelope, and the run's own
events (requested, envelope reduced, role started and completed, budget consumed, plan decided, journal intent and outcome,
reconciliation, terminal state) are audited like any tool call, including the refusals. Evidence carries where it came from.
Telemetry never carries output or arguments.

## Surfaces

| Surface | Use |
|---|---|
| CLI | `bops delegate "<objective>" [--skill --capability --target --environment ...]`, `bops delegate status\|resume\|cancel <run-id>`, `bops delegate reconcile <run-id> --accept\|--abandon`. Exit codes are in the README. The plan is approved at the console. |
| API | `/api/delegations`, role-gated: [reference](delegations-api.md). |
| Dashboard | The Delegations view: roles in order, findings and the evidence they cite, the plan hash and its approval, the verification verdict, the journal, and any step awaiting reconciliation. |

## What it does not do

- **No parallelism.** One role at a time, one objective at a time per run. Parallel agents need their own decision on conflicts and
  blast radius.
- **No agent approval.** Not at the plan, not per step, not for low risk. Auto-approval by an agent is a non-goal.
- **No agent-to-agent conversation, no model-chosen roles or order.** The set and order of roles are fixed.
- **No microservices, no separate processes.** An agent is a logical execution context in the host process, not a service.
- **No remote or multi-node guarantees.** State and audit are node-local. There is no distributed delegation, Control Plane or
  remote agent.
- **No per-role model.** One model serves the whole run.
- **No roles or profiles from plugins.** Profiles are host configuration only.
- **Not a sandbox.** An envelope restricts the calls that go through the runtime's delegated entry points. See the limits in the
  [threat model](../security/threat-model.md), which also names the residual risks (for example, there is no four-eyes rule: an
  identity holding both the operator and approver roles can approve the plan of a run it started).

## Known limits of the current implementation

- The runtime does not write the `AwaitingApproval` status: a run waiting for its plan is `Running`. The API and the dashboard report
  `awaitingPlanApproval` from their own approval queue.
- The dashboard is translated (English and Italian); what the API returns, such as findings and evidence descriptions, and its own error messages stay as sent.
- The administrator role for reconciling is enforced by the API. The CLI trusts whoever is at the terminal, as `bops vault rotate-key`
  does.
