# BOPS-0119 | PROJECT-GOVERNANCE-02 — Remote synchronizer (register ↔ Issues ↔ Project #6)

| | |
|---|---|
| Register | `BOPS-0119`, milestone `GOVERNANCE`, area `Governance` |
| Depends on | BOPS-0118 (register, validator, Project #6 populated) |
| Authorization | operator, 2026-10-21 |
| Scope | scripts, tests, CI step, process text. **No application code.** |
| Gates | offline tests; CI (post-merge: it touches a validator tool and the CI workflow); read-only remote verification |

## Objective

A small, maintainable tool that detects — and, only when told to, repairs — divergence between
`agentic/_tasks/register.json` (authoritative), the GitHub Issues of `babbubba/bOps` and the Status/fields of Project #6
`DEV Plan`, without silent overwrites and without ever deciding anything the operator has not decided.

## Design

`scripts/governance/sync_remote.py`, Python standard library only, `gh` as transport.

1. **Pure planner.** `plan(register, remote) -> Plan` has no I/O. `remote` is a snapshot: Project items (id, kind
   Issue/DraftIssue, issue number, Status and the five custom fields, title) and Issues (number, state, title, body).
2. **Three-way Status rule.** `desired` = the board column of the register status; `baseline` = `sync.status` stored in the register
   at the last verified sync; `remote` = current value.
   - `remote == desired` → in sync (if `baseline` differs, record a *pending register update*, see 5).
   - `remote == baseline` ≠ `desired` → the register moved: **update** the remote.
   - otherwise (a person changed the remote, or both changed, or no baseline) → **conflict**; nothing is written.
3. **Other fields** (Priority, Area, bOps Milestone, Evidence Level, Effort) and titles: the register is authoritative, but a
   non-empty differing remote value is a **conflict**, never overwritten; an empty remote value is **filled**. Priority, milestone,
   dependencies and authorization are never invented: they come from the register only.
4. **Guards.**
   - Only `babbubba`: the tool strips `GH_TOKEN`/`GITHUB_TOKEN`, asks `gh api user` and aborts if the login differs.
   - The register must pass `validate_register.py` first; `DONE` can only reach the board when the register says `DONE` (which the
     validator ties to its gates); an Issue is closed only with `--close-done` and a `DONE` register status; a closed Issue whose task is
     not `DONE` is a conflict, never reopened.
   - Duplicates (two remote items/Issues with one `BOPS-NNNN` prefix), remote items with a prefix unknown to the register, and tasks
     whose linked item vanished are reported; nothing is created or deleted (creation stays an explicit operator action).
   - Issue bodies are not rewritten; a body whose "Status in the register" line is stale is reported as `body-stale`.
5. **Pending register updates.** After a verified apply (or when remote already matches), the tool lists the register fields that
   should change (`sync.status`, `sync.verified_at`, link fields). It prints and writes them with `--report`; `--apply-register` patches
   the working-tree `register.json` locally. Nothing is committed or pushed by the tool: the patch rides the next substantive PR, so
   no bookkeeping-only PR is needed, and until then the divergence is reported as **temporary and pending**.
6. **Rate limits.** Before any call the tool reads `gh api rate_limit` (REST, free) and aborts when GraphQL `remaining` is below a
   threshold. Primary limit: wait until reset only if the operator passes `--wait`. Secondary limit (message `secondary rate limit`,
   `already exceeded` with a healthy primary budget, HTTP 403/429): exponential backoff (default 60/120/240 s, three retries), then abort
   with exit code 3 and no partial claim of success. Writes are serial with a minimum delay between them (default 2 s).
7. **Dry-run by default.** `--apply` is required for any write. After each write batch the tool re-reads the remote and only then
   reports the item as synchronized; a mismatch is reported as failure.
8. **Idempotence.** A second run after a successful apply produces an empty plan.

Exit codes: `0` in sync · `1` differences (dry-run) or pending register updates · `2` conflicts · `3` environment (account, rate limit,
validator, transport).

## Process rules added to `PROCESS.md`

- *No bookkeeping-only PRs.* A delivery PR may set `DONE` only when every gate is verifiable before the merge and the validator does not
  count a future event as achieved. Otherwise the task stays `VALIDATION`, and its closure is recorded by the first later PR that changes
  the register for substantive reasons.
- *CI by category.* Documentation-only changes need the green CI of the PR head, with the branch up to date with `main`. Changes to
  application code, tests, validators, CI workflows or infrastructure keep the post-merge CI gate. Live or operator-validated work keeps
  its specific gates. Gates already achieved are never rewritten.
- *Operator approval.* The operator's merge accepts the delivery of the PR; it is an `OPERATOR_GATE` only if the acceptance criteria
  say so.
- *Late events.* If an event after the merge completes a gate before `main` can be updated, record the verifiable evidence, report the
  divergence as temporary and pending (tool report), never claim that `main` is updated, reconcile in the first useful PR.

## Acceptance criteria

- [ ] Offline tests cover: every Status case of the three-way rule; fill-only and conflict for other fields; DONE and close guards;
  duplicates, unknown and vanished items; draft vs Issue; idempotence (second plan empty); dry-run writes nothing; account guard;
  rate-limit retry and abort; register validator failure aborts; pending-register-update output.
- [ ] CI runs the new tests on Windows and Linux (the tool itself is never run against GitHub in CI; no secret).
- [ ] A read-only run against the real Project #6 completes and its report is attached to the PR description; the 19 existing items are
  not modified.
- [ ] `PROCESS.md` documents the tool and the policy above; `validate_register.py` passes.

## Out of scope

Creating Issues or items, editing Issue bodies, changing views or workflows, application code, DIAG-ASSURE-01.
