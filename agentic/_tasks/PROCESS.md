# Task governance process

Introduced by PROJECT-GOVERNANCE-01 (BOPS-0118). This file defines how task state is kept; it is a
process document, not a product rule. Updating a task's status, `HANDOFF.md` or the register is
bookkeeping and is part of every task. Changing the *rules* in this file, in `agentic/0*.md` or in an
accepted ADR is a normative change and needs operator approval.

## 1. Authoritative source per fact

| Fact | Single source | Everything else |
|---|---|---|
| Task identity, status, priority, dependencies, validation level, links to Issue/PR | [`register.json`](register.json) | GitHub Issue and Project #6 are projections; they never lead |
| Executable specification of a task | the task packet (`packet`), or the `spec` reference until one exists | the Issue body links to it, never copies it |
| Goals, milestones, delivery gates, sequencing | [consolidated roadmap](../_plans/2026-09-16-consolidated-roadmap.md) and the active plans | no status tables elsewhere |
| Current session state (active task, blockers, next step) | [`HANDOFF.md`](../../HANDOFF.md), at most ~100 lines | history goes to `docs/handoff-archive/` |
| Architectural decisions | ADRs and [`06-decisions.md`](../06-decisions.md); pending operator decisions are listed in `register.json` `decisions` | an ADR becomes Accepted only by the operator, never by merging |
| Milestone quality evidence | `agentic/_quality/` | a milestone report is evidence for a gate, not a status |

Do not copy a status into any other document. Link to the register instead.

## 2. Progressive identifiers

`BOPS-NNNN`: global, increasing, immutable, never reused, independent of GitHub Issue numbers.

- The number records the order of creation, not priority or implementation order.
- A new task takes `numbering.next` from the register, and the register increments it in the same commit.
- Numbers survive deferral, blocking, completion, renaming and milestone moves. A task is never deleted: use
  `SUPERSEDED` with a note.
- The historical technical id (`AF-05H2-R2`, `LIVE-05`, ...) stays in `legacy_id`. New tasks choose a
  `legacy_id` that is unique and stable.
- Issue and task-packet titles start with the progressive: `BOPS-0111 | DIAG-ASSURE-01 — Evidence assurance projection`.
  Existing packet files are not renamed; the register points to them.
- The migrated mapping is `PROVISIONAL` until the operator approves it (`numbering.state`); only then it becomes `FROZEN` and the validator enforces the hash
  of `(id, legacy_id, created.at)`. Every task carries `created.order_certainty`: `verified` (distinct, Git-proven instant) or `conventional`
  (order within one commit fixed by the tie-break rule, not chronologically proven). The progressive of a conventional task is kept, but it must not be read
  as proof of birth order.
- Historical creation instants come from Git (first add of the packet, else first mention) or the document
  date. `created.basis` says which, and `created.order_certainty` says when the order inside one commit is a
  tie-break rule, not a proof. Nothing is inferred from last-modified dates.

## 3. Status vocabulary and validation

| Status | Board column | Meaning |
|---|---|---|
| `PLANNED` | Backlog | defined; dependencies or specification not ready |
| `READY` | Ready | dependencies `DONE` and a spec/packet exists; may still lack `authorized` |
| `IN_PROGRESS` | In Progress | development has actually started; requires `authorized: true` and the carrying `branch`; several tasks may run in parallel on distinct branches/sessions |
| `VALIDATION` | Validation | implementation done, at least one gate open (`validation.open_gates`) |
| `BLOCKED` | Blocked | cannot proceed; `blocked_reason` is mandatory |
| `DEFERRED` | Deferred | consciously postponed; `reopen_condition` is mandatory |
| `DONE` | Done | every required gate achieved, no open gate |
| `SUPERSEDED` | (not on the board) | replaced or withdrawn; kept for history; `note` says by what |

Progress and validation are separate. `validation.achieved` / `required` use: `OFFLINE_TESTS`,
`REPLAY_REAL_MODEL`, `LIVE_REAL_TOOLS`, `CI_VERIFIED`, `OPERATOR_GATE`. No level = nothing achieved. A merge
achieves `CI_VERIFIED` only when CI of the merge commit is verified; it never implies `DONE`.

`READY` is not permission to start. Only `authorized: true`, set from an explicit operator decision, is.

Parallel work: any number of authorized tasks may be `IN_PROGRESS`, each on its own branch/session (`branch` in the register, unique among
`IN_PROGRESS` tasks) and, by convention, on disjoint files. Each branch's `HANDOFF.md` names exactly one *current task* (the machine-readable
block); other in-progress tasks are found with `--summary`, never listed in HANDOFF.

Milestones carry three separate dimensions plus an overall status: `implementation` (COMPLETE/PARTIAL/NOT_STARTED), `validation`
(COMPLETE/PARTIAL/NONE) and `release` (RELEASED/PRERELEASED/NOT_RELEASED/NOT_APPLICABLE). A milestone is `DONE` only when implementation and validation
are COMPLETE and release is RELEASED, PRERELEASED or NOT_APPLICABLE; a gate is never marked passed retroactively without evidence.

