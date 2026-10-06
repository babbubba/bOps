# Delegations API

Authenticated HTTP endpoints for delegated multi-agent runs (ADR-0030 section 9, V1.2-J, ADR-0044). A delegated run takes an
objective through the roles in a fixed order: Discovery, Diagnostic, Remediation, Verification. A diagnosis needs only Discovery
and Diagnostic and ends `DiagnosisCompleted`; a run that prepares a change needs all four. Each role has less authority than the
operator who started it, a change is made only after a human approves the plan by its hash, and Verification is independent of
Remediation. Nothing here lets a caller widen that: a request can narrow a run's budget, never its authority, and the roles'
profiles come from the `delegation` section of `policy.yaml` (ADR-0031, [delegation-policy.md](delegation-policy.md)). Without a
profile for a role the request requires, or with a `policy.yaml` that failed to load, a start ends `Denied` on the `Profile`
dimension before any model call. `GET /api/delegations/readiness` says in advance which roles are usable.

The HTTP surface follows the task pattern of ADR-0018: a start returns `202` with the run's id before the run has finished, and a
client follows it by reading the store. Bearer authentication, the rate limit and the configured API keys are the same as for every
other `/api` endpoint.

## Roles

| Need | Role |
|---|---|
| Read, list, readiness, the Skill catalog | `viewer` |
| Start, cancel, resume | `operator` |
| Decide a plan, list plans waiting | `approver` |
| Settle a step whose outcome is not known | `administrator` |

The role is checked before anything runs. The person who decides is always the authenticated principal: no body names who
approves, cancels or reconciles. A principal holding both `operator` and `approver` may approve the plan of a run it started;
requiring a second person is not part of V1.2 (see the threat model).

## Endpoints

| Endpoint | Purpose |
|---|---|
| `POST /api/delegations` | Starts a run. `202` with `{ "delegationId": "..." }`; `400` for a bad body (see below); `415` for a body that is not JSON; `503` when the host is at capacity. |
| `GET /api/delegations/readiness?remediation=false\|true` | Which roles are usable for a request of that shape, before submitting. See below. |
| `GET /api/skills` | The activated Skill catalog, with each Capability's input schema and risk. See below. |
| `GET /api/delegations?status=&limit=` | The most recent runs, or those at one status. `limit` is at most 100. |
| `GET /api/delegations/{id}` | One run. `404` if it was never stored. |
| `POST /api/delegations/{id}/cancel` | Cancels a run. `200` with the run; `409` for a run waiting for reconciliation. |
| `POST /api/delegations/{id}/resume` | Continues a run a crash or restart left running. `202`; `409` if this host is executing it or it has ended; `404`. |
| `POST /api/delegations/{id}/reconcile` | `{ "decision": "accept" \| "abandon", "note": "..." }` for a run at `RequiresReconciliation`. `200` with the run; `409` if it is not waiting; `404`. |
| `GET /api/delegations/approvals` | The plans waiting for a decision, each with its hash, steps, findings, the authority it would run under and the run's evidence limitations. |
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

The body is read as strict JSON: a property repeated anywhere in it, `input` included, is `400` with `"code": "malformed_json"`,
never first- or last-wins. Property-name casing, unknown members and numbers bind as before. With a `remediation`, the Skill and
Capability are checked against the activated catalog and `input` against the Capability's input schema before any run is created
or any model is called:

| `400` body | When |
|---|---|
| `{ "message": "...", "code": "unknown_capability" }` | `skillId`/`capabilityName` is not an activated Capability. |
| `{ "message": "...", "code": "capability_input_invalid", "parameter": "<name or null>" }` | `input` breaks the schema: an unknown or missing required field, a wrong JSON type, a value outside `allowedValues`, a bound or a length. Nothing is coerced or clamped. |
| `{ "message": "...", "code": "malformed_json" }` | The body is not strict JSON. |

The message names the parameter and the rule, never the value of a `sensitive` parameter. Other `400` bodies keep their `message`.
The same schema is enforced again when the Capability is prepared, for every Capability invocation, delegated or not. Capability
input is stored with the run as plain JSON in the delegation database; there is no separate secret storage for it.

A request that passes these checks can still be refused by the runtime before any model call, as a run that ends `Denied` naming
the dimension: a role the request requires has no usable profile (`Profile`), or the requested change is outside the Remediation
profile (`Targets`, `Environments`, `Capabilities`, ...). Readiness does not predict this: it is about the role profiles, not the
selected change.

An `Idempotency-Key` header (at most 128 characters) makes a repeated start safe: the same operator with the same key gets the run
the first call created and nothing else starts. The delegation store decides, so this holds across a restart.

### Readiness before submitting

`GET /api/delegations/readiness?remediation=false|true` (`remediation` defaults to `false`; any other value is `400` with
`{ "message": "'remediation' is true or false." }`). It runs the runtime's own reducer over the loaded profiles for a request of
that shape, executes nothing, writes no audit event and is sent with `Cache-Control: no-store`.

