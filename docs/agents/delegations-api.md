# Delegations API

Authenticated HTTP endpoints for delegated multi-agent runs (ADR-0030 section 9, V1.2-J). A delegated run takes an objective
through four roles in a fixed order: Discovery, Diagnostic, Remediation, Verification. Each role has less authority than the
operator who started it, a change is made only after a human approves the plan by its hash, and Verification is independent of
Remediation. Nothing here lets a caller widen that: a request can narrow a run's budget, never its authority, and the roles'
profiles come from the `delegation` section of `policy.yaml` (ADR-0031). Without that section, or with a `policy.yaml` that failed to
load, a start ends `Denied` on the `Profile` dimension before any model call.

The HTTP surface follows the task pattern of ADR-0018: a start returns `202` with the run's id before the run has finished, and a
client follows it by reading the store. Bearer authentication, the rate limit and the configured API keys are the same as for every
other `/api` endpoint.

## Roles

| Need | Role |
|---|---|
| Read, list | `viewer` |
| Start, cancel, resume | `operator` |
| Decide a plan, list plans waiting | `approver` |
| Settle a step whose outcome is not known | `administrator` |

The role is checked before anything runs. The person who decides is always the authenticated principal: no body names who
approves, cancels or reconciles. A principal holding both `operator` and `approver` may approve the plan of a run it started;
requiring a second person is not part of V1.2 (see the threat model).

## Endpoints

| Endpoint | Purpose |
|---|---|
| `POST /api/delegations` | Starts a run. `202` with `{ "delegationId": "..." }`; `400` for a bad body; `503` when the host is at capacity. |
| `GET /api/delegations?status=&limit=` | The most recent runs, or those at one status. `limit` is at most 100. |
| `GET /api/delegations/{id}` | One run. `404` if it was never stored. |
| `POST /api/delegations/{id}/cancel` | Cancels a run. `200` with the run; `409` for a run waiting for reconciliation. |
| `POST /api/delegations/{id}/resume` | Continues a run a crash or restart left running. `202`; `409` if this host is executing it or it has ended; `404`. |
| `POST /api/delegations/{id}/reconcile` | `{ "decision": "accept" \| "abandon", "note": "..." }` for a run at `RequiresReconciliation`. `200` with the run; `409` if it is not waiting; `404`. |
| `GET /api/delegations/approvals` | The plans waiting for a decision, each with its hash, steps, findings and the authority it would run under. |
| `POST /api/delegations/{id}/approval` | `{ "planHash": "...", "approved": true, "note": "..." }`. `204` when delivered; `409` if the plan waiting has another hash (nothing is decided); `404` if none is waiting. |

### Starting a run

```json
{
  "objective": "Find out why nginx stopped and fix it",
  "maxSteps": 20,
  "maxTokens": 100000,
  "remediation": {
    "skillId": "service.skill",
    "capabilityName": "service.restore",
    "target": "web-1",
    "environment": "prod",
    "blastRadius": "single",
    "dryRun": false,
    "input": { }
  }
}
```

Without `remediation` the run only diagnoses and ends `DiagnosisCompleted`; no one is asked for anything. `maxSteps` and
`maxTokens` can only narrow what the role profiles allow. `blastRadius` is `single`, `multiple` or `fleet`.

An `Idempotency-Key` header (at most 128 characters) makes a repeated start safe: the same operator with the same key gets the run
the first call created and nothing else starts. The delegation store decides, so this holds across a restart.

### Following a run

Poll `GET /api/delegations/{id}`. `status` is one of `Running`, `AwaitingApproval`, `RequiresReconciliation`, `Completed`,
`DiagnosisCompleted`, `Rejected`, `VerificationFailed`, `Denied`, `PolicyBlocked`, `BudgetExceeded`, `DeadlineExceeded`, `Cancelled`,
`Abandoned` and `Failed`. While a run waits for a human to decide its plan its status is `Running` and `awaitingPlanApproval` is
`true`; `runningInThisHost` says whether this host is executing it (a run left `Running` by a crash is not).

To approve: read `GET /api/delegations/approvals`, check the plan, and post its `planHash` to `/{id}/approval`. A decision on any
other hash is refused. Approvals are never persisted: a plan that is waiting when the host stops is asked again when the run is
resumed. The approval of each step of a plan, where a tool requires one, goes through the existing `/api/approvals` queue.

### What a response contains

A run shows its status, objective, the operator, each role with its agent id, status and consumption, the findings and the ids and
descriptions of the evidence they cite, the plan hash and who approved it, the step journal with each step's outcome and
verification, and any denial or bounded error text. It never carries what a tool returned (the data of each piece of evidence), the
full authority of a role, model requests or replies, or secrets. Text is bounded.

## Exit paths of a run

`Completed`: the approved plan ran and independent verification confirmed it. `Rejected`: the plan was rejected; nothing changed.
`VerificationFailed`: verification did not confirm; the change is not confirmed. `RequiresReconciliation`: a step may or may not have
taken effect and could not be settled; it is never retried, and an administrator accepts it as done or abandons the run.
`Denied`, `PolicyBlocked`, `BudgetExceeded`, `DeadlineExceeded`, `Cancelled`, `Abandoned` and `Failed` are as their names say.
