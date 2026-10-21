# Codex instructions for bOps

Before every task, read `CLAUDE.md`, `agentic/00-bootstrap.md` and its required
references, `agentic/_tasks/PROCESS.md`, `agentic/_tasks/register.json`,
`HANDOFF.md` and the selected task packet. Verify the actual checkout, GitHub
Issue and Project #6 item before relying on historical documents.

`CLAUDE.md` contains the shared architecture, security, testing, commit identity
and GitHub account rules. Follow it; `PROCESS.md` is authoritative for governance.
Do not change normative rules or accepted ADRs without operator authorization.

Task management is part of development. Start only an authorized task with
satisfied dependencies, record its branch, and move it to `IN_PROGRESS` when
work starts. Use `VALIDATION` when implementation is ready but gates remain,
`BLOCKED` with an explicit reason for a real impediment, and `DONE` only when
all required gates are evidenced. A PR or merge alone does not close a task;
application changes retain the merge-commit CI gate. Use `Refs`, not automatic
Issue-closing keywords.

For every transition, update the existing register first, then the Issue, then
Project #6 Status in the same session, and HANDOFF when the current task,
blocker or next step changes. Read back Issue and card to verify writes. Keep
commit, PR, evidence and open-gate references current. No second register or
state convention. Preserve operator work and manual card movements.

Validate with `python scripts/governance/validate_register.py --summary` and
`python scripts/governance/validate_register.py`; inspect
`python scripts/governance/sync_remote.py` before authorized `--apply` changes.
Use its three-way baseline, `--report` and `--apply-register` as PROCESS defines;
never overwrite a conflict. Record pending remote operations and any temporary
branch/main divergence explicitly. Use personal GitHub account `babbubba`
with process-only token suppression, as specified in CLAUDE.md.
