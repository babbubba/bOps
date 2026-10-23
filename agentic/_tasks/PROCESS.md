# Task governance process

Introduced by PROJECT-GOVERNANCE-01 (BOPS-0118). This file defines how task state is kept; it is a
process document, not a product rule. Updating a task's status, `HANDOFF.md` or the register is
bookkeeping and is part of every task. Changing the rules in this file, in `agentic/0*.md`, or in a
current architecture contract is a normative change and requires operator approval.

## 1. Authoritative source per fact

| Fact | Single source | Everything else |
|---|---|---|
| Task identity, status, priority, dependencies, validation level, links to Issue/PR | [`register.json`](register.json) | GitHub Issue and Project #6 are projections; they never lead |
| Executable specification of a task | the task packet (`packet`), or the `spec` reference until one exists | the Issue body links to it, never copies it |
| Goals, milestones, delivery gates, sequencing | [consolidated roadmap](../_plans/2026-09-16-consolidated-roadmap.md) and the active plans | no status tables elsewhere |
| Current session state (active task, blockers, next step) | [`HANDOFF.md`](../../HANDOFF.md), at most ~100 lines | history goes to `docs/handoff-archive/` |
| Architectural decisions | the five current contracts in [`docs/architecture/current/`](../../docs/architecture/current/); pending operator decisions are listed in `register.json` `decisions` | a decision becomes binding only by the operator, never by merging |
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

## 4b. Remote reconciliation: `scripts/governance/sync_remote.py` (BOPS-0119)

The register leads, but a person may legitimately edit an Issue or drag a card. The tool never overwrites silently. Spec:
[`2026-10-21-bops-0119-project-governance-02-sync.md`](2026-10-21-bops-0119-project-governance-02-sync.md).

```bash
python scripts/governance/sync_remote.py                        # read-only plan (default)
python scripts/governance/sync_remote.py --apply                # write Status/field updates, re-read, verify
python scripts/governance/sync_remote.py --apply --close-done   # also close Issues of DONE tasks
python scripts/governance/sync_remote.py --report r.json --apply-register   # patch the local register baseline
```

- **Three-way Status.** register-desired vs remote vs `sync.status` (the baseline of the last verified sync). Only the register moved →
  update. Remote differs from the baseline (a person moved it, or both did, or there is no baseline) → conflict, nothing written.
- **Other fields and titles.** Fill-only: an empty remote value is filled, a differing one is a conflict. Priority, milestone,
  dependencies and authorization come from the register and are never invented.
- **Guards.** Only the `babbubba` account (tokens are stripped from the environment); the register must pass the validator; `DONE`
  reaches the board only if the register is `DONE`; an Issue is closed only with `--close-done`; a closed Issue of an unfinished task is a
  conflict and is not reopened; duplicates, unknown prefixes and vanished items are reported, never created or deleted.
- **Rate limits.** `gh api rate_limit` (REST) is read first; a GraphQL refusal while the budget is healthy is a *secondary* limit
  (retry later, exponential backoff, then exit 3), a low `remaining` is the *primary* limit (`--wait` sleeps until reset). No other token is
  ever used to get around a limit.
- **Idempotent.** Reruns after a verified apply produce an empty plan; "synchronized" is only claimed after a re-read.
- Exit codes: 0 in sync · 1 differences or pending register updates · 2 conflicts · 3 environment.

### Closure without bookkeeping-only PRs

- A delivery PR may set `DONE` when every gate is verifiable **before** the merge and the validator does not count a future event as
  achieved. Otherwise the task stays `VALIDATION` and its closure is recorded by the first later PR that changes the register for
  substantive reasons. No PR exists only to move a task from `VALIDATION` to `DONE`.
- **CI by category.** Documentation-only changes need the green CI of the PR head, with the branch up to date with `main` and no
  incompatible change since. Changes to application code, tests, validators, CI workflows or infrastructure keep the post-merge CI gate.
  Live or operator-validated work keeps its specific gates. Gates already achieved are never rewritten retroactively.
- **Operator approval.** The operator's merge accepts the delivery of the PR; it is an `OPERATOR_GATE` only when the task's acceptance
  criteria say so.
- **Late events.** If an event after the merge completes a gate before `main` can be updated: record the verifiable evidence (run ids,
  SHAs) in the Issue/Project and in the tool report, report the divergence as temporary and pending, never state that the register on
  `main` is updated, and reconcile it in the first useful PR. Pending register updates are never written to `main` without a commit: the
  tool lists them (`--report`) and `--apply-register` stages them in the working tree for that next PR.
- Until register and Project agree, do not declare the synchronization complete.

## 5. Checks

```bash
python scripts/governance/validate_register.py            # offline, no secrets; CI runs it on Windows and Linux
python scripts/governance/validate_register.py --summary  # counts, active task, READY/BLOCKED, pending decisions
```

The validator covers: unique, contiguous, frozen ids; legacy ids; closed status vocabulary; existing packets;
dependencies without cycles; `DONE` only with required gates and no open gate; every `IN_PROGRESS` task authorized and
on its own branch (parallel tasks allowed, branches distinct); milestone dimensions; recorded divergences; the
machine-readable block of `HANDOFF.md`. Comparing the register with Issues and Project #6 needs a token with the
`project` scope and is done by `sync_remote.py` (section 4b).

## 6. Project #6

Owner `babbubba`, <https://github.com/users/babbubba/projects/6>, linked to `babbubba/bOps`, title `DEV Plan` (not renamed), visibility **private**
(operator decision D11). Configured on 2026-10-21; the Project holds no Issue or item yet.

- **Status** (seven options; the pre-existing option ids were preserved): Backlog, Ready, In Progress, Validation, Blocked, Deferred, Done
  — the column of each register status is in §3. `SUPERSEDED` has no column.
- **Custom fields** (five): `Priority` (P0–P3), `Area`, `bOps Milestone` (distinct from GitHub Milestones, which the repository does not use), `Evidence Level`
  (None / Offline tests / Replay (real model) / Live (real tools)) and `Effort` (text). Native fields are used for the rest: *Linked pull requests*
  instead of a PR field, Assignees, Labels. There are no Task ID, Legacy ID, Packet or Depends-on fields: the Issue title carries `BOPS-NNNN | LEGACY-ID`,
  its body links the packet, and native Issue dependencies are used if needed.
- **Views** (three): `Kanban — Active Development` (board, columns by Status), `Backlog` (table) and `Milestones` (table). The API only sets visible
  fields, so sorting (Backlog: Priority, then bOps Milestone) and grouping (Milestones: by bOps Milestone) are set in the web UI. There is no Roadmap
  timeline view; it will be added only when real dates exist.
- **Workflows.** Built-in workflows must not change the operational status of a task. `Item closed`, `Pull request merged` and `Auto-close issue` are
  disabled (verified through the API). The operator decided to also disable `Pull request linked to issue` and `Auto-add sub-issues to project`;
  the register's `remote_sync` records whether that is verified. `Item added to project` stays enabled: its effect on the initial Status must be read
  back from the first real item before that item counts as synchronized.
- The register is never derived from the board. Known divergence: `agentic/_quality/active-milestone.json` still says V1.3-H; it is recorded in
  `register.json` `known_divergences` (QM-1, BOPS-0117), checked by the validator, and neither it nor any historical quality result is altered until reconciled.
- Only open or relevant tasks get an Issue; completed history stays in the register. Issues in this public repository are public: titles and bodies must
  not contain local paths, private data or commercial detail; tasks without a publishable spec stay private draft items.
