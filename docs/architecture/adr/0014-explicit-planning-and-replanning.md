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

## HARDEN-8 amendment — context and budget economy (Accepted 2026-10-04)

This amendment was accepted on 2026-10-04 after the final independent architecture delta review
passed. Implementation completed on `feat/harden-08-context-budget-economy` on 2026-10-04 and is ready
for independent implementation review; no further architecture gate remains. It supplements the original
PLAN/REPLAN decision; ADR-0039 model failure containment, ADR-0040 resume accounting and ADR-0042
evidence reasoning remain authoritative at their respective boundaries. The source baseline is
`main` at `ad4d196` (HARDEN-9 merged in PR #74). The 12-step, zero-replan diagnostic that used
269,452 tokens and the incident request with roughly 47 KB of schemas in 58 KB motivate the bounds.
Schema relevance/filtering is deferred and its fixed cost is only measured here.

For token-budget enforcement, HARDEN-8 governs initial planning, replanning and non-final step
execution. Once a step response enters the ADR-0042 final-answer path, accepted ADR-0042 §§6
and 13 govern completion, disclosure and final-step `ModelCalls`. That path does not re-evaluate
`MaxTotalTokens` after the original final answer or after an admitted disclosure re-ask. This
precedence preserves the accepted HARDEN-9 behavior; it does not amend ADR-0042.

### Decision summary

| Topic | Decision |
|---|---|
| Verbatim history window | `Agent:VerbatimHistorySteps = 3` completed tool-call steps; configurable 0–15. |
| Recent compact window | At most `M = 12` older tool-call steps, each record at most 256 UTF-16 code units including newline. M is fixed, not configurable. |
| Archive summary | One runtime-authored aggregate of at most 512 UTF-16 code units for every step older than the compact window. |
| Evidence-id format | `ev1:<task-guid-N-lowercase>:<decimal-step-index>`; index is the persisted `PlanStep.Index`. |
| Full evidence retrieval mechanism | Internal Runtime conversation primitive `EvidenceRead/v1`, encoded as an exact model text reply; no registered operational tool. |
| Retrieval chunk bound | At most 4,000 UTF-16 characters of source evidence per read; at most four reads per executable step or one replan call. |
| Replan representation | Same bounded archive/compact/recent history, current plan only, and the triggering step verbatim. |
| ContextOverflow retry | Only when compactable history exists: rebuild with K=0 and retry once; otherwise terminate the logical call without an identical retry. Failure kind is `ModelFailureKind.ContextOverflow`. |
| Normal MaxTotalTokens default | `Agent:MaxTotalTokens = 350000`, cumulative for the task lifetime. |
| Per-attempt duration default | `Agent:MaxAttemptDuration = 01:00:00`, active execution time excluding human approval waits; nullable operator opt-out. |
| Additive contracts | `TaskTerminalKind.AttemptDurationBudget`, `PlanStep.VerificationStatus`, and a metadata-only `EvidenceReadAuditEvent` in `bOps.Abstractions`. |
| Persisted evidence modified? | NO. |
| HARDEN-9 digest source changed? | NO. |
| Tool-schema filtering included? | NO. |

### 1. Model-facing history and stable references

Before every step model call, including after resume, Runtime derives history from the current
task's canonical completed `PlanStep` list in ascending `Index` order. The initial goal remains
first. The newest K completed tool-call steps retain their existing assistant tool-call turn and
delimited observation verbatim, including ADR-0038 `UnexecutedToolCalls` responses. Of the older
tool-call steps, only the newest M=12 receive individual compact records. **All remaining older
tool-call steps become one aggregate archive summary**. A step without a tool call contributes no
historical turn. The compact block and archive are runtime-authored, delimited context turns;
neither is persisted over the original. Rebuild live history after every completed step, as on
resume, so the same persisted state yields byte-identical base history.

The compact record grammar is `s=<index>;e=<id>;r=<revision-or-?>;t=<tool-label>;
a=<argument-digest>;o=<outcome>;f=<failure-kind-or->;c=<completeness-or->;v=<verification-or->;
nr=<result-length-or->;no=<observation-length-or->\n`.
The argument digest is the first 12 lowercase hexadecimal digits of SHA-256 over the persisted
arguments' canonical JSON; it discloses no argument value. `t` is the registered manifest name,
or `(unknown tool)` for an unresolved name, escaped to one line and capped at 48 characters. Enum
fields use their C# names; absent fields use `-`. `nr` and `no` are the UTF-16 lengths of the
respective persisted sources; a null source is `-`, an empty source is `0`. `v` comes only from
the additive typed `PlanStep.VerificationStatus` (the existing `VerificationStatus` enum); it is
never parsed from arbitrary observation or tool text. In particular `v=Refuted` survives when a
step leaves the verbatim window. Legacy steps without this field use `v=-`, meaning unknown, not
confirmed. The record is cut to 256 code units only at field boundaries, preserving
`s,e,o,f,c,v,nr,no` first, then `r,t,a`; the closing newline is always present. Required fields
and the canonical evidence id must fit or history construction fails closed rather than silently
removing a typed outcome. No raw evidence enters a compact record.
No `Observation`, `Result.Output`, `ErrorMessage`, model prose, raw argument value or tool-result
fragment enters a compact record. The archive summary uses only typed runtime-authored values:
the minimum and maximum archived persisted indexes, archived tool-call step count, counts by
`ToolOutcome`, failure kind and completeness where present, and counts by typed verification
status where present. It ends with the fixed statement that an individual old tool-result step
is addressable by `ev1:<current-task-guid>:<known-persisted-step-index>`. It contains no list of
evidence ids or concatenated observations. Format fields are ordered, invariant-culture and
truncated only at whole optional count fields to the 512-code-unit cap; range, count and the
fixed addressability statement are mandatory. Fixed block headers and delimiters together have
a 128-code-unit budget. The archive exists only when archived steps exist.

For a fixture bounding each recent verbatim step turn at 4,500 code units, the historical
component is at most `K*4500 + 12*256 + 512 + 128`. At default K=3 this is **17,212 UTF-16
code units**, constant with respect to total task lifetime once more than K+M tool-call steps
have completed. Production must also enforce or measure the recent-turn bound separately:
raw tool-call argument envelopes can exceed the fixture's 4,500-unit bound, so this formula is
the E2E-13 fixture bound, while the archive/compact portion is a production fixed 3,712 units.

The evidence id is derived, never persisted: `task.Id.ToString("N").ToLowerInvariant()` plus the
persisted nonnegative `PlanStep.Index` in invariant decimal without leading zeroes. The current
task id is supplied by Runtime, not trusted from model text. A resolver rejects another task id,
an index absent from this task's persisted `Steps`, a step without a tool call, and a malformed or
noncanonical id. Resolution requires **exactly one** persisted tool-call step with that index;
zero or multiple matches and synthetic/non-tool steps are rejected, never arbitrarily selected.
Step index `i` in ADR-0042's limitations digest maps exactly to
`ev1:<current-task-id>:i`; the digest itself continues to print `i` and needs no format change.

### 2. Read complete source evidence through Runtime

The one request shape is:

```json
{"runtime":"EvidenceRead/v1","evidenceId":"ev1:<task-guid>:<step-index>","source":"result","offset":0,"length":4000}
```

`source` is exactly `result` or `observation`. The reply is assistant **textual content**, never a
provider tool call or registered operational tool. After trimming ordinary leading/trailing JSON
whitespace, the entire text must be one JSON object, with exactly these five case-sensitive keys,
correct JSON types, no duplicates or extras, the exact discriminator, and an integer offset and
length. Markdown fences or any surrounding prose invalidate it. The OpenAI-compatible
JSON-schema fallback adapter must preserve this exact object as a runtime directive before its
normal final/tool JSON validation; native tool mode must likewise pass the textual object to
Runtime. A native response containing both provider tool calls and a textual EvidenceRead claim
is invalid. Replan parsing must recognize a valid read before malformed-plan validation;
the read continues the same replan logical call without using the single malformed-plan
corrective re-ask. Initial planning has no previously persisted task evidence and does not
permit EvidenceRead. Before normal initial-plan parsing, recognize a response claiming
`EvidenceRead/v1`. A structurally valid request in that phase performs no read, is audited once
as `NotAllowedInPhase` with `StepIndex=null` and `PlanRevision=null`, and enters the existing
invalid-plan path, consuming its single malformed-plan corrective re-ask. A malformed claimed
directive in that phase likewise performs no read, is audited once as `Malformed` with both
indexes null, and enters the same corrective re-ask path. If that re-ask again fails to produce
a valid plan, use the existing malformed-plan outcome (an empty plan and step-by-step execution).
Neither case starts an EvidenceRead continuation loop or needs read-slot accounting during
initial planning; a claimed directive on the corrective response follows the same phase rule.

A response **claims** a runtime directive when its whole trimmed text is a JSON object with a
top-level `runtime` key, or when it begins with a JSON object prefix whose first complete key is
`runtime` (including a truncated object). A `runtime` value other than `EvidenceRead/v1`,
missing/wrong fields, extra keys, duplicates, bad JSON, fences around a would-be request, or
prose accompanying a JSON object containing `runtime` is a malformed claimed directive. For
the last two forms recognition is limited to a literal `"runtime"` key in the fenced/object
text; no fuzzy search for IDs or arbitrary words. Outside initial planning, malformed claims
produce a fixed bounded error continuation and audit result `Malformed`; they never access the store or become an
ordinary final answer. Text with no such claim follows existing final/plan/tool parsing.

`source=result` reads exactly persisted `ToolCallResult.Output`; a null `Result` or null `Output`
is `UnavailableSource`, while an empty output is valid and immediately ends. It does not fall
back to Observation. `source=observation` reads exactly persisted `PlanStep.Observation`; null is
`UnavailableSource`, empty is valid and ends. This source includes the unchanged runtime-authored
verification annotation. Both sources are current-task-only; no files, database objects, other
tasks, or package tools are addressable. The step instruction and compact record describe both
source lengths and the ability to read beyond character 4,000.

Offsets count UTF-16 code units from zero; `length` is 1–4000. Valid ranges return at most
`min(length, source.Length-offset)` code units. An offset equal to length returns
`EndOfEvidence`; negative, greater-than-end or surrogate-splitting boundaries are `OutOfRange`.
Validation and errors disclose no evidence contents. Replies use fixed markers and existing
tool-output escaping; marker/JSON overhead is at most 256 code units. The fragment is only in
the same logical call's local continuation, never copied into future base history. Each read
request, including malformed and rejected ones, consumes one of **four** slots per normal step
or replan logical call. Provider retries and ContextOverflow recovery do not reset the counter.
A fifth attempted read performs no evidence read, is audited exactly once as `LimitExceeded`,
and terminates the **entire task** as `Failed/RuntimeFailure`. Persist one bounded synthetic
non-tool failure step at the next global step index, with the fixed runtime-authored text
`EvidenceRead/v1 limit exceeded` and any model-call records required by existing recording
rules; do not attach the same records twice. It has no `ToolCall`, is not evidence-addressable,
is excluded from the ADR-0042 evidence-limitations digest and cannot match an ADR-0042
final-response marker. No new public terminal kind is required. At most four read continuations
occur. A successfully read fragment returns to the same logical model conversation, then
ordinary replan/step response handling resumes.

Add one metadata-only `EvidenceReadAuditEvent` to `bOps.Abstractions.AuditEvent`'s derived-type
registration. Its fields are `TaskId` (base), nullable `StepIndex` for a step or triggering
replan index (`null` during initial planning), nullable `PlanRevision` for a replan (`null` for a
step or initial planning before any plan has been accepted), `EvidenceId` (bounded raw claimed
value or null), `Source` (`result`/`observation` or null), `Offset` and `RequestedLength` (nullable when
malformed), `ReturnedLength` (always 0 on rejection), and `ResultCode` (closed enum): `Success`,
`EndOfEvidence`, `Malformed`, `InvalidId`, `CrossTaskRejected`, `MissingStep`,
`UnavailableSource`, `OutOfRange`, `LimitExceeded`, `NotAllowedInPhase`, and
`AttemptBudgetInterrupted`. A duplicate index is `InvalidId`; a non-tool/synthetic index is
`MissingStep`. Audit every attempted read
once before a continuation or termination, including interrupted reads. Never place retrieved
raw evidence in this event. Existing `ModelCallRecord` and model-call audit still record each
model attempt and known usage; payload-retention settings do not affect the read audit.

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

Persist an additive nullable `PlanStep.VerificationStatus` of the existing enum type for new
tool-call steps, populated directly from the runtime's verification outcome. Null means absent
or unknown (including legacy steps). The existing Observation suffix includes arbitrary
verification detail and can contain status-looking text, so it is **not** a safe source for
parsing a typed status. This additive public JSON field needs no SQLite column migration;
legacy rows deserialize null. Keep the Observation suffix for model-visible detail and never
rewrite it or Result.Output. Compact `v` and archive verification counts use only the typed
field. In particular `Refuted` remains visible after the full step leaves recent history.

`ReplanAsync` receives the goal, the current plan revision only (a deterministic projection of
at most 2,048 characters), bounded archive/compact/recent history, and the **step that directly
triggered replan verbatim** as current triggering context. This holds even when configured or
aggressive K=0. Do not duplicate it if it is already in the recent K window; exclude it from
old-history compaction in that case. Current revision number and triggering index are explicit.
No earlier plan revision or full plan-rationale history is included. With the 4,500-unit fixture
turn bound, a K=0 replan has a separate 4,500-unit trigger plus 3,712 units of bounded older
history. Historical growth plateaus independently of total task lifetime.

### 4. Overflow recovery and budgets

ADR-0039 never retries `ModelFailureKind.ContextOverflow` with the same request. Here
"compactable historical context" means at least one completed historical tool-call step is
currently carried verbatim by K and can move to compact/archive form; the replan trigger does
not qualify because it must stay verbatim. At the Runtime caller boundary, the first overflow
**only when compactable historical context exists** rebuilds
with aggressive K=0 and retries exactly once. The bounded M records and archive remain; the
replan triggering step stays verbatim. Goal and current-plan projections on this recovery request
are capped at 2,048 and 1,024 code units by deterministic head/tail projection. Persisted values
remain complete. An initial plan has no compactable history: its overflow terminates the logical
call as `Failed/ModelFailure/ContextOverflow` without an identical retry. The same rule applies
to any other call lacking compactable history. Both attempted calls keep their ADR-0039
`ModelCallRecord` and audit; known usage counts. A second overflow terminates with that same
classified reason and bounded failure step. No provider retry multiplies this one compaction
retry. After a successful aggressive retry, all EvidenceRead continuations and other model
continuations of **that logical call** remain aggressive; the next normal step/replan restores
configured K. EvidenceRead stays available under its four-request limit.

Ship `Agent:MaxTotalTokens=350000` in `AgentRunnerOptions` and API defaults. This is a finite
**emergency ceiling**, not a target; retain nullable explicit operator opt-out and validate
non-null as positive (no clamping). The current 15-step attempt and 60-step lifetime caps stay.
The 269,452-token incident justifies a ceiling with headroom, not a guaranteed 60-step run.
Count known prompt plus completion usage for **every** model attempt, including failed/retried
attempts, planning, steps, replans, disclosure asks, EvidenceRead continuations and overflow
retries, cumulatively across resumes. EvidenceRead continuations are full, potentially expensive
model calls. A token cap may therefore stop non-final execution before `MaxLifetimeSteps`; the
separately measured tool schemas remain a fixed per-call cost. Exhaustion is strictly
`TokensUsed > MaxTotalTokens`, not `>=`; exactly reaching the cap is not over budget. Apply the
existing `>` pre-call check before another non-final model call and the ADR-0042 `>` availability
check before a disclosure re-ask. ADR-0040 counts only provider-reported
usage: absent usage remains null in its persisted `ModelCallRecord` and `ModelCallAuditEvent`,
and `TaskState.Accounting.TokensUsed` is then a lower bound, not an invented estimate.

When a planning, replanning or non-final step response crosses the cap after accounting for its
reported usage (`TokensUsed > MaxTotalTokens`), preserve the paid
call before terminating. Create **one synthetic non-tool `PlanStep`** at the next global step
index, carrying the crossing call's `ModelCalls`, a fixed runtime-authored token-crossing
description, `ToolCall=null`, `Result=null`, and bounded model text if relevant. Plan/replan
crossing calls are recorded there too; the same `ModelCallRecord` must not also be attached to
a plan/other step. Execute no proposed tool; perform no evidence read, retry or replan afterward.
End `BudgetExceeded/TokenBudget`. This bookkeeping step is excluded from executable-step and
evidence-id resolution, is excluded from the ADR-0042 evidence-limitations digest, cannot match
a final-response marker, and counts in task state/audit and cumulative usage. If a crossing
response occurs during initial planning, the synthetic step is still persisted; no plan is
accepted from it. This is the HARDEN-8 synthetic token-crossing rule.

An original final answer is the intentional ADR-0042 exception to that rule. Even if its usage
crosses the cap, persist it on the existing final-response step and allow `Completed`; do not
convert it to `BudgetExceeded` or create a HARDEN-8 token-crossing step. If no disclosure re-ask
is needed, completion proceeds normally. If one is considered, ADR-0042 §6 first checks budget
availability using `TokensUsed > MaxTotalTokens`: when already exceeded, skip the re-ask and
persist the original answer. When a re-ask is admitted while budget remains, its result resolves
under ADR-0042 even if its reported usage then crosses the cap. The task remains `Completed`;
use the disclosed answer if accepted, otherwise the preserved original. All re-ask `ModelCalls`
stay on the existing final-response step per ADR-0042 §13. Do not relocate them to a synthetic
step or re-evaluate the token budget after the admitted re-ask. This preserves accepted
HARDEN-9 final-answer and disclosure semantics.

Ship nullable `Agent:MaxAttemptDuration=01:00:00`; explicit null opts out and non-null must be
positive. Validate `VerbatimHistorySteps` as integer 0–15 (fixed upper bound matching the shipped
`MaxSteps` default); values outside that range fail `Validate()`. Validate `MaxAttemptDuration`
as at most 24 hours as well, without clamping. Use `TimeProvider` for deterministic active-time
accounting, starting at attempt admission and resetting only for a newly admitted attempt.
Pause accounting **immediately before** awaiting explicit human approval and resume after a
decision; caller cancellation still cancels approval waiting. Model-provider calls, runtime
planning/replanning, EvidenceRead processing and continuations, retry waits and tool execution
consume active duration. Approval latency does not.

Use a dedicated attempt-budget cancellation signal distinct from caller cancellation and the
existing provider/tool timeout signals. At expiry, stop in-flight work, persist and audit the
model-call record or deterministic tool interruption/failure record (or EvidenceRead audit with
`AttemptBudgetInterrupted`), then terminate `BudgetExceeded/AttemptDurationBudget`. A model
expiry does not use HARDEN-2 timeout retry or replan; a tool expiry does not retry or replan; a
read expiry does not continue its read loop. No operation is silently abandoned. Caller
cancellation remains `Cancelled`, ordinary provider/tool timeout retains `Timeout`, and operator
rejection retains its existing approval semantics. The additive
`TaskTerminalKind.AttemptDurationBudget` gives this terminal reason without a new task status.

### 5. E2E-13 and review obligations

A fake model drives 40 executable steps across **two resume boundaries** (15 + 15 + 10). Use
bounded tool-call arguments and observations so each recent verbatim turn is at most 4,500 UTF-16
code units. Measure pre-provider normal-step **and replan** historical components at steps 10,
20 and 40. The exact maximum at all three points is
`K*4500 + M*256 + archive(512) + headers(128)` = **17,212** at K=3, M=12. At step 10 the
archive may be absent; at steps 20 and 40 assert the same maximum, with no rising allowance.
For a replan, count the trigger only once; at K=0 the separate trigger is at most 4,500 and
the older historical maximum is 3,712. Report fixed system/goal/current-plan/tool schemas,
HARDEN-9 rule and digest (at most 6,608), and bounded EvidenceRead continuations separately.
Do not hide these fixed costs in the history assertion.

After an old record moves into the archive, request a `source=result` range containing a
sentinel beyond character 4,000; use it in a later decision. Assert the persisted full
`Result.Output` and `Observation` remain byte-identical, `source=observation` returns the
persisted verification annotation, `v=Refuted` survives compaction, and the HARDEN-9 digest
still reflects persisted shortening/partial evidence. Exercise invalid, duplicate, cross-task,
missing and non-tool ids; malformed claims in native and fallback modes; the fifth-read limit;
initial-plan overflow without retry; one aggressive overflow retry; duration interruption;
and a default non-final token-budget crossing with a persisted synthetic step. For the fifth
EvidenceRead attempt, assert no read, one `LimitExceeded` audit event, one bounded synthetic
non-tool failure step with the crossing logical call's retained `ModelCalls`, and
`Failed/RuntimeFailure` with `EvidenceRead/v1 limit exceeded`. For initial planning, assert a
valid read is audited `NotAllowedInPhase` and a malformed claimed read is audited `Malformed`;
both have null `StepIndex` and `PlanRevision`, perform no read, use the existing single
malformed-plan corrective re-ask, and do not enter a read continuation loop. Assert the
corrective re-ask's existing malformed-plan outcome if it also fails.

Preserve the existing HARDEN-9 outcomes with explicit budget tests: `TokensUsed == MaxTotalTokens`
is not over budget and retains exact-cap disclosure behavior; an original final
answer that crosses the cap persists normally and ends `Completed` without an H8 synthetic
step; an admitted disclosure re-ask that crosses the cap ends `Completed` with all its
`ModelCalls` on the final step; a non-final tool-calling response that crosses the cap ends
`BudgetExceeded/TokenBudget`, records the crossing call exactly once in the H8 synthetic step,
and does not execute the tool. No live provider is needed.

Before acceptance, independent review must verify unchanged persisted evidence and audit, access
limited to the current task, retrieval past 4,000 characters, deterministic replan/request sizes,
exactly one overflow recovery, lifetime accounting on resume, no HARDEN-9 regression and no tool
relevance work. Implementation is primarily `bOps.Runtime`, options/API defaults and tests; no
new project, package dependency inversion, operational evidence tool or generic datastore API.
