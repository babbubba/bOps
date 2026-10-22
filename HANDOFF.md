# Handoff

Last updated: 2026-10-22 · `main` @ `e0e9de7` (squash merge of PR [#123](https://github.com/babbubba/bOps/pull/123), BOPS-0120) ·
working branch `docs/adr-consolidation-2026-10-22` (BOPS-0121, DOC-CONSOL-01 docs-only consolidation).

<!-- register-sync:begin -->
Current task: BOPS-0121 DOC-CONSOL-01 (IN_PROGRESS)
Branch: docs/adr-consolidation-2026-10-22
Ready: BOPS-0111
Next number: BOPS-0122
<!-- register-sync:end -->

The lines above (the *current task of this branch/session*; other authorized tasks may run in parallel on other branches) are machine-checked against [`register.json`](agentic/_tasks/register.json) by
`python scripts/governance/validate_register.py`. Rules: [`agentic/_tasks/PROCESS.md`](agentic/_tasks/PROCESS.md).
Task state is never copied here; ask the register (`--summary`).

## Where things are

- Register (authoritative task state): [`agentic/_tasks/register.json`](agentic/_tasks/register.json) — 121 tasks, BOPS-0001..BOPS-0121.
- Roadmap: [`agentic/_plans/2026-09-16-consolidated-roadmap.md`](agentic/_plans/2026-09-16-consolidated-roadmap.md).
- Reconciliation of the real state (read-only audit, 2026-10-21): [`docs/maintenance/roadmap-reconciliation-01.md`](docs/maintenance/roadmap-reconciliation-01.md).
- Kanban: GitHub Project #6 (`DEV Plan`, private) — <https://github.com/users/babbubba/projects/6>. Configured: 7 Status options, 5 custom fields (Priority, Area,
  bOps Milestone, Evidence Level, Effort), 3 views (Kanban, Backlog, Milestones). **Populated (19 items):** 11 public Issues (#109–#119) and 8 private draft items, each read back and matching the register (`remote_sync`, per-task `sync`). Sorting/grouping of the table views is set in the web UI.
- Previous HANDOFF and task index (verbatim): [`docs/handoff-archive/`](docs/handoff-archive/).

## Milestone and task

- Formal milestone: **V1.3** (A–M merged; stable V1.3 gate not closed). Candidate focus DIAG-QUALITY awaits decision D7.
- Current task of this branch: **BOPS-0121 DOC-CONSOL-01** (Issue #125, authorized at GATE-01 on 2026-10-22) — documentation-only consolidation of the ADRs,
  `06-decisions.md` and `01-architecture-rules.md`; plan [`agentic/_plans/ADR-consolidation-2026-10-22.md`](agentic/_plans/ADR-consolidation-2026-10-22.md). DOC-01, GATE-01 and DOC-02 done: the five candidate contracts are in `docs/architecture/current/` and the evidence in
  `agentic/_plans/_migration-work/ADR-consolidation-2026-10-22/DOC-02-*` (start with `DOC-02-GATE-02-report.md`);
  **next step: GATE-02** (operator approval, plan §7.3). DOC-03 and DOC-04 are not started. No ADR is archived or removed before GATE-02.
- BOPS-0120 F-26 (Issue #122): PR #123 is merged into `main` (`e0e9de7`); its register entry was not reconciled on this branch — the published `bOps.Api` serves the compiled Angular SPA; packet
  [`agentic/_tasks/2026-10-21-bops-0120-f26-production-spa-hosting.md`](agentic/_tasks/2026-10-21-bops-0120-f26-production-spa-hosting.md), operator guide `docs/deployment.md`. This branch also carries the approved bookkeeping (BOPS-0061/0062 dependency fix, F-25 evidence in its packet, BOPS-0119 baseline).
- In validation: **BOPS-0119 PROJECT-GOVERNANCE-02** (draft item, no Issue) — `scripts/governance/sync_remote.py` + offline tests + policy in `PROCESS.md` §4b; spec
  [`agentic/_tasks/2026-10-21-bops-0119-project-governance-02-sync.md`](agentic/_tasks/2026-10-21-bops-0119-project-governance-02-sync.md). Open gate: CI of the PR head and merge commit. Read-only run on Project #6 done: 18 of 19 items in sync, 2 pending updates for BOPS-0119, 0 conflicts (a secondary rate limit refused GraphQL earlier; the tool classified and aborted, then succeeded).
- In validation: **BOPS-0118 PROJECT-GOVERNANCE-01** (Issue #109) — PRs #108 and #120 merged. Open gate: operator approval to close; DONE is recorded in the BOPS-0119 PR once approved.
- Last completed: **BOPS-0110 ROADMAP-RECONCILIATION-01** (2026-10-21, read-only audit).

## Quality gates (verified 2026-10-21)

- CI: PRs #108 and #120 heads were green on Windows and Linux including "Task register validation". CI of `main` @ `9aa2a70`: running when written.
- Offline suites run during the reconciliation (Debug, `Category!=LiveModel`): Architecture 210/210, Runtime 2301/2301, Knowledge 308/308,
  AgentFramework 615 passed + 8 ignored (live/operator only), System.Core 481/481. Not run: full suite, Linux/WSL, Packages.* on Windows, Api, UI.
- Governance: `python scripts/governance/validate_register.py` and `python -m unittest discover -s scripts/governance/tests` (validator and synchronizer tests, offline).
- Remote sync (read-only by default): `python scripts/governance/sync_remote.py`; remote writes need `--apply` and operator approval.
- `agentic/_quality/active-milestone.json` still points at V1.3-H, and `known-failures.json` lists different Filesystem tests than the old HANDOFF did
  (BOPS-0117 QUALITY-GATE-DOCS).

## Next READY task

- **BOPS-0111 DIAG-ASSURE-01** — evidence assurance projection (P0, 2.5–4 days). Spec: reconciliation report §6. **Not authorized**: needs decision D10
  (and a task packet) before any work. Nothing else is READY.
- Candidates after it (PLANNED, dependencies in the register): PLAN-FIDELITY-01 (BOPS-0112), HOST-SATURATION-01 (0113), LIVE-GATE-01 (0114), DIAG-CLAIMS-01 (0115).

## Blockers

- BOPS-0121 remote sync owed: Issue #125 exists but is not yet a Project #6 item (no Project access from the session that created it); see `sync_pending`
  in the register. Run `sync_remote.py` or add it in the web UI, then record `issue`/`project_item`.
- No other governance blocker. Project #6: the five workflows that could move task status (`Item closed`, `Pull request merged`, `Auto-close issue`, `Pull request linked to issue`,
  `Auto-add sub-issues to project`) are disabled (verified via API); only `Item added to project` is on. Remote writes beyond configuration (Issues, items) are not authorized yet.
- BOPS-0095 LIVE-01 (decision D3), BOPS-0074/0075 ARCH-01/ARCH-03 (decision D1).

## Decisions waiting for the operator

D1 ADR-0053/0054 (MAF B+) · D2 reference model/timeouts · D3 `MaxObservationCharacters` default · D4 ADR-0050 acceptance · D5 Knowledge ADR statuses ·
D6 live-campaign authorization · D7 active milestone · D8 stable V1.3 gate (BOPS-0061, BOPS-0062) · D10 authorize DIAG-ASSURE-01.
Resolved: D9 (mapping **FROZEN**, 82 of 119 progressives conventional order, accepted) and D11 (Project #6 is PRIVATE). Details and recommendations: `register.json` `decisions`. No ADR status was changed.

## Local state not to touch

- `src/core/bOps.Api/appsettings.json` has a pre-existing uncommitted edit (model-call timeouts); it is not part of any slice.
- The local know-bench worktree (branch `codex/know-bench-01a`) and the untracked local audits in `docs/maintenance/` other than the
  reconciliation report are unrelated to governance and were left as they were. Private data and backups live outside the repository.

## Resume instructions for the next session

1. `python scripts/governance/validate_register.py --summary`; read the register entry of the task you are told to do. On this branch: BOPS-0121,
   execute DOC-02 exactly as the plan §7 says.
2. The id mapping is frozen: never renumber. Check the CI of `main` @ `9aa2a70`; the BOPS-0119 PR keeps the post-merge CI gate (it changes validators and CI).
3. Remote Project #6 is behind the register for BOPS-0119 (draft still Backlog): run `sync_remote.py` (dry-run), then `--apply` only with the operator's approval. If GitHub answers with a secondary rate limit, wait; never switch tokens.
4. BOPS-0118 closes only with the operator's approval, recorded in the BOPS-0119 PR (no bookkeeping-only PR). No other task is authorized; never start BOPS-0111 without D10.
