# ADR-0036 — Product-neutral entitlement applicability and execution-time enforcement

Status: Accepted
Date: 2026-09-24
Accepted: 2026-09-24 by the operator
Amends: ADR-0016 only for pre-invocation runtime authorization denial of required verification

## Context

bOps already separates model proposal, policy, human approval, execution, verification and audit.
Those controls must remain independent when an operation has an entitlement requirement. In
particular, standalone Apache-licensed bOps must continue to operate when no entitlement provider is
installed, reachable or healthy. Conversely, once an operation is explicitly governed by an
entitlement requirement, a missing, failed, stale or unsuitable entitlement decision must prevent
that operation from executing.

The distinction cannot be inferred from a provider's presence, absence, package/product naming,
tier or identity. It must be a structural, runtime-visible attribute of the requested
operation. The model may propose an operation, but must never decide whether entitlement applies or
grant it.

## Decision

### Explicit applicability boundary

M2 will add a dependency-free public `EntitlementApplicability`/requirement to the resolved execution
context. At registry registration the host stamps its effective value from trusted host configuration
or a trusted host-registered capability boundary, analogous to host-assigned package identity. A
package declaration is not authoritative. It is not supplied by a model, provider response, product
name or installation state.

The requirement has exactly two meanings:

- **Not governed:** entitlement is explicitly not applicable. The runtime records that result and
  continues through the normal policy, approval, execution and verification path. No provider is
  consulted.
- **Governed:** entitlement is required for this particular operation. The runtime constructs a
  neutral request and requires a current, matching allow decision immediately before the operation
  executes.

There is no implicit third state. A missing applicability assignment is a configuration/registration
error for an operation intended to be governed and must fail closed at registration or invocation;
it must not be converted to not-applicable. The applicability assignment is structural so unrelated
standalone capabilities remain usable even when a governed provider is absent or unhealthy.

### Public request and decision model

M2 will define serializable, dependency-free contracts. Exact type names are implementation detail,
but the request must contain an opaque per-evaluation `RequestBinding`; subject identity;
installation identity; feature; Skill identity and version when applicable; capability; requested
resource and amount/limit; node; target; and requested validity/constraint context. Identifiers are
opaque strings or existing public identity types; Runtime does not interpret product or provider
meanings.

Trusted Runtime/host code generates a fresh `RequestBinding` for every entitlement evaluation
attempt. It is opaque to the LLM, copied into the provider request, returned unchanged in the
provider decision, and contains no license, token, credential, vendor payload, signature, or
commercial identifier. It binds the decision to this Runtime evaluation attempt; it is not
entitlement proof. It is never reused as authorization across retry, resume, replan, delegation,
or verification.

The decision is a closed provider result with `Allowed` or `Denied`; the same `RequestBinding`; a
reason code; a source category; effective limits/constraints or accepted result metadata; and
validity metadata (`ValidFrom`, `ValidUntil`, and an authority/freshness identifier suitable for
correlation but not secret material). `NotApplicable` is a host-owned applicability result: no
provider is consulted for it, and a provider cannot return it for a governed request.

The provider returns the final interpreted authorization decision, including resource consumption or
limit evaluation. Runtime validates the decision's structure, requires its `RequestBinding` to
match the current request, checks its accepted usability/validity rule, and consumes no raw grants.
Missing, malformed, or wrong bindings are invalid and fail closed for governed execution. Runtime
does not independently compare or hash every entitlement dimension: the provider is authoritative
for subject, installation, feature, Skill/version, capability, resource constraints, node, target,
validity, and entitlement-specific constraints. This preserves one authority for entitlement
interpretation and prevents duplicated, divergent grant and limit logic in Runtime.

### Provider boundary and lifetime

M2 will expose one node-scoped, dependency-free entitlement service interface that accepts the
neutral request and returns the neutral decision asynchronously. It has no product-specific types,
raw credential/token fields, mutable global state or package service-locator access. Runtime resolves
the service per executing node, consistent with A3/A5; the host owns provider registration and
lifetime. A provider cannot widen policy, approval, the delegation envelope, package trust, or the
applicability boundary.

