# ADR-0052 — Microsoft Agent Framework as the bOps agent infrastructure boundary

Status: **Accepted**

Accepted: 2026-10-10 by the operator (D-048)

Research baseline: bOps `main` at `2f961f7493c7ed94f96dd07ff173fcfdf0d140c9`; Microsoft Agent Framework .NET `Microsoft.Agents.AI` 1.24.0.

## 1. Context

The operator reaffirmed an original product requirement: **bOps is an agentic system built on Microsoft Agent Framework (MAF)**.

Early architecture work correctly protected two things:

1. `bOps.Abstractions` owns the public contracts and stays dependency-free.
2. bOps owns the operational safety boundary rather than allowing an LLM framework to execute arbitrary operations.

That requirement was later interpreted too broadly as “bOps is not a framework consumer”. As a result `AgentRunner` now owns both product-specific authority and generic agent infrastructure: conversation representation, history projection, context compaction, model-call plumbing, evidence paging, planning, policy, approval, entitlement, execution, verification, persistence, resume and recovery.

The distinction that matters is not “framework versus no framework”. It is:

> **Microsoft Agent Framework owns generic agent infrastructure. bOps owns operational authority, safety, evidence semantics and recovery.**

MAF may influence what the model sees. MAF may not decide what bOps executes.

## 2. Decision

bOps adopts Microsoft Agent Framework as its reference agent framework.

MAF is the preferred implementation for generic agent infrastructure:

- conversation/message infrastructure;
- context construction and history management;
- context compaction;
- tool-result compaction;
- LLM-based summarization;
- context-window management;
- agent-context middleware;
- function-call plumbing where a later task proves it useful without weakening bOps authority;
- future Coordinator-level sessions, workflows and memory where appropriate.

bOps retains exclusive ownership of:

- `AgentPlan`, plan revisions and execution-state semantics;
- exact step/tool authority and tool visibility;
- tool manifests and argument validation;
- `RiskLevel`;
- policy;
- entitlement;
- human approval;
- mutation intent journal;
- machine-side tool execution;
- post-action verification;
- `EvidenceFact`, completeness, evidence limitations and grounding;
- `TaskState`, resume and execution-attempt fencing;
- unknown-outcome handling and reconciliation;
- audit semantics;
- provider pinning/fallback policy;
- task/model budgets.

No MAF component may bypass those responsibilities.

## 3. Relationship to existing decisions

### ADR-0001 — project-owned `IChatModel`

**Preserved and clarified.**

`bOps.Abstractions` remains dependency-free. `IChatModel`, `ChatTurn`, `ModelToolCall`, `ModelRequest`, `ModelResponse`, `ChatModelDescriptor` and `ModelUsage` remain project-owned public contracts.

ADR-0001 must no longer be interpreted as prohibiting MAF inside the implementation.

The distinction is:

```text
public bOps contract      != Microsoft framework contract
runtime implementation    may use Microsoft Agent Framework
```

### ADR-0014 — explicit planning and HARDEN-8

Planning/replanning remains bOps-owned.

The HARDEN-8 deterministic `BoundedHistory` implementation becomes migration/reference code rather than the target context-management architecture. Raw persisted evidence and EvidenceRead addressability remain.

### ADR-0013 / ADR-0039 — model-call audit and failure containment

Preserved and extended: every inference caused by a bOps task, including context summarization, must use the bOps-governed model invocation path.

### ADR-0017 / ADR-0040 — persistent/resumable tasks

Preserved. MAF sessions/checkpoints do not replace `TaskState`, execution-attempt fencing or bOps resume semantics in this decision.

### ADR-0030 / ADR-0036

Delegation authority and entitlement remain bOps-owned.

### ADR-0042 / ADR-0050

Evidence semantics remain bOps-owned. MAF summaries are context, never evidence.

### ADR-0045

Provider selection, execution pinning and fallback remain bOps-owned.

### ADR-0046 / ADR-0047

Step-scoped tool authority and correction semantics remain bOps-owned. MAF cannot widen the operational tool surface.

### ADR-0051

Mutation journaling and reconciliation remain unchanged. MAF workflow/session state is not a substitute for external-side-effect uncertainty.

