# Contract 01 — MAF as agent infrastructure, governed model calls, context and providers

| | |
|---|---|
| **Status** | **Current** — in force since DOC-03 (`BOPS-0121`); GATE-02 approved by the operator on 2026-10-22. Entries under *Pending decisions* stay non-binding. |
| **Date** | 2026-10-22 |
| **Git base** | Consolidated at `21c10f6ef806bbb9ab649468197090aef77e64a9` (DOC-02, corrected at `207ad7d`); activated by DOC-03 from source commit `57990d26f362676624d203064b69d3ced9c3448f` on `docs/adr-consolidation-2026-10-22` |
| **Task** | `BOPS-0121` / `DOC-CONSOL-01` (phases DOC-02 and DOC-03), plan `agentic/_plans/ADR-consolidation-2026-10-22.md` |
| **Binding rules** | 23 (`C01-R01`…`C01-R23`); `C01-R22` added 2026-10-23 by operator decision D12 (`BOPS-0122`); `C01-R23` added 2026-10-27 by operator mandate (`BOPS-0129`) |
| **Language** | English |

Rule text below consolidates the historical ADRs, architecture rules, security invariants and decision register entries listed under *Sources*. Decision status and implementation status are independent: a rule can be Accepted and Not implemented, or implemented without being a decision. Every `Implemented` claim carries `file:line` evidence located in the DOC-02 session. The evidence anchors the implementing symbol (and the architecture test that enforces the rule where one exists); it does not re-prove every clause of the rule, which the named tests do. Code line numbers move, so an anchor is a pointer to a symbol, not a frozen citation.

## 1. Scope and boundaries

This contract binds how bOps uses the Microsoft Agent Framework (MAF) and how every model call is made: the project-owned model contract, provider packages, the governed model-invocation broker, model-facing context construction and compaction, summarization, the provider wire contract, truthful history for unexecuted tool calls, model-failure classification, retry and containment, and provider configuration, execution-scoped pinning and fallback.

MAF is infrastructure only. It shapes what the model sees and never what bOps executes.

**Boundaries with the other contracts**

- **Contract 02** owns tools, policy, risk, approval, entitlement, verification, secrets and the vault. This contract only requires that those stay in bOps and that MAF cannot reach them.
- **Contract 03** owns `TaskState`, plans, step authority, replanning, budgets, resume and the mutation journal. Model-call retry and containment (C01-R12–R14) feed task terminal reasons defined there (C03-R10).
- **Contract 04** owns evidence, completeness, limitations and grounding. MAF summaries are derived context and never evidence (C01-R08).
- **Contract 05** owns the HTTP/CLI/UI surfaces that expose provider configuration; the rules for the configuration state machine itself are here (C01-R15–R21).

## 2. Binding rules

### C01-R01 — MAF is infrastructure, never operational authority

Microsoft Agent Framework (MAF) is the reference framework and preferred implementation for generic agent infrastructure only: conversation/message infrastructure, context construction and history management, compaction, tool-result compaction, governed summarization, context-window management, agent-context middleware, and (only where proven useful without weakening bOps authority) function-call plumbing. bOps retains exclusive ownership of AgentPlan and revisions, exact step/tool authority and tool visibility, tool manifests and argument validation, RiskLevel, policy, entitlement, human approval, the mutation intent journal, machine-side tool execution, post-action verification, EvidenceFact/completeness/limitations/grounding, TaskState/resume/execution-attempt fencing, unknown-outcome handling and reconciliation, audit semantics, provider pinning and fallback, and task/model budgets. MAF influences what the model sees and never what bOps executes: it may not independently choose or execute operational tools, authorize an operation, satisfy entitlement or approval, mutate TaskState, settle a mutation, replace verification or evidence semantics, or bypass model-call audit and budgets. MAF is the reference framework by operator decision, not an A/B-selectable option; benchmarks tune the MAF implementation and do not decide adoption.

- **Supersedes / consolidates:** A13, D-048, ADR-0052 §1-§3, ADR-0052 §21, ADR-0052 §25, 00-project-spec (what bOps is not), 00-project-spec (What bOps is not), 00-project-spec P1-P7
- **Decision status:** Accepted (ADR-0052, D-048)
- **Implementation status:** Implemented — evidence below (isolated MAF assembly exists; the authority responsibilities remain in bOps.Runtime)
- **Evidence:** `src/core/bOps.AgentFramework/bOps.AgentFramework.csproj:23`; `src/core/bOps.Runtime/AgentRunner.cs:46`

### C01-R02 — MAF dependency boundary

MAF lives behind a dedicated implementation assembly above bOps.Runtime (its name is not architectural). No MAF or Microsoft.Extensions.AI type may enter bOps.Abstractions, the third-party plugin SDK, the Runtime public surface or Managed-Agent persistence contracts. Provider packages remain implementations of the project-owned IChatModel. The MAF package version is explicitly pinned; floating ranges are forbidden. The experimental status of MAF compaction APIs is accepted only because the dependency is isolated and reversible; a MAF upgrade that breaks the integration or its regression tests is rejected until fixed and reviewed.

- **Supersedes / consolidates:** A13, ADR-0052 §4, ADR-0052 §20 (upgrade), ADR-0052 §25 item 5, D-048
- **Decision status:** Accepted (ADR-0052, D-048)
- **Implementation status:** Implemented — evidence below
- **Evidence:** `src/core/bOps.AgentFramework/bOps.AgentFramework.csproj:23`; `tests/bOps.Architecture.Tests/AbstractionsStaysDependencyFreeTests.cs:16`; `tests/bOps.Architecture.Tests/AgentFrameworkBoundaryTests.cs:6`

### C01-R03 — Project-owned, dependency-free model contract

IChatModel, ChatTurn, ModelToolCall, ModelRequest, ModelResponse, ChatModelDescriptor and ModelUsage are defined in the permanently dependency-free bOps.Abstractions and owned by the project, so a vendor, Microsoft.Extensions.AI or MAF break touches exactly one adapter. The contract represents native tool calling (a ChatTurn carries tool calls on assistant turns and a tool-call id on tool turns) and accepts 0..N tool calls per response; the runtime executes one per step. Streaming, when added, is a separate optional interface (IStreamingChatModel : IChatModel), never a change to IChatModel. ADR-0001 is clarified, not reversed: it no longer prohibits MAF inside the implementation (public bOps contract is not a Microsoft contract; the runtime implementation may use MAF).

