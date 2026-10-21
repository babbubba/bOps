# Handoff

Last updated: 2026-10-21 · Verified against `main` @ `09902a2` (CI run 39542752002 and Quality finalize 39544658812: success) ·
working branch `docs/project-governance-01` (local, not pushed).

<!-- register-sync:begin -->
Active implementation task: NO ACTIVE IMPLEMENTATION TASK
Ready: BOPS-0111
Next number: BOPS-0120
<!-- register-sync:end -->

The three lines above are machine-checked against [`register.json`](agentic/_tasks/register.json) by
`python scripts/governance/validate_register.py`. Rules: [`agentic/_tasks/PROCESS.md`](agentic/_tasks/PROCESS.md).
Task state is never copied here; ask the register (`--summary`).

## Where things are

- Register (authoritative task state): [`agentic/_tasks/register.json`](agentic/_tasks/register.json) — 119 tasks, BOPS-0001..BOPS-0119.
- Roadmap: [`agentic/_plans/2026-09-16-consolidated-roadmap.md`](agentic/_plans/2026-09-16-consolidated-roadmap.md).
- Reconciliation of the real state (read-only audit, 2026-10-21): [`docs/maintenance/roadmap-reconciliation-01.md`](docs/maintenance/roadmap-reconciliation-01.md).
- Kanban: GitHub Project #6 `dev plan` — <https://github.com/users/babbubba/projects/6>. **Not yet configured or populated** (see Blockers).
- Previous HANDOFF and task index (verbatim): [`docs/handoff-archive/`](docs/handoff-archive/).

## Milestone and task

- Formal milestone: **V1.3** (A–M merged; stable V1.3 gate not closed). Candidate focus DIAG-QUALITY awaits decision D7.
- Active implementation task: **NO ACTIVE IMPLEMENTATION TASK**.
- In validation (governance): **BOPS-0118 PROJECT-GOVERNANCE-01** — register, validator, process and documents are done locally;
  Issue: none yet; PR: none (not pushed). Open gates: operator approval of the mapping and plan, remote Project/Issue setup.
- Last completed: **BOPS-0110 ROADMAP-RECONCILIATION-01** (2026-10-21, read-only audit).

## Quality gates (verified 2026-10-21)

- `main` @ `09902a2`: GitHub CI and Quality finalize green (checked with `gh run list`).
- Offline suites run during the reconciliation (Debug, `Category!=LiveModel`): Architecture 210/210, Runtime 2301/2301, Knowledge 308/308,
  AgentFramework 615 passed + 8 ignored (live/operator only), System.Core 481/481. Not run: full suite, Linux/WSL, Packages.* on Windows, Api, UI.
- Governance validator: run `python scripts/governance/validate_register.py` (tests: `python -m unittest discover -s scripts/governance/tests`).
- `agentic/_quality/active-milestone.json` still points at V1.3-H, and `known-failures.json` lists different Filesystem tests than the old HANDOFF did
  (BOPS-0117 QUALITY-GATE-DOCS).

## Next READY task

- **BOPS-0111 DIAG-ASSURE-01** — evidence assurance projection (P0, 2.5–4 days). Spec: reconciliation report §6. **Not authorized**: needs decision D10
  (and a task packet) before any work. Nothing else is READY.
- Candidates after it (PLANNED, dependencies in the register): PLAN-FIDELITY-01 (BOPS-0112), HOST-SATURATION-01 (0113), LIVE-GATE-01 (0114), DIAG-CLAIMS-01 (0115).

## Blockers

- Project #6 cannot be read or edited with the current `gh` token (scopes: `gist`, `read:org`, `repo`, `workflow`; `read:project` is missing). Operator action:
  `gh auth refresh -s project` (interactive; credentials were not touched by this session).
- BOPS-0095 LIVE-01 (decision D3), BOPS-0074/0075 ARCH-01/ARCH-03 (decision D1).

## Decisions waiting for the operator

D1 ADR-0053/0054 (MAF B+) · D2 reference model/timeouts · D3 `MaxObservationCharacters` default · D4 ADR-0050 acceptance · D5 Knowledge ADR statuses ·
D6 live-campaign authorization · D7 active milestone · D8 stable V1.3 gate (BOPS-0061, BOPS-0062) · D9 approve/freeze the id mapping · D10 authorize DIAG-ASSURE-01 ·
D11 Project #6 visibility. Details and recommendations: `register.json` `decisions`. No ADR status was changed.

## Local state not to touch

- `src/core/bOps.Api/appsettings.json` has a pre-existing uncommitted edit (model-call timeouts); it is not part of any slice.
- Worktree `C:\source\repos\babbubba\bOps-knowbench01a` (branch `codex/know-bench-01a`) and the untracked local audits in `docs/maintenance/` other than the
  reconciliation report are unrelated to governance and were left as they were. Private data and backups live outside the repository.

## Resume instructions for the next session

1. `python scripts/governance/validate_register.py --summary`; read the register entry of the task you are told to do.
2. If the operator approved the proposal: finish the remote step (Project #6 fields/views, wave-1 Issues), record the Issue numbers and Project item ids in
   the register, set BOPS-0118 to `DONE` only when its gates are met, and update this file.
3. Otherwise do nothing functional: no task is authorized. Never start BOPS-0111 without D10.