## 4. Dependency boundary

MAF is introduced behind a dedicated non-SDK implementation assembly. The exact project name is not architectural; `bOps.AgentFramework` or `bOps.Integrations.AgentFramework` are acceptable.

Target dependency direction:

```text
bOps.Abstractions
       ↑
bOps.Runtime
       ↑
MAF integration assembly
       ↑
API / CLI composition roots
```

Normative rules:

1. No MAF or `Microsoft.Extensions.AI` type enters `bOps.Abstractions`.
2. Provider packages remain valid implementations of project-owned `IChatModel`.
3. `bOps.Runtime` consumes project-owned runtime interfaces and does not require MAF types in its public surface.
4. The MAF package version is explicitly pinned; floating package ranges are forbidden.
5. The current experimental status of MAF compaction APIs is accepted because the dependency is isolated and migration retains a temporary deterministic fallback/reference path.
6. MAF types must not leak into the third-party plugin SDK.

## 5. Governed model invocation

Today `AgentRunner.CallModelAsync` owns retry classification, retry attempts, `Retry-After`, attempt timeout, logical-call budget, provider fallback, model-call audit, model-call records and cumulative task token accounting.

That behavior must be extracted behind a bOps-owned runtime service, conceptually:

```csharp
IModelInvocationBroker
```

The exact API is implementation detail. The invariant is not.

Every inference caused by a bOps task must pass through the broker:

- planning;
- replanning;
- step reasoning;
- final synthesis;
- grounding/final checks;
- evidence-disclosure correction;
- context summarization;
- any future MAF internal reasoning performed on behalf of the task.

MAF components that require an `IChatClient` are adapted over the bOps broker. MAF must not own provider credentials or independently implement retry/fallback.

Summarization calls count toward the task's model usage and token budget and are model-call audited like any other task inference.

## 6. MAF context management

bOps will not evolve `BoundedHistory` into a second general-purpose context framework.

The target flow is:

```text
authoritative TaskState
        │
        ├── current plan/current step
        ├── EvidenceFacts
        ├── grounding
        ├── limitations
        └── historical messages/evidence
                         │
                         ▼
                  MAF context layer
                         │
                 compacted history
                         │
                         ▼
                   ModelRequest
                         │
                         ▼
              IModelInvocationBroker
```

Projection/compaction is model-facing only. It must never rewrite persisted raw task/evidence state.

## 7. Protected context

Generic historical compaction must not remove or summarize current execution authority.

At minimum these are protected:

- standing bOps security instructions;
- current user goal/input needed by the logical call;
- current `AgentPlan` revision;
- current planned step and `ExpectedTool`;
- semantic/argument correction state;
- current `EvidenceGrounding`;
- current `EvidenceLimitations`;
- mutation-reconciliation notices;
- other runtime-authored state required to interpret the current call.

Implementation may keep some of these outside the compactable history list. The invariant is that a generic MAF compaction strategy does not decide that they are disposable.

## 8. Message mapping

The bOps/MAF mapper must preserve native tool-call semantics.

An assistant function call and its matching result remain an atomic logical group. The mapper must preserve:

- roles;
- assistant text;
- call IDs;
- tool names;
- arguments;
- multiple tool calls emitted in one assistant message;
- existing “not executed” results for unexecuted calls;
- ordering;
- Unicode.

Mapping or compaction cannot transform an unexecuted historical call into an executed one.

## 9. Model context profile

bOps introduces a runtime/host-side model-context profile, conceptually:

```text
ModelContextProfile
    ProviderId
    ModelId
    MaxContextTokens
    ReservedOutputTokens
    Source
```

The exact internal shape is implementation detail.

Rules:

1. Context limits are never guessed from provider family names.
2. Router models such as `openrouter/free` have unknown limits unless configuration/provider metadata supplies a conservative guaranteed value.
3. Different fallback candidates may have different context profiles.
4. Projection uses the currently effective execution-scoped candidate's profile.
5. Unknown capacity must fail conservatively rather than claiming unsupported capacity.

The profile is runtime/host configuration, not part of the plugin SDK.

## 10. Context budget and token estimation

The useful history budget is derived from the whole request:

```text
input capacity
    = model context window
    - reserved output

history budget
    = input capacity
    - system prompt reserve
    - offered tool-schema reserve
    - protected runtime-context reserve
    - safety reserve
```

No production trigger percentage is architectural.

Current MAF C# compaction can operate on token estimates, but its ad-hoc public path does not provide an exact tokenizer for every bOps provider/model. When no tokenizer is supplied, current MAF internals use an approximation.

Therefore:

- MAF compaction token counts are estimates unless proven exact for the active model;
- provider-reported `ModelUsage.PromptTokens`, when available, is authoritative after the call;
- telemetry compares estimate and actual usage;
- no hard safety guarantee relies only on an unvalidated estimate;
- exact tokenizer integration can be added later without delaying MAF adoption.

## 11. Compaction pipeline

The target pipeline is:

```text
historical context
       │
       ▼
ToolResultCompactionStrategy
       │
       ▼
SummarizationCompactionStrategy
       │
       ▼
SlidingWindowCompactionStrategy
       │
       ▼
TruncationCompactionStrategy
```

The ordering is from lower information loss to higher information loss.

Exact thresholds, history budgets, recent-group counts and safety reserves are tuning/configuration values measured on real workloads. MAF defaults are reference values, not bOps architecture.

## 12. Summarization

### 12.1 Provider/model selection

By default:

```text
Summarizer.Provider = task provider
Summarizer.Model    = task model
```

The operator may explicitly configure a provider and model used only for summarization.

That dedicated profile may be smaller, faster, local or cheaper. bOps must never move summarization to another provider automatically merely because it is cheaper/faster.

### 12.2 Governance

A summarizer call:

- goes through `IModelInvocationBroker`;
- is audited/recorded at model-call granularity;
- obeys timeout/failure containment;
- counts toward task token budgets;
- uses the normal provider/credential configuration path;
- treats a separately configured provider as an explicit data-egress choice.

### 12.3 Authority

A summary is derived context, never evidence.

Authority order:

```text
raw persisted evidence
        >
EvidenceFact / completeness / verification
        >
runtime grounding / limitations
        >
LLM summary
```

A summary cannot create facts, change completeness/verification, activate a conditional step as authoritative evidence, satisfy policy/approval, settle a mutation or replace raw evidence in persistence.

### 12.4 Failure

Summarization is an optimization. Failure does not itself fail the task.

On summarization failure, restore/preserve the candidate history and continue deterministic compaction. Normal cancellation still propagates as cancellation.

## 13. EvidenceRead

`runtime.evidence_read` remains a bOps control-plane mechanism.

Its target role is **exceptional exact drill-down**, not ordinary conversational memory reconstruction.

Normal reasoning should rely on:

```text
MAF compacted context
+
EvidenceFacts
+
grounding/limitations
```

EvidenceRead remains appropriate for exact raw records/fragments.

The current rule whereby EvidenceRead budget exhaustion can terminate the whole task as `Failed/RuntimeFailure` is superseded.

Required semantics:

1. valid read-service exhaustion and malformed control protocol are distinct conditions;
2. after the valid read budget is exhausted, EvidenceRead is no longer offered for that logical call;
3. the model continues with already available evidence;
4. ordinary step/replan/token/duration budgets remain the anti-loop guards;
5. EvidenceRead exhaustion alone never owns the terminal state of the task.

Initial chunk/count values may remain during the semantic migration.

## 14. EvidenceFact coverage

High-density diagnostic tools should expose bounded, decision-relevant structured facts so ordinary reasoning does not repeatedly reopen raw JSON.

Priority tools:

- `system.stability`;
- `system.crashes`;
- `system.dump_analyze`;
- `storage.health`.

Packages own fact vocabulary. Runtime remains opaque to domain-specific keys.

Do not mirror complete tool JSON into facts; emit high-salience counts, identities, status and artifacts used by reasoning/conditional steps.

## 15. Tool authority

The operational tool surface remains bOps-owned.

For a normal planned step it remains equivalent to:

```text
exact ExpectedTool
+
runtime.evidence_read when bOps says it is eligible
```

MAF must not independently expose hosted web search, filesystem access, shell/code execution, MAF skills, background agents, arbitrary function tools or extra package tools.

