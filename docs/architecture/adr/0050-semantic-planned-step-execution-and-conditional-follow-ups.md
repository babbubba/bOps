# ADR-0050 — Semantic planned-step execution and conditional follow-ups

Status: **Proposed — 2026-10-08**
Date: 2026-10-08
Refines: [ADR-0046](0046-step-scoped-tool-routing-and-transactional-replanning.md) and
[ADR-0047](0047-bounded-planned-step-argument-correction.md) (does not supersede either)

## Context

ADR-0046 binds an execution turn to the current `PlannedStep.ExpectedTool`, and ADR-0047 keeps that
step current for one correction when the exact tool fails argument validation. Tool-name equality is
necessary but is not sufficient when one plan uses the same tool for different purposes.

Task `8cc482b5-3ac3-4de5-af07-1d1f334bf80b` exposed the gap. Several consecutive steps expected the
same event-reading tool. A call using the arguments for a later diagnostic question still had the
current step's tool name, so it consumed the current step and moved the cursor out of alignment with
the plan. Neither the persisted description nor a second model judgement is a safe execution check.

The same task exposed a separate planning gap. A planner can know that a follow-up tool exists before
a discovery step has produced the concrete evidence that would make that follow-up applicable. The
current result contract persists outcome, failure kind, completeness and text, but it has no generic,
typed fact channel on which Runtime can base a condition without parsing tool output.

This ADR defines the minimum contract for both gaps. It does not implement the contract.

## Existing decisions preserved

- ADR-0038 first-call-wins remains unchanged: Runtime evaluates and may execute only the first emitted
  call; later calls remain persisted as `UnexecutedToolCalls` and cannot satisfy, correct, activate or
  complete any planned step.
- ADR-0046 still exposes at most the current step's exact authorized operational tool and never falls
  back to the full catalog. Plan and replan calls expose no native operational tools.
- ADR-0046 transactional replanning, ADR-0040 resume and all step, replan, token and duration budgets
  remain authoritative.
- ADR-0047 retains exactly one bounded argument-validation correction for the semantic call expected
  by the current step.
- The executor model still proposes every concrete `ModelToolCall`; a plan never auto-executes a tool.
- Policy, entitlement, approval, manifest validation, execution and verification remain Runtime-owned.

## Decision

### 1. A planned step carries a bounded expected-call signature

Choose **expected argument constraints**. A newly produced `PlannedStep` retains its concise objective
and `ExpectedTool` and may add an `ExpectedArguments` object. `ExpectedArguments` is a bounded JSON
object whose properties are an equality-only subset of the expected tool's manifest parameters.

Conceptually:

```text
PlannedStep
  Index
  Description                 concise objective, not private reasoning
  ExpectedTool
  ExpectedArguments?          parameter-name -> typed expected value
  Activation?                 absent, or one EvidenceFactExists condition
```

There are no operators, alternatives, ranges, wildcards, regular expressions, coercion rules or
nested predicates. A listed property means only:

```text
the emitted call must contain this argument
AND its manifest-typed value must equal this expected value
```

Arguments omitted from `ExpectedArguments` remain unconstrained. Consequently the object is an exact
match for its own named members and a subset match against the complete call.

Plan acceptance validates every constraint against the `ToolManifest` selected by `ExpectedTool`:

- the property must name a declared, non-sensitive parameter;
- its JSON shape must be representable by that parameter's `ToolParameterType`;
- equality is evaluated on the value parsed as that declared type, not on formatted text;
- string, enum and path values use ordinal equality; integer, number, boolean and duration values use
  their typed equality; a `PathList` uses ordered element-for-element ordinal equality;
- `null`, objects and undeclared parameter names are not valid constraint values;
- the number of constraints and serialized size use small Runtime-owned bounds;
- a constraint never widens the manifest or supplies an argument to execution.

An optional manifest parameter becomes required for this planned step when it appears in
`ExpectedArguments`. An omitted constrained argument is therefore a semantic mismatch.

