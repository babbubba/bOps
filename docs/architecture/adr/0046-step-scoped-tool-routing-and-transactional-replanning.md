# ADR-0046 — Step-scoped tool routing and transactional replanning

Status: **Accepted and implemented — 2026-10-07**
Date: 2026-10-07
Amended: 2026-10-11 by AF-05H — "a premature final replans" (end of this document)
Amended: 2026-10-12 by AF-05H2 — "an early-final replan without a plan" (end of this document)

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

## Amendment — internal control-plane functions (PRE-3A, 2026-10-09)

The single-`ExpectedTool` visibility rule governs **operational** authority. Internal control-plane functions may coexist with it, because they do not grant operational authority and cannot execute package operations.

- The only control function is `runtime.evidence_read` (typed arguments `step`, `source`, `offset`, `length`; read-only; current task taken from the runtime context, never from the model). It is declared by the runtime, is never in the `ToolRegistry`, passes no policy, entitlement or approval, and the `runtime.` namespace is reserved so no package can register a tool in it. It is offered beside the step tool only when persisted evidence exceeds `MaxObservationCharacters`; the step tool view and `ConstrainToStepTool` are unchanged.
- A control read never completes a planned step, never advances the cursor, and spends neither the PRE-2 semantic correction nor the ADR-0047 argument correction. It is audited as `EvidenceReadAuditEvent`, counts against the existing four-read limit, and writes no `PlanStep`.
- Same-turn rule (ADR-0038 first-call-wins, conservative): when a response carries a control read, the read is served and no other call of that response executes; each other call id is answered `Not executed` and the model is asked again with the evidence. At most one operational execution per model turn is preserved, and a read never makes a wrong operational tool valid.
- An unrecognised `runtime.*` name is a control protocol error (audited `Malformed`), never a package tool or final prose.
- The legacy `EvidenceRead/v1` text directive remains a compatibility path and converges on the same `EvidenceRead.Read` handler. Terminal-response hardening of leaked tool markup is PRE-3B.

### PRE-3B1 addendum (2026-10-09)

- A control function is valid only when advertised in the model-call control view for that turn (the same request that carries `AvailableTools`). A `runtime.evidence_read` call that was not offered is a control protocol error (`NotOffered`): nothing is read, no step is written, the planned cursor does not move and no PRE-2/ADR-0047 correction is spent. `runtime.*` names the runtime does not implement stay control protocol errors, never package tools or final prose.
- Runtime control/tool protocol artifacts are not valid user-facing terminal responses. A candidate final response whose whole shape is a tool/control envelope (`<tool_call>` / `<function=` markup, or a serialized `{name, arguments}` call object) is not accepted; the model gets one bounded terminal correction, and if the artifact persists the task fails rather than completes. Prose that merely mentions a function name is unaffected. What happens when a disclosure re-ask fails is PRE-3B2, not decided here.

## Amendment — a premature final replans (AF-05H, 2026-10-11)

**Status: Accepted.** Live runs on a real 64k-window model completed two tasks whose accepted plan still had unresolved
executable steps: one harmless step (LIVE-2) and five diagnostically relevant ones (LIVE-3, whose own answer said the cause could
not be determined without further analysis). Both ended `Completed`, and the execution-plan projection then showed the unresolved
suffix of the active revision as failed. Accepting any tool-free reply as completion was the cause.

1. **Acceptance rule.** A final candidate (a reply with no tool call) is accepted directly only when the current plan revision has
   no unresolved executable step: the persisted `PlannedStepPosition` fold, after `ConditionalSteps.Settle`, has its cursor at or
   past the end of the plan. An empty or fully consumed plan, or one whose remaining steps were skipped deterministically by
   ADR-0050 conditions, therefore completes as before.