- **Supersedes / consolidates:** A13 , B5, D-007, ADR-0001, ADR-0052 §3 (ADR-0001)
- **Decision status:** Accepted (ADR-0001 as clarified by ADR-0052)
- **Implementation status:** Implemented — evidence below (the optional streaming interface is a stated future addition and does not exist)
- **Evidence:** `src/core/bOps.Abstractions/Model.cs:276`; `src/core/bOps.Abstractions/Model.cs:69`; `src/core/bOps.Abstractions/Model.cs:240`

### C01-R04 — Provider packages and registration

IModelProviderPackage, ChatModelOptions and IChatModelRegistry live in bOps.Abstractions (there is no bOps.Models project). The OpenAI-compatible Chat-Completions adapter is implemented once in the plain shared library bOps.Packages.Providers.OpenAiCompatible (including the JSON-schema-in-prompt fallback for models without reliable native tool calling); OpenRouter, Ollama, LlamaCpp, OpenAi and DeepSeek are thin IModelProviderPackage wrappers; Anthropic implements IChatModel natively against the Messages API and is the single place native tool_use/tool_result blocks are handled. A new OpenAI-compatible provider is a thin package, never a new adapter.

- **Supersedes / consolidates:** B6, ADR-0005, 07-plan-corrections (provider contracts)
- **Decision status:** Accepted (ADR-0005)
- **Implementation status:** Implemented — evidence below
- **Evidence:** `src/packages/bOps.Packages.Providers.OpenAiCompatible/OpenAiCompatibleChatModel.cs:25`; `src/packages/bOps.Packages.Providers.Anthropic/AnthropicChatModel.cs:26`

### C01-R05 — Governed model invocation

Every inference caused by a bOps task passes through one bOps-owned runtime model-invocation broker (IModelInvocationBroker): planning, replanning, step reasoning, final synthesis, grounding/final checks, evidence-disclosure correction, context summarization and any future MAF reasoning performed for the task. The broker owns retry classification and attempts, Retry-After handling, the per-attempt timeout, the logical-call budget, provider fallback, model-call audit/records and cumulative token accounting. MAF components that need an IChatClient are adapted over the broker; MAF never holds provider credentials and never implements its own retry or fallback. Summarization calls count toward the task's model usage and token budget and are model-call audited like any other.

- **Supersedes / consolidates:** A13 , ADR-0052 §5, ADR-0013/0039 (preserved and extended), D-048
- **Decision status:** Accepted (ADR-0052, D-048)
- **Implementation status:** Implemented — evidence below
- **Evidence:** `src/core/bOps.Runtime/ModelInvocationBroker.cs:85`; `src/core/bOps.AgentFramework/GovernedChatClient.cs:20`

### C01-R06 — Model-facing context is derived, protected context is never compacted

Model-facing projection and compaction never rewrite persisted raw task or evidence state: authoritative TaskState (plan/step, EvidenceFacts, grounding, limitations, history) feeds the MAF context layer, then the ModelRequest, then the broker. Generic compaction must not remove or summarize current execution authority: standing security instructions, the current user goal, the current plan revision, the current planned step and ExpectedTool, semantic/argument-correction state, current EvidenceGrounding and EvidenceLimitations, mutation-reconciliation notices and any other runtime-authored state needed to interpret the current call (these may live outside the compactable list; no generic strategy may decide they are disposable). The bOps/MAF message mapper preserves roles, assistant text, call ids, tool names, arguments, multiple tool calls in one assistant message, existing "not executed" results, ordering and Unicode; a call and its result are one atomic group; mapping or compaction can never turn an unexecuted historical call into an executed one. The MAF context layer is the only model-facing history path; the earlier custom bounded history, Agent:VerbatimHistorySteps and the K=0 rebuild are removed, and a runtime-authored evidence index keeps EvidenceRead/v1 addressable.

- **Supersedes / consolidates:** C3, ADR-0052 §6-§8, ADR-0052 §30, ADR-0014 HARDEN-8 (superseded part), D-048
- **Decision status:** Accepted (ADR-0052, D-048)
- **Implementation status:** Implemented — evidence below
- **Evidence:** `src/core/bOps.Runtime/ModelContext.cs:107`; `src/core/bOps.AgentFramework/MafChatTurnMapper.cs:14`; `src/core/bOps.AgentFramework/MafContextProjector.cs:19`

### C01-R07 — Model context profile, budget and compaction order

The model-context profile (provider id, model id, maximum context tokens, reserved output tokens, source) is runtime/host configuration, not part of the plugin SDK, and is never guessed from provider family or model names. A router model such as openrouter/free has an unknown limit unless configuration or provider metadata supplies a conservative guaranteed value; an unprofiled provider/model pair receives only a structural tool-result bound with no invented capacity (profile_source "unknown-structural"). Each fallback candidate has its own profile and projection uses the currently effective execution-scoped candidate. The history budget is the context window minus reserved output, system-prompt reserve, offered tool-schema reserve, protected runtime-context reserve and safety reserve; no production trigger percentage is architectural. MAF token counts are estimates unless proven exact for the active model; provider-reported prompt tokens are authoritative after the call; telemetry compares both; no hard safety guarantee may rely only on an unvalidated estimate. Compaction order runs from lower to higher information loss: tool-result compaction, summarization, sliding window, truncation; thresholds and reserves are measured tuning values (MAF defaults are references, not architecture).

- **Supersedes / consolidates:** ADR-0052 §9-§11, ADR-0052 §30, C3
- **Decision status:** Accepted (ADR-0052, D-048)
- **Implementation status:** Implemented — evidence below
- **Evidence:** `src/core/bOps.Runtime/ModelContext.cs:12`; `src/core/bOps.AgentFramework/MafContextProjector.cs:22`

### C01-R08 — Summarization is governed, derived and fail-soft

Summarization is an optimization. It defaults to the task's provider and model; an operator may explicitly configure a summarization-only provider/model, which is an explicit data-egress choice and is never selected or switched automatically by cost or speed, and bOps never silently sends evidence to an unconfigured third provider. A summarizer call goes through the broker, is model-call audited, obeys timeout and failure containment, counts toward task token budgets and uses the normal credential path. A summary is derived untrusted context, never evidence or authority (order: raw persisted evidence > EvidenceFact/completeness/verification > runtime grounding/limitations > LLM summary); it cannot create facts, change completeness or verification, activate a conditional step, satisfy policy or approval, settle a mutation or replace raw evidence. A summarization failure does not fail the task: the candidate history is preserved and deterministic compaction continues; genuine cancellation still propagates.

- **Supersedes / consolidates:** ADR-0052 §12 (12.1-12.4), ADR-0052 §28, ADR-0052 §21 (11), C3, D-048
- **Decision status:** Accepted (ADR-0052, D-048)
- **Implementation status:** Implemented — evidence below
- **Evidence:** `src/core/bOps.AgentFramework/GovernedSummarizationChatClient.cs:78`

