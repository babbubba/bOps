# BOPS-0122 AF-TURN-02 — CLI opt-in MAF governed turn host

| | |
|---|---|
| Task | `BOPS-0122` (register: `agentic/_tasks/register.json`), Issue and Project #6 item recorded there |
| Contract | [Contract 01](../../docs/architecture/current/01-maf-agent-infrastructure.md), rule `C01-R22`; operator decision `D12` (2026-10-23) |
| Draft and rationale | `docs/maintenance/d1-revision-contract-01-amendment-draft-01.md` on the branch that carries the earlier documentation work (the approved scope, with the two operator corrections below) |
| Priority / effort | P1 / medium |
| Depends on | nothing in the register: the code it wires (`MafGovernedTurnBroker`, `BopsContextProvider`, `ContextGate`, `GovernedChatClient`) is already implemented and tested under BOPS-0086/0087, which stay `DEFERRED` and are not reopened |

## Operator decision (2026-10-23)

1. General architectural convergence (ADR-0053/0054 as a whole, D1) stays **deferred**. `D12` authorizes **only** C01-R22 and the `MafGovernedTurnBroker` experiment in the CLI.
2. BOPS-0086/0087 are not reopened. This task is only the operational wiring of code that already exists.
3. Excluded: PR #124 / BOPS-0111, the API host, live benchmarks, push, PR and merge (each needs a separate authorization).

## Scope

CLI only (`CliRunnerComposition.Create`), goal and resume runs, switch `Cli:Experimental:MafGovernedTurn` (default `false`), refused when Knowledge is enabled, startup-visible. No change to `AgentRunner`, the broker contract, public types, the API, delegation, recovery or reconciliation. No new orchestrator, no duplicated code.

## Invariants

The model proposes, bOps decides and executes. MAF executes nothing (declaration-only tools), has no tool loop and no session needed for resume. No new authority over policy, entitlement, approval, mutation journal or fencing. Exactly one broker call per turn; retry, fallback, model-call audit and budget stay in the broker. No MAF type in `bOps.Abstractions`, the Runtime public surface or persistence. `ContextGate` refuses any context that differs from what bOps authorised. Provenance, EvidenceFact, grounding and completeness unchanged. Switching between normal and experimental mode, also across a restart or during resume, must not change or corrupt the persisted state: the same `TaskState` must resume identically in either mode.

## Acceptance criteria

- A1 default off: with the key absent or `false` the composition is the one of `main` (request, response, audit, accounting, persisted state byte-identical).
- A2 parity: for each scenario below the runner-visible requests, tool declarations, responses, audit events and model-call records, token accounting, persisted `TaskState`/journal, tool executions and terminal status are identical with the switch off and on (only run ids and timestamps normalised).
- A3 mode switch: a task started in one mode and resumed in the other (also after a process restart, after a failure and after an approval wait) resumes with the same request and the same persisted state, and records nothing mode-specific that the other mode cannot read.
- A4 refusal: switch on with Knowledge enabled fails closed with an explicit message; delegation, recovery and reconciliation ignore the switch.
- A5 safety: exactly one broker call per turn, no extra LLM call, no extra tool execution; MAF-side violations are controlled model failures that execute nothing; architecture and boundary tests unchanged and green.
- A6 visibility: the active turn host is reported at startup and in telemetry.
- A7 Release build and the `Category!=LiveModel` suites green locally; Windows and Linux CI are gates of the PR (not requested yet).

Scenarios: planning and replanning (incl. early-final recovery); single tool step; several proposed calls (first-call-wins); denied tool, bad arguments, approval granted/denied; mutation unknown outcome and reconciliation; resume from persisted state; prompt/messages/tools order; accounting, audit, timeout, cancellation, provider failure, retry and fallback; EvidenceRead, compaction and disclosure.

## Plan

1. Characterization tests of the current CLI composition (switch absent) to freeze today's behaviour.
2. The switch and the composition branch (single call site), the Knowledge refusal and the visibility line.
3. Host-level parity tests reusing `MafTurnEquivalenceTests`/`Regression` scaffolding and the Cli test scaffolding.
4. Mode-switch and resume tests (restart simulated with a reopened `SqliteTaskStore`).
5. Overhead measurement (offline) and a report of the differences from the current path in `docs/maintenance/`.
6. Contract 01 evidence anchors, operator configuration documentation, HANDOFF.

## Status log

Task state lives only in the register; this section records evidence as it is produced.
