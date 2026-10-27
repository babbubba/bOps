# BOPS-0129 MAF-TROUBLESHOOT-01 — MAF multi-agent troubleshooting workflow (read-only, CLI)

| | |
|---|---|
| Task | `BOPS-0129` (register: `agentic/_tasks/register.json`); Issue and Project #6 item owed, see `sync_pending` there |
| Contract | [Contract 01](../../docs/architecture/current/01-maf-agent-infrastructure.md), rule `C01-R23` (new, circumscribed); C02, C03 and C04 unchanged |
| Reference | [`docs/maf-troubleshooting-workflow.md`](../../docs/maf-troubleshooting-workflow.md) |
| Priority / effort | P1 / 5-8 days |
| Depends on | nothing in the register: it reuses the MAF isolation, the governed broker and the step pipeline that exist |

## Operator mandate (2026-10-27)

A real multi-agent troubleshooting workflow orchestrated by Microsoft Agent Framework: Triage → specialist agents (System; PostgreSQL when its tools exist) →
the bOps tool gateway → diagnostic review with a bounded follow-up loop → report. First increment: read-only tools only. The operator authorized one
circumscribed amendment of C01 (`C01-R23`, distinct from the experimental host `C01-R22`) and nothing else normative; ADR-0053/0054 and D1 stay as they are.
Excluded: push, PR and merge (separate authorization), new benchmarks, evaluator V2, live model runs, a web UI.

## Scope

- `bOps.Runtime`: the gateway — `AgentRunner.OpenWorkflowToolSession` and `WorkflowToolSession` / `WorkflowAgentHandle`: per-agent exact tool authority frozen at start,
  hard Read-only ceiling and run-wide tool-call budget enforced before policy, then the one `ExecuteStepAsync`; an additive `WorkflowCorrelation` stamped on every
  audit event of a run and a `WorkflowRunAuditEvent` lifecycle event.
- `bOps.AgentFramework/Troubleshooting`: the MAF workflow (`WorkflowBuilder`, executors, fan-out and barrier edges, `InProcessExecution`), the specialist catalog,
  the governed leaf, the gateway function, limits and the report.
- `bOps.Cli`: `bops diagnose "<goal>" --workflow maf`, enabled by `Cli:Experimental:MafWorkflow` (off by default).

## Acceptance criteria

- A1 triage selects specialists from registered, available ones by their declared capabilities; an unavailable specialist (no tool on the node) never blocks the others.
- A2 the workflow is run by MAF (`Microsoft.Agents.AI.Workflows` 1.24.0), specialists of a round run concurrently, the reviewer can send another round, with configurable limits (conservative defaults, hard maxima).
- A3 MAF function tools execute for real and reach a tool only through the gateway; unknown, out-of-authority, malformed and non-Read calls are refused and audited and never run; policy and approval are not bypassed.
- A4 every model call goes through the governed broker; budgets (model calls, tokens, time, tool calls, rounds) are shared by the run and stop it; cancellation and errors end it cleanly.
- A5 audit and accounting are complete and not duplicated; every event carries the run, the operator, the agent and the invocation.
- A6 the ordinary `AgentRunner` path and every other CLI command are unchanged; disabled by default; no shipped configuration enables it.
- A7 an offline end-to-end scenario (slowdowns and restarts: Triage → System → two tools → Reviewer finds the evidence insufficient → System calls again → diagnosis separating facts and hypotheses) and a System + PostgreSQL scenario through a fake package.
- A8 Release build and the `Category!=LiveModel` suites green; Windows and Linux CI are gates of the PR (not requested yet).

## Known limits (by design, documented, not hidden)

- No checkpoint and no resume in this version (C01-R23 clause 7, an implementation limit, not a prohibition; C03-R23 is not extended to workflows): the record is the audit chain,
  a retry is a new run, no tool is re-executed automatically, an interrupted run is incomplete and never `Completed`. Future checkpoint/resume needs an explicit operator decision.
- The run's evidence is not persisted as stored-task state; originals live in memory in the run's ledger for the report only.
- Read-only: no operation with effects, no remediation, no approval on behalf of an agent.
- No PostgreSQL package exists in this repository; the specialist becomes available only when `postgres.*` read tools are registered. Tested with a fake package.

## Status log

Task state lives only in the register; this section records evidence as it is produced.
