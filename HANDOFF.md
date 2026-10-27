# Handoff

Last updated: 2026-10-27 · `main` @ `20b66c8` (PR [#140](https://github.com/babbubba/bOps/pull/140) consolidation merged) ·
working branch `feat/maf-multiagent-troubleshooting`, cut from `main` @ `20b66c8`, pushed, PR [#141](https://github.com/babbubba/bOps/pull/141) open (base `main`, Refs BOPS-0129, CI Windows/Linux pending, merge only on operator request). The consolidation worktree `bOps-consol` and its branch still exist: not deleted, operator go-ahead owed.

<!-- register-sync:begin -->
Current task: BOPS-0129 MAF-TROUBLESHOOT-01 (IN_PROGRESS)
Branch: feat/maf-multiagent-troubleshooting
Ready: BOPS-0111
Next number: BOPS-0130
<!-- register-sync:end -->

The lines above (the *current task of this branch/session*; other authorized tasks may run in parallel on other branches) are machine-checked against [`register.json`](agentic/_tasks/register.json) by
`python scripts/governance/validate_register.py`. Rules: [`agentic/_tasks/PROCESS.md`](agentic/_tasks/PROCESS.md).
Task state is never copied here; ask the register (`--summary`).

## Where things are

- Register (authoritative task state): [`agentic/_tasks/register.json`](agentic/_tasks/register.json) — 129 tasks, BOPS-0001..BOPS-0129.
- Roadmap: [`agentic/_plans/2026-09-16-consolidated-roadmap.md`](agentic/_plans/2026-09-16-consolidated-roadmap.md). Real-state audit: [`docs/maintenance/roadmap-reconciliation-01.md`](docs/maintenance/roadmap-reconciliation-01.md).
- Kanban: GitHub Project #6 (`DEV Plan`, private) — <https://github.com/users/babbubba/projects/6>.
- Previous HANDOFFs (verbatim, history only): [`docs/handoff-archive/`](docs/handoff-archive/) — the one replaced today is `HANDOFF-until-2026-10-27.md`.

## Consolidation (operator, 2026-10-27)

One branch replaces the parallel branches and worktrees. Recovered from `origin/main` @ `ea557c1` after an inventory of every worktree and branch:

- the 14 commits of PR [#135](https://github.com/babbubba/bOps/pull/135) (`codex/maf-ab-diagnostic-reliability`, two of them never pushed), cherry-picked unchanged; the code tree equals the
  tested local branch `codex/bops-0128-diag-evidence-02` (`fb00497`). Content: P0 runtime-marker finals and unsupported attributed claims (`4530ae0`, attributed to BOPS-0125),
  MAF × Knowledge A/B/C/D and `ContextBudget` (C01-R22, C05-R13), plan-time refusal of impossible argument combinations and over-budget plans (C03-R24, C03-R09),
  Knowledge wording (C05-P04), the benchmark leak scanner, the BOPS-0126 P1 evaluator (`tests/bOps.DiagnosticEvaluation`), packets BOPS-0126/0127/0128. Contract amendments approved by the operator with the consolidation matrix;
- the ADR-link repair of the unpublished `48766e4` (links and two status notes only);
- from the unpublished `e64926a`, only the scrub of ambient credentials from the measured CLI's environment.

Excluded: decision D1 "keep deferred" and its drafts (superseded by PR #129; tag `archive/maf-resume-d1-168078c`), BOPS-0111 code (task set aside, PR #124 closed; branch kept on the remote).
Local-only commits are preserved by tags `backup/20261027/*`. Branches and worktrees are deleted only on the operator's go-ahead.

## Milestone and task

- Current task of this branch: **BOPS-0129 MAF-TROUBLESHOOT-01** (IN_PROGRESS, authorized by the operator mandate of 2026-10-27; Issue #142, Project #6 In Progress) — the read-only MAF multi-agent troubleshooting workflow, Contract 01 `C01-R23`, [reference](docs/maf-troubleshooting-workflow.md). PR #141 open; CI result and merge are the open gates.
- Task of the consolidation branch (not this one): **BOPS-0126 MAF-DIAG-EVAL-01** (Issue #136, Project #6 In Progress) — offline evaluation of diagnostic quality, phase **P1** only (deterministic, no model judge).
  2026-10-27: R01 calibrated (private); P1 baseline R01–R06 frozen (manifest sha256 `2ea60ce8…`). Next: blind calibration of the R02–R06 sample with the operator. P2 needs a new authorization.
- In validation (2026-10-27 reconciliation): BOPS-0061 (PRE-6, PRE-7, F-26 closure, stable tag), BOPS-0120 (real Nginx/IIS check, first release-workflow run), BOPS-0125 (live A/B campaign not concluded; only B01 pilots). BOPS-0118, 0119, 0121, 0122, 0123, 0062 and 0128 (slices A+B; slice C unauthorized) are DONE. BOPS-0111 (board Validation vs register READY) and BOPS-0117 (Issue #119 closed, register PLANNED) await an operator decision.
- Formalized, **not authorized**: BOPS-0127 MAF-DIAG-EVAL-02 (evaluator V2). Not to be started in this phase: evaluator V2, any new diagnostic benchmark. (The multi-agent MAF workflow is BOPS-0129, started by operator mandate on 2026-10-27.)
- The multi-agent MAF workflow was the next step after the consolidation PR: it is BOPS-0129, on its own branch `feat/maf-multiagent-troubleshooting`.
- Formal milestone: **V1.3** (stable gate not closed). Candidate focus DIAG-QUALITY awaits decision D7.

## Quality gates

- Local evidence of this branch: see the BOPS-0126 register entry (Release build, .NET suites, Python suites, register validation).
- CI of the PR #140 head and of its merge commit: pending.
- Governance: `python scripts/governance/validate_register.py`; `python -m unittest discover -s scripts/governance/tests`.

## Blockers and owed remote updates

- BOPS-0127: Issue and Project #6 item not created (`sync_pending` in the register).
- BOPS-0121: Issue #125 is not yet a Project #6 item (`sync_pending`).
- BOPS-0126 Issue #136: record that the work moved from PR #135 to PR #140 (comment not posted yet).
- Decisions: BOPS-0095 (D3), BOPS-0074/0075 (D1).

## Decisions waiting for the operator

D1 ADR-0053/0054 (MAF B+) · D2 reference model/timeouts · D3 `MaxObservationCharacters` default · D4 ADR-0050 acceptance · D5 Knowledge ADR statuses ·
D6 live-campaign authorization · D7 active milestone · D8 stable V1.3 gate (BOPS-0061, BOPS-0062) · D10 authorize DIAG-ASSURE-01. Details: `register.json` `decisions`.

## Local state not to touch

- `src/core/bOps.Api/appsettings.json` in the `bOps` worktree has an uncommitted operator edit (model-call timeouts); it belongs to no task.
- Private campaign data, calibration verdicts and backups live outside the repository (`bOps-backups`).

## Resume instructions for the next session

1. `python scripts/governance/validate_register.py --summary`; verify branch `feat/maf-multiagent-troubleshooting`, HEAD and a clean tree (the operator's uncommitted `src/core/bOps.Api/appsettings.json` edit is not part of this task); set the repository-local Git identity.
2. Push, PR (`Refs`, never `Closes`) and the closing of PR #135 only on explicit operator authorization; then delete obsolete branches and worktrees on the operator's go-ahead.
