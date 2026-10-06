# ADR-0044 — Request-dependent delegation authority and operability

Status: Accepted (2026-10-05, operator decision after the independent HARDEN-11 authority/security architecture review
PASS — blockers 0, non-blocking N-1…N-13 incorporated; operator decisions A–E — see "Independent review and operator
acceptance (2026-10-05)")
Date: 2026-10-05

Amends [ADR-0030](0030-privilege-reducing-multi-agent-delegation.md) §1 and §3 (which roles a delegation requires and
what the root envelope is derived from), [ADR-0031](0031-per-role-envelope-requirements.md) (§1 and §5: the table applies
to the roles a request requires, and "a role with no profile denies delegation" becomes "a *required* role with no
profile") and [ADR-0018](0018-bops-api-minimal-surface.md) (two additive read-only endpoints and their authorization).
Closes the HARDEN-11 gap that [ADR-0042](0042-evidence-reasoning-and-limitation-disclosure.md) records under "Parent
responsibility — and the gap it exposes" (typed limitation metadata) and decides its `FindingsOf` follow-up. Their texts
are not edited; each carries a forward pointer to this ADR. Governs HARDEN-11 of the V1.3.x reliability train
([packet](../../../agentic/_tasks/2026-09-25-v1.3x-harden-11-delegation-operability.md);
[plan](../../../agentic/_plans/2026-09-25-v1.3x-reliability-hardening.md) §10, findings F-14, F-15, F-23, F-24 and the
delegation side of F-05; operator case B). One ADR, not two: the authority change, readiness, profile tooling, the
catalog and the typed limitation metadata are one delegation-operability decision with one lifecycle, and the
limitation contract only becomes reachable through the delegation views this ADR defines.

The ADR was independently reviewed (PASS) and accepted by the operator on 2026-10-05. HARDEN-11 subsequently
implemented these decisions, passed the independent implementation authority/security review and merged through PR #77
(`12a03165ef458ecb3b36625d955731e34671c6f8`). The review's clarifications are incorporated in the sections they concern;
N-1 (§10.2), N-5 (§20 P1) and N-7 (§9.2) were **mandatory Phase-1 obligations**.

## Context

Read at `main` `99530507b20058ce4b7869912d97165218d8f737` (HARDEN-10 merged via PR #76). Line numbers drift; re-anchor
before editing.

1. **The pipeline is fixed and complete.** `RoleRequirements.Pipeline` is `Discovery, Diagnostic, Remediation,
   Verification` (`src/core/bOps.Abstractions/RoleRequirements.cs:36-37`, internal, friend-visible to the runtime).
   `RiskCap(role)` is `Read` for every role but Remediation (`:47`). Skills and Capabilities are `Required` for
   Remediation, `Optional` for Diagnostic, `NotApplicable` otherwise (`:62-67`); tokens are required for Discovery and
   Diagnostic only (`:70-72`).
2. **The root needs every role.** `EnvelopeReducer.DeriveRootAttributed` (`src/core/bOps.Runtime/EnvelopeReducer.cs:56-110`)
   loops over the whole pipeline and returns `Denied` on `EnvelopeDimension.Profile` for the first role whose
   `IRoleProfileSource.GetProfile` is `null` (`:63-78`, message `:415-418`: *"no usable profile is configured. Delegation
   stays off until the operator configures one for every role."*). `BuildRoot` (`:232-257`) takes the union of the four
   profiles' sets, the highest capped risk and blast radius, and the **sum** of their steps, tokens and durations. It
   then proves at the start that every role reduces from that root (`:94-107`).
3. **A diagnosis-only run never reaches Remediation or Verification.** `DelegationRunner.RunFromDiagnosticAsync`
   (`src/core/bOps.Runtime/DelegationRunner.cs:386-447`) ends `DiagnosisCompleted` when there is no prepared plan or the
   request is a dry run (`:438-443`); it calls `PrepareDelegatedSkillAsync` only when `Request.Remediation` is set
   (`:411`). `ResumePipelineAsync` ends the same way for a stored run without a remediation request (`:477-483`).
   `PrepareCheckAsync` (`:716-743`) runs only for a remediation request (`:355`).
4. **Consequence — the primary authority defect (F-14).** A request with `Remediation == null` still needs Remediation
   and Verification profiles, and because ADR-0031 makes Remediation's Skills and Capabilities `Required` and its risk
   floor `Low` (`RoleRequirements.RiskFloor`), an operator must author a mutation-capable grant merely to diagnose. The
   profiles that can never run still widen the root (union of tools, sum of budgets and durations).
5. **Per-step enforcement already holds a read-only role to Read.** `EnvelopeEnforcer.CheckStep`
   (`src/core/bOps.Runtime/EnvelopeEnforcer.cs:46-96`) refuses a non-`Read` tool without a Skill scope (`:60-66`),
   refuses a tool outside `AllowedTools`, refuses `Critical`, and caps the ceiling at `RoleRequirements.RiskCap(role)`
   (`:81-89`). It is called on every delegated step and verification step (`AgentRunner.cs` `:1359`, `:2901`, `:3325`).
6. **Profiles and the policy file.** `IRoleProfileSource.GetProfile` returns `null` both for an absent profile and for
   malformed configuration (`RoleProfile.cs:263-273`). `DelegationSectionParser` refuses a whole `delegation` section
   when any part is malformed, and the hosts turn any `PolicyConfigurationException` into `PolicyConfig.AllForbidden`
   (`src/core/bOps.Api/Program.cs:390-421`, `src/core/bOps.Cli/Program.cs:322-352`). `LoadedPolicy` keeps the engine and
   config but **not why** it is what it is, so today nothing can tell "missing" from "malformed". The policy is read
   once per host start (an API singleton; once per CLI process).
7. **A policy file without `defaults` forbids everything.** `PolicyEngine` resolves a risk level with no default entry
   to `Forbidden` (`src/core/bOps.Policy/PolicyEngine.cs:94-101`, rule S3). With no file at all the hosts use
   `PolicyConfig.SafeDefault` (`Read`/`Low` automatic, `Medium`/`High` approval, `Critical` forbidden). A generated file
   that contained only a `delegation` section would therefore make every tool — including the Read tools a diagnosis
   needs — `Forbidden`.
8. **Tools.** `IToolRegistry.GetAvailableManifests()` lists the platform-matched, capability-satisfied manifests as of
   the last `RefreshCapabilitiesAsync` (`src/core/bOps.Abstractions/Registry.cs:49-50`). There is no listing of
   registered-but-unavailable tools; `Resolve` returns `null` for unknown or disabled names.
9. **Skills and Capabilities.** `ISkillRegistry.GetAvailableSkills()` returns activated Skills in ordinal order as
   `SkillDescriptor(SkillId, Package, Trust, Capabilities)` (`src/core/bOps.Abstractions/Capabilities.cs:172-176`). A Skill
   has **no** version or description. `CapabilityManifest` has `Name`, `Version`, `Description`, `Risk`,
   `RequiredPermissions`, `InputSchema`, `OutputSchema`, `Timeout`, `SupportsDryRun`, `Verification`,
   `RollbackDescription` and the host-stamped `Package` (`:13-90`). No API endpoint lists Skills.
10. **Capability input is not validated.** `CapabilityManifest.InputSchema` is declared and never read by the runtime;
    `AgentRunner.PrepareDelegatedSkillAsync` (`:1101-1185`) resolves the Capability and calls `PrepareAsync` with the
    caller's `ToolArguments` as they came. Tool arguments, by contrast, are validated by `AgentRunner.ValidateArguments`
    and `ViolatedConstraint` (`:3704-3810`) — unknown names, required, JSON-native type, `AllowedValues`, and the
    HARDEN-6 constraints — and tool constraint consistency is checked at registration (`ToolRegistry.cs:45-48`, `:79-120`);
    `SkillRegistry` performs no such check on `InputSchema`.
11. **`ToolParameter` after HARDEN-6** (`src/core/bOps.Abstractions/Tools.cs:34-133`): `Name`, `Type`
    (`String`, `Integer`, `Number`, `Boolean`, `Path`, `Duration`, `Enum`, `PathList = 7`), `Description`, `Required`,
    `Sensitive`, `AllowedValues`, `Minimum`/`Maximum` (Integer, Number), `MinLength`/`MaxLength` (String, Path),
    `MinItems`/`MaxItems` (PathList). There is **no** pattern constraint and no Duration format constraint.
12. **API start.** `DelegationsEndpoints.ToRequest` (`src/core/bOps.Api/DelegationsEndpoints.cs:170-208`) accepts free-text
    `skillId`/`capabilityName` and an arbitrary `input` object. The body is bound with the minimal-API default serializer
    options, which do not set `AllowDuplicateProperties = false` (contrast `BrowserSessionEndpoints.cs:37`, HARDEN-10).
13. **UI.** The Delegations page takes Skill and Capability as free text and sends no capability input at all
    (`web/bops-ui/src/app/features/delegations/delegations.ts:66-75`, `:159-185`); there is no readiness, so the page looks
    usable and fails after submit (F-15).
14. **CLI.** `bops delegate` parses verbs `status|resume|cancel|reconcile`, anything else is an objective
    (`src/core/bOps.Cli/DelegateCommand.cs:63-81`); exit codes `0, 1, 2, 3, 4, 5, 6, 10, 130` (`:43-53`, `:255-266`). The
    composition root uses `Host.CreateApplicationBuilder()` without command-line configuration and requires a
    `ModelProvider` section before any delegate command (`Program.cs:97`, `:206-207`). No CLI command has a JSON output
    mode.
15. **`FindingsOf`** (`src/core/bOps.Runtime/DelegationRoleData.cs:87-146`) slices from the first `{` to the last `}`
    of the final reply and calls `JsonNode.Parse` with default options (`:107`). A repeated property name is accepted at
    parse time and throws `ArgumentException` only on a later access (`document["findings"]`), which the
    `catch (JsonException)` (`:109`) does not catch: by reading, a duplicate-key Diagnostic reply escapes the parser and
    ends the run `Failed` only through the orchestrator's catch-all (`DelegationRunner.cs:107-113`). The final reply is
    the model's full `TextResponse` (`AgentRunner.cs:774-788`), not bounded by the observation budget; `FindingsOf` has no
    size bound. HARDEN-1 and HARDEN-2 contain the same class of input with
    `JsonDocumentOptions { AllowDuplicateProperties = false }` (`StrictJson.cs`, `AgentRunner.cs:4008`,
    `EvidenceRead.cs:34`).
16. **Evidence limitations.** `DelegationRoleData.EvidenceOf` (`:57-75`) turns only successful tool steps into
    `Evidence`; failed, denied, shortened or partial collection is not typed anywhere in `SkillReport`, `Evidence`,
    `PlanApprovalRequest`, `DelegationRoleRun` or the views. The role task is persisted (`TaskOrigin.Delegated`,
    `DelegationId`), but `DelegationRoleRun` does not record its task id and `ITaskStore` cannot list tasks by delegation.
    `EvidenceLimitationsDigest` (`src/core/bOps.Runtime/EvidenceLimitationsDigest.cs`) is the deterministic ADR-0042
    classification of those steps.
17. **The HARDEN-3 guard.** `TaskResume.Evaluate` refuses `TaskOrigin.Delegated` before anything else
    (`src/core/bOps.Runtime/TaskResume.cs:144-149`, refusal `task_delegated`), so `POST /api/agents/tasks/{id}/resume` and
    `bops resume` answer 409 / exit 1 for a role task; a delegated run resumes only through
    `POST /api/delegations/{id}/resume` / `bops delegate resume` → `DelegationRunner.ResumeAsync`.
18. **Approval view.** `ApiPlanApprovalProvider` builds `PendingPlanApproval` from the `PlanApprovalRequest` when the
    runtime asks (`src/core/bOps.Api/ApiPlanApprovalProvider.cs`); the Diagnostic role's completion — report included — is
    persisted by `CompleteRoleAsync` (`DelegationRunner.cs:880-908`) before `RunApprovedPlanAsync` asks for approval.

## Decision

### 1. The required role set depends on the request

The **request shape** of a delegation is one boolean: whether `DelegationRequest.Remediation` is set (for a stored run,
whether `DelegationRun.Remediation` is set). Nothing else contributes to it: not the profiles, not the model, not a
package, Skill or plugin, not the API, CLI or UI, not configuration.

| Request shape | `RequiredRoles(shape)`, in pipeline order | Not required |
|---|---|---|
| **Diagnosis-only** (`Remediation == null`) | `Discovery`, `Diagnostic` | `Remediation`, `Verification` |
| **Remediation** (`Remediation != null`, `DryRun` true or false) | `Discovery`, `Diagnostic`, `Remediation`, `Verification` | — |

Normative consequences:

- **One canonical selector.** `RequiredRoles(shape)` has exactly one definition, owned by the runtime's role-requirement
  table (today the internal `RoleRequirements` in `bOps.Abstractions`, friend-visible to the runtime), beside the
  ADR-0031 table it complements. `EnvelopeReducer`, `DelegationRunner`, the readiness evaluator (§6), the API, the CLI and
  the UI never carry their own role table: the reducer and runner call the selector; API and CLI call the runtime's
  readiness evaluator; the UI renders the API's answer. The ADR fixes ownership and the invariant, not a class name.
- **Invariants.** `RequiredRoles(shape) ⊆ RoleRequirements.Pipeline`; its order is the pipeline order; it is a total,
  pure function of `shape`; `Discovery` and `Diagnostic` are in every result; `Remediation ∈ RequiredRoles(shape) ⇔
  Verification ∈ RequiredRoles(shape) ⇔ shape is Remediation`.
- **A not-required role**, for this run: needs no profile; its profile is **not looked up** by root derivation or start;
  it contributes nothing to the root; it receives no budget reservation; it never begins (`BeginRoleAsync` is never
  called for it). A not-required role's absence, or a valid profile for it, can neither block nor widen that run. A
  **malformed** profile for it may make the entire policy file fail to load, under the existing fail-closed
  policy-loader semantics (§4); the loader architecture is not changed.
- **Remediation is unchanged and fail-closed.** All four roles remain required, each must satisfy its profile and the
  ADR-0031 table, `PrepareCheckAsync` still runs before any model call, and a missing or unusable Remediation or
  Verification profile still ends the run `Denied` before Discovery starts.
- **Dry run stays a remediation shape.** A dry-run request still prepares a Capability in Diagnostic (which
  `PrepareCheckAsync` checks against *both* Diagnostic and Remediation) and ends `DiagnosisCompleted`. Making it a
  two-role shape would change what `PrepareCheckAsync` proves and is out of scope; it remains four-role, exactly as today.
- **Normal terminal path unchanged.** A diagnosis-only run still ends `DiagnosisCompleted` after Diagnostic.
- **Defence in depth.** `BeginRoleAsync` refuses to begin a role that is not in `RequiredRoles(stored shape)`: the run ends
  `Failed` with a fixed runtime message ("role not required by this request"), audited as an orchestrator failure. No
  current code path reaches it; it exists so that a later change cannot silently start Remediation for a diagnosis.

### 2. The root envelope is derived only from the required roles

ADR-0030 §3's *"the root envelope of an objective is derived from the operator request and the role profile in the same
way"* and `BuildRoot` are amended to:

```text
Root(request, profiles, now) = Narrow_request( Build( { profile(r) : r ∈ RequiredRoles(shape(request)) } ) )
```

where `Build` is today's construction (union of sets, the highest capped risk and blast radius, the saturating **sum**
of steps, tokens and durations, deadline `now + Σ duration`), and `Narrow_request` is today's request narrowing. The
per-role start proof (`EnvelopeReducer.cs:94-107`) iterates `RequiredRoles(shape)` only.

- **Diagnosis-only root** is built from the Discovery and Diagnostic profiles only, and additionally has **Skills and
  Capabilities forced empty**: a diagnosis-only run never prepares a Capability (fact 3), so the authority is unused and
  removing it is a pure reduction. Consequences: root `MaxRisk ≤ Read` (both inputs are capped at `Read`); tools are the
  union of the two profiles' tools only; budgets and duration are the sums of the two profiles only; the Remediation
  and Verification profiles cannot widen any of tools, Skills, Capabilities, risk, blast radius, targets, environments,
  steps, tokens, window or deadline, and their absence cannot make the root fail.
- **Remediation root** is exactly today's four-role construction. No dimension is relaxed.
- **Stored runs.** The root is persisted (`DelegationRun.RootEnvelope`) and never re-derived on resume. A run stored
  before this ADR keeps its four-role root; whether its later roles begin is still decided by its stored request (fact 3),
  so a stored diagnosis-only run never begins Remediation. A stored request is immutable, so configuration changes
  between start and resume can never turn a diagnosis into a remediation.

`child ⊆ parent ∩ profile ∩ request` (ADR-0030 §3) and every ADR-0031 cell are unchanged for every role that runs.

### 3. Why diagnosis-only remains read-only (the primary security invariant)

**Property R (read-only diagnosis).** For every request with `Remediation == null`, no tool whose manifest risk exceeds
`RiskLevel.Read` is executed by the run — whatever the profiles contain (mutation tool names included), whatever extra
names appear in the request or profile sets, whatever the model proposes, and whatever tools packages register later.

The boundary is the envelope and the role risk cap, not the UI. Argument, layer by layer, each sufficient on its own for
the step path:

1. **Selector.** Only Discovery and Diagnostic can begin (§1, plus the `BeginRoleAsync` guard).
2. **Root.** The root's `MaxRisk` is `≤ Read` and it grants no Skill and no Capability (§2).
3. **Reduction.** `ReduceForRole` caps each role at `min(parent, profile, request, RiskCap(role))`; `RiskCap` of both
   roles is `Read` (fact 1).
4. **Per-step enforcement.** `CheckStep`, before `IPolicyEngine.Evaluate`, refuses any non-`Read` tool without a Skill
   scope and any tool above `min(envelope.MaxRisk, RiskCap(role))` (fact 5). A model-proposed mutation, or a mutation
   name present in a profile, is therefore refused before policy and before execution and audited as a `Forbidden`
   decision with an "Authority envelope:" reason. Two real paths exist and both refuse before `IPolicyEngine.Evaluate`
   and before `ITool.ExecuteAsync`: on the **provider/model path** a tool the role was never offered can be rejected
   earlier as an *unknown tool* under the provider/tool contract (no envelope audit event is required on that path);
   on the **direct/fake-model path** a call that reaches runtime resolution is refused by `EnvelopeEnforcer.CheckStep` as
   `Forbidden`. §20 P1 proves both.
5. **No preparation.** The runner never calls `PrepareDelegatedSkillAsync` for a diagnosis-only run (fact 3), and
   `CheckPreparation` would refuse it anyway on the empty Skills/Capabilities dimensions.
6. **New packages.** A tool registered later is either `Read` (allowed only if its exact name is in the envelope's
   tools) or not (refused by layers 3–4). No wildcard, category or dynamic grant exists (ADR-0030 §3).

Generated read-only profiles (§10) are a stronger safe default; they are **not** the boundary.

### 4. Absent and malformed profiles; the policy file stays strictly fail-closed

`DelegationSectionParser` and the hosts' `AllForbidden` fallback are **unchanged**: one malformed delegation profile —
used by this request or not — still invalidates the whole `policy.yaml` (rule S3; ADR-0031 §2: "a half-loaded policy
would be more permissive than what you wrote"). Making an unused role's malformation tolerable would require a partial
load, which this ADR rejects. The operator's remedy for a malformed unused profile is to fix or delete it.

To *report* this precisely, each host retains its **policy load state** next to `LoadedPolicy`: `NoFile` (built-in
`SafeDefault`), `Loaded`, or `LoadFailed`. The evaluator-facing load-state abstraction (the three-valued state the
readiness evaluator takes) is defined in **`bOps.Runtime`**, beside the evaluator; no `bOps.Abstractions` change. The
loader's message stays host-side — kept for the host log and the local CLI, never sent over HTTP (§6.3). Each host logs
the **absolute resolved policy path** and its load state at start, without logging the file's contents.

| Request | Role required? | Profile state | Readiness role state | Real start |
|---|---|---|---|---|
| any | no | absent | `notRequired` | not looked up; cannot block or widen |
| any | no | present and valid | `notRequired` | not looked up; cannot widen |
| any | no | malformed | (whole policy `LoadFailed`) every **required** role `malformed`, not-required roles `notRequired` | `Denied` on `Profile` at the first required role (policy has no profiles) |
| any | yes | absent (policy `NoFile` or `Loaded`) | `missing` | `Denied` on `Profile`, attributed to that role |
| any | yes | malformed (policy `LoadFailed`) | `malformed` (dimension `Profile`) | `Denied` on `Profile` |
| any | yes | present, for another role (defensive; the source keys by role) | `malformed` (dimension `Profile`) | `Denied` on `Profile` |
| any | yes | present, well-formed, reduction refused (e.g. `maxSteps: 0`, required set `[]`, Remediation `maxRisk: read`, own window already ended) | `malformed` with the reducer's dimension | `Denied` with the same dimension and reason |
| any | yes | present, reduces | `ready` | proceeds past root derivation |

`diagnosis-only + no Remediation profile` and `diagnosis-only + no Verification profile` are therefore `notRequired`
and never block a diagnosis.

### 5. Readiness is a runtime function, never a second policy implementation

A public **readiness evaluator** in `bOps.Runtime` (not `bOps.Abstractions`) takes the profile source, the host's policy
load state, the request shape and an instant, and is the only thing API and CLI call. It is defined in terms of the
reducer, not beside it:

1. `required = RequiredRoles(shape)` (§1).
2. For each pipeline role in order: not in `required` → `notRequired`; else `GetProfile(role)`: `null` → `malformed`
   if the load state is `LoadFailed`, otherwise `missing`; profile for another role → `malformed` (`Profile`).
3. For the required roles that have a profile, the evaluator builds the root of §2 from those profiles and applies the
   reducer's own per-role step (`ReduceForRole` plus the "own window already ended" check, the body of
   `EnvelopeReducer.cs:94-107`) with a **non-narrowing synthetic request** (`new DelegationAuthorityRequest()`, no
   remediation scope). Denied → `malformed` with the denial's dimension and reason; granted → `ready`.
4. The reducer is refactored so that **one** loop produces per-role results and `DeriveRootAttributed` is defined as
   "the first non-granted role in pipeline order of that loop" (with the existing request-deadline and request-window
   checks before it). There is one reduction implementation and two projections of it.

**Attribution after the refactor (deterministic).** The new loop evaluates the required roles in canonical pipeline
order. When several independent faults exist, the first reported role/dimension may therefore differ from the one the
pre-ADR four-role reducer reported. The **authority outcome** is equivalent — a grant stays a grant and a denial stays a
denial — and for multi-fault inputs the new pipeline-order attribution is authoritative. Exact old/new role and
dimension equality is required only for single-fault inputs (§20 P3, P5).

**Lemma (role independence).** With a non-narrowing request, `ReduceForRole(Root(P), r, p_r)` is refused iff
`ReduceForRole(Root({p_r}), r, p_r)` is refused, for any set `P ∋ p_r` of required profiles: the root is a union/sum/max
of `P`, so every root dimension is a superset of (or `≥`) `p_r`'s own, and the role result is `p_r`'s value capped by
the role table in every dimension (sets: `root ∩ p_r = p_r`; steps and tokens: `min(Σ, own) = own`; deadline:
`min(now + Σ, now + own) = now + own`; risk: `min(max, own, cap) = min(own, cap)`; window: the profile's own). Each
required role's state is therefore its own and does not depend on whether another role is missing; that is what lets
readiness report all four roles while start stops at the first.

**Property P5 (readiness predicts start).** For the same profile source, policy load state, registered tools, request
shape and instant `t`: readiness `ready` overall ⇔ `DeriveRootAttributed` grants the root for any request of that shape
whose authority request is non-narrowing; and a role reported `missing`/`malformed` makes the real start `Denied` on that
role with the **same dimension** (the first such role in pipeline order is the one the start reports; exact
start-state and dimension comparison is asserted under stable single-fault inputs) and, for
`profile_missing` and `reduction_denied`, the **same reason text**. For `policy_load_failed` the start reports the
`profile_missing` text on the same role and dimension, because the runtime's start path receives the profile source, not
the host's load state; readiness is the more specific of the two.

What readiness does **not** evaluate, stated so P5 is not over-claimed:

- **Time.** Readiness at `t₁` and start at `t₂ > t₁` can differ if a window ends or a profile's maintenance window closes
  in between. This race is documented and accepted.
- **A maintenance window not yet open.** Where a window is enforced later, at step time, readiness does not predict it:
  `ready` does not mean the window is open when a step runs.
- **Configuration change between calls.** The API reads the policy once per host start; the CLI per process. A file
  edited after the API started is invisible to both readiness and start until restart (they agree).
- **Request narrowing.** An operator's `maxSteps`/`maxTokens` narrowing (≥ 1, validated by API and CLI) cannot empty a
  required budget in the root or role reduction; budget *exhaustion* during the run is `BudgetExceeded`, not a denial
  (ADR-0031 §3).
- **The named change.** For a remediation request, start runs `PrepareCheckAsync` for the specific Skill, Capability,
  target, environment and blast radius against Diagnostic and Remediation. Readiness `remediation=true` proves the four
  profiles are present and reducible, not that a particular change is inside them; a change outside them is still
  `Denied` at start, before any model call, naming the dimension.
- **Tool availability.** Neither readiness nor the reducer checks that profile tool names are registered; drift (§11)
  reports it.

**Readiness scope (operator decision E, accepted).** Readiness is **profile / role-infrastructure readiness** for a
request *shape* (diagnosis vs remediation), not specific-request executability. `GET /api/delegations/readiness?
remediation=false|true` answers whether the role infrastructure that shape requires is usable; it does **not** prove that
a selected Skill, Capability, target, environment or Capability input will be accepted. Those are checked at submit (API
`400` / CLI exit `1`) and at runtime (`PrepareCheckAsync`, preparation). A submit may therefore fail safely after
readiness is green; that is expected, provided the UI says so (§13). No second, request-aware readiness endpoint is part
of HARDEN-11. Normative wording for API and UI: `ready` = "the required role profiles are usable for this type of
delegation" — never "this change is executable".

### 6. `GET /api/delegations/readiness`

#### 6.1 Request

`GET /api/delegations/readiness?remediation=false|true`. `remediation` is optional and defaults to `false`; the only
accepted values are `true` and `false` (case-insensitive); anything else is `400` with
`{ "message": "'remediation' is true or false." }`. No other parameter is read. Safe, idempotent, no state change, no
audit event (it reads configuration, executes nothing). `Cache-Control: no-store`. Same rate limiting as every `/api`
endpoint.

#### 6.2 Response `200`

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

Normative:

- `roles` has **exactly four entries, one per pipeline role, in pipeline order**. `role` is the `AgentRoleKind` name.
- `state` ∈ `ready | missing | malformed | notRequired` (exact, camelCase strings). **Exactly four public states**
  (operator decision B); no fifth state is added. `malformed` means *the profile/configuration is present but not usable
  for the role* — not necessarily syntactically malformed; `reasonCode`, `dimension` and `reason` carry the precise
  distinction. User-facing labels render `malformed` as "Not usable" (or an equivalent), never as a claim that the file is
  syntactically broken.
- `dimension` is the `EnvelopeDimension` name of the dimension that owns a non-ready result (`Profile` for absence or
  load failure, otherwise the reducer's denial dimension), and `null` for `ready` and `notRequired`.
- `reasonCode` is the typed reason, the field clients branch on. It is stable and machine-readable and follows the
  repository's API code convention, **snake_case** (as `task_delegated`, `csrf_rejected`, `invalid_request`):

  | `reasonCode` | `state` | `dimension` |
  |---|---|---|
  | `ready` | `ready` | `null` |
  | `not_required` | `notRequired` | `null` |
  | `profile_missing` | `missing` | `Profile` |
  | `policy_load_failed` | `malformed` | `Profile` |
  | `profile_for_other_role` | `malformed` | `Profile` |
  | `reduction_denied` | `malformed` | the reducer's dimension |

- **Forward compatibility.** The set of `reasonCode` values may grow; an existing value never changes meaning. A client
  that receives an unknown `reasonCode` treats that role as **not ready**, shows the bounded human `reason`, and never
  fails open; `ready` stays authoritative.
- `reason` is the bounded (≤ 500 characters, as `DelegationRunner` bounds messages) English text **the reducer would put
  in the denial** for `reduction_denied`, a fixed runtime text for the other non-ready codes, and `null` for `ready` and
  `not_required`. For `policy_load_failed` it is the fixed text "policy.yaml could not be loaded; delegation is off until
  it is fixed (see the host log)"; the loader's message (key path and line) is **not** sent over HTTP. Clients display
  `reason`; they never parse it.
- `ready` is explicit and normative: `ready == (every role whose state is not notRequired has state ready)`. Clients use
  the field and never recompute it. It means "the required role profiles are usable for this type of delegation" (§5,
  readiness scope), not that a selected change will be accepted at submit.
- `policy` ∈ `noFile | loaded | loadFailed` (§4).
- `profileDriftCount` is §11's count over the host's loaded profiles and available manifests at evaluation; it does
  not depend on `remediation` and never affects `ready`.
- `evaluatedAtUtc` is the instant passed to the evaluator.
- The `missing` reason text for a required role replaces today's "…until the operator configures one for every role":
  the reducer's `NoProfile` message becomes "{role} role: no usable profile is configured." for both readiness and start,
  so they agree verbatim. "No *usable* profile" is deliberate: the start path cannot tell an absent profile from a policy
  that failed to load, and must not claim that no profile exists when loading failed.

#### 6.3 Errors

`401`/`403` as every Viewer endpoint (HARDEN-10 schemes, §15); `429` from the rate limiter; `400` for a bad
`remediation` value; `500`-class problem responses as every endpoint after HARDEN-10. Nothing in an error response says
anything about profiles.

### 7. Readiness matrices (deterministic examples)

Profiles below are "valid" when they reduce (§5 step 3). `D/D` = Discovery and Diagnostic.

| # | Configuration | `remediation` | Discovery | Diagnostic | Remediation | Verification | `ready` |
|---|---|---|---|---|---|---|---|
| M1 | Fresh installation, no `policy.yaml` | false | `missing` | `missing` | `notRequired` | `notRequired` | false |
| M2 | Fresh installation, no `policy.yaml` | true | `missing` | `missing` | `missing` | `missing` | false |
| M3 | Read-only setup: valid D/D only (`profiles init --read-only`) | false | `ready` | `ready` | `notRequired` | `notRequired` | **true** |
| M4 | Same read-only setup | true | `ready` | `ready` | `missing` | `missing` | false |
| M5 | All four valid | false | `ready` | `ready` | `notRequired` | `notRequired` | true |
| M6 | All four valid | true | `ready` | `ready` | `ready` | `ready` | true |
| M7 | `policy.yaml` fails to load (any section malformed) | false | `malformed` (`Profile`, `policy_load_failed`) | `malformed` (same) | `notRequired` | `notRequired` | false |
| M8 | Same | true | `malformed` | `malformed` | `malformed` | `malformed` | false |
| M9 | D/D valid except Diagnostic `maxTokens: 0` | false | `ready` | `malformed` (`Tokens`, `reduction_denied`) | `notRequired` | `notRequired` | false |
| M10 | D/D valid, Remediation `maxRisk: read`, Verification valid | false | `ready` | `ready` | `notRequired` | `notRequired` | true |
| M11 | Same as M10 | true | `ready` | `ready` | `malformed` (`Risk`, `reduction_denied`) | `ready` | false |
| M12 | D/D valid, Discovery `tools: []` | false | `malformed` (`Tools`, `reduction_denied`) | `ready` | `notRequired` | `notRequired` | false |

M7/M8 are the "malformed required profile" cases at file level; M9, M11 and M12 are well-formed profiles the reducer
refuses. In every row the real start of that shape is `Denied` on the first non-ready role in pipeline order, with the
same dimension and, except for `policy_load_failed`, the same reason (P5).

### 8. `GET /api/skills` — the activated catalog

Additive, `Viewer`, safe and idempotent, `Cache-Control: no-store`. It reads `ISkillRegistry.GetAvailableSkills()` only:
only activated Skills, nothing from disabled, failed or uninstalled plugins and no plugin internals (no assembly, path,
manifest file, signature or entitlement detail).

```json
{
  "skills": [
    {
      "skillId": "service.skill",
      "package": "bops.packages.service",
      "trust": "Official",
      "capabilities": [
        {
          "name": "service.restore",
          "version": "1.0.0",
          "description": "Restores a stopped service.",
          "risk": "High",
          "supportsDryRun": true,
          "inputSchema": [
            {
              "name": "serviceName", "type": "String", "description": "…", "required": true, "sensitive": false,
              "allowedValues": null, "minimum": null, "maximum": null,
              "minLength": 1, "maxLength": 256, "minItems": null, "maxItems": null
            }
          ]
        }
      ]
    }
  ]
}
```

- **Skill fields** are exactly those that exist: `skillId`, `package` (the host-stamped id, as `/api/tools` already
  exposes), `trust` (`PackageTrustLevel` name). A Skill has no version or description in the contract; none is invented.
- **Capability fields:** `name`, `version`, `description`, `risk` (`RiskLevel` name), `supportsDryRun`, `inputSchema`.
  `OutputSchema`, `RequiredPermissions`, `Timeout`, `Verification` and `RollbackDescription` are not needed by the form
  and are omitted (the plan approval view already shows the plan's own verification).
- **`inputSchema`** is `CapabilityManifest.InputSchema` projected field-for-field from `ToolParameter`: every member
  listed in fact 11, always present, `null` where the manifest has none; `type` is the `ToolParameterType` name.
  Order is the manifest's own (it is the order the Capability author declared).
- **Ordering:** Skills by `skillId` ordinal (as the registry returns them), Capabilities by `name` ordinal. Deterministic
  for one registry state.
- An empty catalog is `200` with `"skills": []`.

### 9. Capability input: one schema, validated on the server

#### 9.1 Schema-driven controls

The Delegations UI renders the form from `inputSchema` only. There is no second, capability-specific schema anywhere.

| `type` | Control | Client constraints (convenience only) | JSON value sent |
|---|---|---|---|
| `String` | text input (password-type input when `sensitive`) | `required`; `minLength`/`maxLength` (UTF-16 code units, as the server counts) | string |
| `Path` | text input | `required`; `minLength`/`maxLength` | string (the path policy, S11, still decides at execution) |
| `Duration` | text input | `required` only (no format constraint exists in the contract) | string |
| `Enum` | select of `allowedValues`, in manifest order | `required` | one of `allowedValues`, exact |
| `Integer` | number input, step 1 | `required`; whole number; `minimum`/`maximum` | JSON integer (`int` range) |
| `Number` | number input | `required`; finite; `minimum`/`maximum` | JSON number |
| `Boolean` | checkbox, or a three-state control when not `required` (unset / true / false) | — | `true`/`false`, or omitted when unset |
| `PathList` | list editor of text entries | `required`; `minItems`/`maxItems`; each entry a non-empty string | array of strings |

`String` with non-empty `allowedValues` is rendered as a select, as the server enforces `AllowedValues` on any
string-valued type. An optional field left empty is **omitted** from the object (never sent as `""` or `null`). Values
are never clamped, trimmed or coerced by the client to make them pass: an out-of-bounds value shows the violated bound
and disables Submit. A Capability whose `inputSchema` is empty shows "This capability takes no input."

#### 9.2 Server validation is authoritative

- A **runtime-owned argument validator** — the existing `ValidateArguments`/`ViolatedConstraint` logic, lifted out of
  `AgentRunner` so tool arguments and capability input share one implementation — validates a `CapabilityRequest.Input`
  against `CapabilityManifest.InputSchema`: unknown names rejected, required present and non-null, JSON-native type,
  `AllowedValues`, `Minimum`/`Maximum`, `MinLength`/`MaxLength`, `MinItems`/`MaxItems`. No clamping, no coercion.
- It runs **before Capability code**, in the shared preparation path (`AgentRunner` between Capability resolution and
  `PrepareAsync`, for both the delegated and the non-delegated preparation), so malformed input never reaches a
  Capability whichever client sent it. A failure is a preparation `Failed` with a validation reason and a
  `SkillRunAuditEvent` refusal, exactly like an unknown Capability today.
- **Scope (operator decision D, accepted).** Invariant: *`CapabilityManifest.InputSchema` is authoritative wherever that
  Capability is invoked.* All Capability invocations validate their input against it before Capability code runs —
  delegated **and non-delegated** preparation alike. This is an intentional HARDEN-11 tightening of the general
  Capability boundary for security and contract consistency, not an accidental side effect. It stays additive and
  fail-closed: no silent coercion, no silent clamping, no unknown-field guessing. No first-party Skill behaviour is
  expected to regress — there are currently no first-party Skill providers in `src` — but compatibility tests over the
  sample/test Skills are still required.
- It also runs **at delegation start, before any model call**: the API and the CLI call the same validator with the
  currently activated manifest before starting (API `400`, CLI exit `1`; no run is created), and `PrepareCheckAsync`
  repeats it inside the runner (a failure ends the run `Failed` with the bounded validation reason, before Discovery).
  The second check covers a catalog that changed after the first.
- **Schema validity at registration (mandatory Phase-1 obligation, review N-7).** `SkillRegistry` validates every
  `CapabilityManifest.InputSchema` when the Skill registers, and refuses at least: a blank parameter name; a duplicate
  parameter name (ordinal); an `Enum` parameter with empty `AllowedValues`; and every constraint inconsistency
  `ToolRegistry` already refuses for tool parameters under HARDEN-6 (a constraint on the wrong type, a non-finite bound,
  a negative length, a minimum above its maximum). The failure is a deterministic, bounded registration failure of that
  Skill; no Capability with an internally invalid schema becomes active. Validation never waits for a request and never
  relies on `ToDictionary` or the argument validator throwing. A schema the UI renders is therefore always well-formed
  and satisfiable.
- **Sensitive parameters (security rule, review N-12).** `ToolParameter.Sensitive` governs validation and output
  handling: the **submitted value** of a `Sensitive` parameter never appears in a validation error, log line, audit
  diagnostic text, API error, CLI error or UI error; messages name the parameter and the violated constraint only.
  Accurately stated: Capability input is part of the delegation request and is **persisted as plain JSON** in the
  delegation store (`DelegationRun`, SQLite) like the rest of the request. HARDEN-11 does **not** introduce encrypted
  secret storage and does not redesign it; an operator who submits sensitive Capability input relies on the protection
  boundary of the delegation/session database files (§23, threat model).
- **API failure contract** (`400`, before a run exists):
  `{ "message": "<bounded text>", "code": "capability_input_invalid", "parameter": "<name or null>" }` for validation,
  `{ "message": "…", "code": "unknown_capability" }` when `(skillId, capabilityName)` is not in the activated catalog,
  `{ "message": "…", "code": "malformed_json" }` for a body or `input` that is not strict JSON (§9.3). Existing `400`
  bodies keep their `message` and gain `code` only where this ADR defines one (additive).

#### 9.3 Raw JSON fallback

- Shown only behind an explicit "Advanced: edit as JSON" control, collapsed by default, for the selected Capability.
- Switching *to* JSON serializes the current form values (the same omitted-when-empty object); switching *back* parses
  the JSON and, only if it parses as a JSON object, repopulates the form fields it can represent; a key that is not in
  `inputSchema` keeps the editor in JSON mode with an "unknown field" message. Invalid JSON never replaces form values
  and disables Submit with "Not valid JSON".
- Submit sends exactly one object, from whichever mode is active. Raw JSON is subject to the **same** client checks and
  the **same** server validator; it is not a second, unrestricted input path.
- The API binds the start body with strict JSON (`AllowDuplicateProperties = false`, as HARDEN-10's session endpoint),
  so a duplicate key anywhere in the body, `input` included, is `400 malformed_json`, never first- or last-wins. The CLI's
  `--input` is parsed with the same strict options.
- **Scope of the strict binding (review N-8).** For `POST /api/delegations` the change adds **duplicate-key rejection
  only**. It does not import the rest of HARDEN-10's login-body parser semantics and, absent a separate requirement,
  does not change property-name casing, unknown-member behaviour or ordinary numeric binding. The narrow security
  objective is that ambiguous duplicate properties are never accepted first-wins or last-wins.
- **Advanced raw-JSON mode in Angular.** Duplicate keys must be detected **before** ordinary `JSON.parse` semantics erase
  the ambiguity (a duplicate-aware scan of the raw text), or the raw text must be submitted through a path where the
  server's duplicate detection sees it unchanged. The browser never silently normalizes duplicate-key input; a duplicate
  shows a "Duplicate key" message and disables Submit.

#### 9.4 Selection from the catalog

- The UI selects Skill, then Capability, from `GET /api/skills`; there is no free-text Skill or Capability field in the
  ordinary form. The selected manifest drives the input form and shows `risk` and `supportsDryRun` (the dry-run box is
  disabled when `false`, mirroring the existing server refusal).
- The API stays fail-closed for a handcrafted client: an unknown `(skillId, capabilityName)` is `400 unknown_capability`
  at start, and the runtime's `Resolve` still refuses at preparation (`AgentRunner.cs:1137-1143`).
- A catalog that changes between page load and submit (plugin disabled, Capability removed, schema changed) fails
  safely at submit (`400`) or, in a race after start, at the runner's re-check or at preparation — never by running
  stale input. The UI re-fetches the catalog after any `400 unknown_capability` / `capability_input_invalid`.

### 10. `bops delegate profiles init --read-only`

#### 10.1 Command and behaviour

```text
bops delegate profiles init --read-only [--write [--overwrite]]
```

- `--read-only` is **mandatory**: no other generation mode exists, so the command can never be mistaken for one that
  grants mutation. Without it the command is a usage error (exit 1).
- **Default prints, never writes**: the generated YAML goes to standard output, preceded on standard error by the
  resolved absolute policy path, the tool count and the sentence "Review it, then run again with --write." This is the
  review step.
- `--write` writes the generated **whole file** to the policy path the CLI resolves for every other command:
  `Policy:FilePath` from the CLI's configuration (`appsettings.json` or the `Policy__FilePath` environment variable),
  default `policy.yaml` relative to the working directory. No new path option is introduced. The command prints the
  absolute path it wrote and reminds that the API reads the file only at start ("restart bOps.Api to apply") and must be
  configured with the same `Policy:FilePath`.
- **Atomic, never silent:** the content is written to a temporary file in the same directory, flushed, then moved into
  place with a create-new move for a new file. If the target appears in between, the command fails and leaves it
  untouched. A **new** file's permissions follow the directory's defaults; a replaced file keeps its existing
  restrictive permissions/ACL where the platform supports it (see `--overwrite`).
- **Sensitive file handling.** The command never prints an existing file's contents (only the newly generated content
  or fragment) and never logs the policy file's contents; it prints the resolved absolute path.
- **Existing file:** `--write` without `--overwrite` refuses (exit 1) with: the path, "a policy file already exists and
  is never overwritten silently", and the `delegation` fragment printed for a manual merge. bOps never merges YAML: a
  structural merge cannot preserve comments, ordering or the operator's intent, and cannot be proven not to add
  authority.
- **`--overwrite`** (only with `--write`) is **not** permission to broaden or discard hand-edited authority silently
  (review N-2). It replaces an existing file **only if all** of the following hold:
  1. the file loads successfully with `PolicyConfigLoader.Load`;
  2. its non-delegation configuration is equivalent to the expected safe baseline the generator writes (`defaults`
     equal to `SafeDefault`; no `tools`, `packages` or `skills` entries);
  3. it configures no Remediation and no Verification profile;
  4. its existing Discovery and Diagnostic profiles equal the generator-owned defaults (§10.2) in **every** authority
     dimension — `skills`, `capabilities`, `maxRisk`, `maxBlastRadius`, `targets`, `environments`, `maxSteps`,
     `maxTokens`, `maxDuration`, `window` — except the `tools` list, the only drift being refreshed;
  5. immediately before the replacement the file is re-read and conditions 1–4 are re-checked on that content (a file
     changed in between is refused);
  6. the replacement preserves the existing file's restrictive permissions/ACL where the platform supports it.

  If any condition fails the command **refuses** (exit 1), names the first failed condition, leaves the file untouched
  and prints the generated fragment/config for manual review. There is no structural merge, no best-effort merge and no
  silent widening. So `--overwrite` exists for one purpose — refreshing the tool list of a previously generated
  read-only file after drift — and can never discard a stricter default, a tool override, a package ceiling, a Skill
  rule, a narrowed budget or a configured mutation grant. Comments in the replaced file are lost; the command says so.
  The replacement is atomic (temporary file + replace).
- **No model provider is needed.** `profiles init`, `profiles check` and `readiness` compose the tool and Skill
  registries, enabled plugins (`ActivateEnabledAsync`), the capability probe and the policy loader, and never create a
  chat model; a missing `ModelProvider` section does not affect them.
- Never run automatically: not at startup, not by the API, not by the UI. There is no HTTP equivalent.

#### 10.2 Generated content (finite, deterministic)

```yaml
# Generated by `bops delegate profiles init --read-only` on 2026-10-05T12:00:00Z.
# Read-only delegation for diagnosis. Grants no Remediation or Verification profile, no Skill,
# no Capability and nothing above Read. Tools are listed by exact name; a tool installed later is
# NOT included until this file is regenerated (`profiles check` reports it). `defaults` repeats
# the built-in policy that applies when no policy.yaml exists, so creating this file changes no
# decision outside delegation.
defaults:
  read: automatic
  low: automatic
  medium: approval
  high: approval
  critical: forbidden
delegation:
  roles:
    discovery:
      tools: [<every available Read tool, ordinal order>]
      maxRisk: read
      maxBlastRadius: single
      targets: [local]
      environments: [local]
      maxSteps: 15
      maxTokens: 150000
      maxDuration: 00:30:00
    diagnostic:
      tools: [<the same list>]
      maxRisk: read
      maxBlastRadius: single
      targets: [local]
      environments: [local]
      maxSteps: 15
      maxTokens: 150000
      maxDuration: 00:30:00
```

- **Tools:** every manifest in `IToolRegistry.GetAvailableManifests()` (after `RefreshCapabilitiesAsync`, with enabled
  plugins activated) whose `Risk == RiskLevel.Read`, by exact canonical name, de-duplicated, ordinal order. Expanded at
  generation time. No wildcard, category or rule evaluated at runtime. A Read tool installed later, or one unavailable at
  generation (capability missing), is absent until the operator regenerates and reviews. If no Read tool is available
  the command refuses (exit 1): a profile with `tools: []` could never be ready.
- **Values** are fixed constants of the generator, using only fields `RoleProfile` and the parser have today:
  `maxRisk: read` (the only legal ceiling that is not above Read); `maxBlastRadius: single` (required key, the lowest
  value); `targets`/`environments: [local]` (exact labels; plain Read calls are not matched against them, ADR-0031 §4,
  so they bound only what carries a scope, which a diagnosis never does — `local` does **not** constrain ordinary Read
  calls in any way the runtime does not actually enforce); `maxSteps: 15` (the per-attempt `Agent:MaxSteps` default);
  `maxTokens: 150000` per role; `maxDuration: 00:30:00` per role. The diagnosis-only root therefore sums to 300,000
  tokens; that figure is the delegation root-envelope budget and is **not** comparable to `Agent:MaxTotalTokens`
  (default 350,000), which bounds an ordinary single task; what bounds each role is its own 150,000 envelope budget. The contract expresses time as a **maximum duration**, not an absolute deadline (ADR-0031 §5): the run's
  deadline is derived at start as `start + 30 min + 30 min` (one hour, the `Agent:MaxAttemptDuration` default). No
  `window`, `skills`, `capabilities`, `remediation` or `verification` key is ever written.
- **Accepted budgets (operator decision C).** The operator accepted, for Discovery and for Diagnostic each:
  `maxSteps: 15`, `maxTokens: 150000`, `maxDuration: 00:30:00`, `maxRisk: read`, `maxBlastRadius: single`,
  `targets: [local]`, `environments: [local]`; Discovery and Diagnostic only; no Skills, no Capabilities, no Remediation
  and no Verification profile. These are finite safe defaults, not a claim that every diagnosis needs these amounts. If
  real platform evidence during implementation (§22) shows token exhaustion before E2E-9 can complete, implementation
  **stops and reports the evidence**; the accepted defaults are not changed silently.
- **Safe YAML emission (mandatory Phase-1 obligation, review N-1).** The generator emits YAML through a real YAML
  serializer/emitter, or through provably safe quoted-scalar emission; a raw tool name is **never** interpolated into
  YAML text. It handles every tool name the actual `ToolRegistry` accepts (registration accepts any non-reserved
  string). Before printing or writing, the generated configuration is **parsed back through the real
  `PolicyConfigLoader`** and verified structurally:
  - profiles are exactly Discovery and Diagnostic;
  - every generated tool name, in both profiles, equals the intended set of available Read tools (exact, ordinal);
  - `maxRisk` is `read`;
  - no Skills and no Capabilities;
  - no Remediation and no Verification profile;
  - no maintenance window (none is accepted for the generated profiles);
  - the accepted finite budgets above;
  - `targets` = `[local]` and `environments` = `[local]`;
  - the non-delegation configuration equals `SafeDefault` (P7).

  A tool name that cannot be represented legally by the delegation envelope/profile contract, or does not round-trip
  exactly through the loader, is never silently altered and never silently granted: generation **fails** (exit 1),
  naming the tool. Omitting it instead would falsely present an incomplete read-only profile as complete, so the
  deterministic rule is fail, not skip. Any assertion failure is a generator failure (exit 1); nothing is printed as
  valid and nothing is written.
- **Header timestamp.** The generation instant in the header comes from an injected `TimeProvider`; tests of the header
  and of determinism use a fake `TimeProvider`, never the wall clock.
- **Defaults.** Because a file without `defaults` forbids every risk level (fact 7), the generated whole file states the
  built-in `SafeDefault` explicitly. Its effective non-delegation policy is identical to the no-file state (P7); this is
  restating, not granting. A fragment printed for manual merge contains only the `delegation` section.
- The header comment is informative only; YAML comments are not parsed, so nothing about the file's origin is trusted
  later.

### 11. `bops delegate profiles check` and the drift count

```text
bops delegate profiles check
```

Reads the CLI's loaded policy and the CLI host's available manifests (same composition as §10.1) and prints one line per
**drift item**, then a summary line. Drift is information: it grants nothing, changes nothing and never affects
`ready`. A drift item is a unique tuple `(role, kind, subject)`:

| `kind` | Roles | `subject` | Condition |
|---|---|---|---|
| `policy_load_failed` | — | — | The policy failed to load. One item; the CLI also prints the loader's message (key path, line). |
| `unusable_profile` | any configured | the dimension | A configured profile the reducer refuses with a non-narrowing request (§5 step 3), whether or not the role is required by some shape. |
| `read_tool_not_granted` | Discovery, Diagnostic (configured) | tool name | An available `Read` tool absent from the profile's `tools`. |
| `unavailable_tool` | any configured | tool name | A name in the profile's `tools` that is not among the available manifests (unknown, removed, disabled, or unavailable on this host — the registry cannot tell these apart, fact 8). |
| `above_role_risk_cap` | Discovery, Diagnostic, Verification (configured) | tool name | An available tool in the profile's `tools` whose risk is above the role's cap (inert — `CheckStep` refuses it — but misleading). |

- **Counting:** `profileDriftCount` = the number of items, i.e. per `(role, kind, subject)`; a tool missing from both
  Discovery and Diagnostic counts twice, because each is a separate grant the operator reviews. A role with no profile
  produces no item (that is readiness, not drift). Ordering: role in pipeline order (`policy_load_failed` first), then kind
  in the table's order, then subject ordinal.
- The API's `profileDriftCount` is computed by the **same runtime function** over the API host's loaded profiles and
  available manifests. API and CLI agree when they load the same file and the same plugins; they can differ when the
  hosts are configured differently, which is why each reports its own.
- Profiles that were not generated (an operator's deliberately narrow Discovery) also produce `read_tool_not_granted` items:
  the file carries no trustworthy origin marker, and an extra review prompt is the safe direction.

### 12. CLI contract

| Command | Output | Exit codes |
|---|---|---|
| `bops delegate readiness [--remediation]` | Four lines in pipeline order, `<Role>: <state> [<dimension>] — <reason>`, then `Ready: yes\|no`, `Policy: <path> (<noFile\|loaded\|loadFailed>)`, `Profile drift: <n>`. On `loadFailed` the loader's message is printed (local operator). | `0` ready; `2` not ready (as "denied" elsewhere in `bops delegate`); `1` usage or composition error |
| `bops delegate profiles init --read-only [--write [--overwrite]]` | §10.1 | `0` printed or written; `1` usage error, refusal (existing file, failed `--overwrite` condition, no Read tool) or I/O error |
| `bops delegate profiles check` | First `Policy: <absolute resolved path> (<noFile\|loaded\|loadFailed>)`, then one line per item `<Role>: <kind> <subject>`, then `Profile drift: <n>` | `0` no item; `2` any `policy_load_failed` or `unusable_profile` item (delegation is blocked for some shape); `8` only informational items; `1` usage or composition error |

- Human-readable output only: no CLI command has a machine-readable mode today and this ADR adds none (no new CLI
  framework); the API is the machine-readable surface.
- `readiness` and `profiles` become reserved verbs of `bops delegate`, like `status` and `resume`; an objective that is
  literally one of these words is quoted with other text, as for the existing verbs. Exit code `8` is new and used only
  by `profiles check`; the existing codes keep their meaning. The README exit-code table is updated.

### 13. UI readiness behaviour

- On load, and **immediately** whenever the "Prepare a change" toggle changes, the Delegations page calls
  `GET /api/delegations/readiness?remediation=<toggle>`; a response for a superseded toggle value is discarded (the
  latest request wins).
- A readiness panel lists **all four roles** with a localized label for `state`, and the server `reason` verbatim as the
  detail (E2E-10 "same server reasons"); `dimension` is shown when not `null`. `profileDriftCount > 0` shows an
  informational note pointing to `bops delegate profiles check`; it never disables Submit.
- **Wording (review N-3, normative).** The `malformed` label reads "Not usable" (or an equivalent), not "Malformed".
  `ready` is presented as "the required role profiles are usable for this type of delegation", never as "this change is
  executable". For the remediation shape the panel always shows a fixed note equivalent to: *"The selected change,
  target, environment and input are validated when you submit."* When a submit then fails validation (`400
  unknown_capability`, `capability_input_invalid`, `malformed_json`, or a start `Denied` naming a dimension), the UI
  shows that server result as a submit error beside the form, while the readiness panel keeps showing the role
  readiness unchanged — the two are never conflated.
- An unknown `reasonCode` (§6.2) is shown as not ready with the server `reason`; the UI never fails open.
- **Submit is enabled only when the latest readiness response for the current toggle has `ready: true`** (and the form is
  otherwise valid). The UI does not know which roles are required: it never hides or computes a role, and never
  derives `ready` from role states.
- **Failure to obtain readiness fails closed** — Submit disabled, panel shows an actionable message, never "ready":

  | Outcome | Panel message (localized) | Behaviour |
  |---|---|---|
  | `401` | Session expired — sign in | The HARDEN-10 interceptor's existing session-expired path; re-query after sign-in |
  | `403` | Your key lacks the viewer role | No retry |
  | `429` | Too many requests — retrying | Back off as the existing stores do, then re-query |
  | `5xx` | bOps could not evaluate readiness | Retry action |
  | network error / status `0` | bOps API unreachable | Retry action; re-query on `online` |

  No new authentication path: requests go through the existing API client and interceptor (Bearer or the HARDEN-10
  cookie; GET needs no CSRF header).
- **No authority logic in Angular.** The UI renders `roles`, `ready`, `reason`; the catalog drives only the input form.

### 14. The role-task resume guard is preserved

- `TaskResume.Evaluate` keeps refusing `TaskOrigin.Delegated` first (fact 17). Nothing in this ADR creates, modifies or
  resumes a task: readiness, the catalog and the profile commands are read-only or file-writing, and touch no task.
- The UI resumes a delegated run **only** through `POST /api/delegations/{id}/resume`; the Dashboard keeps rendering the
  server's `resumable: false` / `task_delegated` for role tasks (HARDEN-4).
- Required regression test: after a diagnosis-only run under the new root, `POST /api/agents/tasks/{roleTaskId}/resume`
  for its Discovery and Diagnostic tasks returns `409` with `task_delegated`, and `bops resume <roleTaskId>` exits `1`
  with the same refusal; `POST /api/delegations/{id}/resume` keeps its existing contract.

### 15. ADR-0018 amendment: API surface and authorization

| Endpoint | Policy | Notes |
|---|---|---|
| `GET /api/delegations/readiness` | `Viewer` (`bops.viewer`) | §6. Safe/read-only. |
| `GET /api/skills` | `Viewer` (`bops.viewer`) | §8. Safe/read-only. |
| `POST /api/delegations` | `Operator` (unchanged) | Gains `400` bodies with `code` (`capability_input_invalid`, `unknown_capability`, `malformed_json`) and strict JSON binding (§9). |

Under ADR-0043 both new endpoints accept Bearer or a valid browser session; as safe `GET`s they need no
`X-bOps-Request` header; an `Authorization` header that is present but invalid fails and never falls back to the cookie.
Authentication is not redesigned.

### 16. Typed evidence-limitation metadata (closes the ADR-0042 HARDEN-11 gap)

**Decision: add it, as a small additive `bOps.Abstractions` contract persisted on the delegation aggregate.**

Options evaluated:

1. *View-only, derived on read from persisted role tasks.* Not possible without a contract change:
   `DelegationRoleRun` does not record its task id and `ITaskStore` cannot query by delegation (fact 16); adding either
   is itself an Abstractions change, and every host would re-derive on every read.
2. *Delegation-specific runtime/report contract.* The durable delegation aggregate (`DelegationRun`, `DelegationRoleRun`)
   **is** an Abstractions type persisted whole as JSON; any persisted field is an Abstractions change.
3. *Extend `Evidence`, `Finding` or `SkillReport`.* Rejected: `SkillReport` is produced by Capabilities (package code), so a
   field there would let a package author limitation metadata; a synthetic `Evidence` would invent an evidence id a
   Finding could cite; a Finding field would change findings (ADR-0042 rule 4, D-039).

So the smallest contract is three additive, init-only properties on `DelegationRoleRun` and their types (operator
decision A, accepted: typed limitation metadata is persisted in `bOps.Abstractions` in HARDEN-11; the ADR-0042 gap is not
deferred again). The names below are normative — implementation does not invent others. All types are in namespace
`bOps.Abstractions`, dependency-free, and serialize enums by name like the existing delegation aggregate.

```text
DelegationRoleRun.EvidenceLimitations : IReadOnlyList<EvidenceLimitation>?   // null = not recorded; [] = no recorded limitation
DelegationRoleRun.EvidenceLimitationsOmitted : int                           // entries beyond the cap, 0 by default
DelegationRoleRun.FindingsReply : DiagnosticReplyOutcome?                    // Diagnostic only; null otherwise / not recorded

public sealed record EvidenceLimitation
  StepIndex : int
  ToolName : string?                 // resolved canonical name passing the ADR-0042 shape check; null otherwise
  UnknownTool : bool                 // a pre-execution rejection of an unresolved name
  Outcome : ToolOutcome
  FailureKind : ToolFailureKind
  Completeness : ToolResultCompleteness
  ShortenedFromCharacters : int?     // the output length when the persisted observation was shortened
  EvidenceId : string?               // the id of the Evidence this step produced ("<role>-<index>"), when it produced one;
                                     // null when the step produced none (a failed or denied read) — never manufactured

public sealed record DiagnosticReplyOutcome
  Status : DiagnosticReplyStatus
  Problem : DiagnosticReplyProblem
  DiscardedFindings : int            // entries dropped by the per-finding rules (no summary, no or unrecorded evidence ids)

public enum DiagnosticReplyStatus  { Valid = 0, Absent = 1, Malformed = 2 }
public enum DiagnosticReplyProblem { None = 0, NoJsonObject = 1, InvalidJson = 2, DuplicateProperty = 3, TooLarge = 4,
                                     NotAnObject = 5, MissingFindingsArray = 6, TooManyFindings = 7 }
```

The typed `DiagnosticReplyOutcome` replaces any need to persist the raw Diagnostic model reply for this contract; the raw
reply is not persisted for it.

- **Computed by the runtime only**, deterministically, when a Discovery or Diagnostic role ends (completed or failed) and
  its `TaskState` is in hand, from the same typed-field classification as the ADR-0042 digest (`EvidenceLimitationsDigest`
  is refactored to produce typed entries first and its text from them, so the two cannot disagree): completeness
  `Partial`/`Unavailable`; non-success outcomes except a `Validation` failure superseded by a later success on the same
  resolved tool; shortened observations. No tool output, error text, argument or model text enters it (rule S5). No model
  is asked for it.
- **Scope of what it represents (review N-6).** The list produced from a role's `TaskState` covers that role's
  **model-loop evidence collection** only: Discovery model-loop evidence and Diagnostic model-loop evidence. It does
  **not** claim to cover evidence reads performed inside Capability preparation (or anything else) unless those reads
  are separately represented in the contract. API, CLI and UI identify every limitation by **role and source**; an empty
  list is rendered as *"No recorded limitations for this role's model-loop evidence"*, never as a bare "limitations:
  none" that could read as "all possible evidence collection was complete".
- **Bounded:** at most 64 entries per role, the most recent kept (as the digest keeps the most recent), the rest counted
  in `EvidenceLimitationsOmitted`.
- **Persisted before approval:** the Diagnostic role's completion, including both fields, is persisted by
  `CompleteRoleAsync` before the plan approval is requested (fact 18; a required test).
- **Materiality without reasoning.** The runtime never decides whether a limitation is material. It records every
  limitation; a view marks a Finding `restsOnLimitedEvidence` when one of its `evidenceIds` equals some limitation's
  `EvidenceId` — an exact join, computed in the view, not stored. A limitation never creates, removes or edits a
  Finding, never invents or changes an evidence id, and never changes a severity (ADR-0042 rule 4, D-039). The model's
  qualified finding summary (ADR-0042 HARDEN-9 amendment) is unchanged; the typed metadata is runtime-authored and sits
  beside it, so there is no second prose interpretation.
- **Finding flag semantics (normative).** `restsOnLimitedEvidence = true` only when the Finding cites recorded Evidence
  that is itself typed as limited/partial (its id equals a limitation's `EvidenceId`). `restsOnLimitedEvidence = false`
  means **only** "this finding cites no Evidence item that is itself marked limited". It does **not** mean the finding is
  unaffected by missing, failed or denied evidence: a failed or denied read may produce no Evidence id at all, and the
  runtime never manufactures an id to force a linkage and never infers semantic materiality. Views that show the flag
  show the role's limitation list beside it.
- **Visibility.** `GET /api/delegations/{id}` and the list gain, per role, `evidenceLimitations` (typed entries, enums by
  name), `evidenceLimitationsOmitted`, `findingsReply`, and per finding `restsOnLimitedEvidence`. The plan approval view
  (`GET /api/delegations/approvals`) is enriched **from the persisted run** by `DelegationId` with the Discovery and
  Diagnostic limitations, the `findingsReply` and the per-finding flag; `PlanApprovalRequest` itself is unchanged.
  **Approval requirement:** before an operator approves a plan, the approval surface (API view, UI, console) must be able
  to display the persisted Discovery/Diagnostic limitation metadata of that delegation. The `DelegationId` join is
  acceptable only because a failure to load the run — or a run whose fields are `null` — is shown as "limitations
  unavailable / not recorded", **never** as "no limitations"; the operator is never shown a false clean state. The CLI
  prints the same entries in `bops delegate status` and before the console plan approval (`ConsolePlanApprovalProvider` receives the store). The
  UI renders an "Evidence limitations" section per role and the per-finding marker. A role recorded before this ADR
  (`null`) is shown as "not recorded".
- **Compatibility.** Additive only; nullable/backward-safe init properties with `[JsonIgnore(Condition = WhenWritingNull)]`
  (as existing optional members); rows stored before this ADR (historical runs) deserialize with `null`/`0` and are shown
  as "not recorded". New enums start at the explicit values above and are only appended to later; no existing enum is
  renumbered and no member is removed or changed; the frozen 1.x public surface stays compatible. Round-trip
  serialization tests for every new type and backward-deserialization tests for an old row are required; the 1.0
  public-surface snapshot stays green (additive). No audit-schema change.

### 17. Diagnostic JSON: strict, bounded, contained

**Decision on `FindingsOf` tolerance: rejected — the role contract stays strict.** Reading "the first complete JSON
object and ignoring the rest" would accept replies that break the Diagnostic contract ("ONLY one JSON object"), and a
reply with two objects is ambiguous: choosing the first is a guess. The existing containment is kept exactly — the
slice from the first `{` to the last `}` (the same convention as `AgentRunner.TryParsePlan`) — so prose or a code fence
around **one** object is tolerated as today, and two top-level objects remain invalid JSON. Nothing becomes more
permissive.

Hardening, all deterministic and all ending in a typed `DiagnosticReplyOutcome` (§16) rather than an exception:

- **Strict parse:** `JsonDocumentOptions { AllowDuplicateProperties = false }` (HARDEN-1/HARDEN-2 convention). A
  repeated property name **at any depth** — `{"findings":[…],"findings":[…]}`, or a repeated `summary`, `evidenceIds` or
  `severity` inside a finding — is `Malformed`/`DuplicateProperty` and yields **zero findings**: ambiguous model JSON is
  rejected, never first- or last-wins, never filtered per finding.
- **Structural classification (review N-9).** `DuplicateProperty` is decided **structurally**, never from exception
  message text: the slice is parsed for syntax and depth into a `JsonDocument` (failure → `InvalidJson`), then every
  object at every depth is walked with `EnumerateObject` and a repeated name (ordinal) is `DuplicateProperty`; the
  strict-options parse remains the defence-in-depth gate, consistent with HARDEN-1 strict JSON parsing. Exceeding the
  maximum depth maps to `InvalidJson` (no more precise member exists).
- **Bounds:** a reply longer than 65,536 UTF-16 code units is `Malformed`/`TooLarge` without parsing (there is no global
  bound on a model reply: the token meter counts it only after the call returns; a truncated JSON would be ambiguous,
  so it is refused, not cut); maximum depth 16 (exceeded → `Malformed`/`InvalidJson`); more than 64 findings is
  `Malformed`/`TooManyFindings` (zero findings).
  Existing per-finding rules are unchanged (a finding without a summary, or with any unrecorded or missing evidence id,
  is discarded and counted in `DiscardedFindings`).
- **Other outcomes:** empty or whitespace → `Absent`; no `{…}` slice → `NoJsonObject`; syntax error → `InvalidJson`;
  root not an object → `NotAnObject`; no `findings` array → `MissingFindingsArray`; otherwise `Valid`. The required
  distinctions — valid, invalid JSON, duplicate property, too large, too many findings — are therefore the accepted
  enum members `Valid`, `InvalidJson`, `DuplicateProperty`, `TooLarge`, `TooManyFindings`. **Every** failure outcome
  yields zero Findings and no exception escapes.
- **Containment:** `FindingsOf` catches every parse failure itself (`JsonException`; `ArgumentException` cannot occur
  with strict options, and is caught defensively), so no exception reaches `DelegationRunner` from Diagnostic output.
- **Run outcome unchanged in kind:** a malformed reply is a completed diagnosis with zero findings and a visible
  `findingsReply: Malformed/<problem>` — not a failure that hides what Discovery gathered, and not a guess.
- **ADR-0023 invariant preserved:** every Finding still cites only Evidence recorded in this run; nothing in this
  section creates a citation, and the stricter parse can only drop findings, never add one.

### 18. Public contract and versioning

| Addition | Classification | Compatibility |
|---|---|---|
| `RequiredRoles(shape)` in the role-requirement table | internal (`bOps.Abstractions` internal, runtime friend) | none |
| Root from required roles only; diagnosis-only root without Skills/Capabilities; `BeginRoleAsync` guard; `NoProfile` text | internal behaviour (`bOps.Runtime`) | Behavioural authority change governed by this ADR; no API break. A diagnosis-only start that was `Denied` for a missing R/V profile now proceeds; a remediation start is unchanged |
| Readiness evaluator and its result types; drift function; shared argument validator | `bOps.Runtime` public/internal types (not the SDK) | additive |
| Policy load state: evaluator-facing three-valued type in `bOps.Runtime`; the loader message and resolved path stay host-side (`bOps.Api`, `bOps.Cli`) | `bOps.Runtime` (not the SDK) + host internal | additive |
| `EvidenceLimitation`, `DiagnosticReplyOutcome`, `DiagnosticReplyStatus`, `DiagnosticReplyProblem`; `DelegationRoleRun.EvidenceLimitations`, `.EvidenceLimitationsOmitted`, `.FindingsReply` | **`bOps.Abstractions` public contract** | Additive only, product-neutral, dependency-free, init properties; round-trip tests mandatory; 1.0 snapshot compatible; no enum renumbered, no member removed or changed; version moves to the next `1.3.0-preview.N` |
| `SkillRegistry` `InputSchema` validity check at registration; capability input validation before `PrepareAsync` on every (delegated and non-delegated) Capability invocation | internal behaviour | Intentional tightening (operator decision D): an invalid input or an internally invalid schema that was tolerated is now refused, fail-closed, with no coercion or clamping; no first-party Skill providers exist in `src`, compatibility tests over sample/test Skills required |
| `GET /api/delegations/readiness`, `GET /api/skills` | API DTO (additive endpoints) | additive |
| `POST /api/delegations` duplicate-key rejection and `400 code` | API DTO | additive field; duplicate keys now `400 malformed_json` (previously accepted by the default binder); casing, unknown-member and numeric binding unchanged (§9.3) |
| Delegation view and approval view limitation fields, `restsOnLimitedEvidence` | API DTO | additive |
| `bops delegate readiness`, `bops delegate profiles init`, `bops delegate profiles check`, exit code `8` | CLI surface | additive; two reserved verbs |
| Readiness panel, catalog selection, schema form, limitation rendering | Angular model only | — |

### 19. Security matrix

| # | Scenario | Expected authority / result |
|---|---|---|
| S1 | Diagnosis-only + valid D/D profiles only | Root from D/D, `MaxRisk Read`, no Skills/Capabilities; Discovery and Diagnostic run; `DiagnosisCompleted`; R/V never looked up |
| S2 | Diagnosis-only + no profiles | Readiness M1; start `Denied` on `Profile` (Discovery) before any model call |
| S3 | Diagnosis-only + model proposes a non-Read tool (provider/model path) | Rejected as an unknown tool under the provider/tool contract when it was never offered, or refused by `CheckStep` if it reaches resolution; policy engine not reached; `ExecuteAsync` never called; no envelope audit event required on the unknown-tool path |
| S4 | Diagnosis-only + a mutation tool name in a D/D profile | Name may enter the envelope's tool set; any call refused by the risk cap and the no-Skill-scope rule; `profiles check` reports `above_role_risk_cap` |
| S5 | Remediation request + D/D profiles only | Readiness M4; start `Denied` on `Profile` naming Remediation, before any model call |
| S6 | Remediation request + all four valid | Today's four-role root and behaviour, unchanged; approval, journal, verification unchanged |
| S7 | Required profile malformed | Whole policy `AllForbidden`; readiness `malformed`/`policy_load_failed`; start `Denied` on `Profile`; `profiles check` exit 2 |
| S8 | Non-required profile absent | `notRequired`; no effect on readiness, start, root or budgets |
| S9 | Unknown configured tool | No authority effect (intersection simply cannot use it); drift `unavailable_tool` |
| S10 | New registered Read tool absent from profile | Not callable by any role; drift `read_tool_not_granted`; granted only by a reviewed regeneration or edit |
| S11 | Unknown Skill/Capability in a start | API `400 unknown_capability`; runtime `Resolve` refuses at preparation; no Capability code runs |
| S12 | Malformed capability input | API `400 capability_input_invalid` (or `malformed_json`); runner re-check ends `Failed` before Discovery; preparation-path validator refuses before `PrepareAsync` |
| S13 | Ordinary resume of a role child task | `409 task_delegated` / CLI exit 1; only `POST /api/delegations/{id}/resume` continues the run |
| S14 | Duplicate-key Diagnostic JSON — root property (`findings` twice) | `findingsReply: Malformed/DuplicateProperty`, zero findings, `DiagnosisCompleted`; no exception reaches the runner |
| S15 | Evidence-limited Diagnostic result | Typed `evidenceLimitations` persisted and shown in run, approval view, CLI and UI; findings, evidence ids and severities unchanged; `restsOnLimitedEvidence` marks exact citations |
| S16 | Stored pre-ADR diagnosis-only run resumed | Keeps its stored four-role root; still ends `DiagnosisCompleted` without beginning R/V |
| S17 | `profiles init --write --overwrite` over a file with a stricter `defaults`, a tool override, a Remediation profile or a D/D profile that differs from the generator defaults in any dimension but `tools` | Refused, file untouched, fragment printed |
| S18 | Diagnosis-only + broad valid R/V profiles (mutation tools, Skills, Capabilities, large budgets) | Root identical to S1 (and its hash); `MaxRisk ≤ Read`; no Skill/Capability; R/V never looked up or begun |
| S19 | Direct/fake-model call of a non-`Read` manifest reaching runtime resolution under a diagnosis-only run | `EnvelopeEnforcer.CheckStep` → `Forbidden` "Authority envelope:" before policy; policy spy not called; tool `ExecuteAsync` spy never called; audited |
| S20 | Failed evidence collection (a Read tool fails) | Typed `EvidenceLimitation` with the failure outcome/kind, `EvidenceId` `null`; no Evidence manufactured; findings citing other evidence keep `restsOnLimitedEvidence: false` |
| S21 | Denied evidence collection (a Read call refused by envelope or policy) | Typed limitation recorded with the denied outcome, `EvidenceId` `null`; nothing executes; finding flags unaffected by manufactured ids |
| S22 | Partial evidence (`Completeness: Partial`/`Unavailable` or shortened observation) | Typed limitation with its `EvidenceId`; a Finding citing that id has `restsOnLimitedEvidence: true` |
| S23 | Readiness `remediation=true` green + selected remediation target/Capability rejected at submit | Readiness stays `ready: true`; submit `400` (`unknown_capability`/`capability_input_invalid`) or start `Denied` naming the dimension before any model call; UI shows the submit error separately from readiness |
| S24 | Malformed profile for a **non-required** role (whole policy fails to load) | Whole policy `AllForbidden` (unchanged loader semantics); required roles `malformed`/`policy_load_failed`; not-required roles `notRequired`; start `Denied` on `Profile` |
| S25 | Valid unused profile (e.g. valid Verification only, diagnosis-only request) | `notRequired`; root, budgets and readiness unchanged |
| S26 | Invalid Capability schema at Skill registration (blank or duplicate name, `Enum` with empty `AllowedValues`, inconsistent HARDEN-6 constraint) | Deterministic, bounded registration failure of that Skill; the Capability never becomes active; absent from `/api/skills` |
| S27 | Validation error on a `Sensitive` Capability parameter | Error names parameter and constraint only; the submitted value appears in no API/CLI/UI error, log or audit text |
| S28 | Duplicate property in the `POST /api/delegations` body (top level or inside `input`) | `400 malformed_json`; no run created; never first- or last-wins |
| S29 | Duplicate property inside one Diagnostic finding (`summary`, `evidenceIds`, `severity`) | `findingsReply: Malformed/DuplicateProperty`, zero findings (not filtered per finding) |
| S30 | Diagnostic reply exceeding maximum depth 16 | `findingsReply: Malformed/InvalidJson`, zero findings, no exception |
| S31 | Diagnostic reply with more than 64 findings | `findingsReply: Malformed/TooManyFindings`, zero findings |

### 20. Required tests (written before the code they govern; the core is test-first)

Property tests (FsCheck-style or exhaustive generators over valid `RoleProfile` values and request shapes):

- **P1 — diagnosis never exceeds Read (mandatory Phase-1 evidence, review N-5).** For every generated valid
  diagnosis-only request and profiles, every envelope derived for an executable role has `MaxRisk ≤ Read`, the root has
  `MaxRisk ≤ Read` and no Skill or Capability; and for every generated manifest with `Risk > Read`, `CheckStep` refuses
  it under every such envelope. The proof uses **both** a policy spy and a tool-execution spy: for a non-Read proposed
  step, the policy engine is not reached when the envelope refuses before policy, and the tool's `ExecuteAsync` is never
  called. Both real paths are covered: the provider/model path (a tool never offered may be rejected earlier as an
  unknown tool; no specific envelope audit event is required there) and the direct/fake-model path (the call reaches
  runtime resolution and `EnvelopeEnforcer.CheckStep` returns `Forbidden` before policy and execution). Includes S18 and
  failed/denied evidence collection (S20, S21).
- **P2 — no unused-role authority.** For a fixed diagnosis-only request and fixed D/D profiles, varying the Remediation
  and Verification profiles over absent, valid and any generated value leaves the root (and its hash) unchanged.
- **P3 — remediation unchanged.** For every generated remediation request and four profiles, the new root and every role
  envelope equal the pre-ADR reduction (a frozen copy of today's `BuildRoot`/loop kept in the test); grant stays grant
  and deny stays deny for every input. Exact old/new role **and** dimension equality is required for **single-fault**
  remediation fixtures (e.g. only the R/V profile missing or refused); for multi-fault fixtures the result must remain
  denied and the new deterministic pipeline-order attribution is authoritative (§5).
- **P4 — child subset.** For every required role, `child ⊆ root ∩ profile ∩ request` in every dimension (sets, ceilings,
  budgets, deadline, window), for both shapes.
- **P5 — readiness equivalence.** For generated profile sources, load states, shapes and instants: `ready` ⇔
  `DeriveRootAttributed` grants with a non-narrowing request; for every non-ready role the start's denial (first in
  pipeline order) has the same role and dimension, and the same reason for `profile_missing` and `reduction_denied`.
  Readiness/start state and dimension are compared under stable single-fault inputs; instants are injected, and
  conditions readiness does not promise to predict (§5: time races, a maintenance window not yet open where enforced at
  step time, the named change) are excluded. Includes the role-independence lemma (§5).
- **P6 — generated configuration is safe (mandatory Phase-1 obligation, review N-1).** For generated tool registries
  spanning the **complete tool-name range `ToolRegistry` accepts** (YAML-significant characters, quotes, `#`, `:`, leading
  `-`/`?`/`*`/`&`/`!`, whitespace, Unicode, YAML keywords such as `yes`/`null`/`~`, numeric-looking names), the
  generated YAML loads with `PolicyConfigLoader.Load` and every post-generation assertion of §10.2 holds: profiles
  exactly Discovery + Diagnostic; tool names equal the intended available Read tools exactly; `maxRisk: read`; no
  `skills`, `capabilities`, `remediation`, `verification` or `window`; the accepted finite budgets; `targets` and
  `environments` = `[local]`. A name the contract cannot represent fails generation, never silently altered or granted.
  Header and determinism tests use an injected `TimeProvider`.
- **P7 — creating the file changes no non-delegation decision.** For every manifest and context,
  `PolicyEngine(Load(generated)).Evaluate == PolicyEngine(SafeDefault).Evaluate`.

Negative and regression tests: the security matrix rows S1–S31 one by one; `BeginRoleAsync` guard; the readiness HTTP
contract (four roles, order, states, codes, `ready` rule, `400` for a bad `remediation`, `401`/`403` under Bearer and
cookie, no CSRF header needed); `/api/skills` shape, ordering and that disabled plugins are absent; capability
validation for every `ToolParameterType` and constraint, including boundary values and no clamping; strict start body;
`FindingsOf` for each `DiagnosticReplyProblem`, duplicates at root and inside a finding, 65,536/65,537 characters, depth
and count; limitations persisted before approval and shown in the approval view; old `DelegationRun` rows round-trip;
`profiles init` refusal paths and atomic write; `profiles check` counting and ordering; CLI exit codes; Angular store and
component tests for every readiness failure path and for the toggle re-query; Playwright E2E-9 and E2E-10.

Review additions (N-1…N-13), each deterministic: `--overwrite` refusal for every failed condition of §10.1 including a
file changed between check and replace, and permission/ACL preservation where supported (N-2); readiness wording, the
fixed remediation note and "readiness green, submit rejected" (N-3, S23); single-fault vs multi-fault attribution (N-4,
P3/P5); P1 with both spies on both paths (N-5); limitation scope wording and "not recorded"/"unavailable" never rendered
as "no limitations", including a failed run load in the approval view (N-6); finding-flag `false` semantics with
failed/denied reads that produced no Evidence id (S20/S21); `InputSchema` registration validation (N-7, S26) and
compatibility tests over sample/test Skills on the non-delegated preparation path; duplicate-key rejection on
`POST /api/delegations` without other binding changes (casing, unknown members, numbers) and Angular raw-JSON duplicate
detection before `JSON.parse` (N-8, S28); structural duplicate classification independent of exception text, depth →
`InvalidJson`, `>64` findings (N-9, S14, S29–S31); a malformed non-required profile failing the whole load (N-10, S24);
unknown future `reasonCode` treated as not ready by the UI (N-11); `Sensitive` values absent from every error, log and
audit text (N-12, S27); `profiles check` printing the resolved policy path and hosts logging it without contents (N-13).

### 21. End-to-end acceptance (packet scenarios preserved)

- **E2E-9.** Fresh configuration directory → `bops delegate profiles init --read-only --write` → API started on that
  file → `GET /api/delegations/readiness?remediation=false`: Discovery `ready`, Diagnostic `ready`, Remediation
  `notRequired`, Verification `notRequired`, `ready: true` → diagnosis-only delegation (deterministic fake model) →
  `DiagnosisCompleted`. The audit log shows no tool call with risk above `Read` executed (and no `Forbidden` needed for
  that).
- **E2E-10.** Fresh configuration with no profiles → readiness `remediation=false`: D/D `missing`, R/V `notRequired` → the
  UI renders the four roles with the server's reasons and Submit disabled → **no** `POST /api/delegations` is issued
  (asserted on the network log).
- **E2E-10 remediation variant.** Read-only D/D profiles → toggle "Prepare a change" → re-query with `remediation=true`:
  Remediation and Verification `missing` → Submit disabled; a handcrafted `POST` with a remediation is `Denied` naming
  Remediation.
- **E2E-10 readiness-vs-submit variant (review N-3).** All four profiles valid → readiness `remediation=true` reports
  `ready: true` and the panel shows the fixed note "The selected change, target, environment and input are validated
  when you submit." → the operator selects a Capability/target outside the Remediation profile (or invalid input) →
  submit fails validation (`400` or start `Denied` naming the dimension, before any model call) → the UI shows the
  submit error separately while the readiness panel still shows role readiness, never "this change is executable".

### 22. Platform evidence gate (Phase 1, not Phase 0)

On **Windows** and on **Linux** (WSL Ubuntu 24.04 or CI), each from an empty configuration directory, recorded verbatim
in the packet: (1) `bops delegate readiness` before init (M1, exit 2); (2) `bops delegate profiles init --read-only`
(print) and `--write`; (3) the generated `policy.yaml` contents (tool list = that host's available Read tools); (4)
`bops delegate profiles check` (exit 0); (5) `bops delegate readiness` after init (M3, exit 0) and
`readiness --remediation` (M4, exit 2); (6) one diagnosis-only `bops delegate "<objective>"` against a real or recorded
provider ending `DiagnosisCompleted` (exit 0), with the audit excerpt showing only `Read` tools executed. Windows and
Linux CI green.

### 23. Documentation impact (Phase 1)

- `docs/agents/delegation-policy.md`: required roles per request; "a fresh install" safe path —
  `bops delegate profiles init --read-only` → review → `--write` → restart the API → `bops delegate profiles check` →
  `bops delegate readiness` → a diagnosis; why `defaults` appears in the generated file; regeneration with `--overwrite`;
  drift. It must say plainly that bOps never grants delegation by itself and never at startup.
- `docs/agents/delegation.md`: request-dependent roles, the read-only invariant, typed limitations and `findingsReply`.
- `docs/agents/delegations-api.md`: readiness and skills endpoints, new `400` codes, view fields.
- `docs/security/threat-model.md`: the diagnosis-only authority argument (§3), readiness discloses configuration shape to
  `viewer`, generated profiles are not the boundary, unchanged whole-file fail-closed parsing, strict Diagnostic JSON;
  and the §9.2 `Sensitive` rule — a sensitive value never appears in errors, logs or audit text, while Capability input
  is persisted as plain JSON in the delegation store (no encrypted secret storage is introduced; the database files'
  protection boundary applies).
- `README.md` (CLI exit codes, `bops delegate` usage) and `docs/operator-configuration.md` (`Policy:FilePath` shared by
  API and CLI; the API reads it at start).

### 24. Out of scope

Automatic profile creation at startup; runtime wildcards or category grants; automatic mutation grants; relaxing
remediation; model-driven role selection; a model in Remediation or Verification; approval, plan-hash, journal or
reconciliation changes; partial policy loading; readiness for a specific Capability scope; multi-node delegation,
Control Plane, commercial Skills; HARDEN-12 layout work, HARDEN-13 provider fallback, HARDEN-14 composition.

## Alternatives considered

- **Keep four roles; ship generated "inert" Remediation/Verification profiles.** Rejected: Remediation's table requires
  Skills, Capabilities and a risk floor of `Low`, so an inert Remediation profile does not exist; any valid one is a
  mutation grant. That is precisely F-14.
- **Make R/V `Optional` per role in the ADR-0031 table.** Rejected: it would weaken remediation requests, where all four
  must stay required.
- **Root from all *present* profiles.** Rejected: present-but-unused Remediation profiles would still widen a diagnosis's
  root (tools, budgets, deadline), violating P2.
- **Tolerate a malformed unused profile (partial load).** Rejected: a half-loaded policy can be more permissive than
  the file the operator wrote (S3, ADR-0031 §2).
- **Readiness computed in the API or UI.** Rejected: a second policy implementation (packet stop condition).
- **A fifth readiness state (`unusable`) or `reasonCode` instead of `dimension`.** Rejected (operator decision B): the
  packet fixes four states; a refused well-formed profile is reported as `malformed` ("Not usable") with the reducer's
  dimension and `reasonCode: reduction_denied`, which keeps clients typed without a fifth state.
- **Readiness with Capability-scope parameters.** Not part of HARDEN-11 (operator decision E): readiness is
  profile/role-shape readiness; the named change is validated at submit and start, before any model call.
- **Generated profiles as a fragment only, never a whole file.** Rejected as the only mode: a fresh install has no file,
  and a file with only `delegation` forbids every tool (fact 7). The fragment remains the answer for existing files.
- **YAML merge into an existing policy.** Rejected: cannot be proven not to add authority or to preserve intent.
- **`FindingsOf` first-complete-object tolerance.** Rejected (§17).
- **Typed limitations as `Evidence`, `Finding` or `SkillReport` fields, or as prose.** Rejected (§16).
- **Persist the role task id and derive limitations on read.** Rejected: also an Abstractions change, plus derivation on
  every read in every host; computing once at role end is simpler and equally reproducible.

## Consequences

- A fresh installation can diagnose after one explicit, reviewable, read-only step; mutation still needs an operator to
  write all four profiles by hand.
- The diagnosis-only root is strictly smaller than before (two profiles, no Skills/Capabilities); remediation is
  unchanged.
- Operators see readiness before submit, with the same reason the start would give.
- Profiles stay explicit; new tools surface as drift, never as silent grants.
- Capability input is validated by the server for the first time, for every client.
- Delegation results carry typed, runtime-authored limitation metadata; the Diagnostic parser can no longer throw or
  accept ambiguous JSON.
- `bOps.Abstractions` grows by four small types and three init properties (additive, next preview).
- One API host still reads the policy only at start; readiness reflects that, and the docs say to restart.

## Open questions for the reviewer and the operator — resolved 2026-10-05

The five questions of the Proposed draft are closed by the operator decisions below; their original wording is kept in
the Proposed commit `3b6a5a4`.

1. **Abstractions addition** → decision A: accepted (§16).
2. **`malformed` for a well-formed but refused profile** → decision B: four states kept; `malformed` = present but not
   usable, labelled "Not usable" (§6.2).
3. **Generated constants** → decision C: accepted as written (§10.2).
4. **Capability-input validation on the non-delegated path** → decision D: accepted in HARDEN-11 (§9.2).
5. **Readiness for a named change** → decision E: not in HARDEN-11; readiness is profile/role-shape readiness (§5).

## Independent review and operator acceptance (2026-10-05)

**Review.** HARDEN-11 INDEPENDENT AUTHORITY ARCHITECTURE REVIEW — **PASS** of the Proposed ADR (`3b6a5a4`): BLOCKER 0;
NON-BLOCKING N-1 … N-13; recommended next action "accept ADR-0044 and implement HARDEN-11". All thirteen findings are
incorporated above:

| Finding | Where | Kind |
|---|---|---|
| N-1 safe YAML generation, loader round-trip, structural assertions | §10.2, §20 P6 | **Mandatory Phase-1 obligation** |
| N-2 `--overwrite` protection | §10.1, S17 | Implementation requirement |
| N-3 readiness wording and remediation note | §5, §6.2, §13, §21, S23 | Clarification + E2E-10 requirement |
| N-4 pipeline-order attribution, single-fault equivalence, maintenance window | §5, §20 P3/P5 | Clarification |
| N-5 Read-authority proof with policy and execution spies, both paths | §3, §20 P1, S3, S18–S21 | **Mandatory Phase-1 obligation** |
| N-6 limitation scope by role/source; finding-flag semantics; approval visibility | §16 | Clarification |
| N-7 `InputSchema` validity at registration | §9.2, S26 | **Mandatory Phase-1 obligation** |
| N-8 strict JSON scope; Angular raw-JSON duplicates | §9.3, S28 | Clarification |
| N-9 structural duplicate detection; depth → `InvalidJson`; typed outcomes | §17, S14, S29–S31 | Clarification |
| N-10 malformed non-required profile wording | §1, S24 | Clarification |
| N-11 snake_case `reasonCode`, unknown codes fail closed, "no usable profile" | §6.2, §7, §11, §13 | Clarification |
| N-12 `Sensitive` values never in errors/logs/audit; plain-JSON persistence stated | §9.2, §23, S27 | Security rule |
| N-13 editorial/evidence corrections (ADR-0042 pointer, `Official`, 300k vs `Agent:MaxTotalTokens`, load-state layering in `bOps.Runtime`, policy path printed/logged, `TimeProvider`, `1c9cf5e` record) | §4, §8, §10, §12, packet | Correction |

**Operator decisions (2026-10-05), accepted.**

- **A — typed evidence limitations:** persist typed limitation metadata in `bOps.Abstractions`, the smallest additive,
  backward-compatible contract of §16; the ADR-0042 gap is not deferred again.
- **B — readiness states:** exactly four public states `ready`, `missing`, `malformed`, `notRequired`; `malformed` =
  profile/configuration present but not usable for the role; `reasonCode`, `dimension`, `reason` carry the detail; UI
  wording "Not usable".
- **C — generated budgets:** per role 15 steps, 150,000 tokens, 30 minutes; risk `Read`; blast radius `Single`; targets
  `[local]`; environments `[local]`; Discovery and Diagnostic only; no Remediation or Verification profile. Finite safe
  defaults; token exhaustion before E2E-9 completes in real platform evidence is a stop-and-report, not a silent change.
- **D — Capability input validation:** `CapabilityManifest.InputSchema` is authoritative wherever that Capability is
  invoked, delegated and non-delegated; an intentional, additive, fail-closed tightening.
- **E — readiness scope:** profile / role-infrastructure readiness, not specific-request executability; the selected
  Skill, Capability, target, environment and input are checked at submit/runtime.

**Acceptance.** ADR-0044 is **Accepted** on 2026-10-05 by the operator after the review PASS and decisions A–E
(`agentic/06-decisions.md`, D-041). HARDEN-11 is **not implemented**; Phase 1 implementation is unblocked, with N-1, N-5
and N-7 as mandatory Phase-1 obligations and the other findings as accepted clarifications or implementation
requirements as stated in their sections.
