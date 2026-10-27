# MAF multi-agent troubleshooting workflow

Technical reference of rule [`C01-R23`](architecture/current/01-maf-agent-infrastructure.md) (`BOPS-0129`). Read-only, CLI only, opt-in, off by default.
The binding text is in the contract; this page says how it is built and used.

## Use

```bash
Cli__Experimental__MafWorkflow=true bops diagnose "Windows restarts and is slow" --workflow maf
```

Optional limits (they can only tighten what the hard maxima allow): `--max-rounds`, `--max-tool-calls`, `--max-tokens`, `--timeout-minutes`.
Without `Cli:Experimental:MafWorkflow=true` in the host configuration the command refuses to run (exit 2). `bops "<goal>"` and every other command are unchanged.

Exit codes: `0` completed, `4` ended on a limit (the report is still printed), `1` nothing investigated or failed, `2` configuration, `130` cancelled.

## Shape

```
bops diagnose ──► MAF workflow (Microsoft.Agents.AI.Workflows 1.24.0, in-process)
                    triage ──fan-out──► specialist-<name> … (concurrent in a round) ──barrier──► review
                      ▲                                                                            │
                      └────────────── another round, only if the reviewer asks and limits allow ◄───┘
                                       │ MAF function invocation
                                       ▼
                    GovernedToolFunction ─► WorkflowAgentHandle ─► AgentRunner.ExecuteStepAsync
                                            (frozen per-agent tool set,     (validation, policy, approval,
                                             Read-only ceiling, budget)      entitlement, timeout, execution, audit)
                    every model call ─► WorkflowGovernedChatClient ─► IModelInvocationBroker (retry, fallback, audit, tokens)
```

- **Triage** reads the goal and the registered, available specialists (by what each declares) and assigns objectives. It has no tool and authorizes nothing.
- **Specialists** are MAF agents whose function tools are the gateway adapter. Each decides which of its allowed tools to call and follows up on results.
  A specialist with no assignment in a round reports `NotAssigned` without a model call.
- **Reviewer** compares the reports, flags contradictions and unsupported claims, separates facts from hypotheses and either concludes or asks a registered specialist for a follow-up round.
- **Report**: cited observations (the evidence ids they cite are checked against the calls that ran; the content is not verified), hypotheses, contradictions, unsupported, missing, next checks, an *advisory* intervention plan, the evidence list, limits and consumption.

## Authority

| What | Where it is enforced |
|---|---|
| Which tools an agent may call | exact set frozen at run start from the Read tools the runner reports visible, by the registered specialist's selector; hash audited in `WorkflowRunAuditEvent(Started)` |
| Read-only | `AgentRunner.WorkflowTools.cs`, before policy: a tool above Read is refused and audited. `DiagnosticSpecialistCatalog` also never offers one |
| Unknown / unoffered / malformed calls | refused and audited as the ordinary unknown-tool or validation path |
| Policy, approval, entitlement, timeout | the unchanged `ExecuteStepAsync` — the gateway adds only narrower refusals |
| Model calls | one leaf → `IModelInvocationBroker`; no other channel, no credential in MAF |
| Tool output as data | tool-result turns with the runtime's delimiters; reports and the goal inside neutralized delimiters |

`bOps.AgentFramework` never sees the tool registry, policy, approval or a tool (architecture guards `EvidenceReadAndFactsBoundaryTests`, `MafWorkflowBoundaryTests`).

## Identity, audit, accounting

The run id is the task id of every event. The operator is the `Actor`. The runtime stamps `Workflow = {RunId, AgentName, AgentInstanceId, InvocationId}` on every tool-call
and model-call event of the run; `WorkflowRunAuditEvent` records start, triage, each round, each review and the end. Tokens are the sum of what providers reported (a lower bound).

## Limits

Defaults / hard maxima: investigation rounds 2 / 4, tool calls 24 / 100, model calls 40 / 120, tokens 200,000, time 15 min / 1 h. Reaching one ends the run as `LimitReached` with the evidence collected.

## Not supported (on purpose)

- **Checkpoint and resume** (C01-R23 clause 7). An implementation limit of this first increment, not a prohibition and not an extension of C03-R23 to workflows: the run is not in the
  task store and uses no MAF checkpoint; interruption or failure ends it, a retry is a new run, and no tool is re-executed automatically. An interrupted run is incomplete
  (`Cancelled`, `Failed`, `LimitReached`, `NothingInvestigated`; a start with no end in the audit chain), never `Completed`. Checkpoint and resume may be introduced later
  by an explicit operator decision that keeps tool authority with bOps. The normal resume of `AgentRunner` is unchanged.
- Operations with effects, approvals, remediation, journal, verification; an API or UI host; live benchmarks.
- Persistence of the run's evidence: originals live in memory in the run's ledger for the report only and are not declared persistent; the audit chain holds bounded summaries.

## Adding a specialist

Register a `DiagnosticSpecialist` (name, description, capabilities, instructions, a selector over the Read tool manifests) in the host's `DiagnosticSpecialistCatalog`.
Nothing else changes: the triage prompt lists it by its declarations, its tool set is frozen per run, and it is reported unavailable (never an error) when no tool matches.
The CLI registers `system` (first-party system, service, network, storage, scheduler, identity and Docker families) and `postgresql`, which becomes available when `postgres.*`
read tools are registered; no PostgreSQL package exists in this repository.
