# 05 — Workflow

## Before writing code

1. Follow [`00-bootstrap.md`](00-bootstrap.md): read [`00-project-spec.md`](00-project-spec.md) for scope and, for the task, the architecture
   contracts in [`docs/architecture/current/`](../docs/architecture/current/) that the bootstrap routes you to.
2. Identify which roadmap version the task belongs to. If it belongs to a later one, stop and
   say so (see *Scope discipline* below).
3. Check the current contracts (binding rules and *Pending decisions*) before proposing any architectural alternative; do not re-open a settled rule without new information.
4. If the task touches the core, write the failing test first.

## Scope discipline

Build the current version. Not the next one.

The contract may accommodate future versions — that is what the architecture contracts in
[`docs/architecture/current/`](../docs/architecture/current/) are for. The implementation must not
anticipate them. Concretely: no plugin loader while implementing V0.5, no approval flow while
implementing V0.2, no streaming while implementing V0.1.

A task that appears to require work from a later version is a signal to stop and ask. It is
usually a sign that the contract is missing something small, not that the roadmap is wrong.

## Task state and synchronization

Every task has a `BOPS-NNNN` id and one entry in `agentic/_tasks/register.json`; `agentic/_tasks/PROCESS.md` defines the status
vocabulary, the validation levels and the mandatory synchronization of register, GitHub Issue, Project #6 and `HANDOFF.md` on every
status change. An unauthorized task is never started; `DONE` requires every gate, not a merge. `python scripts/governance/validate_register.py`
is part of CI.

## Branches and commits

Trunk-based. Short-lived branches off `main`, merged quickly.

```
feat/v0.3-policy-engine
fix/registry-platform-filter
docs/operator-configuration-update
```

Conventional Commits, imperative mood, in English:

```
feat(policy): reject Critical configured as anything but forbidden
fix(runtime): treat tool timeout as a distinct outcome
test(packages): add /proc/meminfo parsing against a real container
docs(architecture): clarify the package loading boundary
refactor(abstractions): replace object? arguments with ToolArguments
```

Scopes follow the project layout: `abstractions`, `runtime`, `policy`, `memory`, `audit`,
`cli`, `packages`, `providers`, `ci`, `docs`.

The body explains *why*. The diff already explains what.

## When a structural decision is needed

Architectural decisions are no longer written as ADRs. A change to a structural decision or a safety invariant is a change to a current
architecture contract in [`docs/architecture/current/`](../docs/architecture/current/), and it is made **only with the operator's approval**,
never as a side effect of a feature task. Stop and ask before any change that:

- alters a type in `bOps.Abstractions`;
- changes how packages are loaded, isolated, trusted or identified;
- changes the risk model, the policy semantics, or the verification semantics;
- adds a dependency to the core;
- changes the execution topology or the audit schema;
- reverses or amends a binding rule of a contract.

Everything else does not need a contract change. A decision that depends on an open operator decision (see `register.json` `decisions` and the
*Pending decisions — non-binding* section of each contract) stays pending; the code is not proof of acceptance. Task scope and due dates for planned
work are in the [consolidated roadmap](_plans/2026-09-16-consolidated-roadmap.md), not here.

## Definition of done

A change is done when all of the following are true:

- Tests exist at the discipline required for the layer touched, and they pass on every
  platform the change affects.
- The build is warning-free under `TreatWarningsAsErrors`.
- No new suppression, or a documented one in `docs/architecture/suppressions.md`.
- Every new tool has a manifest, a risk level, and — if not `Read` — a `VerificationSpec` and
  an `IVerifiableTool` implementation.
- Every new outcome path emits an audit event.
- If the change was structural (the list above), the operator approved the matching contract change.
- The public surface of `bOps.Abstractions` is documented if it changed.
- Nothing dead was left behind: no unused code, no compatibility shim, no commented-out block.

## Reporting work

State what changed and what remains. Do not claim a feature works because the tests pass
unless the tests actually exercise the feature — for anything touching a real machine, say
explicitly whether it was run against one or only against a test double.

If a rule in `agentic/` blocked the task, say which one and what you propose. Do not route
around it and mention it afterwards.

## Things to never do without being asked

- Push, force-push, or open a PR.
- Commit a secret, or anything matching a key pattern, even in a test fixture.
- Add a dependency to `bOps.Abstractions`. It stays dependency-free, permanently.
- Change a normative file in `agentic/` (`00`–`07`, the current architecture contracts, `PROCESS.md` rules) as part of a feature task. Recording task status
  in `agentic/_tasks/register.json`, the task packet checklist or `HANDOFF.md` is bookkeeping, not a normative change, and is required.
- Read or revive files under `agentic/obsolete/` as active scope. Historical research must be
  explicitly requested by an active task.
- Introduce a framework that hides or takes over the **bOps operational authority state machine**.
  Microsoft Agent Framework is the reference framework for generic agent infrastructure ([Contract 01](../docs/architecture/current/01-maf-agent-infrastructure.md));
  using its context/history/compaction/model plumbing is expected. Planning/execution state, exact
  tool authority, policy, entitlement, mutation safety, verification, evidence semantics and recovery
  remain explicit bOps responsibilities unless an operator-approved contract change moves a named boundary.
