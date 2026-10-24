# AF-TURN-02 — CLI opt-in MAF governed turn host: parity report

Task `BOPS-0122` · rule `C01-R22` · decision `D12` · base `main` @ `2b54880` · date 2026-10-23 · evidence class: **offline, scripted model** (no live model, no network).

## What was built

`CliRunnerComposition.Compose` (`src/core/bOps.Cli/CliRunnerComposition.cs`) wraps the governed `ModelInvocationBroker` in the existing `MafGovernedTurnBroker` (assembly mode `BopsProvider`) when the host key `Cli:Experimental:MafGovernedTurn` is `true`; `Program.cs` reads the key strictly, applies it to goal and resume only, prints an `EXPERIMENTAL` line and exits with code 2 on a typo or when Knowledge is enabled. Default: absent = off, composition unchanged. No change to `AgentRunner`, the broker, the MAF project, public types or the API. Test-only visibility: `InternalsVisibleTo bOps.Cli.Tests` on `bOps.AgentFramework`.

## Differences from the current path

| Observable | Result (27 new tests, `MafGovernedTurnHostTests`) |
|---|---|
| Requests offered to the model (prompt, tool names/descriptions/parameters, whole history) | **identical** in 7 scenarios |
| Persisted `TaskState` after the run | **identical** (run ids, instants and durations normalised) |
| Audit trail (all events, including model-call events) | **identical**, event by event |
| Tool executions, terminal status, token accounting | **identical** (accounting is part of the stored state and the audit) |
| Extra model calls or tool executions | **none** (`Requests.Count` equals the scripted turns; the second proposed call is never run) |
| Interrupted in one mode, resumed in the other (all 4 combinations, new store handle per attempt) | **identical** requests and stored state to the all-default control |
| Knowledge enabled with the switch on | refused before anything is composed; with Knowledge disabled it runs |
| What differs | only the startup line, the composed broker type, and per-run CPU: median 9.4 ms default vs 9.9 ms hosted (short scenario) and 4.6 vs 4.7 ms (long scenario), scripted model, n = 10 each — about +0.1 to +0.5 ms per run, not a provider measurement |

Scenarios: plan-tool-final, several proposed calls (first wins), unknown tool rejected, high-risk tool denied (never executed, also asserted per mode), early final and replan, provider failure (unscripted call), compaction pressure (five 6 KB tool results through the structural projector). A control test proves the comparison detects a difference. Retry and fallback timing is not parity-tested at host level: the hosted broker delegates to the same `ModelInvocationBroker`, whose retry suites run unchanged, and accounting, audit and retry were already covered at turn level by `MafTurnEquivalenceTests`. Cancellation and timeout are not covered at host level either (gap noted).

## Regression

Release build of `bOps.slnx`: 0 warnings, 0 errors. Non-live suites: Cli 217, Architecture 210, AgentFramework 615 (+8 skipped), Runtime 2301, Knowledge 308 — all passed. The three architecture guards that forbade any production composition of the MAF turn host were narrowed (not removed) to C01-R22: exactly one composition, in `CliRunnerComposition.cs`, one construction, behind the switch, refused with Knowledge, no shipped configuration mentions the key; the context provider, leaf, agent and Knowledge types stay forbidden in the CLI.
Pre-existing failures, unrelated and reproduced on the unchanged `main` code: `FsSizeTests.FsSize_ReturnsCompleteBoundedSummary_ForNestedTree`, `FsDeleteTreeTests.DeleteTree_FailsClosedBeforeDeleting_WhenTheParentWasReplaced`, `FsDeleteTreeTests.ApprovalBinding_ExpiresWithoutDeleting` (Windows, `bOps.Packages.Filesystem.Tests`; not listed in `agentic/_quality/known-failures.json`, which lists other Filesystem tests). Windows and Linux CI have not run (no push or PR was requested).

## Not done / next

No live run, no API wiring, no telemetry tag, no process-level test of `Program.cs` exit codes (the parser and the refusal are unit-tested). Extension to the API needs a separate operator decision on this evidence.
