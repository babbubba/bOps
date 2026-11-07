# BOPS-0130 | API-AUDIT-TEST-01 — Synchronize approval tests with audit completion

Operator mandate: 2026-11-07, proceed with the next development tasks and run focused tests.
Task state and validation evidence are in [the register](register.json).

## Failure and scope

[Windows CI run 40598988483](https://github.com/babbubba/bOps/actions/runs/40598988483)
reported a sharing violation on `audit.jsonl` in
`PermanentDeletionApproval_RequiresAcknowledgement_AndUsesPagedScopedPreview`.
`AgentRunner.FinishAsync` saves the terminal task state before awaiting the terminal
lifecycle audit write. An API client can therefore observe the terminal state while the
launcher still owns the attempt and the file may still be open for writing.

Synchronize the API tests that inspect the audit file with completion of the detached
attempt. Keep the existing assertions for the actor and permanent deletion audit fields.
Use the launcher's execution state and the existing bounded polls.

[Windows CI run 42414628077](https://github.com/babbubba/bOps/actions/runs/42414628077)
reported the same sharing violation in the ordinary-task fallback audit assertion.
The fallback and V1.1/V1.2 release-gate fixtures now also wait for the host to finish
the relevant ordinary task or delegation before inspecting the audit file.
The three changed test classes passed 29 tests on Windows in Release, with zero failures or skips.

## Acceptance

- Run the approval endpoint test class on Windows, including both audit assertions.
- Run the fallback and release-gate API test classes on Windows.
- Preserve production task-state, approval and audit behavior.
- Verify Windows and Linux CI on the integrated fix before closing the task.

Track the Windows and Linux head checks on [PR #145](https://github.com/babbubba/bOps/pull/145/checks). The closure gate uses the CI run on the merge commit.

This task is part of the [V1.3 stabilization plan](2026-10-08-v1.3-pre-release-stabilization.md).