2. **Otherwise the runtime replans.** The reply is not completion. The paid call is recorded once on a runtime-authored, non-tool
   **bookkeeping step** (`Premature final response; plan re-evaluation required`): it carries the call's `ModelCalls` (audited and
   token-accounted like every call), a runtime-authored observation naming only the unresolved planned positions and their
   catalog-validated tool names, no tool call, no result, no planned-step index. It is never evidence (not addressable, never a
   limitation), never a final-response step, and it consumes no planned step; it counts as one step of the attempt like any other
   paid step. The answer's text is not repeated to the replan.
3. **The replan is the normal governed replan** of §4: zero native tools, through the broker, audited, token-counted, counted
   against the attempt and lifetime replan budgets, transactional (a malformed reply after its one retry leaves the accepted plan in
   force and fails the attempt). Its protected trigger turn is the bookkeeping observation plus the fixed directive: *the model
   indicated the goal may already be achieved, but the accepted plan still has unresolved executable steps; re-evaluate; return an
   empty `steps` array if no further operational work is needed, otherwise the revised remaining plan.*
4. **Empty replan.** `{"steps":[]}` is an accepted revision. The next normal step call offers no operational tool, and the final
   it produces — a new call in the context of the empty revision, never the earlier premature reply — completes the task.
5. **Non-empty replan.** Execution continues with the new revision.
6. **Resume.** The fold treats a bookkeeping step as `ReplanRequired` for every plan contract, so an attempt that ended after the
   bookkeeping step and before the replan (for example at its step limit) replans first on resume, with the same runtime-authored
   trigger turn; after an accepted replan the new revision starts at its first step.
7. **Projection.** The unresolved suffix of a revision replaced by such a replan is `Superseded`, as for every accepted replan; an
   empty active revision has no steps. While the replan is still owed, the unattempted step stays `Pending`, never `Failed`. No
   new status is introduced.
8. **Bounds.** No planned tool is ever executed without a model call, MAF decides nothing about completion, and the replan budget
   bounds a model that keeps answering early.

The step prompt now says that while planned steps remain unresolved the runtime re-evaluates the plan before it accepts a final
answer. Tests: `EarlyFinalReplanTests`, `LiveRegressionTests` (AF-05H fixtures). Evidence:
`agentic/_tasks/2026-10-11-af-05h-live-regression-hardening.md`.

## Amendment — an early-final replan without a plan (AF-05H2, 2026-10-12)

**Status: Accepted.** Live BENCH-00/BENCH-01 runs ended `Failed` when the early-final replan of §3 of the AF-05H amendment
stopped at the provider's output limit (`finish_reason=length`, 4096 completion tokens) without a parseable plan, although the
next planned step was a harmless read. AF-05H2-R1 (`3d2afb7`) added a bounded, runtime-authored continuation of the persisted
plan; BENCH-01 then showed it refused every task whose history contained a call the runtime had rejected for invalid arguments
(`SemanticMismatch` or `ArgumentValidationFailure`) even though a matched, successful call of the same planned step followed.
R2 corrects that and makes the decision explicit.

1. **Scope.** Only a replan triggered by a rejected early final, whose reply was received and was not a plan, is eligible.
   Provider exceptions, other C8 triggers, budgets, cancellation and accepted plans (including empty ones) are unchanged. A
   received `length` reply is not retried at the same limit; other malformed replies keep the single corrective retry.
2. **Continuation proof.** The persisted plan continues at its unchanged cursor only when: the plan has the semantic contract;
   the early final is the latest step of the revision; the fold over the earlier steps is coherent, has an unresolved step and no
   spent correction pending; the pending step is unconditional, and its tool is registered, read-only and offered in the current
   tool view; every earlier tool call of every revision has a result, used a registered read-only tool, was not denied, did not
   time out and was not refuted; and every earlier failure is a validation-class rejection of the planned tool in the current
   revision that a later `Matched`, successful call of the same tool at the same planned index resolved. Anything else is
   refused. A validation failure corrected only in a later revision stays unresolved.