For a new plan that names the same tool more than once, every occurrence must have a non-empty
signature, and each pair must share at least one constrained parameter with unequal typed values.
This pairwise discriminator makes the occurrences provably distinct. If the tool's manifest cannot
express such a discriminator, the planner must combine the work or produce a different plan; Runtime
must not pretend that prose alone proves which occurrence a call serves. The normal one corrective
plan re-ask and ADR-0046 transactional failure semantics apply to an unusable plan candidate.

The planning catalog must expose bounded parameter identity metadata needed to form the constraints:
name, type, required/optional and allowed enum values. It must not expose secrets, parameter values or
package-specific Runtime knowledge.

### 2. Deterministic membership check

Runtime evaluates the first emitted call in this fixed order:

```text
1. exact ExpectedTool / offered-tool check
2. ExpectedArguments typed subset-equality check
3. complete ToolManifest argument validation
4. policy, entitlement, approval, execution and verification
```

A call belongs to the current planned step exactly when checks 1 and 2 pass. The decision uses only
the persisted current plan, the emitted call and the referenced manifest. It never parses the step
description, tool output or error text and never asks a model to judge semantic equivalence.

If a constrained argument is missing, has the wrong JSON/type shape or has another value, check 2
fails. Because semantic membership is decided before full manifest validation, a bad constrained
value is a semantic mismatch. ADR-0047 applies only after every semantic constraint matches and the
remaining call fails check 3.

### 3. One semantic correction, then replan

A same-tool call that fails `ExpectedArguments`:

- is not sent to policy or the tool and has no machine effect;
- is persisted as a typed `SemanticMismatch` execution event linked to the plan revision and planned
  step index, including the proposed call under the existing persistence/redaction rules;
- does not advance the planned-step cursor;
- does not consume the ADR-0047 argument-validation correction;
- receives one fixed Runtime-authored correction turn with the same one-tool view, objective and
  expected-call signature.

A second semantic mismatch for that planned step requires an immediate bounded replan with zero
native tools. It receives no third semantic attempt. A wrong tool name remains ADR-0046
`UnknownTool` and replans immediately; it does not receive the semantic correction.

The semantic-correction and ADR-0047 correction budgets are independent and each is at most one per
planned step. This keeps classification exact. For example, a first semantic mismatch followed by a
semantically matching call with an unrelated manifest-validation error may still use ADR-0047 once.
A second event of either already-spent class requires replan. Neither correction increments the
replan counter; only an accepted replan does.

#### Required repeated-tool regression

```text
Plan revision N
1. inspect hardware events  | diagnostic.events | constraint A
2. inspect shutdown events  | diagnostic.events | constraint B
3. inspect PnP events       | diagnostic.events | constraint C
```

At step 2, `diagnostic.events(arguments matching C)` passes the tool-name check and fails the typed
constraint-B check. Runtime persists `SemanticMismatch`, keeps the cursor on step 2 and offers
`diagnostic.events` once more with the fixed semantic-correction instruction. A corrected B call can
consume step 2. Another non-B call replans; it never silently completes step 2 or advances to step 3.

### 4. Persisted progress events are the single source of plan state

PRE-2B adds only the fields needed to make the plan and each execution event self-identifying:

- nullable `ExpectedArguments` and `Activation` on `PlannedStep`;
- the planned-step index on each new execution `PlanStep`, in addition to its existing plan revision;
- a typed execution classification sufficient to distinguish `Matched`, `SemanticMismatch` and
  `ArgumentValidationFailure`;
- typed `EvidenceFact`s on `ToolCallResult.Facts`, from which conditional `Activated` or `Skipped`
  outcomes are derived (see the amendment below).

Exact member names are an implementation detail, but these facts must be serialized. Mutable cursor,
retry counters and UI status must not be stored as independent authority. In-memory state is only a
projection of the persisted plan revisions, execution steps and their facts.

**Amendment (conditional state is a derived fold).** `Activated` and `Skipped` are logical states
deterministically derived from persisted execution history — `PlanStep`, `PlanRevision`,
`ToolCallResult.Facts`, the execution classification and the cursor/history — not a second state
machine. They need no dedicated transition record and no new audit event. No essential state may be
volatile: restart and resume derive the same result. A `Skipped` step is never offered to the model or
a tool. The PRE-4 UI must use this same projection/fold, not maintain its own state machine. A future
implementation may add separate audit observability for these outcomes, but audit never becomes the
authoritative source of state.

