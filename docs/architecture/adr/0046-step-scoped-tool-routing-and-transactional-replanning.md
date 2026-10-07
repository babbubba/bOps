# ADR-0046 — Step-scoped tool routing and transactional replanning

Status: **Accepted and implemented — 2026-10-07**
Date: 2026-10-07

## Context

The V1.3.x reliability train is closed through HARDEN-14. A later live troubleshooting run with a small/local model exposed two runtime behaviours that compose poorly:

1. execution-step model calls receive the complete authorized native-tool schema set even when the current `PlannedStep.ExpectedTool` names one intended tool;
2. after one corrective retry, a malformed replan is converted into a new empty `AgentPlan` and committed.

With about one hundred registered tools, repeating every schema on every execution step wastes prompt budget and increases tool-selection entropy. Plan and replan calls already expose no native tools and receive only the bounded textual catalog from ADR-0038.

A malformed replan is not equivalent to an intentional valid plan with `Steps=[]`. Replacing the last valid plan with an empty one destroys routing state and can make later execution wander.

This ADR is deliberately narrow and does not reopen HARDEN-14.

## Existing decisions preserved

- ADR-0038: plan/replan expose no native tools; if one execution turn emits multiple tool calls, only the first is executed and later calls remain `UnexecutedToolCalls`.
- ADR-0039: provider-neutral model-failure classification, bounded retry accounting and one corrective re-ask remain.
- ADR-0040: ordinary `Failed` tasks remain resumable subject to existing policy/budgets.
- ADR-0042 and ADR-0014 HARDEN-8 amendment: evidence persistence, bounded history, compaction and `EvidenceRead/v1` remain unchanged.
- ADR-0045: execution-pinned provider configuration/fallback remain unchanged.

## Decision

### 1. ExpectedTool is binding for execution-step tool visibility

`ExpectedTool` remains a plan intention, never a concrete call and never a source of arguments. The executor model still emits the actual `ModelToolCall`; policy, entitlement, approval, argument validation, execution and verification remain runtime-owned.

For a normal executable step:

```text
StepToolView =
    AuthorizedToolView
    INTERSECT
    exact(CurrentPlannedStep.ExpectedTool)
```

`AuthorizedToolView` is the existing `ToolViewFor(delegation)`.

Rules:

- a normal executable step receives exactly one native tool schema;
- `ExpectedTool` may only narrow authority, never widen it;
- canonical tool-name matching keeps existing ordinal semantics;
- missing, unavailable, unknown or unauthorized expected tools never cause fallback to the complete catalog;
- executor supplies all arguments as today;
- every proposed call still traverses the normal runtime safety path.

The step prompt states that the single offered tool is the current plan step's tool and should be invoked at most once. Choosing another operational tool belongs to replanning, not executor-side search.

### 2. Plan-step executability

For newly produced plans, every non-empty step is executable and must name a non-empty `expectedTool`. An empty `steps` array means no further tool execution is planned.

This tightens semantics without changing the JSON shape.

A persisted legacy plan, or a candidate/current step without a usable authorized `expectedTool`, must not expose the full catalog. Runtime requires replanning before another operational tool can execute.

If the current plan has no remaining executable steps, Runtime replans before executing another operational tool. A valid replan may return an empty plan when no further tool work is needed; the following model call has no native tools and can produce the final answer.

### 3. Multiple tool calls remain first-call-wins

ADR-0038 is unchanged:

- execute only the first emitted call;
- persist later calls in `PlanStep.UnexecutedToolCalls`;
- preserve provider-valid history with existing not-executed results;
- do not spend a corrective model call solely because multiple calls were emitted.

### 4. Replanning is transactional

Let `Pcurrent` be the last accepted plan.

1. Ask for candidate `Pc1`.
2. If valid, commit it.
3. If malformed or semantically unusable, perform the existing single corrective re-ask.
4. If `Pc2` is valid, commit it.
5. If `Pc2` is still invalid:
   - do not create or append an empty replacement plan;
   - keep `Pcurrent` as the last persisted accepted plan;
   - retain model-call records, auditing and token accounting;
   - end the current execution attempt as `AgentTaskStatus.Failed`;
   - use the existing model-failure terminal path with `ModelFailureKind.MalformedResponse`;
   - ordinary tasks remain resumable under ADR-0040.

A failed candidate never increments accepted plan history and never resets the cursor by pretending an empty plan was accepted.

Preserving `Pcurrent` is for durable state/resume; it does not authorize continuing indefinitely on a stale plan in the same attempt.

### 5. Initial planning is not redesigned here

The initial planning state machine is not broadened in this packet. Existing bounded malformed-reply handling remains except that subsequent execution must obey this ADR and may never regain the full tool catalog solely because routing information is absent.

If live evidence after this change shows initial-plan fallback remains material, that is a separate decision.

### 6. No duplicate suppression

No exact or semantic duplicate suppression is added. Residual duplication will be measured later by the Model & Provider Performance Registry.

### 7. Evidence/context are unchanged

No Evidence Store, evidence-search API or new compaction is introduced.

HARDEN-8 remains authoritative: complete `ToolCallResult.Output` stays persisted; model history may be compacted; `EvidenceRead/v1` retrieves bounded ranges from current-task persisted evidence; resume reconstructs context from persisted state.

The immediate prompt reduction comes from eliminating irrelevant native-tool schemas.

### 8. Model/provider routing is out of scope

No manual or automatic model selection by planner/replanner/executor/final response is introduced. Replan reliability will be measured later per provider + model + invocation purpose.

## Implementation boundary

Expected implementation is local to the model-driven runtime:

- `AgentRunner` step-view construction and step/replan control flow;
- planning/replanning instructions and candidate semantic validation;
- only minimal updates to existing tests whose expectations directly change;
- required documentation/decision alignment.

Do not introduce a new planner service, provider-specific branch, persistence subsystem, public tool or evidence abstraction.

## Validation scope

The operator explicitly chose not to add new broad E2E/Qwen regression scenarios in this first iteration.

Use the repository's existing relevant test suites and Release build. Existing assertions may be updated only where this ADR intentionally changes tool visibility or malformed-replan outcome.

After implementation, validate with a real local-model troubleshooting run before adding further context or deduplication work.

## Rejected alternatives

- Keep all authorized tools visible on every execution step.
- Expose expected tool plus a related-tool group.
- Re-ask solely because multiple tool calls were emitted.
- Convert failed replan to a new empty plan.
- Continue indefinitely on the old plan after replan failure.
- Add duplicate suppression now.
- Add model-per-role routing now.
- Add a new Evidence Store now.

## Compatibility

- No provider wire-format change.
- No tool manifest/package contract change.
- No evidence persistence migration.
- No new task status.
- Existing persisted plans remain readable; unusable routing fails closed through replanning rather than full-catalog exposure.

## Acceptance gate

Accepted by the operator on 2026-10-07 and implemented on
`fix/step-scoped-tool-routing-replan-safety`. The later Model & Provider Performance
Registry remains independent and is not a prerequisite.