3. **Anti-loop and cost.** A continuation of a revision must be strictly further along than any earlier continuation of the same
   revision (`RepeatedWithoutProgress` otherwise). Once an early-final replan of a revision stopped at the output limit and was
   continued, a later early final of that revision is not sent to the model again: the proof above decides directly, with no
   reserved replan. Early-final replan attempts that make a paid call are reserved against the attempt and lifetime replan budgets
   before the call; continuations are never charged.
4. **Decision record.** Every evaluation is persisted on the early-final step as an additive, optional
   `EarlyFinalRecoveryDecision` (accepted, typed `EarlyFinalRecoveryReason`, revision, cursor, blocking step, the resolved
   validation steps, whether a paid replan was skipped). An acceptance writes a runtime-authored continuation record and the
   `EarlyFinalPlanContinued` lifecycle audit event; a refusal writes the `EarlyFinalPlanContinuationRefused` lifecycle audit event
   with a stable snake_case refusal code, then fails the attempt as a malformed-response model failure. Steps written before this
   amendment have no decision and fold as before.
5. **Authority.** A continuation grants nothing: the model still has to call the pending tool, which then passes validation,
   step authority, policy, entitlement, approval and the mutation journal as any other call. Resume re-proves a persisted
   continuation against the current tool view and the journal admission still refuses an unknown mutation outcome.
6. **Model calls.** Replan calls of an early final are kept on its step as `EarlyFinalReplanCalls`, disjoint from its own
   `ModelCalls`; token accounting, the API projection and the web UI read both.

Tests: `EarlyFinalReplanTests`, `EarlyFinalValidationRecoveryTests` (BENCH-01 B02/B03 shapes). Evidence:
`agentic/_tasks/2026-10-12-af-05h2-r1-implementation.md`, `agentic/_tasks/2026-10-12-af-05h2-r2-implementation.md`.

## Amendment — a failed initial plan fails the attempt (AF-PLAN-01, 2026-10-12)

**Status: Accepted.** BENCH-01 R2 B02 (`2dbb7b3`) proposed a fenced plan whose `system.events` step had `expectedArguments`
`limit: 1000` against the declared maximum of 500. The fence was parsed; the step failed `SemanticExpectedArguments.Validate`.
The generic re-ask did not name the problem, the model repeated it, and the runtime continued with an empty plan as ADR-0014 and
ADR-0039 §8 prescribed. Under §2 of this ADR an empty plan offers no native tool, so that continuation cannot make progress: the
model spent two 4,096-token replies on reasoning and the attempt ended `EmptyResponse` without any tool call.

1. **Exact correction.** A rejected initial plan is re-asked with the runtime's own validation problem (bounded to 512
   characters) ahead of the standing format instruction. A reply cut at the output limit with no text gets an output-limit notice
   instead, with no empty assistant turn. The replan corrective re-ask carries the problem the same way; its count and the
   AF-05H2 early-final rules are unchanged.
2. **Bound.** At most two corrections follow the first reply (`AgentRunner.MaxPlanCorrections`). A correction whose reply
   leaves the identical problem ends the corrections: an identical request would fail the same way. Each call is recorded,
   audited and charged as before, and the token budget is checked after every call.
3. **No plan-less continuation.** When no valid plan results, the attempt fails as `ModelFailure`/`MalformedResponse` with a
   synthetic failure step `Planning failed: … Last problem: …` carrying the planning calls. No plan is persisted, so a resume plans
   again (ADR-0040 §7). This supersedes the empty-plan degradation of ADR-0014 and ADR-0039 §8 for the initial plan; an accepted
   empty plan is unchanged.
4. **Framing only.** The plan text is taken from the first markdown fence that holds a JSON object, otherwise from the outermost
   braces. Strict JSON, schema, `expectedArguments`, activation and discriminator validation are unchanged.
5. **Step loop.** The one empty-reply re-ask (`EmptyFinalResponseRetries`) tells an output-limited model to call the offered tool
   or answer without further deliberation; its bound and the `EmptyResponse` terminal are unchanged.

Tests: `PlanningCorrectionTests` (B02 shapes). Evidence: `agentic/_tasks/2026-10-12-af-plan-01-planning-robustness.md`.

