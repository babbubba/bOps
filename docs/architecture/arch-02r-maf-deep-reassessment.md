# ARCH-02R — Deep MAF architecture reassessment

Status: **Proposal — 2026-10-12.** Evidence report; changes no ADR. ADR-0053 stays **Proposed**; the amendments in §9 are
proposals for ARCH-03. AF-FINAL-01, AF-EVID-01, AF-GROUND-01 and KNOW-01 are **not** started by this work.

Base: `3aa8608` (ARCH-02A) on top of `e7ffab6` (ARCH-01) · branch `spike/arch-02r-maf-reassessment` · runtime reference
`998e9523` · `Microsoft.Agents.AI` / `.Abstractions` / `.Workflows` **1.24.0** (latest on NuGet on 2026-10-12; no upgrade),
`Microsoft.Extensions.AI` 10.10.0 / `.Abstractions` 10.10.1 · new spike `spikes/arch-02r-maf-reassessment/` (unreferenced,
outside `bOps.slnx`, throwaway) · ARCH-02A code and report untouched, its 12 cases not re-run.

**Method.** Public surface enumerated from the 1.24.0 NuGet XML docs; every decisive behaviour read in the source at tag
`dotnet-1.24.0` (microsoft/agent-framework) and `v10.10.0` (dotnet/extensions); five new offline tests (8 cases, scripted
`IChatClient`, no live model) where reading was not enough. Learn pages describe the *current* docs and include Python;
they were used only for intent, never as proof of a 1.24.0 .NET API. Line counts of bOps code are from the reference commit
and are **estimates of responsibility size**, not measured removals.

## 1. Answer

**ARCH-02A's NO-GO is correct for MAF defaults and wrong as a conclusion about MAF.** Every ARCH-02A NO-GO (E1a, E1c, E2a)
disappears under a supported 1.24.0 configuration (R1, R2 below) without moving any authority out of bOps. What MAF does
*not* offer — and must not be asked to offer — is the bOps state machine, step authority, policy/approval, journal, fencing
and resume. That was settled by ADR-0053's K-rows; ARCH-02A measured it again and called it "0 removable lines".

The useful MAF surface for bOps is not the tool loop. It is **(1) `ChatClientAgent` as the host of one governed model turn
with a bOps-owned pipeline, and (2) the `AIContextProvider` pipeline as the single mechanism for assembling context —
including Knowledge, Experience and Skills (SKILL.md) packs, retrieval and progressive disclosure.** The bigger saving is
code that bOps would otherwise have to write in KNOW-01…03, not code removed from `AgentRunner`.

**Proposed architecture: "B+" (a restricted D)** — MAF turn agent + MAF context/knowledge/skills providers behind bOps
guard decorators; bOps keeps the task state machine and every authority contract. MAF Workflows: not for the task loop;
re-evaluate for the V1.4 Coordinator. `HarnessAgent` and the Harness planning/approval/loop components: no.

## 2. Verified MAF API inventory (1.24.0 unless stated)

V = verified in source at the tag; T = exercised by a test here (R*) or in ARCH-02A (E*); D = docs only.