### C01-R09 — Context failures, overflow recovery and telemetry

A message-mapping or context-conversion failure is a controlled model-context failure; no operational tool executes from a partially built request. A compaction failure fails safely. ContextOverflow is never retried with the same request: the first provider-reported overflow rebuilds aggressively and retries exactly once, and only when compactable history exists (never for the initial plan or a call without compactable history); goal and plan projections are capped at 2,048 and 1,024 units by deterministic head/tail; both attempts keep their model-call records; a second overflow terminates with the classified reason; provider retries never multiply this retry; the rest of that logical call stays aggressive; an estimate miss is recorded. Task-wide caps remain: Agent:MaxTotalTokens (cumulative) and Agent:MaxAttemptDuration (active time excluding approval waits). Context telemetry records strategy, profile source, history groups and estimated tokens before/after, protected-context and tool-schema estimates, summarizer calls/tokens/latency, provider-reported usage, EvidenceRead calls/exhaustion, overflow recovery and all-in task tokens, and never raw evidence, arguments, secrets or summary text in attributes.

- **Supersedes / consolidates:** ADR-0052 §19-§20, ADR-0014 §4 (overflow recovery), ADR-0014 HARDEN-8 summary (budgets), ADR-0014 HARDEN-8 amendment status note
- **Decision status:** Accepted (ADR-0014 as amended by ADR-0052, D-048)
- **Implementation status:** Implemented — evidence below
- **Evidence:** `src/core/bOps.Runtime/AgentRunner.cs:1179`; `src/core/bOps.Runtime/ModelInvocationBroker.cs:618`

### C01-R10 — Provider wire contract: closed alias map

Canonical tool names never change (identity in Runtime, Policy, Audit, PlanStep, plans, UI, logs). Aliases exist only inside one provider adapter for one request and are invisible above IChatModel. Each request builds one deterministic, ordinal-order ToolWireNames map over the union of offered tools and tool names in history assistant turns: a name matching ^[A-Za-z0-9_-]{1,64}$ maps to itself; any other name gets the readable alias (characters outside the set become '_'); if a readable alias collides with another name's alias, every non-verbatim name in the collision group uses the injective alias (kept characters [A-Za-z0-9-], every other character including '_' and '.' encoded as '_' plus two uppercase hex digits per UTF-8 byte); the finished map is verified (regex, at most 64 characters, all distinct) or building the request fails closed with a ModelProtocolException naming the canonical names and nothing is sent. A tool-call name in a response is looked up exactly (ordinal) in that request's map and replaced by the canonical name; a name not in the map, including a string equal to a real canonical tool name, is never passed through: the adapter returns the call with ToolNameError set and the runtime rejects it as an unknown tool without resolving or executing anything. The JSON-schema fallback path obeys the same closed rule. The helper is one internal source file owned by the OpenAI-compatible package and linked into the Anthropic package; every future adapter with native tool calling must use the same map for definitions, history and responses.

- **Supersedes / consolidates:** ADR-0038 §1 (alias map and closed reverse mapping), D-034
- **Decision status:** Accepted (ADR-0038, D-034)
- **Implementation status:** Implemented — evidence below
- **Evidence:** `src/packages/bOps.Packages.Providers.OpenAiCompatible/Wire/ToolWireNames.cs:23`

### C01-R11 — Truthful history and first-call-wins

The runtime executes one tool call per step. The assistant turn records every call the model emitted, in order; the first is executed; every other call receives a tool-result turn with the truthful text "Not executed: only one tool call is executed per step. Ask again next step if still needed." wrapped as tool output, never represented as success, so each tool-call id refers to a call in the immediately preceding assistant turn. Unexecuted calls are persisted on PlanStep.UnexecutedToolCalls so history rebuilt on resume is identical to live history, are never executed, never audited as tool calls and never given an invented result, and no corrective model call is spent solely because several calls were emitted. A malformed argument payload (present-but-blank, syntactically invalid or non-object JSON) is a validation failure recorded and audited like any other validation failure (ArgumentsError set, Arguments = empty placeholder, never executed, policy not evaluated as if valid arguments had been supplied); an absent or JSON-null arguments member is the explicit empty object. The model is not told about an earlier attempt by a runtime-authored resume note.

- **Supersedes / consolidates:** B5 (one call per iteration), D-007 (consequence), ADR-0038 §3-§4, ADR-0046 §3, ADR-0040 §11 (note withdrawn), C1, D-034
- **Decision status:** Accepted (ADR-0038, ADR-0046, D-034)
- **Implementation status:** Implemented — evidence below
- **Evidence:** `src/core/bOps.Abstractions/TaskState.cs:347`; `src/core/bOps.Runtime/AgentRunner.cs:3076`

### C01-R12 — Model-call failure classification (in providers only)

