# agentic/ — binding rules for AI coding agents

These documents are **normative**. An agent working on this repository starts with
[`00-bootstrap.md`](00-bootstrap.md), then reads [`00-project-spec.md`](00-project-spec.md),
[`01-architecture-rules.md`](01-architecture-rules.md) and
[`03-security-rules.md`](03-security-rules.md) before writing code, and consults the others when
the task touches their subject.

They are written in English because they are parsed alongside the code, and the codebase —
identifiers, comments, commit messages and ADRs — is English.

## Files

| File | Read it when |
|---|---|
| [`00-bootstrap.md`](00-bootstrap.md) | Always first. Stable routing, active milestone and validation baseline. |
| [`00-project-spec.md`](00-project-spec.md) | Always. What bOps is, what it is not, what is in scope for the current version. |
| [`01-architecture-rules.md`](01-architecture-rules.md) | Always. Structural invariants and the authoritative contract definitions. |
| [`02-coding-standards.md`](02-coding-standards.md) | Writing or reviewing any C#. |
| [`03-security-rules.md`](03-security-rules.md) | Always. Non-negotiable safety behaviour of the runtime. |
| [`04-testing-rules.md`](04-testing-rules.md) | Writing tests, or any code that needs them (all of it). |
| [`05-workflow.md`](05-workflow.md) | Committing, opening a PR, deciding whether an ADR is required. |
| [`06-decisions.md`](06-decisions.md) | Before proposing an architectural alternative. Settled questions live here. |
| [`07-plan-corrections.md`](07-plan-corrections.md) | Historical corrections retained for provenance; not active scope. |
| [`_plans/2026-09-16-consolidated-roadmap.md`](_plans/2026-09-16-consolidated-roadmap.md) | The single active roadmap, status, sequencing and gates. |
| [`_tasks/README.md`](_tasks/README.md) | Executable task index; select only the first dependency-ready task. |

## Precedence

When active sources conflict, the later entry wins:

1. The active consolidated roadmap and task file establish scope and execution detail.
2. These normative documents override a plan or task.
3. `docs/architecture/adr/` — an accepted ADR supersedes everything above it on its subject.

`obsolete/` is outside this precedence chain. It contains migrated history only and must be ignored
unless an active task explicitly requests historical research.

## Rules for agents about these rules

- **Never silently deviate.** If a rule blocks the task, stop and say which rule and why.
  Do not work around it and mention it afterwards.
- **Never bootstrap from `obsolete/`.** Archived plans, specifications and tasks cannot authorize
  work or reopen a completed milestone.
- **Never edit these files as part of a feature task.** Changing a rule is its own change,
  with its own justification.
- **Never re-open a settled decision** without new information. `06-decisions.md` records
  what was considered and rejected; re-proposing a rejected option without addressing the
  recorded reason is wasted work.