A future ADR may integrate additional MAF capabilities only through the bOps authority model.

## 16. Harness, sessions and workflows

MAF is the reference framework, but this ADR does not adopt every MAF subsystem.

Full `HarnessAgent` adoption is deferred because Harness overlaps with bOps planning/todo, approval, skills, memory, function invocation, web search, file access and background-agent concerns.

The rule is:

> **Do not transfer a bOps safety/authority responsibility to Harness without explicitly proving and recording the new boundary.**

Similarly, `ChatHistoryProvider`, `AgentSession` and MAF workflow checkpoints do not replace V1.3 `TaskState`, resume fencing, mutation journaling or reconciliation.

The V1.4 Coordinator may use MAF sessions, workflows, `AIContextProvider` and semantic memory more broadly behind the same evidence/authority principles.

## 17. AgentRunner target decomposition

The architectural target is:

```text
AgentRunner
    │
    ├── TaskExecutionStateMachine
    │     planning / replanning / cursor / conditional steps
    │
    ├── GovernedToolExecutor
    │     validation / policy / approval / entitlement
    │     journal / execute / verify
    │
    ├── IModelInvocationBroker
    │     model calls / retry / fallback / audit / budgets
    │
    ├── MAF AgentContextManager
    │     context / compaction / summarization
    │
    └── Evidence and final-synthesis guards
```

This is a staged refactor direction, not authorization for one large rewrite.

## 18. Implementation sequence

Implementation is split into five executable tasks:

- **AF-01 — MAF foundation and governed inference**: isolated MAF integration assembly, pinned dependency, extract `IModelInvocationBroker`, adapt MAF `IChatClient` over the broker.
- **AF-02 — MAF context and compaction**: bOps↔MAF message mapping, model-context profile/budget, protected context, MAF historical compaction, raw persistence unchanged.
- **AF-03 — governed summarization**: `SummarizationCompactionStrategy`, default task provider/model plus explicit summarizer override, broker governance, failure/data-egress tests.
- **AF-04 — evidence cleanup**: soft EvidenceRead exhaustion, protocol-budget separation, drill-down semantics, high-density `EvidenceFact` coverage.
- **AF-05 — real regression, tuning and cleanup**: live/local and external-provider regression, context/summary tuning, remove obsolete HARDEN-8 model-context code after evidence proves it unused.

Benchmarks tune MAF. They do not decide whether bOps uses MAF.

## 19. Telemetry

At minimum observe:

- context strategy;
- model-context profile source/known state;
- history groups before/after;
- estimated history tokens before/after;
- protected-context/tool-schema estimates;
- summarizer call count/tokens/latency;
- provider-reported primary prompt/completion usage;
- EvidenceRead calls/exhaustion;
- context-overflow recovery;
- all-in task tokens.

Never put raw evidence, arguments, secrets or summary text in ordinary telemetry attributes.

## 20. Failure model

| Failure | Required behavior |
|---|---|
| MAF mapping/context conversion fails | Controlled model-context failure; no operational tool executes from a partially built request. |
| MAF compaction fails | Fail safely; during migration the deterministic legacy projection may be used as temporary fallback/reference. |
| Summarizer unavailable/fails | Skip/restore and continue deterministic compaction; not a task failure by itself. |
| Context estimate is wrong and provider reports overflow | Existing bounded overflow recovery; record estimator miss. |
| Context profile unknown | Do not invent a model window. |
| EvidenceRead budget exhausted | Continue without further reads. |
| Dedicated summarizer unavailable | Never silently send evidence to an unconfigured third provider. |
| MAF package upgrade breaks integration/regressions | Reject the upgrade until fixed/reviewed. |

## 21. Security invariants

This ADR does not weaken existing bOps security principles:

1. the LLM never directly executes an OS operation;
2. no generic shell/run-command tool is introduced;
3. every operational call is validated;
4. policy remains authoritative;
5. entitlement remains authoritative;
6. required human approval remains authoritative;
7. side-effecting operations remain journaled where required;
8. side-effecting operations remain post-verified;
9. unknown mutation outcome remains fail-closed;
10. raw tool output remains untrusted data;
11. MAF summary output remains untrusted derived context;
12. MAF cannot widen tool authority;
13. every model call caused by a task remains governed and budgeted by bOps.