The persisted contract allows PRE-4 to derive, without parsing prose or maintaining a frontend state
machine:

- **Pending** — the current revision retains the step and it has neither been consumed nor skipped;
- **Running** — it is the derived current step of a running task and no correction is pending;
- **Correcting** — it is current and either its semantic or ADR-0047 correction is spent;
- **Skipped** — its persisted condition decision was `Skipped`;
- **Completed** — a matching call consumed it without requiring replan;
- **Superseded** — a later accepted revision replaced a step that was neither Completed nor Skipped.

A derived `Activated` state also applies to conditional steps. It changes such a step into the
ordinary Pending/Running path; it is condition history, not a seventh UI execution status. Revision
boundaries remain the existing ordered `AgentPlan.Revision` history. An accepted replan preserves the
completed/skipped prefix and supersedes only the unresolved suffix of the prior revision.

### 5. Conditional follow-up uses one typed fact-existence primitive

The current `ToolCallResult` does **not** contain typed evidence facts, so outcome, completeness and
free-form output are insufficient to implement a generic evidence-driven follow-up. PRE-2C must add a
small, serializable and bounded fact primitive; Runtime must not parse arbitrary result JSON.

Conceptually:

```text
EvidenceFact
  Type                         opaque, stable package-defined identifier
  Key                          opaque, stable package-defined identifier
  ValueType                    one ToolParameterType
  Value                        one bounded scalar or PathList value

EvidenceFactRef
  ProducerPlannedStepIndex     earlier step in the same plan revision
  Type
  Key

Activation
  EvidenceFactExists           exactly one EvidenceFactRef
```

Facts are output metadata, not instructions. Runtime treats `Type` and `Key` as opaque ordinal
identifiers and knows no package, operating-system, diagnostic or artifact vocabulary. Counts, names
and values are host-bounded; object/array graphs and arbitrary predicates are forbidden. The producer
step, its result and its facts are persisted together before activation is evaluated.

A conditional step may additionally source one expected-argument value from the same fact reference.
That is a direct typed binding, not an expression: one fact value equals one named argument constraint.
It lets Runtime prove that the follow-up call uses the discovered value without manufacturing the
call. Literal constraints and fact-bound constraints share the same equality rules.

Plan validation requires the fact producer to be an earlier step of the same revision, the condition
to contain exactly one reference, and any binding's fact type to match the target manifest parameter.
Cross-task, cross-revision, negative, compound and value-comparison conditions are not supported.

### 6. Activation and skip timing

Runtime evaluates a condition only when its step reaches the cursor, after the referenced earlier
producer has been consumed and durably persisted:

- if the referenced typed fact exists, the step derives as `Activated` and executes normally;
- if it does not exist, the step derives as `Skipped` and the cursor advances to the following planned step without a model
  call, tool call or executable-step budget charge;
- if persisted data is contradictory or the producer cannot be identified, fail closed to replan.

This is one deterministic lookup, not a query language and not replanning after every observation.
New evidence can activate only a follow-up that the accepted plan already declared. Evidence that
would justify an unplanned action waits for an existing bounded replan trigger; this ADR adds no
general reflect-after-read rule.

### 7. Deterministic live and resume fold

Live execution and resume use the same oldest-first fold:

```text
accepted persisted plans + persisted execution steps + their typed facts
  -> latest accepted revision
  -> completed/skipped prefix and superseded prior suffixes
  -> current planned-step index
  -> resolved literal/fact-bound expected arguments
  -> semantic correction spent?
  -> ADR-0047 validation correction spent?
  -> conditional state: pending, activated or skipped
  -> replan required?
```

For the current revision, a matched non-deviation consumes the step. A first `SemanticMismatch`
keeps it and spends only semantic correction; a first matching manifest-validation failure keeps it
and spends only ADR-0047 correction. A repeated spent classification, a post-correction deviation or
contradictory history sets `ReplanRequired`. `Activated` keeps the cursor on the conditional step;
`Skipped` advances it. An accepted replan starts a new revision and supersedes the unresolved old
suffix. Failed candidate replans do not alter the fold.