### Semantic execution-time enforcement boundary

There is one semantic entitlement enforcement boundary: immediately before every
entitlement-governed tool invocation. This is not a requirement for one physical call site or a new
public Runtime abstraction. M3 may use one shared internal entitlement-evaluation routine from the
existing execution paths.

M3 applies the routine at both currently known Runtime call sites: (1) ordinary governed tool
execution in `AgentRunner.ExecuteStepAsync`, after delegation-envelope enforcement, argument
validation, policy evaluation, required approval, and approval binding, but before durable
side-effect intent and before `ExecuteWithTimeoutAsync`; and (2) an explicitly governed
runtime-mandated verification invocation in `EvaluateVerificationAsync` /
`ExecuteVerificationToolAsync`, after the applicable delegated envelope permits that verification
read and immediately before it ultimately invokes the verification tool through
`ExecuteWithTimeoutAsync`. M3 must apply the same boundary to any other existing M3-scope
Runtime-owned path that can directly invoke a governed `ITool`; focused inspection identifies no
other such path. Prepared-plan, delegated, evidence, resume, retry, and replan paths that route
through `ExecuteStepAsync` use the ordinary integration point.

For a governed request, only a newly obtained, binding-matched, currently usable `Allowed` decision
permits execution. Therefore approval followed by entitlement revocation produces no governed tool
execution. Entitlement never replaces policy or approval: a governed side effect requires every
applicable gate to pass, and a policy denial remains final.

For an ordinary delegated invocation, the envelope must permit the call, policy must permit it,
required approval must be valid, and entitlement must allow it when governed. For runtime-mandated
post-action verification, the applicable delegated envelope must permit the verification tool and
entitlement must allow it when governed. An entitlement `Allowed` decision cannot override an
envelope denial. Policy and human approval are intentionally absent from this verification branch:
ADR-0016 defines it as runtime-mandated infrastructure, not a model-proposed action.

An entitlement denial of an ordinary invocation is `AuthorizationKind.EntitlementDenied`, distinct
from `PolicyDenied`. The denied tool does not execute. In a prepared or delegated plan, it is a
terminal denial for that plan execution path: the existing stop set (`PolicyDenied`, `UnknownTool`,
`UserRejected`) gains `EntitlementDenied`; the plan becomes `Stopped` with
`StoppedBy = AuthorizationKind.EntitlementDenied`, and no later dependent step executes. A
model-driven call denied by entitlement is surfaced as a denied-action deviation for replanning,
consistent with the existing denial behavior. This adds no global task status.

### Time, cache and resource semantics

Runtime uses its injected `TimeProvider` as the authoritative wall-clock source for comparison;
providers state their validity window in UTC. There is no positive clock-skew tolerance or grace
extension: an allow is usable only when `ValidFrom <= now <= ValidUntil`. A future, expired,
missing-window, malformed, revoked, or binding-mismatched decision is denied. A detected
wall-clock rollback invalidates cached authority and requires a fresh provider decision; it never
extends an allow.

Provider-side cache or offline behavior may derive a fresh decision from a valid cached
entitlement source, but every Runtime evaluation attempt receives a new `RequestBinding` and the
returned decision must echo that current binding. A previously returned `Allowed` decision object
cannot be replayed into a later attempt. Provider absence or failure, including an invalid result,
fails closed only for governed requests; this ADR invents no grace period.

Resource requests are part of the bound request. The provider's final allow must reflect the requested
amount and any remaining limit. Exhaustion or an unrecognized resource/constraint is denied. A later
implementation must make consumption/reservation semantics atomic at the provider boundary where
needed; Runtime must not independently decrement opaque allowances.

### Resumption, retry, replanning, delegation and running work

