# 00 — Agent bootstrap

The single entry point for coding agents. `CLAUDE.md` and `AGENTS.md` are short routers that point here.
This file says what to read and in which order of authority; it does not restate the rules it points to.

## What bOps is

bOps is a .NET 10 runtime that operates Windows and Linux machines through declarative tools, policy,
approval, post-action verification and complete audit. The LLM proposes; the runtime decides and executes.
Current milestone, scope and gates live in the [consolidated roadmap](_plans/2026-09-16-consolidated-roadmap.md),
the task register and `HANDOFF.md`, never in this file. Build the current roadmap version, not the next one.

## Precedence (the only chain)

When two sources disagree, the earlier one wins. Do not choose by date or by file name.

1. **Current architecture contracts** — exactly five files in [`docs/architecture/current/`](../docs/architecture/current/). They are the only source of
   approved structural decisions and safety invariants. Two contracts that conflict are a blocker: stop and ask the operator.
2. **Engineering and governance rules, each within its own scope** — [`02-coding-standards.md`](02-coding-standards.md) (code),
   [`04-testing-rules.md`](04-testing-rules.md) (tests), [`05-workflow.md`](05-workflow.md) (commits, PRs, change control),
   [`_tasks/PROCESS.md`](_tasks/PROCESS.md) (task state), [`00-project-spec.md`](00-project-spec.md) (product scope). They never create architecture decisions.
3. **Roadmap and the active plan.**
4. **The task packet.** It details execution; it cannot contradict a contract.
5. **Technical references** (`docs/*.md`, `docs/architecture/reference/`, `docs/security/`, `docs/knowledge/`) within their scope, subordinate to the contracts.
6. **History** — never an authority and not read by this bootstrap.

Decisions of the operator change a contract; until they do, a pending decision stays pending. A rule that appears only in a
*Pending decisions — non-binding* section of a contract is **not** a rule, even if the code already implements it.

## What to read

1. This file and the router you came from.
2. **The current task.** Run `python scripts/governance/validate_register.py --summary`; `HANDOFF.md` names the current task of this branch.
   Find it by its `BOPS-NNNN` id in [`_tasks/register.json`](_tasks/register.json) (`authorized: true`, dependencies `DONE`), then its packet under
   [`_tasks/`](_tasks/) and the plan it links. Read `_tasks/PROCESS.md` only when you change a task status.
3. **Only the contracts the task touches** (not all five by default):

   | If the task touches | Read |
   |---|---|
   | Model calls, MAF boundary, context, providers, fallback | [Contract 01](../docs/architecture/current/01-maf-agent-infrastructure.md) |
   | Tools, packages, policy, approval, secrets, audit, entitlement, delegation, plugins, filesystem/network safety — **always before adding or changing a tool** | [Contract 02](../docs/architecture/current/02-tools-safety-authority.md) |
   | `TaskState`, planning, resume, mutation journal, budgets, retry | [Contract 03](../docs/architecture/current/03-task-state-resume-mutations.md) |
   | Evidence, diagnosis, disclosure, grounding | [Contract 04](../docs/architecture/current/04-evidence-and-diagnosis.md) |
   | SDK, API, CLI, browser sessions, Knowledge, open-core | [Contract 05](../docs/architecture/current/05-interfaces-knowledge-and-hosts.md) |

   Each contract lists its binding rules (`Cn-Rmm`), their decision and implementation status, and the technical references that carry detail.
4. The engineering rule files of precedence level 2 that match the work (C# → `02`, tests → `04`, commit/PR → `05`).
5. **Technical references** only when the task touches that tool or area (the contract points to them).

Do **not** read, unless the operator asks for historical research: `agentic/obsolete/`, `docs/architecture/archive/`,
`docs/handoff-archive/`, `agentic/_plans/_migration-work/`, closed plans and reports. The compatibility indexes
(`docs/architecture/adr/README.md`, `agentic/01-architecture-rules.md`, `agentic/03-security-rules.md`, `agentic/06-decisions.md`) are lookup tables
for resolving an old identifier (`ADR-NNNN`, `A1`, `S5`, `D-029`) found in code or old documents; they are not part of the reading order.

## Stop and ask when

- The task is not `authorized: true` in the register, or a dependency is not `DONE`.
- Two contracts conflict, or a contract conflicts with the code or the task, and the evidence does not settle it.
- The change would alter a structural decision or a safety invariant. That is a change to a contract and needs operator approval; do not write an ADR and do not
  edit a contract as part of a feature task.
- A rule blocks the task: say which rule, what the task needed and what you propose. Never work around it and mention it afterwards.
- A needed change would touch something outside the task (another milestone, a Proposed decision such as D1, D4 or D5).

## Workflow

- Non-trivial work needs a plan under `agentic/_plans/` and a register task with a `BOPS-NNNN` id. Task state is kept only in the register and mirrored to the
  GitHub Issue, Project #6 and `HANDOFF.md` at every status change ([`_tasks/PROCESS.md`](_tasks/PROCESS.md)); never copy a status into another document.
- Update task checklists as real work completes, not in advance. Keep README, CHANGELOG, HANDOFF, plans, tasks and documentation aligned.
- Short-lived branches, Conventional Commits in English; never rewrite shared history to repair old commit messages.
- Never push, create a release tag or open a PR unless the operator explicitly asks.
- Core Runtime, Policy, Audit and verification changes are test-first; follow [`04-testing-rules.md`](04-testing-rules.md).
- Diagnose the root cause before changing behavior; keep application, infrastructure, configuration and documentation causes distinct.
  Developer credentials and machine configuration must not influence deterministic tests.

## Validation

Validate the affected surface and, for a milestone closeout, at minimum:

- `dotnet build bOps.slnx --configuration Release`
- `dotnet test bOps.slnx --configuration Release --no-build --filter "Category!=LiveModel"`
- Angular production build and headless tests when API/UI behavior changes
- `python scripts/governance/validate_register.py` when the register changes
- the relevant architecture, secret and documentation-alignment checks

Windows and Linux CI are authoritative for real-platform behavior.

## File discipline

- Code, identifiers, comments, commit messages and normative `agentic/` and contract files are English.
- Namespaces use `bOps.*`; tools use lowercase dotted names. One top-level type per file; no `Helper`, `Manager`, `Util` or `Common` dumping grounds.
- No temporary copies, backups, placeholder documents or unused scaffolding.
