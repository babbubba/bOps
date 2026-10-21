# Handoff

Last updated: 2026-10-21. Code baseline `main` @ `e0e9de7` (PR #123); branch `codex/bops-0111-diagnostic-assurance`.

<!-- register-sync:begin -->
Current task: BOPS-0111 DIAG-ASSURE-01 (VALIDATION)
Branch: codex/bops-0111-diagnostic-assurance
Ready: none
Next number: BOPS-0121
<!-- register-sync:end -->

Task states, gates and evidence: [register.json](agentic/_tasks/register.json). Process: [PROCESS.md](agentic/_tasks/PROCESS.md).

## Current work

BOPS-0111 implementation is ready; packet records tests, corpus provenance and residual diagnostic limits.
Issue #112 OPEN / Project #6 Validation and Offline tests were re-read after the transition.
Local register includes approved earlier closures; main is temporarily behind until this substantive PR merges.

## Previous PRE-6 evidence

The operator authorized a bounded PRE-6 campaign and superseded the instruction to keep its parent task in validation during active work.
The remote card was moved to In Progress and re-read before the test, then returned to Validation and re-read after the campaign ended.
Required release/operator gates remain open; PRE-7 and PRE-8 were not started.

One local Qwen3.6-35B real-tool task: `30410464-923e-4163-ad7c-3754727dc258`, code `e0e9de7`, 420.8 seconds.
It completed execution, but independent persisted-fact comparison found a false application-crash count (34 in final versus 14 observed).
The grounding check was skipped after the final call exceeded the isolated 100000-token cap; this run does not measure checker sensitivity.
Two same-tool semantic mismatches were safely refused; two replans recovered the plan. No evidence reads occurred.

Subsequent archived supervised KD task `3fc98548-59df-4678-8d36-6aa57027626b` was inspected read-only.
KD ran for about 80 s, bugcheck 0x1e, symbols loaded; tool-specific timeout worked with host default 30 s.
DumpChk timed out; output was shortened and not retrieved. Causal attribution from process context is unverified.
PRE-6 is not wholly passed; remaining integrity/cache/EvidenceRead/UI/cloud and release gates stay open.
Cloud inference still needs explicit operator authorization. Authenticated/mobile plan UI and evidence continuation lack live proof.
Private report, original task exports, claim ledger and manual runner are outside Git in the dedicated PRE-6 evidence directory.
The test APIs were stopped. No operational configuration, service, ACL or dump was modified.
The first test preparation had copied the operational vault and restricted only its output-directory ACL before the later operator restriction;
that test API was stopped and its vault copy removed. The second test host used an empty test vault and environment-only credentials.

## Bookkeeping to carry forward

The delivery carries approved prior closures, PRE-6 evidence reconciliation and BOPS-0111 transitions.
`main` retains the earlier closure records until the next substantive PR; do not run a synchronizer that overwrites the updated board.
BOPS-0111 delivery PR preparation is active; local validation is complete. No merge or release; BOPS-0120 retains independent gates.
BOPS-0111 packet and implementation explicitly authorized by the operator on 2026-10-21 (D10 resolved). Other unauthorised tasks remain out of scope.

## Preserve local operator work

- `src/core/bOps.Api/appsettings.json` has a pre-existing operator edit; hash unchanged through PRE-6.
- Existing untracked maintenance audits and the know-bench worktree were untouched.
- Private evidence, dump content, credentials and raw diagnostic logs must remain outside the repository.