An entitlement decision is per evaluation attempt, never persisted as reusable authority. Resume,
retry, replan, delegated execution, and verification reconstruct the request, generate a new
binding, and obtain a new decision at their own semantic enforcement boundary. A previously allowed
decision cannot authorize a later attempt. Delegation envelopes only reduce authority and cannot
carry an entitlement allow across roles or retries.

Revocation cannot undo a completed effect. It also cannot promise cancellation of an irreversible
native operation already inside `ExecuteWithTimeoutAsync`; current architecture must record and
reconcile such an outcome through the existing timeout/verification and delegation journal semantics.
It does prevent subsequent not-yet-executed governed effects. Verification of an already executed
effect remains meaningful and must be attempted as ADR-0016 requires.

### Reads and verification

ADR-0016 still requires verification after every executed non-`Read` call, whether the tool reports
success, failure or timeout. If a governed mutation is denied before it executes, it has no executed
effect requiring post-action verification; independent diagnostics may still run according to their
own applicability. A mutation that executes while entitled still has its required verification.

This ADR **amends ADR-0016 in one narrow case**: Runtime authorization prevents the required
verification tool from being invoked at all. A denied delegated envelope, a governed entitlement
denial, a missing or failing governed provider, or an invalid entitlement binding or validity
decision are such pre-invocation authorization denials. The verification tool is not called and
`IVerifiableTool.EvaluateVerificationAsync` is not called. Runtime directly returns
`VerificationStatus.Inconclusive` with bounded neutral detail saying which runtime authorization
boundary prevented verification. It never reports the original effect as `Confirmed` or treats the
denial as `Refuted`; it claims neither success nor failure of that effect and does not automatically
roll back an effect that already occurred. There is no result from an *executed* verification tool
for the package to interpret. Passing a manufactured `Failure` to an arbitrary evaluator is unsafe:
the evaluator can ignore it and return `Confirmed`.

Everywhere else, ADR-0016's interpretation rule remains: when the verification tool actually
executes, its actual `ToolCallResult` goes to the package evaluator, which remains the sole
interpreter even when that result is `Failure`. Runtime accepts the evaluator's `Confirmed` result;
it does not override the package merely because it dislikes the result. ADR-0016's existing handling
of a verification tool that cannot resolve or whose arguments fail validation also remains outside
this authorization-denial amendment.

A verification `Read` that is not governed proceeds normally. A `Read` is governed only when its
own explicit applicability requirement says so; mutation entitlement neither silently propagates to
it nor exempts every `Read`. For post-action verification in a delegated execution, Runtime checks
the applicable envelope of the execution context in which that verification occurs, including its
Tools dimension; this enforces ADR-0030/0031 authority rather than granting new entitlement
authority. Runtime resolves the verification tool, constructs and validates its arguments, checks
that envelope, then evaluates entitlement freshly if the verification invocation is governed. It
invokes the verification tool and passes its actual result to the package evaluator only after all
applicable gates permit it. The separate ADR-0030 Verification role gathers independent Read
evidence under its own envelope through the ordinary step path; it is not this automatic post-action
verification invocation. Runtime does not reuse the mutation's entitlement decision.

### Audit and public/private boundary

M2/M3 will audit a bounded neutral entitlement record correlated with the tool call: applicability,
the request binding where useful as neutral correlation metadata, decision, source category, reason
code, relevant non-sensitive constraints/limits and validity state.
It must never include raw license/token material, provider payloads, credentials, signatures or
unnecessary fingerprints. Verification entitlement decisions use the same neutral audit rules. A
denial after a mutation distinguishes the original effect executed, the verification tool not
executed, the runtime boundary that prevented it, and final verification status `Inconclusive`,
without leaking provider data. Runtime-mandated verification does not acquire a peer
model-requested `ToolCallAuditEvent`. `AuthorizationKind.EntitlementDenied` identifies which gate
denied an ordinary tool invocation; the separate neutral entitlement record explains applicability,
source category, reason, validity, constraints and request binding. Raw provider material does not
enter `ToolCallAuditEvent`. Existing policy, approval, tool outcome and verification records remain
separate, preserving their meanings.

