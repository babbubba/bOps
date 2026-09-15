# ADR-0014 — An explicit PLAN/REPLAN phase, separate from the per-step model call

Status: Accepted

## Context

`agentic/00-project-spec.md`'s roadmap names V0.2 "Explicit agent loop with replanning." V0.1's
`AgentRunner` already implements a REQUEST → UNDERSTAND → PLAN → EXECUTE → OBSERVE → EVALUATE
loop, but PLAN was never its own step: every iteration asked the model the same open-ended
question ("what's next?"), so "the model is still following its original approach" and "the
model just changed its mind" were indistinguishable in the code, the audit log, and
`TaskState`. `agentic/01-architecture-rules.md` §C already named this directly: `AgentRunner`,
not `AgentPlanner`, "because planning and execution are not yet separated in V0.1."

## Decision

- Add `PlannedStep` and `AgentPlan` to `bOps.Abstractions` (`Planning.cs`): a plan is a
  revision number, a rationale, and an ordered list of intended steps (each a description and
  an optional expected tool name — a stated intention, never a tool call with arguments).
- `AgentRunner.RunAsync` now opens every task with a dedicated `CreatePlanAsync` call: a
  `ModelRequest` built specifically to elicit a JSON plan (no tool-calling), parsed leniently
  from `ModelResponse.TextResponse`, with one bounded retry on a malformed reply — the same
  pattern already used for JSON-schema tool-call fallback (plan §3.1.1). If the model still
  cannot produce a parseable plan, the task proceeds with an empty plan (`Steps: []`) rather
  than failing — rule C1 applies to planning exactly as it applies to everything else in the
  loop.
- Every per-step call now includes the current plan's remaining steps in its system prompt, so
  the model's tool-call choice is plan-aware without the runtime auto-executing a planned step's
  tool name blindly — principle 1 still holds: the model proposes the concrete call, with real
  arguments, every time.
- EVALUATE (rule C8, new in `agentic/01-architecture-rules.md` §C): after a step executes, the
  runtime replans — a new `ReplanAsync` call producing `AgentPlan` revision N+1 — when either:
  1. the step's authorization was `PolicyDenied` or `UnknownTool`, or its outcome was
     `Timeout` — outcomes a plan could not have anticipated and that repeating with different
     words will not fix; or
  2. the model proposes a tool call after every step the current (non-empty) plan named has
     already been attempted — the plan under-covered the task.

  A plain tool `Failure` is deliberately **excluded**. The model already sees a failure as its
  very next observation and routinely self-corrects (a bad argument, say) without a new plan;
  replanning on every minor failure would make the loop replan-happy for no benefit, and would
  have broken the existing `RunAsync_HandlesAToolThatThrows_...` test's premise that the model
  simply notices and moves on.
- `AgentRunnerOptions.MaxReplans` (default 3, same anti-runaway spirit as rule C5) bounds this:
  exceeding it ends the task as the new `AgentTaskStatus.ReplanLimitReached`, distinct from
  `PolicyBlocked` (stuck retrying the *same* tool) and from `MaxStepsReached` (never hit a
  terminal state at all) — a task that keeps needing new plans faster than it makes progress is
  its own, differently-diagnosed failure mode.
- `TaskState` gains `Plans` (every revision produced, distinct from `Steps`, which records
  tool-call iterations, not planning itself). `PlanStep` gains `PlanRevision` (nullable, for
  completeness — no code path leaves it null after this change, but the type does not assume a
  plan always exists).
- Model calls made for planning and replanning are audited exactly like step calls (rule S9),
  through the same `CallModelAsync` — now refactored to accept a prebuilt `ModelRequest` instead
  of building one from `history` itself, so all three calling contexts share one audited path.
  `StepIndex` on that audit event is `-1` for the initial plan (it happens before step 0) and
  the triggering step's index for a replan.

## Alternatives considered

- **Extend `IChatModel`/`ModelResponse` with a `Plan` field the model returns alongside its
  tool call**, so planning and execution collapse into one call. Rejected: it would change the
  wire contract for every provider adapter (native tool-calling and the JSON-schema fallback
  both), is untestable end-to-end in this environment (no real provider API key is or should be
  configured, per rule S6 — everything here is verified against `FakeChatModel`), and conflates
  two different questions ("what should happen over the whole task" vs. "what should happen
  right now") into one response shape for a benefit — one fewer HTTP round trip per step — that
  does not offset the risk.
- **Have the runtime execute `PlannedStep.ExpectedTool` directly**, skipping the per-step model
  call once a plan exists. Rejected: a planned step names an *intention*, not concrete
  arguments; the model must still be asked for those, and auto-driving tool execution from a
  plan without the model in the loop for each call would weaken principle 1 ("the LLM never
  touches the machine... it returns a structured intent") into "the LLM approves a checklist
  once and the runtime free-runs it."
- **Replan on every step**, matching the README's diagram literally (EVALUATE → REPLAN/FINAL
  every iteration). Rejected as needlessly expensive: it doubles model calls for the common case
  where the plan is still correct, with no corresponding benefit — the diagram describes what
  the loop *considers* each iteration, not that it must call the model twice to do it.
- **Replan on any tool `Failure`, not just `Timeout`/`PolicyDenied`/`UnknownTool`.** Rejected:
  see "A plain tool `Failure` is deliberately excluded" above.
- **A new `AgentPlanner` class/project.** Rejected for V0.2 specifically as scope creep per
  `agentic/05-workflow.md`'s scope-discipline rule: nothing in the roadmap asks for planning and
  execution to become physically separate components yet, only for planning to become an
  explicit, inspectable phase of the one loop that already exists. `CreatePlanAsync`/
  `ReplanAsync` are private methods on `AgentRunner`, exactly as explicit and testable as the
  rest of the loop already is.
- **Extend `ModelCallAuditEvent` with a "call kind" (plan/replan/step) field.** Considered, to
  make the audit log say *why* a model was called, not only that it was. Deferred: it is a real
  gap, but another `bOps.Abstractions` schema change is not required to ship V0.2, and
  `bops.plan_revision` is already visible on the OpenTelemetry span for the same information at
  lower cost. Left as a flagged follow-up rather than done as a side effect of this change.

## Consequences

- A task's planning history is now a first-class, auditable part of `TaskState`, not an artifact
  reconstructable only by re-reading the raw chat history.
- Every existing `AgentRunnerTests` test needed its canned `FakeChatModel` response sequence
  updated to include the new leading plan call (and, for tests whose scenario now triggers a
  replan, one more canned response for it) — a real, expected cost of changing the loop's shape,
  not an incidental test-fragility problem.
- `TaskState`/`PlanStep` are breaking shape changes, acceptable under D-012: `bOps.Abstractions`
  stays on `0.x` until V1.0 specifically so changes like this do not require a migration story.