## Amendment — coherent tool names, in-step name correction and no repeated completed checks (AF-TOOLROUTE-01, 2026-10-12)

**Status: Accepted.** BENCH 1×3 on AF-PLAN-01 (`1292266`, archive `20261012T111826Z`, OpenRouter Nemotron) ended B03
`ReplanLimitReached` after all five planned read checks had run. The persisted requests show three causes:

- **Protocol (MAF boundary).** `MafChatTurnMapper` dropped `ModelToolCall.ToolNameError`/`ArgumentsError` on the history
  round trip of the context projector. After the model called the not-offered `system_crashes`, later requests carried that
  raw name as an ordinary canonical call: adapters sent it verbatim instead of the HARDEN-1 placeholder, and it collided with
  `system.crashes`, whose alias became `system_2Ecrashes`. The same tool was published under two names; the model copied the
  escape (`system_2Eevents`) and was refused again. Names were never accepted loosely: every refusal was correct.
- **Step prompt.** With every planned step resolved, the prompt said both "do not call an operational tool" and "propose the
  concrete tool call for the next unfinished step".
- **Replan.** The replan saw only "not a tool that was offered", read it as a naming error and re-proposed the five completed
  checks with identical arguments. Each accepted revision restarted at its first step and re-ran them.

1. **Lossless boundary.** Adapter rejections travel across the MAF boundary as reserved `AdditionalProperties`
   (`bops.toolNameError`, `bops.argumentsError`) and come back unchanged. A rejected raw name therefore never contributes to a
   request's alias map, and a canonical tool keeps one wire name for the whole task. No alias, prefix or fuzzy match is
   introduced; `ToolWireNames` is unchanged.
2. **In-step name correction.** A call refused by name (adapter or §1 step view) while the current planned step's single tool
   is offered, as that step's **first** deviation, is classified `UnofferedTool` (append-only persisted value) and keeps the
   step: the next call offers the same single tool with a fixed correction sentence. Nothing runs, the refusal is audited as
   `UnknownTool` as before, policy is never consulted. A second refusal, or one after a spent ADR-0047/0050 correction,
   replans as before. The persisted fold carries the budget (`ToolCorrectionSpent`), so resume continues identically; the
   AF-05H2 early-final continuation treats it as a pending correction and refuses.
3. **Completed plan.** With every planned step resolved the prompt says so: answer from the evidence, or propose the one new
   check needed, which will not run and leads to a replan. The replan that follows such a proposal gets a runtime-authored turn
   stating that the plan was complete and that the call was refused for that reason, not for its name.
4. **Completed checks.** Every replan prompt lists the task's completed read checks (successful, not partial/unavailable, not
   refuted, read-only, from persisted steps; sensitive values redacted; bounded to 2,048 characters). A replan step whose tool
   and complete `expectedArguments` equal one of them (absent arguments equal `{}`; conditional steps excluded) is a validation
   problem of the existing corrective re-ask: `Plan step N repeats completed task step K (tool with identical arguments)…`.
   Any different argument, a different tool or an empty plan is accepted unchanged. This is **not** §6 suppression: nothing is
   dropped, merged or rewritten, and mutations, failures and partial results never count. A model that repeats after the
   correction fails the attempt as `ModelFailure`/`MalformedResponse` with the plan preserved (early-final replans keep the
   AF-05H2 continuation rules); replan counts and budgets are unchanged.

Not changed: provider adapters and `ToolWireNames`, policy, approval, entitlement, mutation journal, audit schema, accounting,
resume rules, the AF-05H2-R2 and AF-PLAN-01 recoveries, EvidenceRead, the collector. MAF orchestration is not used for
routing or replanning (ARCH-02 decides). Tests: `ToolRoutingTests`, `ToolRoutingRegressionTests` (B03 on the real adapter and
MAF projector), `MafContextTests`. Evidence: `agentic/_tasks/2026-10-12-af-toolroute-01-tool-routing.md`.