## 4. Mandatory synchronization

Every status change is reflected in all four places in the same working session, in this order:

1. `register.json` (status, gates, links, `blocked_reason`/`reopen_condition`, PR and commit references);
2. the GitHub Issue (state and a short comment; the Issue is closed only when the task is `DONE`);
3. the Status field of Project #6 (use the column in the table above);
4. `HANDOFF.md` when the active task, blocker or next step changes.

**Before starting a task.** Read the register, the roadmap and HANDOFF. Identify the task by its `BOPS-NNNN`.
Open its Issue and find its Project item. Check dependencies and `authorized`. Move to `IN_PROGRESS` only when
work really starts, and tell the operator the id and title. Never start an unauthorized task.

**During development.** Record scope changes, impediments, commits and PRs on the task. Never change priority,
milestone or dependencies without operator authorization. A blocked task shows `BLOCKED` with a readable
reason on the board; a postponed one shows `DEFERRED`.

**Delivery.** `IN_PROGRESS → VALIDATION → DONE`. `VALIDATION` when the implementation is ready for its gates.
`DONE` only after: acceptance criteria verified, required tests done, PR merged when one is planned, CI of the
merge commit verified, any mandatory live validation done and any operator approval recorded. If a gate stays
open after the merge, the task stays in `VALIDATION`.

PR bodies reference the task (`BOPS-NNNN`, `Refs #issue`) and must not use closing keywords (`Closes`, `Fixes`):
closing the Issue is a register-driven step. Project #6 built-in workflows that set `Done` when an Issue is
closed or a PR is merged would bypass the gates; disable them in the Project settings (Workflows). They can only
be edited in the GitHub web UI as far as the CLI/GraphQL surface is known; verify before relying on this.

**When GitHub is unreachable or permissions are missing.** Do not claim a successful remote update. Keep the
register change, set `sync_pending` on the task (list of the remote steps still owed), say so in HANDOFF and to
the operator, and retry later. A task is not administratively closed while its remote state is unreconciled,
unless the operator authorizes the exception. Every remote operation must be idempotent: look the Issue up by
the `BOPS-NNNN` title prefix before creating anything, and the Project item by Issue URL.

## 4b. Remote reconciliation: idempotent and conflict-detecting (PROJECT-GOVERNANCE-02)

The register leads, but a person may legitimately edit an Issue or drag a card. The synchronizer therefore never overwrites silently:

- Each synchronized task stores the last state it pushed (`sync`: Issue state/title, Project Status, timestamps). Reconciliation is three-way:
  register vs remote vs `sync`.
- Only the register changed → push it. Only the remote changed (Status dragged, Issue closed/edited by a person) → report a **conflict** and stop for that task;
  the operator decides whether the register or the remote wins. Both changed → conflict. Nothing changed → no-op.
- Dry-run is the default; writes need an explicit flag. Lookup precedes create (Issue by the `BOPS-NNNN` title prefix, Project item by Issue URL), so reruns
  create nothing new. The synchronizer never sets `DONE`, never closes an Issue by itself, never changes priority, milestone or dependencies.
- Status is the only field it may move automatically, and only along register-permitted transitions.

## 5. Checks

```bash
python scripts/governance/validate_register.py            # offline, no secrets; CI runs it on Windows and Linux
python scripts/governance/validate_register.py --summary  # counts, active task, READY/BLOCKED, pending decisions
```

The validator covers: unique, contiguous, frozen ids; legacy ids; closed status vocabulary; existing packets;
dependencies without cycles; `DONE` only with required gates and no open gate; at most one `IN_PROGRESS`; the
machine-readable block of `HANDOFF.md`. Comparing the register with Issues and Project #6 needs a token with the
`project` scope and is the job of PROJECT-GOVERNANCE-02 (dry-run first, never creates duplicates, never sets
`DONE`).

## 6. Project #6

Owner `babbubba`, <https://github.com/users/babbubba/projects/6>, linked to `babbubba/bOps`. Read-only inspection on 2026-10-21: title `DEV Plan` (not renamed), private,
no items, one default board view, Status options `Todo / In Progress / Done`, six built-in workflows enabled (including `Item closed` and `Pull request merged`, which
move items to Done and bypass the gates). Fields wanted:
`Status` (the seven columns above), `Priority` (P0–P3), `Milestone`, `Area`, `Effort`, `Task ID`, `Legacy ID`,
`Validation`, `Depends on`, `Packet`, `PR`; views: Kanban (by Status, `SUPERSEDED` and `DONE` older than the
current milestone hidden), Backlog table (sorted by Priority, Milestone, Task ID), Roadmap grouped by Milestone
without invented dates. Known divergence: `agentic/_quality/active-milestone.json` still says V1.3-H; it is recorded in `register.json` `known_divergences` (QM-1, BOPS-0117), checked by the validator,
and neither it nor any historical quality result is altered until reconciled. Only open or relevant tasks get an Issue; completed history stays in the register.