## 22. Consequences

### Positive

- bOps returns to its intended MAF-based architecture;
- generic context infrastructure is no longer reimplemented indefinitely;
- context management becomes model-aware;
- summarization may use a dedicated small/local model;
- the public SDK remains framework-independent;
- existing providers remain usable;
- MAF upgrades are isolated behind one integration assembly;
- `AgentRunner` can become smaller and more focused;
- engineering effort stays centered on bOps's differentiators: operations, safety, evidence and recovery;
- V1.4 Coordinator work aligns naturally with MAF.

### Negative

- MAF becomes an important runtime dependency;
- current compaction APIs are experimental;
- integration/regression tests must protect against framework drift;
- exact token counting is not automatically solved for every provider;
- summarization adds model calls;
- a separate summarizer can introduce a separate data-egress boundary;
- migration touches a central runtime path.

These costs are accepted.

## 23. Alternatives rejected

### Keep a fully custom agent framework

Rejected. It spends engineering effort on infrastructure that is not the bOps product differentiator and contradicts the reaffirmed product requirement.

### Keep MAF and HARDEN-8 as permanent interchangeable engines

Rejected. HARDEN-8 remains temporarily as migration/regression reference, not a permanent parallel architecture unless a future concrete requirement proves otherwise.

### Replace public bOps contracts with MAF types

Rejected. It would couple the plugin/provider SDK to framework churn.

### Adopt full Harness immediately

Rejected. MAF is the reference framework, but bOps authority is migrated only where its invariants remain explicit.

### Increase EvidenceRead limits as the solution

Rejected. It treats paging as memory and increases repeated-context cost without fixing context architecture.

## 24. Required documentation reconciliation

After this decision:

1. `agentic/00-project-spec.md` must no longer say bOps is “not a framework consumer”; it must state that bOps uses MAF for generic agent infrastructure while owning operational authority.
2. `agentic/05-workflow.md` continues to prohibit frameworks that **hide or take over** the bOps authority loop; MAF infrastructure use is expected.
3. ADR-0001 gets an amendment pointer, not rewritten reasoning.
4. ADR-0014 gets an amendment pointer that HARDEN-8 model-facing context management is progressively superseded while raw evidence/addressability remains.
5. V1.4-D wording is reconciled: MAF belongs in its isolated integration layer/Coordinator implementation and still must not leak into `bOps.Abstractions` or Managed-Agent persistence contracts.
6. Architecture rules must replace the old “never summarize with another model call” historical rule with the MAF context/authority boundary.

## 25. Acceptance record

**Accepted by the operator on 2026-10-10 (D-048).**

The operator explicitly confirmed:

1. MAF is the intended reference framework and is not an optional architecture to be A/B-selected.
2. Summarization defaults to the task provider/model.
3. A separate provider/model may be configured specifically for summarization.
4. Benchmarks tune the MAF implementation rather than decide adoption.
5. The experimental status of current MAF compaction APIs is acceptable behind an isolated, pinned dependency boundary and temporary migration fallback.

## 26. Research basis

This decision was checked against the current bOps repository and current Microsoft Agent Framework .NET implementation, including the `Microsoft.Agents.AI` package, compaction namespace, `CompactionProvider`, `CompactionMessageIndex`, context-window/tool-result/summarization strategies, Harness, approval, session/history and workflow-checkpoint surfaces.

Relevant upstream references:

- https://www.nuget.org/packages/Microsoft.Agents.AI
- https://learn.microsoft.com/en-us/dotnet/api/microsoft.agents.ai.compaction
- https://learn.microsoft.com/en-us/dotnet/api/microsoft.agents.ai.chathistoryprovider
- https://learn.microsoft.com/en-us/dotnet/api/microsoft.agents.ai.toolapprovalagent
- https://learn.microsoft.com/en-us/agent-framework/workflows/checkpoints
- https://github.com/microsoft/agent-framework/tree/main/dotnet/src/Microsoft.Agents.AI/Compaction
- https://github.com/microsoft/agent-framework/tree/main/dotnet/src/Microsoft.Agents.AI.Harness