Thus restart cannot reset a correction, re-evaluate a settled condition differently or advance a
cursor that live execution would have kept.

### 8. Legacy compatibility

All new members are additive and nullable or default empty. Existing task JSON remains readable.

A persisted `PlannedStep` with no new semantic-contract marker is a **legacy step**. It retains the
deterministic ADR-0046/0047 `ExpectedTool`-only semantics; an absent field is not reinterpreted as a
malformed new constraint. Legacy execution steps without planned-step index or typed classification
continue to use the existing ADR-0047 fold. Legacy plans cannot declare conditional follow-ups.

Newly parsed plans are marked as using this contract and must satisfy its stronger repeated-tool and
condition validation. This explicit version distinction prevents an empty constraint object produced
by a new planner from silently masquerading as legacy behavior.

## Alternatives considered

### Intent or execution token

Rejected. A model can echo the correct token while supplying arguments for another same-tool step.
The token proves correlation with the prompt, not semantic membership of the call. A Runtime-injected
token inside tool arguments would also change package manifests or require stripping a non-tool
argument before validation, while still proving nothing about the operational values.

### Description or LLM semantic comparison

Rejected. Parsing prose is non-deterministic and fragile. A second model call to judge the first call
adds cost, latency and another fallible semantic decision, and live/resume replay would depend on a
new model response rather than persisted typed state.

### Exact equality of the complete argument object

Rejected. The planner often cannot or should not preselect every execution argument. Exact complete
equality would either turn a plan into an auto-executable call or reject harmless executor-supplied
arguments. Typed subset equality captures only the values that distinguish intent.

### General rule or JSON query language for conditional follow-ups

Rejected. JSONPath, boolean expressions and output predicates would create a workflow engine, make
tool text executable control data and require a new validation/security model. One same-revision typed
fact-existence reference plus an optional direct value binding is sufficient for PRE-2.

## Consequences

- Same-tool plan steps can be consumed only by calls whose persisted typed discriminator matches.
- A semantic mismatch and a manifest-validation error are separate persisted events with separate,
  one-use correction budgets.
- Conditional follow-ups are visible before activation and have Activated/Skipped outcomes derived from persisted history.
- PRE-2B must add the plan/execution contract and fold; PRE-2C must add the bounded fact primitive and
  the derived conditional state. Neither may add package-specific branches to Runtime.
- Persisted constraints and fact values are operational data, not chain-of-thought. PRE-4 may show
  objective, tool and state but must not expose arguments, fact values or secrets by default.

## Future implementation test matrix

| # | Scenario | Required result |
|---|---|---|
| 1 | Same tool, correct constraint | Call matches, runs normally and consumes the current step. |
| 2 | Same tool, wrong constraint | Persist first `SemanticMismatch`; do not execute or advance; second mismatch replans. |
| 3 | Wrong tool | ADR-0046 rejection and immediate replan; no semantic-correction allowance. |
| 4 | Semantically correct call, ToolManifest validation failure | Persist validation failure and spend only ADR-0047 correction. |
| 5 | ADR-0047 retry success/failure | Success consumes the step; a second validation failure replans, with no third attempt. |
| 6 | Resume during correction | Reconstruct the same step and independently spent semantic/validation correction flags. |
| 7 | Resume after semantic mismatch and failed replan | Replan before offering any operational tool; never reset the semantic allowance. |
| 8 | Conditional follow-up activated | Persist fact; step derives `Activated`; resolve any direct binding, then execute the follow-up normally. |
| 9 | Conditional follow-up skipped | Step derives `Skipped`, charge no tool step and advance deterministically. |
| 10 | Legacy persisted plan | Deserialize and retain ADR-0046/0047 `ExpectedTool`-only behavior. |

## Acceptance gate

PRE-2B must not begin until the operator accepts this ADR, including the independent one-use semantic
and ADR-0047 correction budgets and the additive typed fact primitive reserved for PRE-2C.
