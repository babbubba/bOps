# ADR-0002 — Five risk levels, and `Forbidden` as an unbypassable invariant for `Critical`

Status: Accepted

**Backfill note.** Written at V0.9.1 to record a decision made and implemented since V0.1
(`RiskLevel` itself) and hardened at V0.3 (`PolicyEngine`, ADR-0015). `agentic/05-workflow.md`
has listed this ADR as owed since the project's first commit; this file fills that gap without
changing the decision or the code.

## Context

The archived original plan's risk model used `Critical` as "always forbidden" descriptively, but
left the invariant enforceable only by convention: nothing stopped a future `policy.yaml` (or a
future maintainer) from configuring `Critical` as `approval` or even `automatic`, and nothing in
the reference implementation validated against that (`agentic/07-plan-corrections.md`, security
gaps: "`policy.yaml` is user-editable and could assign [Critical] another mode. Invariant or
default is left ambiguous"). Principle 2 (`agentic/00-project-spec.md`) also required risk to be
a declarative property of the tool, decided by policy, not a hardcoded command blacklist.

## Decision

Five ordered risk levels — `Read`, `Low`, `Medium`, `High`, `Critical` — declared once per tool
in its `ToolManifest` (rule B2), never inferred from the tool's name or arguments. Three
authorization modes — `Automatic`, `Approval`, `Forbidden` — are what `IPolicyEngine` maps a
risk level (or a specific tool override, or a package ceiling) onto (rule B7). `Critical` is
`Forbidden`, unconditionally, and cannot be configured otherwise — enforced twice, not once:
`PolicyConfigLoader` rejects a `policy.yaml` document that assigns `Critical` anything else at
load time (a clear, loud error, not a silent coercion), and `PolicyEngine.Evaluate` checks
`RiskLevel.Critical` first, before any config lookup, so even a hypothetical bug that let a bad
config past the loader still cannot produce a different answer at evaluation time (rule S3;
implemented fully at V0.3, `docs/architecture/adr/0015-policy-engine-approval-flow-and-audit-hash-chaining.md`).

## Alternatives considered

- **Fewer levels (e.g., three: Safe/Risky/Dangerous).** Rejected: collapses meaningfully
  different operator decisions — "runs automatically" vs. "needs a human, but is fine to grant"
  vs. "never happens" — into too few buckets to express the per-package ceiling model rule S3
  also requires (a ceiling has to be able to land on any of the five levels to be useful).
- **A command blacklist instead of a declarative risk property.** This was the original plan's
  approach for verification (a hardcoded map of tool names) and would have been the same mistake
  applied to risk: it makes every third-party tool's risk a decision the *core* has to know
  about, violating principle 7 and rule A1 (the core never names a tool).
- **`Critical` configurable with an explicit "break-glass" override.** Rejected outright and
  permanently: `agentic/03-security-rules.md` rule S3 states the documented answer to "but I
  need it" is that the operation happens outside bOps, by hand — a technical override defeats
  the entire point of having an unbypassable level.

## Consequences

A tool's risk is legible to an operator before they ever run bOps (it's in the manifest, and
`ToolManifest.Verification` for anything above `Read`). No tool shipped through V0.9 declares
`Critical` — the first ones that might (irreversible filesystem or database operations) are
scoped for V1.6/V1.8 under the consolidated roadmap, and this invariant is exactly what makes
those versions safe to build without revisiting this decision.