```json
{
  "remediation": false,
  "ready": false,
  "policy": "noFile",
  "roles": [
    { "role": "Discovery",    "state": "missing",     "dimension": "Profile", "reasonCode": "profile_missing", "reason": "Discovery role: no usable profile is configured." },
    { "role": "Diagnostic",   "state": "missing",     "dimension": "Profile", "reasonCode": "profile_missing", "reason": "Diagnostic role: no usable profile is configured." },
    { "role": "Remediation",  "state": "notRequired", "dimension": null,      "reasonCode": "not_required",    "reason": null },
    { "role": "Verification", "state": "notRequired", "dimension": null,      "reasonCode": "not_required",    "reason": null }
  ],
  "profileDriftCount": 0,
  "evaluatedAtUtc": "2026-10-05T12:00:00+00:00"
}
```

- `roles` always has the four roles in pipeline order. `state` is `ready`, `missing`, `malformed` (present but not usable) or
  `notRequired`.
- `reasonCode` is what a client branches on: `ready`, `not_required`, `profile_missing`, `policy_load_failed`,
  `profile_for_other_role` or `reduction_denied`. The set may grow; a client treats an unknown code as not ready.
- `reason` is the text a start would be denied with (bounded, for display, never to be parsed). For `policy_load_failed` it is a
  fixed sentence: the loader's message stays in the host log and is never sent over HTTP.
- `ready` is authoritative: it is true when every required role is `ready`. It means the required role profiles are usable for
  this type of delegation; the selected change, target, environment and input are validated when you submit.
- `policy` is `noFile`, `loaded` or `loadFailed`. `profileDriftCount` counts drift items ([delegation-policy.md](delegation-policy.md));
  it never changes `ready`.

### The Skill catalog

`GET /api/skills` lists the activated Skills only (nothing from a disabled, failed or uninstalled plugin, and no plugin internals),
Skills by `skillId` and Capabilities by `name`, in ordinal order, with `Cache-Control: no-store`. An empty catalog is
`{ "skills": [] }`.

```json
{
  "skills": [
    {
      "skillId": "service.skill",
      "package": "bops.packages.service",
      "trust": "Official",
      "capabilities": [
        {
          "name": "service.restore", "version": "1.0.0", "description": "Restores a stopped service.", "risk": "High", "supportsDryRun": true,
          "inputSchema": [
            { "name": "serviceName", "type": "String", "description": "...", "required": true, "sensitive": false, "allowedValues": null,
              "minimum": null, "maximum": null, "minLength": 1, "maxLength": 256, "minItems": null, "maxItems": null }
          ]
        }
      ]
    }
  ]
}
```

`inputSchema` is the Capability's declared input in declaration order; `type` is `String`, `Integer`, `Number`, `Boolean`, `Path`,
`Duration`, `Enum` or `PathList`, and every constraint is present, `null` where none is declared. A Capability whose schema is
inconsistent is refused when its Skill registers, so a listed schema can always be satisfied.

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

**Evidence limitations (ADR-0044).** Discovery and Diagnostic carry the typed limitations of their own model-loop evidence
collection, recorded by the runtime when the role ends:

- `evidenceLimitations`: at most 64 entries, the most recent, each `{ stepIndex, toolName, unknownTool, outcome, failureKind,
  completeness, shortenedFromCharacters, evidenceId }` with enums by name. `evidenceId` is the Evidence the step produced, or
  `null` when it produced none (a failed or refused read); none is ever made up. No tool output, error text, argument or model
  text is in it. `[]` means no recorded limitation for that role's model-loop evidence; `null` means not recorded (a run from
  before this contract) — never "none".
- `evidenceLimitationsOmitted`: how many further entries were recorded beyond the 64.
- `findingsReply` (Diagnostic only): how its reply was read — `status` `Valid`, `Absent` or `Malformed`, `problem` (`None`,
  `NoJsonObject`, `InvalidJson`, `DuplicateProperty`, `TooLarge`, `NotAnObject`, `MissingFindingsArray`, `TooManyFindings`) and
  `discardedFindings`. A malformed reply is a completed diagnosis with zero findings, never a guess.
- Each finding's `restsOnLimitedEvidence`: `true` when it cites Evidence that is itself typed as limited; `false` only means it
  cites no such Evidence (a failed read produces no Evidence at all, so read it beside the role's list); `null` when the run did
  not record limitations.

`GET /api/delegations/approvals` adds the same data to each plan, joined from the stored run by its id: `limitations` is
`{ available, roles: [{ role, recorded, evidenceLimitations, evidenceLimitationsOmitted, findingsReply }] }` for Discovery and
Diagnostic. `available: false` (the run could not be read) and `recorded: false` mean the limitations are unavailable or not
recorded, never that there are none.

## Exit paths of a run

`Completed`: the approved plan ran and independent verification confirmed it. `Rejected`: the plan was rejected; nothing changed.
`VerificationFailed`: verification did not confirm; the change is not confirmed. `RequiresReconciliation`: a step may or may not have
taken effect and could not be settled; it is never retried, and an administrator accepts it as done or abandons the run.
`Denied`, `PolicyBlocked`, `BudgetExceeded`, `DeadlineExceeded`, `Cancelled`, `Abandoned` and `Failed` are as their names say.
