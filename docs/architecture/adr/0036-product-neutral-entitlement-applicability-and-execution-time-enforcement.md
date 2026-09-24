# ADR-0036 — Product-neutral entitlement applicability and execution-time enforcement

Status: Proposed
Date: 2026-09-24

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
but the request must contain these neutral fields: subject identity; installation identity; feature;
Skill identity and version when applicable; capability; requested resource and amount/limit; node;
target; and requested validity/constraint context. Identifiers are opaque strings or existing public
identity types; runtime does not interpret product or provider meanings.

The decision is a closed result with `Allowed`, `Denied`, or `NotApplicable`; a reason code; a
source category; effective limits/constraints; and validity metadata (`ValidFrom`, `ValidUntil`, and
an authority/freshness identifier suitable for correlation but not secret material). `NotApplicable`
is valid only when the trusted applicability requirement was not governed. A provider cannot return
it for a governed request; that is malformed and denied.

The provider returns the final interpreted authorization decision, including resource consumption or
limit evaluation. Runtime validates that decision against the request and consumes no raw grants.
This gives one authority ownership of entitlement interpretation and prevents duplicated, divergent
limit logic in Runtime.

### Provider boundary and lifetime

M2 will expose one node-scoped, dependency-free entitlement service interface that accepts the
neutral request and returns the neutral decision asynchronously. It has no product-specific types,
raw credential/token fields, mutable global state or package service-locator access. Runtime resolves
the service per executing node, consistent with A3/A5; the host owns provider registration and
lifetime. A provider cannot widen policy, approval, the delegation envelope, package trust, or the
applicability boundary.

### Execution-time enforcement

M3 inserts exactly one entitlement evaluation in `AgentRunner.ExecuteStepAsync`: after argument
validation, delegation-envelope enforcement, policy evaluation and any required human approval and
approval binding have established the intended operation; immediately before durable side-effect
intent is written and before `ExecuteWithTimeoutAsync` invokes the tool. The same shared method is
used by ordinary, resumed, prepared-plan and delegated execution paths, so no secondary execution
path may bypass it.

For a governed request, only a newly obtained, request-matching, currently valid `Allowed` decision
permits execution. Therefore approval followed by entitlement revocation produces no governed tool
execution. Entitlement never replaces policy or approval: a governed side effect requires every
applicable gate to pass, and a policy denial remains final.

### Time, cache and resource semantics

Runtime uses its injected `TimeProvider` as the authoritative wall-clock source for comparison;
providers state their validity window in UTC. There is no positive clock-skew tolerance or grace
extension: an allow is usable only when `ValidFrom <= now <= ValidUntil`. A future, expired,
missing-window, malformed, revoked, or request-mismatched decision is denied. Matching includes
subject, installation, feature, Skill and version, capability, node, target, requested resource and
constraints. A detected wall-clock rollback invalidates cached authority and requires a fresh
provider decision; it never extends an allow.

A cache may avoid a provider round trip only while its decision remains within the explicit validity
window and satisfies the same request/constraint checks. It is bounded execution authority, never an
indefinite allow. Offline use is therefore denied for governed work unless a still-valid cached final
decision is available; this ADR invents no grace period. Provider absence or failure, including an
invalid result, fails closed only for governed requests.

Resource requests are part of the bound request. The provider's final allow must reflect the requested
amount and any remaining limit. Exhaustion or an unrecognized resource/constraint is denied. A later
implementation must make consumption/reservation semantics atomic at the provider boundary where
needed; Runtime must not independently decrement opaque allowances.

### Resumption, retry, replanning, delegation and running work

An entitlement decision is per execution attempt, never persisted as reusable authority. Resume,
retry, replan and delegated execution reconstruct the request and obtain a new decision at their own
execution point. A previously allowed decision cannot authorize a later attempt. Delegation envelopes
only reduce authority and cannot carry an entitlement allow across roles or retries.

Revocation cannot undo a completed effect. It also cannot promise cancellation of an irreversible
native operation already inside `ExecuteWithTimeoutAsync`; current architecture must record and
reconcile such an outcome through the existing timeout/verification and delegation journal semantics.
It does prevent subsequent not-yet-executed governed effects. Verification of an already executed
effect remains meaningful and must be attempted as ADR-0016 requires.

### Reads and verification

Denial of a governed mutation does not suppress the read evidence required to verify or reconcile an
effect that may already have happened. Runtime-mandated verification remains independently
meaningful, as in ADR-0016. A read can itself be governed only when its own explicit applicability
requirement says so; mutation entitlement does not silently propagate to it. If a governed read is
denied, verification reports inconclusive rather than confirmed and audit explains the denial.

### Audit and public/private boundary

M2/M3 will audit a bounded neutral entitlement record correlated with the tool call: applicability,
decision, source category, reason code, relevant non-sensitive constraints/limits and validity state.
It must never include raw license/token material, provider payloads, credentials, signatures or
unnecessary fingerprints. Existing policy, approval, tool outcome and verification records remain
separate, preserving their meanings.

The public surface owns only neutral contracts and generic enforcement. Product-specific business
rules, provider implementations, credential formats, activation rules and product-specific Skills
remain outside this repository. Runtime names none of them.

## Threat and bypass analysis

| Threat | Mitigation |
|---|---|
| Missing provider locks all OSS | Explicit not-governed applicability bypasses provider lookup. |
| Provider failure becomes not-applicable | Governed failure is denied; only trusted structural assignment can be not-applicable. |
| Stale allow or indefinite cache | Per-attempt check, bounded UTC validity and rollback invalidation. |
| Revocation after approval | Check occurs after approval binding and immediately before invocation. |
| Resume, retry or replan bypass | No decision persists as authority; every attempt re-evaluates. |
| Delegation bypass | Shared `ExecuteStepAsync` enforcement after envelope reduction. |
| Verification accidentally blocked | Mutation denial does not govern verification reads; separately governed reads become inconclusive. |
| Audit secret leakage | Audit only neutral outcome metadata; exclude raw provider material and credentials. |
| Ambiguous applicability authorizes execution | Missing/ambiguous governed assignment fails closed. |
| Ambiguous applicability denies all OSS | Explicit not-governed assignment is independent of provider state. |
| Clock rollback extends authority | `TimeProvider` check invalidates cached authority on rollback. |

## Alternatives considered

- **Check during planning only or approval only.** Rejected: both create a revocation/TOCTOU gap
  before side effects and cannot safely authorize resume or retry.
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

M2 adds the dependency-free, serializable host-stamped applicability/requirement, request, decision,
validity/constraint, source-category, reason-code and node-scoped service contracts; it also adds
public-surface and round-trip tests with no provider implementation. M3 registers the service through
the host and adds the single `ExecuteStepAsync` enforcement point described above, including neutral
audit records.

M3 acceptance tests must cover: valid allow; expired, future and revoked authority; wrong subject,
installation, feature, Skill, version, capability, node and target; resource exhaustion; provider
missing/failure; cache/offline; clock rollback; post-approval revocation; not-applicable standalone
OSS; stale resume/retry decision; delegation; and verification/read behavior. They must prove denied
governed mutations never invoke the tool, rather than merely asserting a returned status.

This proposal preserves dependency-free Abstractions, package isolation, runtime product neutrality,
the model-proposes/runtime-decides boundary, independent policy and approval authority, post-action
verification, and non-bypassable delegated execution.
