# Handoff

Last updated 2026-11-07 · branch `fix/windows-audit-test` from `origin/main` @ `a180fbd`.
The local checkout uses a separate worktree from the workspace's pinned public submodule.

<!-- register-sync:begin -->
Current task: BOPS-0130 API-AUDIT-TEST-01
Branch: fix/windows-audit-test
Ready: BOPS-0111
Next number: BOPS-0131
<!-- register-sync:end -->

The lines above (the *current task of this branch/session*; other authorized tasks may run in parallel on other branches) are machine-checked against [`register.json`](agentic/_tasks/register.json) by
`python scripts/governance/validate_register.py`. Rules: [`agentic/_tasks/PROCESS.md`](agentic/_tasks/PROCESS.md).
Task state is never copied here; ask the register (`--summary`).

## Where things are

- Register (authoritative task state): [`agentic/_tasks/register.json`](agentic/_tasks/register.json).
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

- BOPS-0130 synchronizes API approval, fallback and release-gate audit reads with the final audit write. See [Issue #144](https://github.com/babbubba/bOps/issues/144), [PR #145](https://github.com/babbubba/bOps/pull/145) and the [task packet](agentic/_tasks/2026-11-07-bops-0130-api-audit-test-synchronization.md).
- [PR #141](https://github.com/babbubba/bOps/pull/141) merged as `de769be` with green Windows/Linux CI. See BOPS-0129 for the remaining acceptance gate.
- Task of the consolidation branch (not this one): **BOPS-0126 MAF-DIAG-EVAL-01** (Issue #136, Project #6 In Progress) — offline evaluation of diagnostic quality, phase **P1** only (deterministic, no model judge).
  2026-10-27: R01 calibrated (private); P1 baseline R01–R06 frozen (manifest sha256 `2ea60ce8…`). Next: blind calibration of the R02–R06 sample with the operator. P2 needs a new authorization.
- In validation (2026-10-27 reconciliation): BOPS-0061 (PRE-6, PRE-7, F-26 closure, stable tag), BOPS-0120 (IIS host check, first release-workflow run), BOPS-0125 (live A/B campaign not concluded; only B01 pilots). BOPS-0118, 0119, 0121, 0122, 0123, 0062 and 0128 (slices A+B; slice C unauthorized) are DONE. BOPS-0111 (board Validation vs register READY) and BOPS-0117 (Issue #119 closed, register PLANNED) await an operator decision.
- Formalized, **not authorized**: BOPS-0127 MAF-DIAG-EVAL-02 (evaluator V2). Not to be started in this phase: evaluator V2, any new diagnostic benchmark. (The multi-agent MAF workflow is BOPS-0129, started by operator mandate on 2026-10-27.)
- Formal milestone: **V1.3** (stable gate not closed). Candidate focus DIAG-QUALITY awaits decision D7.

## Quality gates

- Locked restore and Release build of `a180fbd` passed locally with no warnings or errors.
- Workspace pin candidate `de769be` passed locked restore and Release build. It includes the MessagePack 2.5.307 update from `946e230`. The prior pin `e30c8f8` fails locked restore with NU1902.
- PR #140 head checks and CI of merge commit `20b66c8` passed on Windows and Linux.
- The approval endpoint class passed 18 tests on Windows. The fallback and release-gate classes passed another 29 after the follow-up synchronization change. See [PR #145 checks](https://github.com/babbubba/bOps/pull/145/checks) for head CI. Verify Windows and Linux CI on the merge commit to close the integration gate.
- BOPS-0120 records the Windows artifact smoke and the Nginx HTTPS smoke with the Linux artifact. The IIS gate is blocked pending a Windows test host with ARR and URL Rewrite. See the [test checklist](agentic/_tasks/2026-10-21-bops-0120-f26-production-spa-hosting.md#iis-validation).

## Blockers and owed remote updates

- BOPS-0127: Issue and Project #6 item not created (`sync_pending` in the register).
- BOPS-0121: Issue #125 is not yet a Project #6 item (`sync_pending`).
- BOPS-0126 and BOPS-0129 have outstanding Issue or Project #6 updates recorded in `sync_pending`.
- BOPS-0130: [Project #6 item](https://github.com/users/babbubba/projects/6?pane=issue&itemId=297181431) verified in the web UI. The register still needs the GraphQL item ID and Project #6 has no `API` Area option. See `sync_pending`.
- Decisions: BOPS-0095 (D3), BOPS-0074/0075 (D1).

## Decisions waiting for the operator

D1 ADR-0053/0054 (MAF B+) · D2 reference model/timeouts · D3 `MaxObservationCharacters` default · D4 ADR-0050 acceptance · D5 Knowledge ADR statuses ·
D6 live-campaign authorization · D7 active milestone · D8 stable V1.3 gate (BOPS-0061, BOPS-0062) · D10 authorize DIAG-ASSURE-01. Details: `register.json` `decisions`.

## Local state not to touch

- `src/core/bOps.Api/appsettings.json` in the `bOps` worktree has an uncommitted operator edit (model-call timeouts); it belongs to no task.
- Private campaign data, calibration verdicts and backups live outside the repository (`bOps-backups`).

## Resume instructions for the next session

1. Review the BOPS-0130 checks on PR #145. After an operator-authorized merge, verify the merge commit in Windows and Linux CI before closing the task or advancing the workspace pin beyond `de769be`.
2. Resume BOPS-0120 on an IIS test host with ARR and URL Rewrite. Record the HTTPS and event-stream results in the task before advancing the F-26 gate.
