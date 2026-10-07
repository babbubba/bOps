# ADR-0047 — Bounded planned-step argument correction

Status: **Accepted and implemented — 2026-10-07**
Date: 2026-10-07
Refines: [ADR-0046](0046-step-scoped-tool-routing-and-transactional-replanning.md) (does not supersede it)

## Context

ADR-0046 made `PlannedStep.ExpectedTool` binding for execution-step tool visibility: an execution request offers only the
current planned step's exact tool, and never the full catalog. It did not change how a planned step is *consumed*.

Before this ADR, every executed step that did not trigger a replan advanced the planned-step cursor. An ordinary tool
failure deliberately does not replan (rule C8), on the assumption that "the model already sees that observation on its very
next turn and routinely corrects course (a bad argument, say)". ADR-0046 broke that assumption for argument-validation
failures: after the cursor advanced, the next request offered only the *next* planned tool, so the model could no longer
call the tool whose arguments it needed to correct.

A real troubleshooting task (`9711a641-22eb-46c7-a747-66ac7490123f`) showed exactly this:

1. the plan was `system.stability`, `system.crashes`, `system.events`, …;
2. step 0 called `system.stability` successfully;
3. step 1 was offered only `system.crashes`; the model chose it correctly but sent `sinceDays` in `raw` mode, where the
   tool contract bounds raw mode by `sinceMinutes`. The runtime recorded `ToolFailureKind.Validation`;
4. a plain failure does not replan, so the cursor advanced and the crashes step counted as consumed;
5. the next request offered only `system.events`. The model tried to correct the crash query, emitted `system_crashes`, and
   was rightly rejected as an unknown, not-offered tool.

The `system_crashes` rejection is correct fail-closed behaviour and stays. The defect is that a validation failure on the
exact intended tool consumed the planned step.

## Decision

### 1. One correction for the exact planned tool

When the current planned step has a usable `ExpectedTool`, the model called exactly that offered tool, the call reached
runtime argument validation (including arguments the provider adapter reported as malformed), and the result is
`ToolOutcome.Failure` with `ToolFailureKind.Validation`:

- the failed step is persisted, audited and accounted like any other step (its full `ToolCallResult`, its model calls,
  the step and token budgets);
- the planned-step cursor does **not** advance and no replan happens;
- the next execution request offers exactly the same single native tool. No other tool is added, and the catalog is
  never restored;
- the model sees the validation failure in its history, and the step prompt adds one fixed sentence asking it to call the
  same offered tool once more with corrected arguments.

### 2. Outcomes of the correction

- **Valid arguments:** the call goes through the normal runtime path: policy, entitlement, approval, execution and
  verification. Any outcome other than another argument-validation failure on that tool consumes the planned step exactly as
  before. Deviations (denial, rejection, timeout, refutation) still replan.
- **A second argument-validation failure on the same planned tool:** no third attempt. It triggers the existing replan
  path, with zero native tools (ADR-0038/ADR-0046), unchanged replan budgets and accounting, and ADR-0046 §4 transactional
  replanning (a doubly malformed candidate keeps the last accepted plan and fails through `MalformedResponse`).
- **Any other tool name:** unchanged ADR-0046 enforcement. The call is not offered, so it is rejected as `UnknownTool`
  (a deviation that replans). No alias, fuzzy match or rewrite is applied.

The correction never increments a replan counter. Only an accepted replan does, as before.

### 3. The position is derived from persisted typed data

The cursor, the spent correction and "replan required" are one runtime value (`PlannedStepPosition`). It is a pure fold over
the persisted steps of the current plan revision, oldest first, and the live loop advances it with the same function. A
step is an argument-validation failure on the planned tool only when all of these hold:

- `PlanStep.ToolCall.ToolName` equals the current `ExpectedTool` (ordinal);
- `ToolCall.ToolNameError` is `null` (an unknown or not-offered call is refused with it set, so it never qualifies, even
  though its rejection also carries `ToolFailureKind.Validation`);
- `Result.Outcome == Failure` and `Result.FailureKind == Validation`.

Once the correction is spent, the live loop's deviations are recognised from typed data too, and each one requires a
replan: a call of any other tool or one with a `ToolNameError`; a runtime refusal (`ToolFailureKind.Authorization`,
assigned only by the runtime for policy, operator, envelope and entitlement denials); `ToolOutcome.Timeout`; and
`PlanStep.VerificationStatus == Refuted`. The live loop replans on these anyway; recording the requirement in the position
matters when that replan does not commit (ADR-0046 §4) and the task is resumed.

Neither error text nor the step description is parsed. Synthetic failure steps carry no plan revision and do not count.

Resume therefore reconstructs:

- **after a first validation failure:** the same planned step, its single tool, and an already-spent correction, so another
  validation failure replans;
- **after a successful correction:** the following planned step;
- **after a spent correction followed by a second validation failure or a deviation, whose replan did not commit** (for
  example a malformed replan, ADR-0046 §4): the resumed attempt replans first, before any operational tool is offered. That
  replan is triggered by the last persisted step of the stale plan revision, not by a later synthetic failure step;
- **after a record written before this ADR**, where a validation failure was followed by an executed call of another tool:
  the history contradicts the one-correction rule, so the runtime fails closed and replans first rather than guess which
  planned step was served.

### 4. Unchanged

ADR-0046 step-scoped visibility (one native tool for an executable step, zero otherwise, never the full catalog);
`ExpectedTool` as intention only; runtime ownership of policy, entitlement, approval, validation, execution and
verification; ADR-0038 first-call-wins and `UnexecutedToolCalls`; transactional replanning; `EvidenceRead/v1`; context
compaction; provider fallback; fail-closed resume; token, step, replan, lifetime and duration budgets; auditing of every
validation failure.

## Alternatives considered

- **Persist a new `PlanStep` field** (for example a cursor or correction marker). Rejected: the existing typed data already
  identifies the case exactly, and `PlanStep` is part of the frozen `bOps.Abstractions` surface. No schema change.
- **An in-memory retry counter.** Rejected: a resume would reset it (an unbounded retry across attempts) or consume the
  step.
- **Treat every validation failure as a deviation and replan immediately.** Rejected: it spends a planning call and a
  replan budget on what is usually a one-argument correction for the same tool.
- **Offer the previous and the next tool together.** Rejected: it contradicts ADR-0046's one-tool step view.
- **Alias or fuzzy-match names such as `system_crashes`.** Rejected: exact, fail-closed name matching is deliberate.

## Consequences

- A planned step can take at most two execution steps for argument validation. Both are ordinary, budgeted, audited steps.
- Live execution and resume compute the position from the same persisted steps, so a crash cannot reset or extend the
  correction.
- A resumed task whose current plan already exhausted its correction spends one replan before offering a tool.
- No public contract, tool manifest, provider wire format, persistence schema or task status changes.
