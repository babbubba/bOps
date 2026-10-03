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

## HARDEN-8 amendment — context and budget economy (Proposed; awaiting independent architecture review)

This amendment is not Accepted and authorizes no implementation yet. It supplements the original
PLAN/REPLAN decision; ADR-0039 model failure containment, ADR-0040 resume accounting and ADR-0042
evidence reasoning remain authoritative at their respective boundaries. The source baseline is
`main` at `ad4d196` (HARDEN-9 merged in PR #74). The 12-step, zero-replan diagnostic that used
269,452 tokens and the incident request with roughly 47 KB of schemas in 58 KB motivate the bounds.
Schema relevance/filtering is deferred and its fixed cost is only measured here.

### Decision summary

| Topic | Decision |
|---|---|
| Verbatim history window | `Agent:VerbatimHistorySteps = 3` completed tool-call steps; configurable 0–15. |
| Compact-step maximum | 256 UTF-16 characters including delimiter and newline, one record per older tool-call step; no historical output or argument values. |
| Evidence-id format | `ev1:<task-guid-N-lowercase>:<decimal-step-index>`; index is the persisted `PlanStep.Index`. |
| Full evidence retrieval mechanism | Internal Runtime conversation primitive `EvidenceRead/v1`, encoded as an exact model text reply; no registered operational tool. |
| Retrieval chunk bound | At most 4,000 UTF-16 characters of source evidence per read; at most four reads per executable step or one replan call. |
| Replan representation | Same three-step recent window plus 256-character older records; only the current plan revision, never all revisions. |
| ContextOverflow retry | On first classified overflow, rebuild with K=0 and retry once; a second overflow ends `Failed/ModelFailure/ContextOverflow`. |
| Normal MaxTotalTokens default | `Agent:MaxTotalTokens = 350000`, cumulative for the task lifetime. |
| Per-attempt duration default | `Agent:MaxAttemptDuration = 01:00:00`, monotonic elapsed time; nullable operator opt-out. |
| Persisted evidence modified? | NO. |
| HARDEN-9 digest source changed? | NO. |
| Tool-schema filtering included? | NO. |

### 1. Model-facing history and stable references

Before every step model call, including after resume, Runtime derives a new history from the current
task's canonical completed `PlanStep` list (saved without rewriting and reloaded on resume) in
ascending `Index` order. The initial goal remains first. Of the completed
tool-call steps, the newest K retain their existing assistant tool-call turn and delimited observation
verbatim, including the `UnexecutedToolCalls` responses required by ADR-0038. Older tool-call steps
are replaced by one runtime-authored user turn containing ordered compact records. A step without a
tool call contributes no historical turn, as in today's `RebuildHistory`. On each new step the window
moves; no compact text is saved over the original. Rebuild live history after every completed step,
not only on resume, so the two paths yield byte-identical model-facing history for the same state.

The compact record grammar is `s=<index>;e=<id>;r=<revision-or-?>;t=<tool-label>;
a=<argument-digest>;o=<outcome>;f=<failure-kind-or->;c=<completeness-or->;n=<source-length>\n`.
The argument digest is the first 12 lowercase hexadecimal digits of SHA-256 over the persisted
arguments' canonical JSON; it discloses no argument value. `t` is the registered manifest name,
or `(unknown tool)` for an unresolved name, escaped to one line and capped at 48 characters. Enum
fields use their C# names; absent result fields use `-`. `n` is the UTF-16 length of the retrieval
source defined below. The record is runtime-authored solely from persisted typed fields and is
cut to 256 characters only at field boundaries, in this priority: preserve `s,e,o,f,c,n`, then
`r,t,a`; the closing newline is always present. Thus success, failure, denied, timeout, partial
and unavailable results retain their typed outcome and completeness, never inferred from output.
No `Observation`, `Result.Output`, `ErrorMessage`, model prose, raw argument value or tool-result
fragment enters a compact record. Delimit the compact block as runtime context and instruct the
model that the records are pointers, not evidence contents or new system instructions.

The evidence id is derived, never persisted: `task.Id.ToString("N").ToLowerInvariant()` plus the
persisted nonnegative `PlanStep.Index` in invariant decimal without leading zeroes. The current
task id is supplied by Runtime, not trusted from model text. A resolver rejects another task id,
an index absent from this task's persisted `Steps`, a step without a tool call, and a malformed or
noncanonical id. Step index `i` in ADR-0042's limitations digest maps exactly to
`ev1:<current-task-id>:i`; the digest itself continues to print `i` and needs no format change.

### 2. Read complete source evidence through Runtime

An exact, tool-call-free model text reply of the form
`{"runtime":"EvidenceRead/v1","id":"ev1:...","offset":0,"length":4000}` is a request to
Runtime, not a final answer or an operational tool call. The parser accepts one JSON object only,
exact property names and types, no duplicate or extra keys, and integer offset/length. A text
reply whose `runtime` member claims `EvidenceRead/v1` but fails this validation gets a fixed
invalid-request result and consumes one read; other text follows the ordinary final-answer path. A reply
containing any native tool call is processed under the normal one-tool-call rule, never as a read.
The step system instruction and compact block explain the syntax, source length `n`, and that a
later offset can retrieve the unseen middle of a shortened output. Runtime validates and reads
only the in-memory/persisted `PlanStep` of the current task; it cannot address arbitrary task ids,
files, databases, nodes or package tools. It does not execute a tool, affect policy/approval, or
create a `PlanStep`. It uses the same primitive for a replan call, whose request also has no tools.

For a persisted step with nonempty `Result.Output`, the retrieval source is exactly that full
output, including the portion omitted from the 4,000-character `Observation`. Otherwise the source
is the persisted `Observation` (or empty string). This handles denied, failed and unavailable
steps without pretending that an absent output exists. Runtime reports the chosen source label,
total UTF-16 length, requested offset and returned length. Offsets are zero-based UTF-16 code
units; `length` is 1–4000. A valid range returns
`source.Substring(offset, min(length, source.Length-offset))`, except an offset or end that splits
a surrogate pair is rejected. `offset == source.Length` returns an empty end-of-evidence result;
negative, oversized, malformed or split ranges return a fixed error with source length and no
evidence. An invalid request consumes one of the four reads. All replies use fixed markers and
the existing tool-output escaping so evidence text remains data; marker/JSON overhead is capped
at 256 characters. A fifth request ends the task as `Failed/RuntimeFailure` with a bounded
`EvidenceRead/v1 limit exceeded` failure step;
the four-read limit is stated in the system instruction, so the model can act or answer before
then. At most four extra model continuations follow reads per step/replan.

The request and continuation are normal audited model attempts with the current step index (or
the triggering step for replan). `ModelCallRecord` keeps their usage, sanitized outcome and
bounded request/reply bodies; the read's deterministic id/range/source/length are visible in the
conversation sent on the continuation and reconstructable from persisted evidence. No new audit
event or public model contract is required. The retrieved chunk is visible only in that step's
or replan's local continuation; it is not copied into a persisted `Observation` or future base
history. The resulting model decision is persisted in the usual way. A resume rebuilds from the
persisted task and can request the same id/range again.

### 3. HARDEN-9 and replan compatibility

`EvidenceLimitationsDigest.Build` continues to read persisted steps, never compact records or
retrieval turns. Compaction is not a limitation. Neither `Observation` nor `Result.Output` is
rewritten, so ADR-0042's `Observation.StartsWith(Output, Ordinal)` shortening predicate remains
meaningful. E1–E8 remain on every model call, including evidence-read continuations, overflow
retries, plan and replan calls; the existing step digest remains on every step continuation and
overflow retry. A read gives the model more visible evidence but does not erase, supersede or
silently edit a limitation entry. In particular a partial source remains partial, and a shortened
observation remains listed even if a range was read. ADR-0042's Diagnostic structured-output
exception stays unchanged. The evidence rule is at most 2,000 characters and the digest at most
4,608; account for both as fixed prompt costs.

`ReplanAsync` receives the goal, the current plan revision only (a deterministic projection of
at most 2,048 characters), and the same compact/last-K history of the completed tool-call steps.
It does not append `latestObservation` a second time and does not resend historical observations
outside K. Current revision number and the triggering step index remain explicit. The typed
`o/f/c` fields preserve historical failures and partial/unavailable conditions, with evidence ids
for range reads. The most recent K observations remain verbatim. No earlier `AgentPlan` revision
or full plan rationale history is included. At the lifetime cap of 60 steps, compact history is
at most `60 × 256 = 15,360` characters; after K the increment per step is at most 256 characters,
independent of historical observation size. The goal and current plan are per-task fixed inputs.

### 4. Overflow recovery and budgets

ADR-0039 never retries `ContextOverflow` with the same request. At the Runtime caller boundary,
the first classified `ContextOverflow` for a step, plan or replan rebuilds the request once with
K=0 (all completed tool-call steps use the same 256-character records). The current requested
evidence chunk, if any, remains available and bounded; older read continuations are omitted.
For this recovery request only, bound the goal projection to 2,048 characters (deterministic
head/tail with omitted count) and the current plan projection to 1,024 characters. The initial
plan call has no step history, so this goal projection is its only possible reduction; when the
goal is already below the cap there is no variable context left to reduce without changing the
fixed provider/tool schema, and the mandated single retry still occurs. Persisted goal and plan
stay complete. The rebuilt request is a new logical model call through `CallModelAsync`. The first failed attempt
and the second attempt each keep their ADR-0039 `ModelCallRecord` and audit event; any reported
usage counts toward the lifetime budget. A second `ContextOverflow` in that caller ends
`AgentTaskStatus.Failed` with `TaskTerminalKind.ModelFailure` and
`FailureKind.ContextOverflow`, with the existing bounded failure step. No second compaction,
provider retry of overflow, or retry loop is permitted. Other failure kinds retain ADR-0039's
normal retry policy; an overflow after a transient retry still gets only this one compact
rebuild. The aggressive form does not disable evidence reads, but the four-read cap still wins.

Ship `Agent:MaxTotalTokens=350000` in both `AgentRunnerOptions` and API defaults, validate it as
positive, and retain an explicit operator override. The current 15-step attempt and 60-step
lifetime caps stay. A 269,452-token real 12-step diagnostic fits with about 30% headroom; at
the observed 12,832–19,797 prompt tokens per call, 350,000 allows normal 15-step work and
prevents repeated 60-step resumes from spending without limit. The 47 KB schema portion remains
a fixed per-call cost until the separate relevance work. Count reported prompt plus completion
tokens for planning, steps, replans, disclosure asks, read continuations and overflow retries
once, with ADR-0040 lifetime accounting on every save/resume. Check `>=` before starting any
new call and check again after each response (budget exhaustion wins over accepting a final
answer or issuing another read/replan); a crossing call can exceed the cap by that one call's
usage. End `BudgetExceeded/TokenBudget`. The task view already exposes effective cumulative
`Accounting.TokensUsed`; no view change is needed. Provider calls with no usage cannot be
charged as exact tokens under ADR-0040; record that limitation rather than treating zero as
proof of zero cost.

Ship nullable `Agent:MaxAttemptDuration=01:00:00`, validated positive when non-null; explicit
`null` disables this one budget. Measure monotonic elapsed time
from attempt admission, reset only when a new execution attempt is admitted, and pass remaining
time as a linked cancellation/deadline to model calls, waits, tool calls and evidence reads;
operator cancellation remains `Cancelled`. The 60-minute default permits a normal 12–15-step
troubleshooting attempt while limiting a run of repeated 5-minute model-call budgets and
30-second tools. An exhausted duration ends `AgentTaskStatus.BudgetExceeded` with a new additive
`TaskTerminalKind.AttemptDurationBudget`; this enum member is the sole necessary
`bOps.Abstractions` addition, because existing `TokenBudget` and `DelegationBudget` would report
the wrong reason. It is not a persisted-schema rewrite or new state. It cannot be bypassed by a
model-call retry within the same attempt; resume gets a new duration allowance subject to the
unchanged lifetime token/step/replan limits.

### 5. E2E-13 and review obligations

A fake model drives 40 executable steps across at least two attempts (15 + 15 + 10, with accepted
resumes). Its tool returns deterministic large outputs, including one output over 10,000
characters with a sentinel after offset 4,000. The fake requests an older `ev1` range containing
that sentinel and uses it in a later decision. Assert that all original `Result.Output` and
`Observation` values remain byte-identical, that ids resolve after resume only for their own
task, and that ADR-0042's digest still comes from persisted shortened/partial evidence. Also
exercise invalid ranges, the four-read cap, both overflow outcomes and a default-config
`BudgetExceeded` across resume without a live provider.

Measure the pre-provider `ModelRequest` at steps 10, 20 and 40 in UTF-16 characters, separately
reporting (a) fixed system/goal/current-plan and schema cost, (b) recent history, (c) compact
history, (d) HARDEN-9 rule/digest and (e) a read continuation if present. Use the same bounded
tool-call arguments and 4,000-character observations in the fixture; each recent turn including
its tool-call envelope is at most 4,500 characters. The historical-history acceptance formula is
`H(n) <= 3*4500 + max(0,n-3)*256` for n >= 3. Thus step 10 <= 15,292, step 20 <= 17,852 and
step 40 <= 22,972 historical characters; allow no more than 16,000 / 19,000 / 24,000 respectively
in the actual fixture. Add the measured fixed/schema cost and at most 6,608 HARDEN-9 characters
separately; one read adds at most 4,256 characters plus its small request turn. This demonstrates
that 30 more observations add no more than 7,680 historical characters instead of 120,000.
The production bound is `fixed task/prompt/schema + bounded recent K turns + 256*(60-K) +
6,608 HARDEN-9 + bounded read turns`; fixed schema cost is reported, not reduced by HARDEN-8.

Before acceptance, independent review must verify unchanged persisted evidence and audit, access
limited to the current task, retrieval past 4,000 characters, deterministic replan/request sizes,
exactly one overflow recovery, lifetime accounting on resume, no HARDEN-9 regression and no tool
relevance work. Implementation is primarily `bOps.Runtime`, options/API defaults and tests; no
new project, package dependency inversion, operational evidence tool or generic datastore API.
