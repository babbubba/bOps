# 05 — Workflow

## Before writing code

1. Read [`00-project-spec.md`](00-project-spec.md), [`01-architecture-rules.md`](01-architecture-rules.md)
   and [`03-security-rules.md`](03-security-rules.md).
2. Identify which roadmap version the task belongs to. If it belongs to a later one, stop and
   say so (see *Scope discipline* below).
3. Check [`06-decisions.md`](06-decisions.md) before proposing any architectural alternative.
4. If the task touches the core, write the failing test first.

## Scope discipline

Build the current version. Not the next one.

The contract may accommodate future versions — that is what
[`01-architecture-rules.md`](01-architecture-rules.md) is for. The implementation must not
anticipate them. Concretely: no plugin loader while implementing V0.5, no approval flow while
implementing V0.2, no streaming while implementing V0.1.

A task that appears to require work from a later version is a signal to stop and ask. It is
usually a sign that the contract is missing something small, not that the roadmap is wrong.

## Branches and commits

Trunk-based. Short-lived branches off `main`, merged quickly.

```
feat/v0.3-policy-engine
fix/registry-platform-filter
docs/adr-0006-packages
```

Conventional Commits, imperative mood, in English:

```
feat(policy): reject Critical configured as anything but forbidden
fix(runtime): treat tool timeout as a distinct outcome
test(packages): add /proc/meminfo parsing against a real container
docs(adr): record the decision to drop Native AOT
refactor(abstractions): replace object? arguments with ToolArguments
```

Scopes follow the project layout: `abstractions`, `runtime`, `policy`, `memory`, `audit`,
`cli`, `packages`, `providers`, `ci`, `docs`.

The body explains *why*. The diff already explains what.

## When an ADR is required

Write one in `docs/architecture/adr/NNNN-title.md` before the code, for any change that:

- alters a type in `bOps.Abstractions`;
- changes how packages are loaded, isolated, trusted or identified;
- changes the risk model, the policy semantics, or the verification semantics;
- adds a dependency to the core;
- changes the execution topology or the audit schema;
- reverses or amends an entry in [`06-decisions.md`](06-decisions.md).

Everything else does not need one. An ADR that records an obvious choice is noise; the ones
that matter are the ones with a real alternative that was rejected for a reason worth
remembering.

Format: context, decision, alternatives considered, consequences. Status is
`Proposed` → `Accepted` → possibly `Superseded by ADR-NNNN`. An accepted ADR is never edited
to change its meaning — it is superseded by a new one.

The first ADRs to exist, per the plan and the decisions taken (still unwritten as of V0.9 — a
real, tracked backlog item, not a rule violation to fix silently as a side effect of unrelated
work):

| | |
|---|---|
| ADR-0001 | A project-owned `IChatModel` rather than depending on `Microsoft.Extensions.AI` throughout the runtime |
| ADR-0002 | Five risk levels, and `Forbidden` as an unbypassable invariant |
| ADR-0005 | One shared OpenAI-compatible adapter, with Anthropic as the only native adapter |
| ADR-0006 | Tools, operating systems and LLM providers are all packages behind the same contract |
| ADR-0011 | Local-only execution now, with a contract shaped for remote agents later |
| ADR-0012 | Dynamic package loading over Native AOT |

ADR-0013 and later records already exist under `docs/architecture/adr/`. The consolidated roadmap
pre-commits the following remaining ADR subjects. Assign a number only when an ADR is written:

| | |
|---|---|
| Open-core, two repositories, and the public/private boundary | Due at V0.9.1 |
| Package loader, manifest shape, and the activation boundary/trust model | Due at V0.10 |
| Freezing the `bOps.Abstractions` 1.0 surface | Due at V1.0 |
| Skill/capability/evidence contracts and the immutable execution plan | Due at V1.1 |
| Skill execution interfaces and restricted same-package invocation | Due at V1.1-A |
| Exact filesystem inventory and hash-bound destructive preflight | Due at V1.1-C/D |
| Web package outbound-network and SSRF boundary | Due at V1.1-E |
| Encrypted local vault, master-key handling and rotation | Due at V1.1-G |
| Multi-agent orchestration and privilege-reducing delegation | Due at V1.2 |
| Entitlement, evaluated locally and remotely at the execution point | Due at V1.3 |
| Mutating plugin lifecycle and upload | Due at V1.3 |
| Node–Control Plane transport and the multi-tenant model | Due at V1.4 |

## Definition of done

A change is done when all of the following are true:

- Tests exist at the discipline required for the layer touched, and they pass on every
  platform the change affects.
- The build is warning-free under `TreatWarningsAsErrors`.
- No new suppression, or a documented one in `docs/architecture/suppressions.md`.
- Every new tool has a manifest, a risk level, and — if not `Read` — a `VerificationSpec` and
  an `IVerifiableTool` implementation.
- Every new outcome path emits an audit event.
- An ADR exists if the list above required one.
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
- Change a file in `agentic/` as part of a feature task.
- Read or revive files under `agentic/obsolete/` as active scope. Historical research must be
  explicitly requested by an active task.
- Introduce a framework that hides the agent loop. Owning that loop explicitly is a project
  decision, not an oversight.