The public surface owns only neutral contracts and generic enforcement. Product-specific business
rules, provider implementations, credential formats, activation rules and product-specific Skills
remain outside this repository. Runtime names none of them.

## Threat and bypass analysis

| Threat | Mitigation |
|---|---|
| Missing provider locks all OSS | Explicit not-governed applicability bypasses provider lookup. |
| Provider failure becomes not-applicable | Governed failure is denied; only trusted structural assignment can be not-applicable. |
| Verification bypasses entitlement | The same semantic boundary applies before an explicitly governed verification invocation. |
| Verification authorization denial becomes success | A pre-invocation runtime denial skips both the verification tool and package evaluator; Runtime records `Inconclusive`. |
| Stale allow, cache replay or indefinite cache | Fresh per-attempt binding, bounded UTC validity and rollback invalidation. |
| Runtime reinterprets entitlement | Provider evaluates entitlement dimensions; Runtime validates structure, binding and usability only. |
| Revocation after approval | Check occurs after approval binding and immediately before invocation. |
| Resume, retry or replan bypass | No decision persists as authority; every attempt re-evaluates. |
| Delegation bypass | Ordinary steps use `ExecuteStepAsync`; runtime-mandated post-action verification also checks its applicable delegated envelope before invocation. |
| Entitlement-denied plan step is followed by dependent steps | `EntitlementDenied` stops prepared and delegated plan execution before any later step. |
| Verification accidentally blocked | Mutation denial does not govern verification reads; separately governed reads become inconclusive. |
| Audit secret leakage | Audit only neutral outcome metadata; exclude raw provider material and credentials. |
| Ambiguous applicability authorizes execution | Missing/ambiguous governed assignment fails closed. |
| Ambiguous applicability denies all OSS | Explicit not-governed assignment is independent of provider state. |
| Clock rollback extends authority | `TimeProvider` check invalidates cached authority on rollback. |

## Alternatives considered

- **Check during planning only or approval only.** Rejected: both create a revocation/TOCTOU gap
  before side effects and cannot safely authorize resume or retry.
- **Enforce only in `ExecuteStepAsync`.** Rejected: ADR-0016 verification invokes its tool through
  `ExecuteVerificationToolAsync`; a separately governed verification read would bypass that one
  physical call site.
- **A public generic entitlement invoker.** Rejected: existing Runtime paths can share one internal
  evaluation routine without redesigning `AgentRunner` or expanding the public surface.
- **Runtime echoes, hashes, or compares every request dimension.** Rejected: it creates a second,
  divergent entitlement-policy interpreter. A fresh opaque request binding associates the decision
  with its evaluation attempt without becoming entitlement proof.
- **Reuse the mutation allow for verification or exempt all reads.** Rejected: verification is a
  distinct invocation; an explicitly governed `Read` must be evaluated immediately before it runs.
- **Absorb entitlement into policy.** Rejected: policy retains its independent authorization domain;
  combining them makes provider state able to obscure or weaken policy semantics.
- **Skill-only entitlement.** Rejected: plain tool calls, prepared plan steps, retries and delegated
  paths would not share the boundary.
- **Provider presence governs every operation.** Rejected: it globally disables standalone OSS and
  makes applicability implicit.
- **Runtime interprets raw grants.** Rejected: duplicated interpretation and resource accounting
  create inconsistent authorization semantics.
- **Indefinite cached allow.** Rejected: it turns a temporary decision into permanent authority.

## Consequences and handoff

M2 adds the dependency-free, serializable host-stamped applicability/requirement, request,
request-binding, decision, validity/constraint, source-category, reason-code and node-scoped service
contracts; it also adds public-surface and round-trip tests with no provider implementation. The
public request and decision contracts must include the neutral binding field: Runtime creates it for
the request and the provider decision returns it unchanged. M2 also appends the public enum value
`AuthorizationKind.EntitlementDenied = 5`. Existing numeric values stay frozen:
`Automatic = 0`, `UserApproved = 1`, `UserRejected = 2`, `PolicyDenied = 3`, and `UnknownTool = 4`.
M2 updates public-surface, version-compatibility tests and snapshots to prove this additive change;
it does not reuse `PolicyDenied` or redesign the authorization contract.