Provider packages classify every failure into the provider-neutral ModelFailureKind {Unknown, Transient, RateLimited, Timeout, Unreachable, Authentication, QuotaExceeded, InvalidRequest, ContextOverflow, MalformedResponse} and throw ModelProtocolException (FailureKind, RetryAfter never negative, ProviderStatusCode); the runtime never parses a provider body and never names a provider. Mapping: 500/502/503/529 Transient; 429 without quota evidence RateLimited; attempt/transport timeout, 408, 504 Timeout; no response (DNS/socket/refused/reset/TLS) Unreachable; 401 and 403 (unless a structured error says otherwise) Authentication; 402 or structured exhausted quota/credit QuotaExceeded; 400/404/405/409/413/422 and other 4xx without context evidence InvalidRequest; a 4xx whose structured code or sanitized message explicitly identifies a context/token limit ContextOverflow (a 413 without such evidence is InvalidRequest); HTTP 200 with a root-level structured error is classified from the embedded code (never both success and failure). Adapters make exactly one HTTP attempt per CompleteAsync (the only exchange allowed inside an adapter is the JSON-schema fallback's corrective re-ask, content-level and not transport) and must not retry transport failures. A new enum value defaults to non-retryable and non-fallback until an ADR explicitly allows it.

- **Supersedes / consolidates:** ADR-0039 §2-§3, D-035
- **Decision status:** Accepted (ADR-0039, D-035)
- **Implementation status:** Implemented — evidence below
- **Evidence:** `src/core/bOps.Abstractions/Model.cs:358`; `src/core/bOps.Abstractions/Model.cs:316`

### C01-R13 — Bounded runtime retry, timeouts and cancellation

The runtime owns retry in one bounded loop over the attempts of one logical call: only Transient, RateLimited, Timeout and Unreachable are retried; Authentication, QuotaExceeded, InvalidRequest, ContextOverflow, MalformedResponse and Unknown are never retried. At most Agent:ModelCallMaxAttempts (default 3) attempts; the whole call ends within Agent:ModelCallBudget (default 300 s), which always wins; each attempt runs under its own timeout of min(Agent:ModelCallAttemptTimeout (default 120 s), remaining budget) linked to the caller's token and awaited with WaitAsync so an adapter ignoring its token cannot hold the loop. Backoff before retrying after failed attempt n is min(ModelRetryMaxDelay (30 s), ModelRetryBaseDelay (1 s) x 2^(n-1)) x (0.5 + 0.5 x jitter); a Retry-After above ModelRetryMaxDelay ends the call (RetryAfterExceedsLimit), otherwise the delay is max(Retry-After, backoff); a delay leaving less than one second of budget ends the call (BudgetExhausted); the wait uses the caller's token. OperationCanceledException while the caller's token is cancelled is a genuine cancellation and propagates unchanged; any other cancellation from an attempt is a Timeout failure, recorded, audited and subject to retry. The classification try/catch surrounds only the adapter invocation: bookkeeping after a successful call (metering, success record/audit, plan-shape check) is outside it, never records a second attempt and never triggers a retry. Empty-final-response re-asks and the plan/replan corrective re-ask are separate logical calls and never nest in each other's counting. Failed attempts consume no tokens. A delegated role's deadline can be overrun by at most one ModelCallBudget.

- **Supersedes / consolidates:** ADR-0039 §1, ADR-0039 §4, D-035, C1 (model-call part)
- **Decision status:** Accepted (ADR-0039, D-035)
- **Implementation status:** Implemented — retry now lives in the governed broker (ADR-0052 §5), not in AgentRunner.CallModelAsync as ADR-0039 wrote; evidence below
- **Evidence:** `src/core/bOps.Runtime/ModelInvocationBroker.cs:748`; `src/core/bOps.Runtime/ModelInvocationBroker.cs:388`; `src/core/bOps.Runtime/ModelInvocationBroker.cs:710`

### C01-R14 — One audit event and one record per model-call attempt; sanitized reasons

Every model-call attempt, successful or failed, writes its own ModelCallAuditEvent and appends its own ModelCallRecord (a later attempt never replaces an earlier one's evidence) carrying ModelAttempt (1-based), its own duration and, for a failure, FailureKind, ProviderStatusCode, the sanitized ErrorMessage, RetryDecision and RetryDelayMs; a failure audit is written with the caller's token (a cancelled write is cancellation). The terminal failure of a task is the last attempt's event. A misbehaving IChatModel (any non-cancellation exception) cannot crash the loop: it is audited as a Failure and the task ends Failed. Reasons are extracted by provider packages (only bounded safe fields; never user ids, is_byok, request content or headers) and the runtime applies its own internal redaction and bounding to every text it records, audits or shows: it replaces Bearer tokens, sk-/sk_-shaped keys, api_key|apikey|token|secret|password|authorization|user_id followed by ':' or '=' and a value, opaque tokens of 32 or more mixed letters/digits and e-mail addresses with [redacted], and bounds a reason to 500 characters; the sanitizer algorithm is not public API.

- **Supersedes / consolidates:** B8 (model-call part), ADR-0013, ADR-0039 §5-§6, D-035, S6 (model-call reasons), S9 (every model call), C1
- **Decision status:** Accepted (ADR-0013 as amended by ADR-0039)
- **Implementation status:** Implemented — evidence below
- **Evidence:** `src/core/bOps.Abstractions/Audit.cs:687`; `src/core/bOps.Abstractions/Audit.cs:723`; `src/core/bOps.Runtime/ModelFailureText.cs:18`

### C01-R15 — Three provider-configuration states and atomic publication

Provider configuration exists in three states: persisted (what Settings and configuration sources store, possibly shadowed by an environment override), effective (one immutable, validated EffectiveProviderConfiguration published by the API host: primary plus ordered fallback chain, field provenance, a monotonic generation, safe credential source/availability metadata; no credential value or historical credential reference; one atomic reference is the only source a new execution reads) and pinned (the safe durable projection of one effective generation plus the execution's sticky fallback ordinal, belonging to one top-level durable execution and never following later Settings changes). The Settings API returns persisted values, effective descriptors, generation, per-field source (environment|settings|default), whether a persisted value is shadowed, credential presence and source only (never plaintext, hash or historical version) and whether a mutation was published or persisted-but-shadowed; the UI must not call a shadowed value active. An API-host singleton coordinator owns mutations and publication: a short write gate; a complete proposed state from one settings revision and one vault revision; validation without any provider network call; atomic persistence; building the effective non-secret configuration; publication by one atomic reference replacement incrementing the generation; a start reads the reference exactly once; no gate is held while building prompts, waiting for approval, calling a provider or running a task; stale writes return conflict; the coordinator is the only API write path to either provider store while running; persistence precedes publication (failure publishes nothing; a crash between rebuilds the snapshot deterministically at startup; no silent rollback); a credential-only mutation atomically replaces or removes the vault value without rewriting an execution pin; a combined profile-and-key operation is serialized, validated and persisted before publication and never routes one provider's key to another. CLI vault rotation stays an offline operation requiring the API stopped.

- **Supersedes / consolidates:** ADR-0045 §5-§6, D-042 (decision), D-025
- **Decision status:** Accepted (ADR-0045, D-042)
- **Implementation status:** Implemented — evidence below
- **Evidence:** `src/core/bOps.Api/ProviderConfigurationCoordinator.cs:12`; `src/core/bOps.Api/ProviderConfigurationCoordinator.cs:23`; `src/core/bOps.Runtime/SettingsStore.cs:20`

### C01-R16 — Environment precedence and provider-bound credentials

If the real ModelProvider__Provider variable is set, the environment owns the primary (the whole merged ModelProvider block is effective, the Settings primary selection is not consulted, and Settings writes are persisted but reported shadowed); otherwise Settings selects the provider/profile and a real environment variable for an individual field (BaseUrl, Model, SupportsNativeToolCalling, RequestTimeout) overrides only that field (one atomic tuple even with differently sourced fields). The block-level ModelProvider:ApiKeySecret is provider-bound: it belongs only to the configured primary provider (the merged ModelProvider:Provider value before any Settings active-provider selection); for any other provider (Settings-selected, fallback candidate or resumed execution) it is categorically ineligible and never read, and only that provider's own provider-keyed vault credential is used. A credential of provider A is never sent to provider B; the Settings view reports per provider whether a usable credential exists and its source and never shows B using A's credential. Environment fallback entries, when present, own the whole ordered list, else the Settings list, else the empty default. Non-secret precedence is evaluated at every publication and credential precedence for the pinned provider at every attempt; environment variables are not polled (a change needs a reload/restart) while vault rotation or removal is visible to the next attempt. The vault, masking, master-key and provider-profile decisions of ADR-0029 otherwise remain in force (see C02).

- **Supersedes / consolidates:** ADR-0045 §7, D-042 (B-1 correction), ADR-0029 forward reference (HARDEN-13)
- **Decision status:** Accepted (ADR-0045, D-042)
- **Implementation status:** Implemented — evidence below
- **Evidence:** `src/core/bOps.Api/ProviderResolution.cs:69`; `src/core/bOps.Api/ProviderCredentialResolver.cs:20`

### C01-R17 — Execution-scoped pinning and late-bound credentials

The execution scope is the largest durable aggregate representing one operator objective: one ordinary TaskState, or one complete DelegationRun (Discovery, Diagnostic, the approval wait, Remediation, Verification, reconciliation and every role task); never a model call, an AgentRunner invocation, a role or an execution attempt. At admission the host receives one effective non-secret snapshot and returns one execution-scoped IChatModel that owns the pinned candidate descriptors and resolves the current credential per provider attempt through a narrow host-owned resolver; Runtime sees exactly one IChatModel and resolves no services, provider ids, profiles or secrets; adapters must not capture a credential for their whole lifetime. TaskState and DelegationRun carry an additive optional PinnedProviderConfiguration persisted atomically with admission before the first model call; it contains only safe data (schema version, generation, primary and ordered fallback provider ids, base URLs, requested models, native-tool flags, request timeouts, relevant non-secret first-party request configuration, field provenance, a snapshot hash that excludes the mutable sticky ordinal, and the sticky ordinal) and never credential plaintext, authorization header, credential hash, historical secret version or correlation id. A legacy unpinned record captures the then-effective snapshot at the first accepted resume, which is audited and surfaced. Credentials are late-bound for every model attempt from the pinned provider's own secret authority and live only in attempt memory; rotation is effective in the next attempt of new and running executions and a rotated key is never preserved. After restart Resume reconstructs the exact pinned non-secret configuration; if the pinned provider package cannot instantiate it or the pinned candidate's current credential is unavailable, Resume or the next attempt fails explicitly and never substitutes current Settings. An invalid credential yields Authentication and cannot trigger fallback; a missing credential is not transient. Direct resume of a delegated role TaskState is refused or inherits the owning run's pin. A change to Settings affects only subsequently admitted executions.

- **Supersedes / consolidates:** ADR-0045 §8-§9, ADR-0045 §17, ADR-0045 §1 (requirement), D-042
- **Decision status:** Accepted (ADR-0045, D-042)
- **Implementation status:** Implemented — evidence below
- **Evidence:** `src/core/bOps.Abstractions/TaskState.cs:458`; `src/core/bOps.Abstractions/DelegationRun.cs:407`; `src/core/bOps.Api/ExecutionRunnerFactory.cs:13`

### C01-R18 — Fallback chain

Fallback is an explicit, ordered, bounded, host-owned recovery policy distinct from router-mode provider behavior ('router' refers only to router-mode providers; the chain is the fallback chain and its members are fallback candidates). ModelProvider:Fallbacks is an ordered list of already-registered providers with non-blank models (no secrets; endpoint and tool capability only from that provider's own pinned profile), at most three entries; duplicates and the primary tuple are rejected; no cycle, discovery, implicit vendor or health-based insertion; an empty or absent list disables fallback. FallbackChatModel is execution-scoped and serial and owns only candidate selection and sticky state; AgentRunner/the broker remains the single owner of same-provider retry and no retry policy moves into adapters. Fallback is permitted only after the candidate exhausted its same-provider retries with Transient, RateLimited, Timeout or Unreachable and is forbidden for Authentication, QuotaExceeded, InvalidRequest, ContextOverflow, MalformedResponse and Unknown (future values forbidden by default; no HTTP status is interpreted by the chain). AttemptsExhausted or RetryAfterExceedsLimit on an allowed kind advances if another candidate exists and the budget permits; BudgetExhausted is terminal; a non-allowed kind is terminal regardless. The sequence is primary attempts 1..N, then fallback ordinal 1 attempts 1..N, and so on, with an absolute ceiling of N x (1 + fallback count), no candidate revisited, and no reset of ModelCallBudget, attempt deadline, token/step budget or role budget on transition; a transition does not wait for the failed provider's Retry-After but must leave the minimum attempt window. ProviderAttempt and FallbackOrdinal (0 primary, 1.. fallbacks) are independent counters. A fallback candidate with no usable current credential fails as Authentication and terminates the logical call (no skipping, no foreign credential, no return to an earlier candidate). Admission requires only a usable primary credential. Fallback is sticky and monotonic for the execution scope: the later ordinal becomes the starting candidate for all later calls in that task or delegated run, persisted before the next candidate is invoked; a new task starts again from its effective primary. A provider routing directive can only advance within the host-pinned chain and cannot make a forbidden kind retryable.

- **Supersedes / consolidates:** ADR-0045 §11-§12, ADR-0045 §15 (controls), D-042
- **Decision status:** Accepted (ADR-0045, D-042)
- **Implementation status:** Implemented — evidence below
- **Evidence:** `src/core/bOps.Hosting/FallbackChatModel.cs:9`; `src/core/bOps.Runtime/SettingsStore.cs:16`; `src/core/bOps.Abstractions/Model.cs:395`

### C01-R19 — Save-time validation and credential revocation

Save-time validation of provider configuration is local and deterministic: provider ids are registered with exact canonical casing (validated against the registry with its case-insensitive semantics and stored in canonical spelling), the model is non-blank and bounded, BaseUrl is an absolute HTTP/HTTPS URI, supported non-secret parameters are valid, primary and fallback tuples are unique and the chain bounded, a candidate newly activated or added to a chain that requires a credential has one currently resolvable through its own provider-scoped authority, and the provider package can construct the adapter from the proposal; no live network request is made (an offline provider is accepted and fails at execution as Unreachable; a 401 is Authentication and never falls back). An inactive profile may be stored without a credential but cannot be newly activated or added to the chain until its credential is present. Explicit removal of a credential is always allowed as a security revocation (availability false immediately; new admission and the next attempt fail explicitly; the old value is not retained). A rejected mutation never replaces the published generation, and the system never silently keeps using the preceding configuration or credential while claiming the new state is active. An invalid persisted fallback at startup is an explicit visible state, never a silent drop.

- **Supersedes / consolidates:** ADR-0045 §13, ADR-0045 §17, D-042
- **Decision status:** Accepted (ADR-0045, D-042)
- **Implementation status:** Implemented — evidence below
- **Evidence:** `src/core/bOps.Api/ProviderConfigurationCoordinator.cs:82`

### C01-R20 — Model-call audit of fallback and configuration changes

Every provider attempt remains one ModelCallRecord and one ModelCallAuditEvent with the existing fields keeping their meaning (Provider is the candidate actually called, RequestedModel/Model, ActualModel as reported by the provider; bOps never guesses an upstream vendor for router mode). Additive optional fields carry PrimaryProvider, PrimaryModel, FallbackOrdinal, ProviderAttempt, ConfigurationGeneration and the safe snapshot hash; a fallback transition is the append-only ModelRetryDecision value Fallback so the audit distinguishes Retry, Fallback, AttemptsExhausted and BudgetExhausted. No event or record contains a secret value, historical version, credential correlation id, hash, header or Authorization data; configuration-mutation audit records the resulting generation and effective-or-shadowed, never the value.

- **Supersedes / consolidates:** ADR-0045 §14, D-042
- **Decision status:** Accepted (ADR-0045, D-042)
- **Implementation status:** Implemented — evidence below
- **Evidence:** `src/core/bOps.Abstractions/Model.cs:395`; `src/core/bOps.Abstractions/TaskState.cs:130`

### C01-R21 — Shipped bootstrap default is disclosed, non-deterministic by design

The shipped zero-cost bootstrap default is OpenRouter with the router alias openrouter/free, requiring an OpenRouter API key. It is intentionally non-deterministic: the actual model can change on every call and must stay visible through the existing requested/actual model-call and audit information. bOps must never imply it is deterministic; documentation (Getting Started/operator guides) and Settings must state the key requirement, how to configure it, the non-determinism and that audit exposes the actual model when reported, and must recommend an explicitly selected specific model for important troubleshooting, reproducible analysis and especially remediation (Settings shows 'The actual model may change on every call; not recommended for troubleshooting sessions.'). The risk is mitigated by disclosure, actual-model visibility and easy selection, not by making the bootstrap deterministic.

- **Supersedes / consolidates:** ADR-0045 §10, D-042 (bootstrap default)
- **Decision status:** Accepted (ADR-0045, D-042 — operator decision)
- **Implementation status:** Partially implemented — the router alias and actual-model audit field exist; the disclosure wording is documentation/UI and was not re-verified in this session
- **Evidence:** `src/core/bOps.Abstractions/Audit.cs:705`

### C01-R22 — Experimental MAF governed turn host (CLI, opt-in)

A host may run each governed model call as one turn of a stateless Microsoft Agent Framework `ChatClientAgent` only when the operator enables it by host configuration; the default is off and the shipped configuration never enables it. The only wrapper is `MafGovernedTurnBroker` over the governed `IModelInvocationBroker`; the agent is created for the turn, hosts a fresh session, has no framework tool loop, no function-invocation layer, no history provider and no context provider other than the bOps-authored one, offers tools as declarations without a body, and its single leaf is `GovernedChatClient`. A turn reaches the broker exactly once; a different count, or a MAF response that differs from the broker response, is a controlled model failure (`InvalidRequest`) and executes nothing. The broker keeps sole ownership of context projection, retry, fallback, model-call audit and records, and token accounting; `AgentRunner` keeps all operational authority (C01-R01), TaskState, resume and fencing (C03), tool routing, validation, policy, approval and the mutation journal. Resume must not depend on any MAF session, and switching between the default and the experimental mode, also across a restart or during resume, must neither change nor corrupt the persisted state. The runner-visible request, response, audit, accounting and persisted state must be observably identical to the default path (the parity suite). The experiment covers the CLI goal and resume runs only. It composes with the CLI Knowledge pilot (C05-R13, ADR-0058): the two switches are independent, the runner serves the knowledge (index, `runtime.knowledge_read`, audited reads) and it reaches the agent only as the protected context the runner authorised, compared by the final context gate like any other turn; the framework skills provider stays a library invoked by bOps (never a tool or a context provider of the agent), and the agent still runs nothing. Extending it to another host, run kind or content source requires a new operator decision. This rule authorizes only that experiment: it does not accept ADR-0053 or ADR-0054 and does not settle D1.

- **Supersedes / consolidates:** nothing (new rule; scoped carve-out from C01-P02)
- **Decision status:** Accepted for experiment (operator decision D12, 2026-10-23)
- **Implementation status:** Implemented in the CLI (`BOPS-0122`, offline parity and mode-switch tests; not yet merged or CI-verified) — evidence below
- **Evidence:** `src/core/bOps.Cli/CliRunnerComposition.cs` (the one composition, behind the switch, independent of Knowledge); `src/core/bOps.Cli/CliRunnerComposition.cs:28` (strict switch); `tests/bOps.Cli.Tests/MafGovernedTurnHostTests.cs` (parity and mode switching); `tests/bOps.Cli.Tests/KnowledgeMafCompositionTests.cs` (the four conditions, parity with Knowledge on, resume and revocation under the MAF host); `tests/bOps.Architecture.Tests/AgentFrameworkBoundaryTests.cs` (single composition, no shipped configuration)

### C01-R23 — MAF multi-agent troubleshooting workflow (CLI, opt-in, read-only)

A host may run the MAF multi-agent troubleshooting workflow only when the operator both names it on the command (`bops diagnose "<goal>" --workflow maf`) and enables it by host configuration (`Cli:Experimental:MafWorkflow`); the default is off and the shipped configuration never enables it. It is distinct from the one-turn host of C01-R22 and does not replace the ordinary `AgentRunner` path, which it leaves unchanged. Extending it to another host, run kind or capability (an operation above Read, an API or UI host, durable resume) requires a new operator decision.

The workflow is orchestrated by Microsoft Agent Framework (`Microsoft.Agents.AI.Workflows`, version pinned like the rest of the MAF dependency, isolated in the MAF implementation assembly): a triage agent, the registered specialist agents (the specialists of a round run concurrently) and a diagnostic reviewer that may send one more round back to the specialists, wired with the framework's own executors and edges and bounded by configurable limits (investigation rounds, tool calls, model calls, tokens, wall-clock time), each with a conservative default and a hard maximum a configuration cannot exceed. Specialists are host-registered data (name, declared capabilities, instructions, a selector over the read-only tools visible on the node); the workflow, the runtime and this rule name no specialist, package or tool, and a specialist with no available tool is reported as unavailable while the others still work.

This rule is a scoped carve-out from C01-R01 and C01-P02, inside this workflow only, and weakens nothing else:

1. **MAF agents may choose which of the offered read-only tools to request and with which arguments**, through MAF's function-invocation layer. The only function body is the bOps gateway adapter. MAF never holds the tool registry, policy, approval, entitlement or a tool, and no MAF approval mechanism replaces or substitutes a bOps one.
2. **Every call goes through the runner's workflow tool session**, which hands it to the one step pipeline every other tool call goes through: argument validation against the manifest (never clamped), policy, approval, entitlement, timeout, execution, audit. No second component resolves, authorizes or executes a tool; the gateway adds only refusals that make the call narrower.
3. **Tool authority is an exact set per agent, frozen when the run starts** (selected from the Read tools visible on the node by the registered specialist data; its hash is audited) and intersected with a hard read-only ceiling that the gateway enforces before policy. A call outside the set, to an unknown or malformed name, to a tool above Read or beyond the run's tool-call budget is refused and audited. The triage agent and the reviewer have no tool. Neither a model, nor a tool output, nor a specialist report, nor the reviewer can add a tool, a specialist or an authority, or change the goal; a reviewer follow-up is only a request to a registered specialist, who decides which of its allowed tools to call.
4. **Every model call goes through the governed `IModelInvocationBroker`** (C01-R05) from one leaf client, with provider pinning, fallback, model-call audit and token accounting unchanged, and the budgets shared by the run. MAF holds no credential and has no other channel to a model.
5. **Tool output and agent reports are data** (C02-R11): they reach a model only as a structured tool-result turn or inside runtime-authored delimiters, with those delimiters neutralized in the text, and never alter the instructions, the tool set or the limits.
6. **Identity and audit.** The run has a task id generated for it, the operator's actor identity, and every audit event of the run carries them; the runtime stamps each event of a tool call or model call with the run, the agent, the agent's runtime-assigned instance id and, for a tool call, the invocation id (a `WorkflowCorrelation`), and the lifecycle (start with the frozen authorities, triage, rounds, reviews, end) is audited as `WorkflowRunAuditEvent`. Denials, refusals, timeouts and failures are events like any other.
7. **No checkpoint and no resume in this version; an interrupted run is incomplete, never `Completed`.** This is an implementation limit of the first increment, not an architectural prohibition. The run is not written to the task store and uses no MAF checkpoint; an interruption (cancellation, process exit) or a failure ends it and nothing resumes it, and a retry is a new run with a new task id and fresh budgets. After an interruption bOps never re-executes a tool automatically. A run that does not end with a report is recorded and shown as `LimitReached`, `NothingInvestigated`, `Failed` or `Cancelled` (audit stage and CLI exit code), never as `Completed`; a run whose audit chain has a start and no end was interrupted and is incomplete. The evidence of a run is held in memory for the report only and is not declared persistent; the operational record is the audit chain. This rule does not extend C03-R23 to workflows, does not change the resume of the ordinary `AgentRunner` path (C03), and does not forbid checkpoint or resume for workflows later: introducing them requires an explicit operator decision that keeps tool authority, validation, policy, approval, the journal and fencing with bOps, and says how completed calls are recognised so that none is re-executed.
8. **The report** separates facts (observations whose evidence ids were checked against what the run really collected), hypotheses, contradictions, unsupported interpretations and missing information, lists the evidence and the limits reached, and its intervention plan is advisory: nothing in bOps executes it.

This rule does not amend C02 (tool contract, policy, approval, entitlement, secrets, audit), C03 (task state, plan, step authority, resume, journal) or C04 (evidence and diagnosis semantics). The workflow has no `AgentPlan`; the authority of each call is the frozen exact set plus the read-only ceiling instead of a planned step, which is acceptable only because no operation with effects, no journaled mutation and no verification can occur in it; adding any of those is outside this rule. The evidence of a run is not persisted as `EvidenceFact` state of a stored task (the run is not stored); the original tool results are preserved unmodified in the run's ledger for the report and the audit carries bounded summaries. This rule does not accept ADR-0053 or ADR-0054 and does not settle D1.

- **Supersedes / consolidates:** nothing (new rule; scoped carve-out from C01-R01 and C01-P02)
- **Decision status:** Accepted for the circumscribed read-only workflow (operator mandate 2026-10-27, `BOPS-0129`; clause 7 as corrected and approved by the operator on 2026-10-27, scope: the experimental read-only CLI)
- **Implementation status:** Implemented in the CLI (`BOPS-0129`; offline workflow, gateway, limits, failure, audit and architecture tests; not yet merged or CI-verified) — evidence below
- **Evidence:** `src/core/bOps.Runtime/AgentRunner.WorkflowTools.cs` (the gateway, one `ExecuteStepAsync`); `src/core/bOps.Runtime/WorkflowToolSession.cs` (frozen per-agent authority, run budget); `src/core/bOps.AgentFramework/Troubleshooting/MafTroubleshootingWorkflow.cs` (the framework workflow); `src/core/bOps.AgentFramework/Troubleshooting/WorkflowGovernedChatClient.cs` (the one leaf to the broker); `src/core/bOps.Cli/MafDiagnoseCommand.cs` (the one composition, behind the switch); `tests/bOps.Architecture.Tests/MafWorkflowBoundaryTests.cs` (no way around the gateway); `tests/bOps.AgentFramework.Tests/MafTroubleshootingWorkflowTests.cs`, `MafWorkflowGatewayTests.cs`, `MafWorkflowLimitsAndFailureTests.cs` (end-to-end, gateway, limits, failures, accounting); `tests/bOps.Cli.Tests/MafDiagnoseCommandTests.cs`, `MafDiagnoseCliProcessTests.cs`

## 3. Pending decisions — non-binding

**Status:** the two decisions below are not in force, except the CLI experiment of C01-R22 (carved out by decision D12). Register decision **D1** (`BOPS-0074`, `BOPS-0075`, `BOPS-0086`, `BOPS-0087`; recommendation recorded in the register: keep Proposed) is open; the general architectural convergence stays deferred. Source code that resembles these proposals (for example a MAF `ChatClientAgent` host for a model turn at `src/core/bOps.AgentFramework/MafGovernedTurn.cs:14`) does not make them accepted and does not change any rule above.

- **C01-P01 — ADR-0053 (Proposed 2026-10-12): MAF convergence and target ownership.** Proposes a target ownership matrix for eleven capabilities (planning and state machine, tool routing and step authority, argument validation, policy/entitlement/approval, mutation journal and fencing, audit/persistence/resume, provider fallback and grounding stay in bOps; context/compaction and governed inference are MAF over the broker), ten invariants I-1…I-10 for any MAF migration, a single open-source engine shared by the Local Agent and the Coordinator, and a rule that adoption depends on ARCH-02 evidence and ARCH-03 approval. It refines ADR-0052 without superseding it. Its §4.5 knowledge-engine contract is recorded in the Contract 05 pending section (D5).
- **C01-P02 — ADR-0054 (Proposed 2026-10-12): "B+" hosting of each model turn.** Proposes that each model turn be hosted by a stateless MAF `ChatClientAgent` (no automatic tool-invocation loop, declaration-only tools, no session reuse, context through an `AIContextProvider` pipeline, leaf `GovernedChatClient` → broker), a classification of eighteen MAF capabilities (reject/adopt/conditional), and refinements to ADR-0053 rows 2, 4 and 11. Its G2 security gate and progressive-Skills contract are recorded in the Contract 05 pending section (D5). Evidence is offline and scripted; end-to-end equivalence with the current `AgentRunner` is explicitly not claimed. The CLI experiment of C01-R22 is the only part in force; the rest of this entry, including every other host, stays pending (D1).

Nothing else in these two entries is a requirement. Until the operator decides D1, the binding rules are C01-R01–C01-R23 only.

## 4. Technical references

Level-C documents that carry the detail behind these rules. They are bound by this contract and are not alternative registers of decisions.

- `docs/operator-configuration.md`
- `docs/architecture/reference/provider-adapters-and-service-tools.md`
- `docs/architecture/af-turn-01b-context-skills-bridge.md`
- `docs/architecture/arch-02s-maf-bplus-conformance.md`
- `docs/maf-troubleshooting-workflow.md`

## 5. Sources

The original ADRs and the previous full text of `01-architecture-rules.md` and `06-decisions.md` are preserved unmodified in `docs/architecture/archive/` (provenance only, not an authority); historical identifiers resolve through [`docs/architecture/adr/README.md`](../adr/README.md) and the compatibility indexes in `agentic/`.

Consolidated from (derived from the DOC-02 traceability matrix, `agentic/_plans/_migration-work/ADR-consolidation-2026-10-22/DOC-02-traceability-matrix.tsv`):

- **C01-R01** — ADR-0052 (Title/Status; Context; Decision s2 ownership split; Relationships s3 (0030/0036, 0042/0050, 0045, 0046/0047, 0051); Decision s15 tool authority; Decision s16 harness, sessions, workflows; …); 01-architecture-rules.md (A13); 06-decisions.md (D-048); 00-project-spec.md (What bOps is not; P1-P7)
- **C01-R02** — ADR-0052 (Decision s4 dependency boundary); 01-architecture-rules.md (A13); 06-decisions.md (D-048)
- **C01-R03** — ADR-0001 (Title/Status/Amendment; Context; Decision; Alternatives considered; Consequences); ADR-0052 (Relationships s3 (0001)); 01-architecture-rules.md (B5); 06-decisions.md (D-007)
- **C01-R04** — ADR-0005 (Context; Decision; Alternatives considered; Consequences); 01-architecture-rules.md (B6)
- **C01-R05** — ADR-0052 (Relationships s3 (0014, 0013/0039, 0017/0040); Decision s5 governed model invocation); 01-architecture-rules.md (A13); 06-decisions.md (D-048)
- **C01-R06** — ADR-0052 (Decision s6 context management; Decision s7 protected context; Decision s8 message mapping); 01-architecture-rules.md (C3); 06-decisions.md (D-048)
- **C01-R07** — ADR-0052 (Decision s9 model context profile; Decision s10 budget and token estimation; Decision s11 compaction pipeline; Evidence s30 AF-05); 01-architecture-rules.md (C3)
- **C01-R08** — ADR-0052 (Decision s12.1-12.2 summarization selection and governance; Decision s12.3-12.4 authority and failure); 01-architecture-rules.md (C3); 06-decisions.md (D-048)
- **C01-R09** — ADR-0014 (HARDEN-8 amendment: implementation-status note; HARDEN-8 decision summary: overflow, budgets, contracts; §4 overflow recovery); ADR-0052 (Decision s19 telemetry; Decision s20 failure model)
- **C01-R10** — ADR-0038 (Title/Status/Scope; Decision §1 alias map; Decision §1 reverse mapping closed; Alternatives considered; Consequences); 06-decisions.md (D-034)
- **C01-R11** — ADR-0038 (Decision §3 truthful history; Decision §4 malformed arguments); ADR-0046 (Decision §3 first-call-wins); 01-architecture-rules.md (B5); 06-decisions.md (D-007; D-034)
- **C01-R12** — ADR-0039 (Decision §2 classification; Alternatives considered); 06-decisions.md (D-035)
- **C01-R13** — ADR-0039 (Title/Status/Operator amendments; Decision §1 timeout vs cancellation; Decision §3 one attempt per call; Decision §4 bounded retry; Decisions (as accepted)); 06-decisions.md (D-035)
- **C01-R14** — ADR-0013 (Title/Status/Amendment; Decision; Alternatives considered; Consequences); ADR-0039 (Decision §5 one event per attempt; Decision §6 sanitized reason); 01-architecture-rules.md (B8; C1); 06-decisions.md (D-035)
- **C01-R15** — ADR-0045 (Decision §5 three states; Decision §6 consistency and publication); 06-decisions.md (D-025; D-042)
- **C01-R16** — ADR-0029 (HARDEN-13 forward reference); ADR-0045 (Decision §7 environment precedence); 06-decisions.md (D-042)
- **C01-R17** — ADR-0045 (Title/Status/Scope; Context: requirement; Decision §8 pinning scope and model lifetime; Decision §9 durable pin and late-bound credentials; Decision §17 implementation obligations); 06-decisions.md (D-042)
- **C01-R18** — ADR-0045 (Decision §11 ordered fallback chain; Decision §12 sticky behaviour; Decision §15 security controls; Alternatives considered; Consequences and acceptance gate); 06-decisions.md (D-042)
- **C01-R19** — ADR-0045 (Decision §13 validation and failure); 06-decisions.md (D-042)
- **C01-R20** — ADR-0045 (Decision §14 audit and records); 06-decisions.md (D-042)
- **C01-R21** — ADR-0045 (Decision §10 shipped bootstrap default); 06-decisions.md (D-042)
- **C01-R22** — operator decision D12 (2026-10-23), scoped from ADR-0054 (Proposed) without accepting it; `BOPS-0122`
- **C01-R23** — operator mandate (2026-10-27), scoped from C01-R01 and C01-P02 without accepting ADR-0053/0054; `BOPS-0129`
