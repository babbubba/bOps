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

## Which roles a request needs

The roles a run requires depend on what it asks for (ADR-0044). A **diagnosis** (no change prepared) requires Discovery and
Diagnostic; a request that **prepares a change** requires all four. The run's root authority is derived from the required roles'
profiles only, so a Remediation or Verification profile, present or absent, neither enables nor restricts a diagnosis, and a role
the request does not require never runs. A required role without a usable profile denies the run before any model call, naming
that role. The remediation path is unchanged: all four profiles, the plan approved by its hash, independent verification.

**A diagnosis is read-only by construction.** Discovery and Diagnostic are held to `Read` whatever their profiles say, and a
diagnosis root carries no Skill and no Capability, so nothing a diagnosis can call has a side effect; every call still goes
through the envelope check and the policy engine. A diagnosis can therefore run with only the read-only profiles that
`bops delegate profiles init --read-only` generates ([delegation-policy.md](delegation-policy.md)), and bOps never creates a
profile by itself.

**Readiness** answers in advance, for each request shape, whether the required roles' profiles are usable, with the reducer's own
reason and dimension: `bops delegate readiness [--remediation]`, `GET /api/delegations/readiness`, and the panel next to the start
form. It is about the role profiles only: the selected change, target, environment and input are validated when the run starts.

## What a role could not see

Evidence can be incomplete: a read that failed or was refused, an output marked partial or unavailable, an observation that was
shortened. When Discovery or Diagnostic ends, the runtime records these as **typed limitations** of that role's model-loop evidence
(at most 64, the most recent, with a count of the rest), from the same classification that feeds the evidence-limitations
section of an ordinary task's answer (ADR-0042). An entry names the step, the tool, the outcome, failure kind and completeness, the
length before shortening, and the id of the Evidence the step produced — or no id when it produced none; none is ever made up. It
carries no tool output, argument, error text or model text, and no model decides what is in it.

A finding is marked as **resting on limited evidence** when it cites Evidence that is itself typed as limited — an exact join of
ids, never a judgement of materiality. The mark never adds, removes or changes a finding. The CLI, the API and the dashboard show
the limitations per role beside the findings, and before a plan is approved; a run recorded before this existed shows "not
recorded", never "none".

The Diagnostic reply must be one JSON object. It is read strictly and within bounds (65,536 characters, depth 16, 64 findings): a
property repeated at any depth, a reply that is too large, not JSON or without a `findings` array yields zero findings and a visible
`findingsReply` outcome, never a guess and never an exception. Every finding still cites only Evidence recorded in the run.

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
| CLI | `bops delegate "<objective>" [--skill --capability --target --environment --input ...]`, `bops delegate status\|resume\|cancel <run-id>`, `bops delegate reconcile <run-id> --accept\|--abandon`; `bops delegate readiness [--remediation]`, `bops delegate profiles init --read-only [--write [--overwrite]]`, `bops delegate profiles check`. Exit codes are in the README. The plan is approved at the console, with the run's evidence limitations shown first. |
| API | `/api/delegations`, `/api/delegations/readiness` and `/api/skills`, role-gated: [reference](delegations-api.md). |
| Dashboard | The Delegations view: the role readiness for the request being prepared (Start stays disabled until the server says ready), the change chosen from the Skill catalog with its input as a form generated from the Capability's schema (raw JSON as an advanced fallback), roles in order, findings and the evidence they cite, the evidence limitations per role, the plan hash and its approval, the verification verdict, the journal, and any step awaiting reconciliation. |

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
