# ADR-0031 — Per-role envelope requirements: amends ADR-0030 §3

Status: Accepted
Accepted: 2026-09-19 by the operator

Amends ADR-0030 §3 (and where §1's role profiles live). The three choices below were made by the
operator on 2026-09-19 when V1.2-C started and are recorded as D-027. Everything else in ADR-0030
stands. ADR-0030 carries an `Amended by ADR-0031` pointer; its text is not edited
(`agentic/05-workflow.md`: an accepted ADR is never edited to change its meaning).

## Context

ADR-0030 §3 says: *"An empty intersection in any dimension is a denial."* The V1.2-B contracts
represent an empty set as "nothing permitted", never as "unrestricted", so they support either reading
and the question was left to V1.2-C (`agentic/_tasks/2026-09-18-v1.2-b-contracts.md`, "Open question
for V1.2-C").

Read literally, the sentence fails in two ways:

1. **Roles do not use every dimension.** Discovery, Diagnostic and Verification are read-only and
   never invoke a Capability; Remediation and Verification make no model call (ADR-0030 §2). A literal
   rule forces the operator to grant Skills, Capabilities or tokens to a role that cannot use them, which
   is authority nobody needs.
2. **It can block an approved plan.** Discovery and Diagnostic legitimately spend the token budget.
   After that the token dimension of Remediation and Verification is empty, so a literal rule would deny
   a plan the operator has already approved, for a resource those roles never consume.

Two facts about the current code also bound what an envelope can enforce. A tool manifest declares no
target or environment; only a step that realizes an ADR-0025 capability carries `Target`,
`Environment` and `BlastRadius` (`SkillExecutionScope`). And `BlastRadius` is `Single < Multiple <
Fleet`, so a ceiling can never be empty: the lowest value still permits one target.

## Decision

### 1. Each role classifies each dimension

For every role kind, each envelope dimension is one of:

- **Required** — must be non-empty after reduction. Otherwise the delegation is denied
  (`DelegationDenied`, audited with the role and the dimension).
- **Optional** — may be empty. Empty means nothing is permitted in that dimension, as in the B
  contracts. It is not a denial; the role simply cannot do what the dimension governs.
- **Not applicable** — the role structurally does not use it. The child's value is forced to empty (or
  zero) whatever the parent, the profile and the request say.

| Dimension | Discovery | Diagnostic | Remediation | Verification |
|---|---|---|---|---|
| Skills | not applicable | optional | required | not applicable |
| Capabilities | not applicable | optional | required | not applicable |
| Tools | required | required | required | required |
| Risk ceiling | fixed to `Read` | fixed to `Read` | required, above `Read` | fixed to `Read` |
| Blast-radius ceiling | minimum of the three ceilings (never empty) | same | same | same |
| Targets | required | required | required | required |
| Environments | required | required | required | required |
| Maintenance window | if the profile, the request and the parent's window are present and do not overlap: denied; absent means unrestricted | same | same | same |
| Steps | required, at least 1 | required | required | required |
| Tokens | required, at least 1 | required | not applicable (0) | not applicable (0) |
| Deadline | earliest of the three; already past when the root is derived: denied | same | same | same |

Notes on the table:

- **Diagnostic** with no Skill or Capability produces Findings only. No plan can be prepared and the run
  ends as a completed diagnosis (ADR-0030 §2). That is a result, not an error, which is why both are
  optional there.
- **Risk ceiling.** The read-only roles get `min(intersection, Read)` by structure. Remediation with a
  resulting ceiling of `Read` could execute nothing, so it is denied. A ceiling of `Critical` changes
  nothing: `Critical` stays `Forbidden` (S3).
- **Tools** covers any tool a step calls, Read or not (V1.2-B interpretation 3): a plan step whose tool
  is not in Remediation's set is denied even when its Capability is allowed.
- **Verification** needs neither Skills nor Capabilities: it evaluates the approved plan's declared
  `VerificationSpec` against Read evidence it gathers itself (ADR-0030 §5), and makes no model call.

### 2. Not applicable is structural, and removes authority only

Forcing a dimension to empty can only take authority away, so `child ⊆ parent ∩ profile ∩ request`
still holds in every dimension and reduction stays monotone (ADR-0030 §3 is preserved).

- A profile that grants a non-empty value in a dimension that is not applicable to that role (Skills to
  Verification, a positive token budget to Remediation) is **malformed**. Loading it denies delegation
  as a whole (ADR-0030 §1, S3). It is never silently ignored.
- The operator's request is a single narrowing for the objective, not per role. A request that names
  Skills is not rejected because Verification does not use them; that role's dimension is simply forced
  empty.
- A model call or a Capability invocation in a role for which the dimension is not applicable is denied
  at enforcement, because empty means nothing permitted.

### 3. Budgets and the deadline: grant versus exhaustion

The table classifies what the profile and the request **grant**. A required budget granted as zero, a
window that does not intersect, or a deadline already past when the root envelope is derived is a
`DelegationDenied`. Once a run has started, using up a granted budget or reaching the deadline is not a
denial of authority: it ends the run as `BudgetExceeded` or `DeadlineExceeded`, exactly as ADR-0030 §6
states. Reserving a child's budget from the parent's remaining amount, and reconciling it, stay in
V1.2-E. Because tokens are not applicable to Remediation and Verification, exhausting them cannot block
an approved plan or its verification.

### 4. What the envelope enforces per step, stated plainly

ADR-0030 §3 is unchanged: the envelope is checked in `ExecuteStepAsync` before `IPolicyEngine.Evaluate`
and can only deny. Per step it checks the tool against Tools, the risk against the ceiling and the time
against the window, at execution and not only at derivation. For a step that carries a skill scope it
also checks the Skill, Capability, target, environment and blast radius.

Because a tool declares no target, the runtime cannot match one for a call that carries none:

- A model-proposed **Read** call is bound by Tools, the `Read` ceiling, the window and the budgets.
  Targets and Environments are recorded in the audit envelope and placed in `PolicyContext`, but are not
  matched per call. What a Read tool touches stays bound by its own argument validation and path policy
  (S2, S11), exactly as outside delegation.
- A step above `Read` that carries **no skill scope** is denied in a delegated run. Only Remediation
  acts, and only by executing the approved plan (ADR-0030 §2), whose steps carry their scope. This closes
  the path by which a delegated role could run a non-Read tool outside a plan.

This is a stated limit in the same sense as ADR-0030's Consequences: an envelope restricts calls that
go through the runtime, and it does not say what a Read tool does with its arguments.

### 5. Role profiles and the operator's request live in the SDK

`bOps.Runtime` must not reference `bOps.Policy`, so the contracts go in `bOps.Abstractions`, additive,
product-neutral and dependency-free, with round-trip tests (A2):

- a **role profile** per `AgentRoleKind`, carrying the dimensions of §1. Time is expressed as a maximum
  duration; the absolute deadline is derived at start as the earliest of the parent's deadline, the
  request's deadline and start plus that duration;
- the operator's **authority request**: narrowing only, every dimension optional, absent meaning no
  narrowing. It can never grant;
- a read-only **source** interface the host implements. `bOps.Policy` implements it from the
  `delegation` section of `policy.yaml`; `bOps.Runtime` depends on the interface only.

A role with no profile, or a malformed section, yields no profile and denies delegation (S3);
`SafeDefault` ships none. The type names are fixed in V1.2-C. `bOps.Abstractions` moves to
`1.2.0-preview.2`. The objective and the idempotency key are not part of the authority request; they
belong to the delegation start request (V1.2-D).

## Alternatives considered

- **Literal reading** (every dimension non-empty for every role). Rejected: it grants authority no role
  uses, and an exhausted token budget would block an operator-approved plan.
- **Requirement declared per dimension in the profile.** Rejected: it puts an architectural fact (that
  Verification makes no model call) into operator configuration, and a loosened profile moves the failure
  from delegation start to the middle of a run.
- **Match targets and environments per Read call by adding target metadata to tool manifests.**
  Deferred: it is an SDK change across every package to bound read-only calls. Revisit by ADR if a
  package needs it.
- **Runtime-owned profile types with a host adapter.** Rejected: two models of one concept and an
  adapter in each host (Api and Cli).
- **Clarification section inside ADR-0030.** Rejected: it changes an accepted ADR's meaning, which
  `agentic/05-workflow.md` forbids.

## Consequences

- Operators write smaller profiles, listing only what a role uses. Granting something a role cannot use
  is rejected when the profile loads, not discovered mid-run.
- Exhausting tokens cannot stop Remediation or Verification.
- The table is both documentation and code. Changing it needs an ADR. V1.2-C tests it table-driven over
  every role and dimension, and a property test shows reduction is monotone with the forced values.
- The SDK grows by three small types in `1.2.0-preview.2`, frozen once 1.2 ships.
- Targets and Environments are not matched for plain Read calls. This is a known limit, recorded here and
  to be repeated in the delegation threat model (V1.2-L).