| API (C#) | Package @ version | Status | What it does for bOps |
|---|---|---|---|
| `ChatClientAgentOptions.UseProvidedChatClientAsIs` | Microsoft.Agents.AI 1.24.0 | V, T(E1f, R1, R3–R5) | Disables all default decorators (FICC, approval binding/bypassing, OTel). Agent = one inner call per run. |
| `ChatClientAgent` → `ChatClientExtensions.WithDefaultAgentMiddleware` (internal) | 1.24.0 | V | Default pipeline: `ApprovalResponseBinding → ApprovalNotRequiredBypassing → [InvocableFunctionBypassing] → FunctionInvokingChatClient → [MessageInjecting] → [PerServiceCallPersistence] → DeferredOpenTelemetry → leaf`. Reuses a FICC already present in the supplied client. |
| `ChatClientAgentOptions.AIContextProviders` + agent-side provider loop | 1.24.0 | V, T(R1) | Providers run in the agent itself, **also when `UseProvidedChatClientAsIs=true`**: instructions, messages and tools are merged before the leaf call. |
| `AIContextProvider` (`ProvideAIContextAsync`, `InvokingCoreAsync`, `InvokedCoreAsync`, `StateKeys`, `GetService`) | Microsoft.Agents.AI.Abstractions 1.24.0 | V, T(R1, R3) | Public, subclassable, decoratable (wrapper calls `inner.InvokingAsync`). Provider messages are tagged `AgentRequestMessageSourceType.AIContextProvider` + provider type name. `InvokingContext`/`InvokedContext` ctors are `[Experimental]`. |
| `FunctionInvokingChatClient` (`TerminateOnUnknownCalls`, `MaximumIterationsPerRequest` ≥ 1, `MaximumConsecutiveErrorsPerRequest`, `FunctionInvoker`, `AllowConcurrentInvocation`, `AdditionalTools`) | Microsoft.Extensions.AI 10.10.0 | V, T(E1f, R2) | Loop exits on any declaration-only call; unknown call → synthetic `NotFound` result + new iteration unless `TerminateOnUnknownCalls`; `MaximumIterationsPerRequest=1` **still** executes once and makes a 2nd call (last-iteration strip of declarations) — not a safety knob. |
| `FunctionInvocationContext.Terminate` + serial processor | Microsoft.Extensions.AI 10.10.0 (`FunctionInvocationProcessor`) | V, T(R2) | Serial mode breaks after the first call that sets `Terminate`; later calls get no result and stay unanswered. |
| `AIAgentBuilder.Use(Func<AIAgent, FunctionInvocationContext, Func<…>, CancellationToken, ValueTask<object?>>)` (`FunctionInvocationDelegatingAgentBuilderExtensions`) | 1.24.0 | V, T(R2) | Official function middleware; wraps each `AIFunction`, so it fires only when FICC is about to invoke one. Short-circuit = don't call `next`. Only with `ChatClientAgentRunOptions` or no options. |
| `AIAgentBuilder.Use(...)` run middleware, `DelegatingAIAgent`, `AsBuilder()` | 1.24.0 | D/V | Run-level interception (before/after `RunAsync`). |
| `AIContextProviderChatClient` / `UseAIContextProviders(ChatClientBuilder)` | 1.24.0 | D | Same providers at `IChatClient` level; requires an ambient running `AIAgent`. Not needed in B+. |
| `AgentSkillsProvider`, `AgentSkillsProviderBuilder`, `AgentSkillsProviderOptions` | 1.24.0 (released) | V, T(R3) | L1 advertisement in instructions + **always** adds `load_skill`, `read_skill_resource`, `run_skill_script` (`ApprovalRequiredAIFunction` by default), even with no scripts. |
| `AgentSkillsSource`, `DelegatingAgentSkillsSource`, `AgentInMemorySkillsSource`, `AgentFileSkillsSource(+Options: AllowedScriptExtensions, ScriptFilter, ResourceFilter, SearchDepth)`, `Filtering`/`Caching`(`CacheIsolationKeySelector`, `RefreshInterval`)/`Deduplicating`/`Aggregating` sources, `AgentInlineSkill`, `AgentClassSkill<T>`, `AgentSkill.GetContentAsync/GetResourceAsync/GetScriptAsync`, `AgentSkillFrontmatter` | 1.24.0 | V, T(R3) | Composable source pipeline; `AgentSkillsSourceContext` carries agent+session. Scripts run only through a caller-supplied `AgentFileSkillScriptRunner` delegate. |
| `AgentMcpSkillsSource` (skill://index.json, zip-slip-guarded archives) | Microsoft.Agents.AI.Mcp **1.24.0-alpha** | D | Remote skill distribution; alpha. Not for v1. |
| `TextSearchProvider` (`BeforeAIInvoke` / `OnDemandFunctionCalling`, `TextSearchResult{SourceName, SourceLink, Text, RawRepresentation}`, `ContextFormatter`, `CitationsPrompt`) | 1.24.0 | D/V | Generic RAG provider over any search delegate; carries provenance fields. |
| `ChatHistoryMemoryProvider` (+`Scope{ApplicationId, AgentId, UserId, SessionId}`, storage/search filters) | 1.24.0 on Microsoft.Extensions.VectorData.Abstractions 10.10.0 | D | Writes **all** chat history to a vector store after each run and retrieves similar turns. |
| `FileMemoryProvider`, `FileAccessProvider`, `AgentFileStore` | 1.24.0 | D | Model-driven file memory / file access tools. |
| `TodoProvider`, `AgentModeProvider`, `TodoCompletionLoopEvaluator` | 1.24.0 | D | Model-mutable todo/mode state in `AgentSession.StateBag`, exposed as tools. |
| `LoopAgent` + `LoopEvaluator` (`DelegateLoopEvaluator`, `CompletionMarker…`, `AIJudge…`), `LoopAgentOptions.MaxIterations` | 1.24.0, `[Experimental]` | V(attr)/D | Bounded re-invocation of an agent with evaluator feedback. |
| `ToolApprovalAgent`, `ToolApprovalRule` | 1.24.0 | D | "Don't ask again" standing auto-approval rules in session. |
| `HarnessAgent`, `HarnessAgentOptions` | Microsoft.Agents.AI.Harness 1.24.0 | D (option docs) | FICC always in pipeline; defaults ON: hosted web search, file memory (`{cwd}/agent-file-memory`), skills from cwd, todo, mode, tool auto-approval, OTel. Each has a `Disable*` flag except FICC. |
| `AIAgentHostOptions.InterceptUnterminatedFunctionCalls` / `InterceptUserInputRequests`, `BindAsExecutor(AIAgent, AIAgentHostOptions)` | Microsoft.Agents.AI.Workflows 1.24.0 | V, T(R4) | Unanswered `FunctionCallContent` is sent as a workflow message (instead of a `RequestPort` request); the agent executor accepts `FunctionResultContent` back to continue. |
| `WorkflowBuilder.AddEdge<T>(…, Func<T,bool>)`, `AddSwitch`/`SwitchBuilder.AddCase/WithDefault`, `AddFanOutEdge`, `StatefulExecutor<T>`, `RequestPort`, `InProcessExecution.Run/Resume(StreamingAsync)`, `CheckpointManager` | Workflows 1.24.0 | D, T(R4 partly) | Typed message routing, conditional edges, supersteps; checkpoints at **end of each superstep** (docs). |
| `AgentEvaluationExtensions.EvaluateAsync` (live or over pre-recorded `AgentResponse`s), `LocalEvaluator`, `EvalChecks.ToolCallArgsMatch`, `ExpectedToolCall`, `MeaiEvaluatorAdapter`, `IAgentEvaluator` | 1.24.0 (+ Microsoft.Extensions.AI.Evaluation 10.10.0) | D | Offline scoring harness; `ExpectedToolCall` is pure data whose arguments evaluators "typically" match as a subset (docs) — same idea as `ExpectedArguments`. |
| `AgentResponse<T>` / `StructuredOutputSchemaUtilities` | Abstractions 1.24.0 | D | Typed structured output. Provider support decides usefulness (ND-4). |
| `Compaction.*` (`CompactionProvider`, strategies) | 1.24.0, experimental | C (AF-02/03) | Already used by `MafContextProjector` via static `CompactionProvider.CompactAsync`. |
| `Microsoft.Agents.AI.AgentHooks` (`AgentHooksGatingAIContextProvider` wraps providers to gate durable writes) | 1.24.0-**alpha** | V | Official precedent for the provider-decorator pattern used by `ToolSurfaceGuard` (R3). Not adopted. |

Release status: `Microsoft.Agents.AI`, `.Abstractions`, `.Workflows`, `.Harness`, `.Workflows.Declarative` are `IsReleased`;
`.Mcp`, `.AgentHooks`, `.Hosting*` are alpha/preview. No decisive capability exists only in a later release: 1.24.0 is
the newest .NET release (`dotnet-1.24.0`).

Links (authoritative for 1.24.0 = source at tag; Learn = current docs):
- NuGet: https://www.nuget.org/packages/Microsoft.Agents.AI/1.24.0 · https://www.nuget.org/packages/Microsoft.Agents.AI.Workflows/1.24.0 · https://www.nuget.org/packages/Microsoft.Agents.AI.Harness/1.24.0
- Source: https://github.com/microsoft/agent-framework/tree/dotnet-1.24.0/dotnet/src — `Microsoft.Agents.AI/ChatClient/ChatClientAgent.cs`, `ChatClient/ChatClientExtensions.cs`, `FunctionInvocationDelegatingAgent.cs`, `Skills/AgentSkillsProvider.cs`, `Skills/File/AgentFileSkillsSource.cs`, `TextSearchProvider.cs`, `Harness/Loop/LoopAgent.cs`, `Microsoft.Agents.AI.Abstractions/AIContextProvider.cs`, `Microsoft.Agents.AI.Workflows/Specialized/AIAgentHostExecutor.cs`, `Specialized/AIContentExternalHandler.cs`, `Microsoft.Agents.AI.Harness/HarnessAgent.cs`
- MEAI source: https://github.com/dotnet/extensions/blob/v10.10.0/src/Libraries/Microsoft.Extensions.AI/ChatCompletion/FunctionInvokingChatClient.cs · `.../Common/FunctionInvocationProcessor.cs`
- Samples at tag: `dotnet/samples/02-agents/Agents/Agent_Step11_Middleware`, `Agent_Step17_AdditionalAIContext`, `02-agents/AgentSkills/*`, `02-agents/AgentWithRAG/*`, `02-agents/AgentWithMemory/*`, `03-workflows/_StartHere/02_AgentsInWorkflows`, `03-workflows/ConditionalEdges/*`, `03-workflows/Checkpoint/*`
- Learn: https://learn.microsoft.com/en-us/agent-framework/agents/middleware/ · https://learn.microsoft.com/en-us/agent-framework/agents/skills · https://learn.microsoft.com/en-us/agent-framework/workflows/checkpoints · https://learn.microsoft.com/en-us/dotnet/api/microsoft.agents.ai.chatclientagentoptions.useprovidedchatclientasis · https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.ai.functioninvocationcontext.terminate · https://learn.microsoft.com/en-us/dotnet/api/microsoft.agents.ai.aicontextprovider · https://learn.microsoft.com/en-us/dotnet/api/microsoft.agents.ai.agentskillsprovider · https://learn.microsoft.com/en-us/dotnet/api/microsoft.agents.ai.textsearchprovider · https://learn.microsoft.com/en-us/dotnet/api/microsoft.agents.ai.workflows.aiagenthostoptions

## 3. Phase 1 — the real extensibility model (findings)

1. **FICC is a default, not the agent.** `ChatClientAgent` adds FICC only through `WithDefaultAgentMiddleware`;
   `UseProvidedChatClientAsIs=true` removes it and every other default decorator. There is no FICC setting that makes the
   auto-loop safe on its own: `MaximumIterationsPerRequest` cannot be 0 and `=1` still executes once and calls again;
   `TerminateOnUnknownCalls` only covers unknown names. Declaration-only tools always end the loop.
2. **Interception before invocation exists twice.** (a) Agent function middleware (`AIAgentBuilder.Use(…FunctionInvocationContext…)`)
   runs before the `AIFunction` body and can short-circuit; `ctx.Terminate=true` stops FICC before any further model call;
   serial processing stops at the first terminating call. (b) `FICC.FunctionInvoker` delegate, same semantics, lower level.
   Both still append a synthetic `FunctionResultContent` for the intercepted call (R2) — an artefact bOps must drop.
3. **"Proposal, not execution" with zero extra model calls** is therefore achievable two ways; the simpler is not to have a
   function-invocation loop at all (R1). In both, verdict/budget/correction remain bOps code — as ADR-0053 requires.
4. **`ChatClientAgent` with a custom pipeline** keeps the parts bOps wants (provider pipeline, `AgentResponse` with
   `RawRepresentation`/usage/finish reason — E3) and drops the parts it must not have.
5. **Workflows** route a non-terminated call natively to another executor (`InterceptUnterminatedFunctionCalls`, R4) or to
   the host via `RequestPort`. Conditional edges/switch route typed messages. The agent hosted in the executor is still an
   ordinary `AIAgent`: if it carries FICC and invocable tools, they execute *inside* the executor — the workflow adds no
   safety by itself.
6. **State.** `AgentSession.StateBag` is where MAF keeps history (default `InMemoryChatHistoryProvider`), todo, mode,
   approval rules, injected messages, skills cache keys. R5: a reused session silently prepends its history; no session ⇒
   stateless, identical request. Workflow checkpoints capture executor state at superstep end; resuming re-runs the
   interrupted superstep (docs) — a replay hazard for anything with side effects.
7. **Harness** is a bundle, not a mechanism: an opinionated `ChatClientAgent`+FICC with providers and decorators on by
   default. Every component is also usable alone from `Microsoft.Agents.AI`. Disabling everything unsafe leaves
   ChatClientAgent+FICC, i.e. less than B+.
8. **Evaluation, retries, finalization, structured output.** MAF has an offline evaluation API (useful for KNOW-03/BENCH
   replay), `LoopAgent` evaluators (generic re-invocation, `[Experimental]`), typed `AgentResponse<T>`. It has **no**
   model-call retry policy at agent level (FICC retries only tool errors) — correct, retries stay in the broker — and no
   notion of plan coverage for finalization.

## 4. Phase 4 — critical review of ARCH-02A

| Case | What it proves | What it does not prove | Default-specific? | Survives supported extension? |
|---|---|---|---|---|
| E1a auto-invoke | Default pipeline runs tool bodies and calls the model again | That MAF needs to own execution | **Yes** | **No** — R1 (no FICC) and R2 (middleware+Terminate): 0 bodies, 1 call |
| E1b declaration-only | Call surfaces untouched; verdict is bOps | Anything negative about MAF: authority is K by ADR | n/a | Parity (by design) |
| E1c unknown tool | Default injects "not found" + hidden call | — | **Yes** | **No** — E1f/R2 (`TerminateOnUnknownCalls`), R1 (no FICC) |
| E1d two calls | First-call-wins is expressible | — | n/a | Parity; R2 shows the same with middleware (2nd call unanswered) |
| E1e escaped wire name | MAF has no alias resolution | That this is a problem: mapping is bOps (`ToolWireNames`) and stays there | n/a | Parity in safe configs (call surfaces for bOps mapping) |
| E1f hand-built pipeline | The fix exists | "Needs hand-built pipeline" as cost: it is one documented option (~10 lines) | — | — |
| E2a validation retry | Default absorbs tool errors and retries autonomously | — | **Yes** | **No** — R2 `wrong_args`: 1 call, 0 retries, proposal to bOps |
| E2b correction budget | Budget lives in bOps | — | n/a | Parity (by design) |
| E3 ×4 | `finish_reason`, empty text, usage reach the caller intact; no hidden retry | Streaming path; plan-coverage gate (bOps by design) | n/a | Parity |

Not tested by ARCH-02A: context providers, knowledge/RAG, skills, sessions/state, function middleware, workflows,
Harness components, evaluation, and E4–E7. **"≈0 removable lines" is demonstrated only for the authority logic exercised by
E1–E3**, which ADR-0053 had already declared non-transferable; it is premature as a statement about MAF adoption, because it
ignored (a) non-authority code in the runner (context/prompt assembly) and (b) code not yet written (KNOW-01…03), where
MAF's value is concentrated. ARCH-02A's own decision — "MAF stays at the chat-client/compaction boundary" — should be
superseded by the B+ proposal below, subject to ND-1/ND-2.

## 5. New offline tests (spike `spikes/arch-02r-maf-reassessment/`, 5 tests / 8 cases, all green)

Run: `dotnet test` in the spike folder only (filtered to the spike assembly; no runtime suite, no WSL, no live model).

| Test | Setup | Measured | Consequence |
|---|---|---|---|
| R1 | `ChatClientAgent`, `UseProvidedChatClientAsIs=true`, **invocable** `svc_restart`, `AIContextProviders=[pack provider]`; model emits `svc_restart` + unknown `firewall_flush` | 1 model call, 0 body runs, both calls returned, 0 synthetic results; model saw step instructions + `KNOWLEDGE(pack=…)` instructions, knowledge message tagged `AIContextProvider`, tools=`[svc_restart]` | Option B core holds; providers work without FICC; ARCH-02A NO-GOs do not apply |
| R2 ×4 | Default pipeline (pre-built FICC with `TerminateOnUnknownCalls`), official function middleware that records a proposal, does not call `next`, sets `Terminate` | All 4 cases: 1 call, 0 bodies. matched/wrong_args/two_calls → 1 proposal `c0` + 1 synthetic result `c0`; two_calls leaves `c1` unanswered; unknown → 0 proposals, 0 results | Proposal capture works on the default pipeline too; synthetic result is a divergence bOps must strip — B+ prefers R1 |
| R3 | `AgentSkillsProvider` over `PinnedPackSkillsSource` (DelegatingAgentSkillsSource, SHA-256 pin) and `ToolSurfaceGuard` (provider decorator) | Stock: tools `+load_skill,read_skill_resource,run_skill_script`; guarded: only the step tool, L1 advertisement kept; tampered content → `pack pin mismatch` before any model call | MAF skills widen the surface by default (I-8), but two ~15-line bOps decorators on public extension points pin and restrict them |
| R4 | Workflow: as-is agent bound with `InterceptUnterminatedFunctionCalls=true` → edge → bOps executor | 1 call; bOps executor received `svc_restart`; no `RequestInfoEvent` | Native hand-off works; it proves nothing about avoiding a second cursor (not testable cheaply) |
| R5 | Same as-is agent: two runs without session, two with one reused `AgentSession` | Request message counts `[1,1,1,3]` | Stateless turns are identical; a reused session becomes a second history authority → B+ uses no/fresh session per turn (I-5) |

## 6. Phase 2 — four architectures

Invariants that are **not negotiable** (ADR-0053 I-1…I-10): execution only under bOps authority; policy/entitlement/approval
never inferred or cached; unknown outcome never auto-repeated; everything audited; resume from persisted state only;
no `Completed` with unresolved mandatory steps; bounded loops; no tool-surface widening; broker owns providers/credentials.
**Replaceable implementation choices**: how the turn's prompt is assembled, how history is projected, how knowledge is
retrieved/injected, how the model call object is constructed, the eval harness. **Current bugs** (B03 early-final, EvidenceRead
repetition, grounding, step coverage) are bOps logic defects; no option fixes them and none should be chosen to fix them.

| Criterion | A — current runner + adapter | B — ChatClientAgent custom (B+ = with providers) | C — MAF Workflow | D — Hybrid Harness/Workflow |
|---|---|---|---|---|
| bOps code removable (est.) | 0 | ~150–400 lines of prompt/context assembly **relocated** into providers (`BuildPlanningSystemPrompt`, `BuildStepSystemPrompt`, `DescribeToolCatalog`, `DescribePlan`, `DescribeStepsSoFar`, ≈ AgentRunner 4716–4880), net deletion small; **avoided** KNOW engine code est. 1.5–3k lines (retrieval/injection, skill discovery/parsing/caching, provenance formatting, vector abstraction, eval harness) | ~0 from runner: the plan/cursor/replan machine must stay authoritative; per-step executors wrap it | Same as B+ for context; planning/todo/approval components would *replace nothing* because plan/approval are K |
| New adapter code (est.) | 0 | 300–500 lines: turn-agent factory, guard decorators, pinned sources, response normaliser, architecture tests | 600–1000: message types, executors, edge predicates, checkpoint discipline, state mapping | High: disable/neutralise defaults, reconcile todo/mode with `AgentPlan` |
| Duplicated state machines | none | none (MAF session disposable, no graph) | **Yes**: workflow graph/supersteps + bOps plan/cursor; dynamic replanning is not a static graph | **Yes** if TodoProvider/AgentMode/LoopAgent used: model-mutable todo vs `AgentPlan` |
| Maintenance | known | +MAF churn on a small surface (agent ctor, providers, skills sources) | +churn on Workflows API, checkpoint formats | Highest; Harness defaults change between releases |
| Step authority, policy, entitlement, approval, journal | bOps | bOps (proposal = surfaced call) | bOps executor; must also guard against superstep replay | bOps; `ToolApprovalAgent` standing rules conflict with I-2 |
| Resume / fencing / unknown outcome | bOps | bOps; MAF stateless (R5) | **Risk**: checkpoint resume re-runs the superstep (I-3) unless mutating work stays behind the journal outside workflow state | Risk via session state (todo/approvals) |
| Audit (I-4) | bOps | bOps leaf `GovernedChatClient` sees the final request after all providers → injected knowledge is audited for free (ND-2 to confirm field parity) | same, plus workflow events | Hidden calls if FICC/LoopAgent/AIJudge are active |
| Loop / hidden retry / unauthorised execution risk | low | low; regression guard = assert no FICC in pipeline + declarations only | medium (agent inside executor may carry FICC) | high (FICC always in HarnessAgent; AIJudge makes its own calls) |
| Framework substitutability | high | high: MAF confined to adapter; contracts stay bOps | low: orchestration expressed in MAF graph | low |
| Local Agent + Coordinator | same engine | same engine; Coordinator can add hosting later | Coordinator-friendly (fan-out, HITL ports); heavy for Local Agent | poor fit for unattended ops |
| **Verdict** | Baseline; leaves KNOW to custom code | **Recommended (B+)** | Not for the task loop; candidate for Coordinator multi-node orchestration (V1.4-D) | Only the context/knowledge/skills/eval parts — which *are* B+ |

## 7. Phase 3 — Knowledge, Experience, Memory, Skills, Playbook

**Naming.** bOps "Skill" = an operational tool package (`SkillRegistry`, manifests, verification). MAF "Agent Skill" = a
SKILL.md instruction bundle with resources and optional scripts (agentskills format). They are different things; a MAF
Agent Skill maps to a **bOps Knowledge/Playbook pack**, never to a tool package.

| Need | Reuse from MAF | Must stay bOps | Decision |
|---|---|---|---|
| **Knowledge** (manuals, procedures, system docs) | `AgentSkill` model + SKILL.md format; `AgentFileSkillsSource` (discovery, frontmatter, resource extension allow-list, `ScriptFilter`), `Filtering`/`Caching`/`Deduplicating`/`Aggregating` sources; `AgentSkillsProvider` L1 advertisement; `TextSearchProvider` (`BeforeAIInvoke`, `SourceName/SourceLink`, `ContextFormatter`) for chunk retrieval | Pack manifest + signature + trust store; `(packId, version, hash)` pin per task (`PinnedPackSkillsSource`); activation/rollback; provenance label format; "knowledge ≠ evidence" (ADR-0042) | MAF extended. Packs are SKILL.md-compatible directories inside a signed bOps envelope |
| Progressive disclosure (load a skill/resource on demand) | `AgentSkill.GetContentAsync/GetResourceAsync` | The *tool*: `runtime.knowledge_read`, governed like `runtime.evidence_read` (eligibility, budget, audit); provider tools stripped by `ToolSurfaceGuard`; `run_skill_script` never exposed, no script runner registered | bOps tool, MAF content API |
| **Experience** (incidents, validated diagnoses, tenant memory) | `Microsoft.Extensions.VectorData` abstractions (already a MAF dependency) + `TextSearchProvider` or a small custom provider for retrieval | Curated record schema (outcome validated by verification/operator), write path after task completion, tenant partition (collection per tenant or mandatory filter), retention | MAF extended for read; bOps write. **Not** `ChatHistoryMemoryProvider`: it stores raw history (incl. tool output) automatically after every run — uncurated, egress-prone |
| **Memory** (conversation) | `ChatHistoryProvider`/sessions only as disposable per-turn objects | `TaskState` + audit are the memory of a task (D3) | bOps |
| **Playbook** (declarative, versioned diagnostic/operational procedure) | Distribution/packaging via the same skills sources (playbook YAML as a pack resource) | Schema, compiler to `PlannedStep` (`ExpectedTool`+`ExpectedArguments`+`Activation`), validation against delegation; every step through the normal path | bOps. `Workflows.Declarative` is rejected: it executes actions (`InvokeFunctionTool`, `InvokeMcpTool`, HTTP) — a second execution engine |
| Remote/MCP skill sources | `AgentMcpSkillsSource` | — | Defer (alpha) |
| Tenant isolation | `AgentSkillsSourceContext{Agent, Session}`, `CacheIsolationKeySelector` | Providers/sources are constructed **per task with the tenant bound**; no shared cache bucket across tenants | MAF extended |
| Privilege | — | `AllowedTools` frontmatter is ignored (never a grant); scripts disabled; guard drops any provider-added tool not allow-listed; playbook may not name a tool outside the delegation | bOps (I-8) |
| OSS vs commercial | Same providers in both | Commercial supplies sources (pgvector collection, pack registry, signing infra) through bOps interfaces; no second engine | One engine (D4) |
| Evaluation (KNOW-03) | `AgentEvaluationExtensions.EvaluateAsync` over **recorded** responses, `LocalEvaluator`, `EvalChecks.ToolCallArgsMatch`, MEAI evaluators | Scenario corpus, grounding metrics, cost accounting | MAF native, offline only |

The small bOps adapter needed is exactly what R3 shows: a source decorator (verify signature/hash, enforce pin, tenant
scope) and a provider decorator (tool allow-list, provenance labelling, audit hook). Everything else — discovery, parsing,
caching, dedup, injection, ordering, tagging — is MAF.

## 8. Phase 5 — component matrix

| Component | MAF native | MAF extended | bOps | Not determined | Evidence · constraint · cost · benefit → decision |
|---|---|---|---|---|---|
| Orchestration (task loop, plan, cursor, replan) | | | ● | | ADR-0053 row 1; R4 shows routing only · I-5/I-6/I-7 · — · — → **keep bOps** |
| Planning (plan generation prompt/parse) | | ○ prompt via providers | ● parse/validate | structured output (ND-4) | `AgentResponse<T>` exists; local-model support unknown → keep parser |
| Model turn execution | | ● `ChatClientAgent` as-is + `GovernedChatClient` leaf | broker | | R1, R5, E3 · I-4, I-9 · small · uniform provider pipeline → **adopt (B+)** |
| Tool calling | | ● declarations only, no FICC | ● verdict, first-call-wins, wire names, correction | | R1/R2/E1* · I-1/I-7 · small · none beyond safety → **proposal-only, no FICC** |
| Finalization | | | ● | `LoopAgent` as carrier (experimental) | E3; I-6 · AF-FINAL-01 is bOps → **keep bOps** |
| Context assembly | ● provider pipeline | ● bOps providers (protected ctx, plan, step, evidence digest) | content | ND-1/ND-2 | R1 · I-4/I-5 · 150–400 relocated · single composition point for knowledge → **adopt** |
| Compaction/summarisation | ● (AF-02/03) | | budgets, protected ctx | in-pipeline `CompactionProvider` (ND-6) | already C → keep as is |
| Knowledge | ○ | ● skills sources/provider + `TextSearchProvider` behind guards | pack contract, signing, pin, provenance | | R3 · I-8 · adapter ~150 · avoids custom retrieval engine → **adopt in KNOW-01** |
| Experience | | ● VectorData + search provider (read) | ● curated write, tenant partition | connector choice (ND-5) | docs · tenant isolation · medium · → **adopt read path** |
| Memory | | | ● TaskState/audit | | R5, D3 → **sessions disposable** |
| Skills (SKILL.md) | ○ format, sources | ● guarded provider | `runtime.knowledge_read`, no scripts | MCP sources (alpha) | R3 · I-8 · small → **adopt guarded** |
| Playbook | | ○ packaging | ● schema + compiler to `PlannedStep` | | ADR-0053 §4.5 · I-1 → **bOps** |
| Policy / entitlement / approval | | ○ approval *UX transport* (Coordinator, later) | ● | Coordinator HITL via `RequestPort`/approval content | I-2 → **bOps**; `ToolApprovalAgent` rejected |
| Persistence / resume / fencing / journal | | | ● | Workflow checkpoints (ND-3) | I-3/I-5 → **bOps** |
| Audit | | | ● (leaf client) | field parity (ND-2) | I-4 → **bOps** |
| Evaluation | ● offline | | scenarios/metrics | | docs → **adopt for KNOW-03/BENCH replay** |
| Multi-node orchestration (Coordinator) | | ○ Workflows (fan-out, HITL) | each node = bOps engine | ● (V1.4-D) | R4 → **re-assess in V1.4-D** |
| Harness bundle / Todo / Mode / Loop / FileMemory / FileAccess / BackgroundAgents / web search | | | | | defaults violate I-2/I-7/I-8; overlap K rows → **reject** |

● = owner/decision, ○ = partial use.

**Simplest architecture that is safe, extensible and sellable:** one bOps engine (state machine + authority) that runs each
model turn through a pass-through `ChatClientAgent` whose only intelligence is the MAF `AIContextProvider` pipeline —
bOps providers for task context, MAF providers (skills, text search, vector retrieval) for Knowledge/Experience behind two
bOps decorators — with the bOps `GovernedChatClient` as the leaf so every assembled request is governed and audited. No
FICC, no MAF session state, no graph. Commercial adds sources and stores, not engines.

## 9. Proposed convergence roadmap (does not alter ADR-0053)

Order respects the active plan: AF-05H2-R1 → BENCH-01 → AF-EVID-01 → AF-05H4-VERIFY → BENCH-00 3×3 → PR #100 decision
come first and are untouched by this report.

1. **ARCH-02R** (this) — evidence + proposal. Operator review.
2. **ARCH-02B′ — close ND-1/ND-2/ND-6 offline** (spike only): run a recorded task's next turn through `GovernedChatClient`
   both via the current runner path and via an as-is `ChatClientAgent` with equivalent providers; compare the leaf request
   byte-for-byte (mod ids/timestamps) and the model-call audit row. Budget: one spike, no runtime change.
3. **ARCH-03** — decide row by row. Proposed ADR-0053 amendments:
   - row 2: "Adapter exposes the step view as `AIFunctionDeclaration`s to an as-is `ChatClientAgent` (no FICC)"; add an
     architecture test that fails if `FunctionInvokingChatClient` appears in the turn pipeline;
   - row 4: context assembly through the `AIContextProvider` pipeline; protected context as a bOps provider;
   - row 11 and D5: "knowledge **contracts** are framework-independent; retrieval/injection is implemented by MAF providers
     in the adapter assembly; replacing MAF rewrites adapters only";
   - I-8 / ADR-0052 §15: allow MAF Agent Skills **only** as pinned, signed instruction/resource content with provider
     tools stripped, scripts disabled and `AllowedTools` ignored;
   - §16 (Harness): component-level rejection list as in §8.
4. **AF-TURN-01** (after ARCH-03) — introduce the turn agent behind the adapter with recorded-request equivalence tests;
   move prompt assembly into bOps providers; no behaviour change.
5. **KNOW-01 (rescoped)** — pack envelope (manifest, signature, pin) around SKILL.md-compatible content;
   `PinnedPackSkillsSource`, `ToolSurfaceGuard`, provenance labels, `runtime.knowledge_read`; no custom retrieval engine.
6. **KNOW-02** — playbook schema + compiler to `PlannedStep`, distributed as pack resources.
7. **KNOW-03** — A/B/C with MAF offline evaluators over recorded responses.
8. **V1.4-D / ARCH-04** — Coordinator: evaluate Workflows (fan-out, `RequestPort` HITL) where each node runs the bOps engine;
   must first close ND-3.

## 10. Not determined (with minimum experiment)

| Id | Question | Minimum experiment |
|---|---|---|
| ND-1 | Resume through the as-is agent yields the byte-equal next request (I-5) | ARCH-02B′ step 2 against a recorded `TaskState` |
| ND-2 | Model-call audit fields identical when content comes from providers (I-4) | Same run, compare audit rows |
| ND-3 | Workflow checkpoint resume cannot re-invoke a mutating step (I-3) | Crash inside a superstep containing a fake mutating executor; resume from last checkpoint; count invocations with the journal in front |
| ND-4 | Structured output (`AgentResponse<T>` / JSON schema) reliability with the pinned LlamaCpp model | Offline replay of recorded plan responses + one guarded live call in a later benchmark task |
| ND-5 | VectorData connector (pgvector/SQLite) pinning and tenant filter enforcement | Connector package audit + filter bypass test |
| ND-6 | `CompactionProvider` inside the provider pipeline equals `MafContextProjector` output | Golden-file comparison on AF-02 fixtures |
| ND-7 | Streaming path parity of `finish_reason`/usage/empty content (E3 was non-streaming) | E3 cases via `RunStreamingAsync` |

## 11. Risks

1. **Default creep**: dropping `UseProvidedChatClientAsIs` or passing invocable `AIFunction`s silently re-enables the
   ARCH-02A NO-GOs. Mitigation: declarations only + pipeline architecture test (row 2 amendment).
2. **MAF churn**: six minor releases in seven weeks (1.19→1.24); some used APIs are `[Experimental]` (compaction,
   `LoopAgent`, `InvokingContext` ctors). Mitigation: pinned version, surface limited to agent ctor/options, provider
   base class, skills sources.
3. **Provider side effects**: providers may add tools (skills, on-demand search) or write state in `InvokedAsync`
   (`ChatHistoryMemoryProvider`). Mitigation: only guarded providers; per-turn fresh session; no auto-writing memory.
4. **R2 artefact**: if middleware is ever used, the synthetic `FunctionResultContent` must not reach persisted history.
5. **Concept collision** "skill" (tool package vs SKILL.md) — keep names distinct in contracts and UI.
6. **Tenant leakage via caches** — per-task construction, isolation key = tenant, no shared bucket.
7. **Over-reach**: B+ must not become a pretext to move K-rows or to fix AF-FINAL/EVID/GROUND defects in the adapter.
