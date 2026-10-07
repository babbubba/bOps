# Runtime follow-up — step-scoped tool routing and transactional replanning

| | |
|---|---|
| ADR | [ADR-0046](../../docs/architecture/adr/0046-step-scoped-tool-routing-and-transactional-replanning.md) |
| Baseline | `main` at `61744acdd48d8a50fc4cc682683e9da9d2bfb482` |
| Suggested branch | `fix/step-scoped-tool-routing-replan-safety` |
| Status | **Complete — ADR accepted and implementation validated 2026-10-07** |
| Priority | High — live local-model runtime follow-up |
| Recommended effort | medium/high reasoning, bounded implementation |

## Objective

Reduce execution-step prompt cost and tool-selection entropy without changing evidence/context architecture, and prevent a malformed replan from replacing the last valid plan with an empty one.

This is a post-HARDEN-14 follow-up. Do not reopen or renumber HARDEN-1…14.

## Operator decisions already fixed

1. Expected tool is binding for step visibility: expose only the current plan step's exact `expectedTool`, intersected with the existing authorized view.
2. Multiple emitted calls keep ADR-0038 behaviour: execute first only; persist later calls as not executed.
3. Replan is transactional: one corrective re-ask; a second invalid response does not commit `Steps=[]`; preserve prior plan and fail the attempt resumably.
4. No duplicate suppression yet.
5. No new Evidence Store or compaction mechanism; keep HARDEN-8 and `EvidenceRead/v1`.
6. No manual or automatic model routing by role.
7. No new broad E2E/Qwen replay in this packet.
8. Model/provider statistics are a separate later packet; ranking stars and charts are not in its first UI scope.

## Current code anchors

At the baseline:

- `AgentRunner.BuildStepRequest` passes `ToolViewFor(delegation)` to every execution-step `ModelRequest`.
- `ToolViewFor(null)` returns `registry.GetAvailableManifests()`.
- `plannedStepCursor` already identifies the current planned step.
- plan/replan already use `NoNativeTools`.
- `ReplanAsync` performs one corrective re-ask, then returns a new empty `AgentPlan`.
- `ContinueAsync` accepts that plan, appends it, increments replan counters and resets `plannedStepCursor`.
- resume already uses the last persisted accepted plan; ordinary `Failed` tasks are governed by ADR-0040.

## Implementation sequence

### A — architecture gate

- Review ADR-0046 against ADR-0014, ADR-0038, ADR-0039, ADR-0040, ADR-0042/HARDEN-8 and ADR-0045.
- Do not implement until ADR-0046 is accepted.
- On acceptance, append the next decision-register entry; do not rewrite prior decisions.

### B — step-scoped tool view

Implement one narrow runtime helper deriving the current step tool view from:

```text
ToolViewFor(delegation)
INTERSECT
current PlannedStep.ExpectedTool
```

Requirements: exact canonical match; never widen authority; never fall back to all tools; normal executable step -> one schema; plan/replan -> zero native schemas; no provider-specific handling; planner's tool name is not auto-executed.

Adjust planning/replanning instructions and candidate validation so every non-empty executable step has a non-empty `expectedTool`. Empty plan means no further planned tool execution.

When the plan is exhausted, or a current/persisted step has no usable authorized expected tool, replan before executing another operational tool.

### C — transactional replan

Refactor `ReplanAsync` so a candidate is not accepted until validation succeeds.

On a second malformed result:

- retain model-call records and token accounting;
- do not append a plan revision;
- do not reset the cursor;
- terminate through existing `Failed / ModelFailure / MalformedResponse`;
- preserve the prior accepted plan for ADR-0040 resume.

Do not add a new public status.

### D — preserve existing contracts

No intentional behaviour change to first-call-wins/`UnexecutedToolCalls`, provider wire aliases, policy/approval/entitlement, provider fallback pinning, evidence compaction/retrieval, token/lifetime accounting or final evidence disclosure.

## Explicitly out of scope

- Performance Registry implementation/UI.
- Ranking, stars or charts.
- Manual/automatic per-role model routing.
- Tool relevance groups.
- Duplicate suppression.
- New Evidence Store/search API.
- Further compaction.
- Arbitrary max-tool-call limits.
- Qwen/LlamaCpp-specific prompts.
- New broad E2E/regression scenarios.

## Validation

Do not add a new broad test scenario.

Run existing relevant runtime/provider/planning/replan/resume/HARDEN-8/HARDEN-14 suites and Release build. Update existing assertions only where ADR-0046 directly changes expected behaviour.

After deployment, run a real local-model troubleshooting task before widening scope.

## Stop conditions

Stop rather than widen scope if implementation requires a provider wire change, new public tool/evidence contract, new persistence subsystem, provider-specific behaviour, per-role model selection, new broad E2E fixture or a new architectural decision outside ADR-0046.

## Completion report

```text
RUNTIME FOLLOW-UP COMPLETE | BLOCKED
BASE / BRANCH / COMMITS
ADR-0046: ACCEPTED / BLOCKED
STEP TOOL VIEW: <summary>
REPLAN FAILURE: <summary>
EXISTING TESTS: <results>
RELEASE BUILD: <result>
NEW E2E: NONE
OUT-OF-SCOPE ITEMS: unchanged
PUSH: <yes/no>   PR: <yes/no>
```
