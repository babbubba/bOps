# agentic/ — index

Rules, plans and task state for AI coding agents. **This file is a folder index, not a reading order and not a rule set.** The one entry point is
[`00-bootstrap.md`](00-bootstrap.md) (reached through `CLAUDE.md` or `AGENTS.md`); it defines the precedence of sources and what to read for a task.
Architectural decisions and safety invariants are in the five contracts in [`docs/architecture/current/`](../docs/architecture/current/), not here.

These files are written in English because they are parsed alongside the code.

| Path | What it is |
|---|---|
| [`00-bootstrap.md`](00-bootstrap.md) | The single bootstrap: precedence, task identification, contract routing, stop conditions |
| [`00-project-spec.md`](00-project-spec.md) | Product scope: what bOps is and is not, version discipline |
| [`02-coding-standards.md`](02-coding-standards.md) | Engineering rules for C# (scope: code) |
| [`04-testing-rules.md`](04-testing-rules.md) | Engineering rules for tests (scope: tests) |
| [`05-workflow.md`](05-workflow.md) | Commits, change control, definition of done (scope: workflow) |
| [`_tasks/PROCESS.md`](_tasks/PROCESS.md) | Task state and synchronization rules; [`_tasks/register.json`](_tasks/register.json) is the authoritative task state |
| [`_tasks/`](_tasks/README.md) | Task packets and the executable task index |
| [`_plans/2026-09-16-consolidated-roadmap.md`](_plans/2026-09-16-consolidated-roadmap.md) | The single active roadmap: scope, sequencing and gates (other files in `_plans/` are plans for specific work) |
| [`_quality/`](_quality/) | Milestone quality evidence |
| `01-architecture-rules.md`, `03-security-rules.md`, `06-decisions.md` | Non-normative compatibility indexes: they only resolve historical identifiers (A/B/C/D, S1–S11, D-NNN) to the contracts |
| [`07-plan-corrections.md`](07-plan-corrections.md) | Historical corrections retained for provenance; not active scope |
| `obsolete/` | History, outside every precedence chain; ignore unless an active task requests historical research |

## Selecting a task

Authoritative rules: [`_tasks/PROCESS.md`](_tasks/PROCESS.md). In short, a task may start only if it is `authorized: true` in the register and its
dependencies are `DONE`; `READY` is not permission to start. `HANDOFF.md` names the current task of the branch.

## Rules for agents about these files

- **Never silently deviate.** If a rule blocks the task, stop and say which rule and why.
- **Never bootstrap from `obsolete/`.** Archived plans, specifications and tasks cannot authorize work or reopen a completed milestone.
- **Never edit these files as part of a feature task.** Changing a rule is its own change, with its own justification and, for architecture, the operator's approval.
- **Never re-open a settled rule** without new information; re-proposing a rejected option without addressing the recorded reason is wasted work.