M3 registers the service through the host, adds a shared internal evaluation routine, and applies it
at both known execution paths: ordinary governed invocation from `ExecuteStepAsync`, and governed
runtime-mandated verification immediately before `ExecuteVerificationToolAsync` invokes its tool.
The required semantic sequences are:

1. **Ordinary tool invocation:** applicable delegated envelope, argument validation, policy,
   required approval and approval binding, fresh entitlement evaluation if governed, durable intent
   where already required, tool execution, required post-action verification, audit and journal
   completion.
2. **Runtime-mandated post-action verification:** resolve and construct the verification call,
   validate its arguments, check the applicable delegated envelope, obtain a fresh entitlement
   decision if governed, execute the verification tool, then call the package evaluator with its
   actual result. If a runtime authorization boundary denies the invocation, skip both execution
   and the evaluator and return `Inconclusive`. Do not add Policy or Approval to this branch.

M3 adds separate neutral audit records without a generic invoker redesign. It treats
`EntitlementDenied` as a stopped prepared/delegated plan step and a model-driven denied-action
deviation, without changing existing policy or approval meaning.

M3 acceptance tests must cover: valid allow; expired, future and revoked authority; resource
exhaustion; provider missing/failure; cache/offline; clock rollback; post-approval revocation;
not-applicable standalone OSS; stale resume/retry decision; delegation; and verification/read
behavior. They must prove denied governed mutations never invoke the tool, rather than merely
asserting a returned status. They must explicitly prove:

| Scenario | Required outcome |
|---|---|
| Envelope denies ordinary step | No execution. |
| Entitlement allows but envelope denies ordinary step | No execution; entitlement cannot widen the envelope. |
| Policy denies but entitlement would allow | No execution. |
| Approval rejects or expires but entitlement would allow | No execution. |
| Earlier gates allow but entitlement denies | `EntitlementDenied`; no execution. |
| Prepared or delegated plan step denied by entitlement | Plan is `Stopped`, `StoppedBy` is `EntitlementDenied`, and later steps do not execute. |
| Mutation allowed; verification ungoverned | Verification executes. |
| Mutation allowed; governed verification allowed | Verification executes. |
| Mutation executes; delegated envelope denies its post-action verification tool | Verification tool and evaluator are not called; Runtime returns `Inconclusive`. |
| Entitlement revoked before governed post-action verification | Verification tool and evaluator are not called; Runtime returns `Inconclusive`. |
| Governed post-action verification provider failure | Verification tool and evaluator are not called; Runtime returns `Inconclusive`. |
| Governed post-action verification wrong request binding or invalid validity | Verification tool and evaluator are not called; Runtime returns `Inconclusive`. |
| Verification tool executes and returns `Failure` | Package evaluator remains the sole interpreter under ADR-0016. |
| Verification tool executes and package evaluator returns `Confirmed` | Runtime accepts that package interpretation. |
| Ordinary governed execution wrong request binding | Fail closed; tool does not execute. |
| Retry, resume, or delegation | Each receives a fresh request binding. |
| Cached provider source | It returns a fresh decision bound to the current request binding. |

Cross-ADR composition: ADR-0016 is preserved except for the explicit pre-invocation runtime
authorization-denial amendment above. ADR-0025's restricted and internal invocations still cross
applicable entitlement enforcement. ADR-0030/0031 delegated authority remains monotonic, including
the applicable Tools envelope for post-action verification reads. Policy and Approval remain
independent and unchanged. This proposal preserves dependency-free Abstractions, package isolation,
runtime product neutrality, the model-proposes/runtime-decides boundary, post-action verification,
and non-bypassable delegated execution.
