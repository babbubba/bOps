# ADR-0045 — Execution-pinned provider configuration and ordered fallback chain

Status: Proposed — awaiting operator acceptance
Date: 2026-10-06

Supersedes only the restart-to-apply consequence of
[ADR-0029](0029-encrypted-local-vault-and-master-key.md). ADR-0029's vault, masking,
master-key, provider-profile and environment-precedence decisions otherwise remain in force. This
ADR also governs HARDEN-13 of the V1.3.x reliability train
([packet](../../../agentic/_tasks/2026-09-25-v1.3x-harden-13-provider-defaults-fallback.md);
[plan](../../../agentic/_plans/2026-09-25-v1.3x-reliability-hardening.md), finding F-17).

Nothing here is implemented yet. In particular, the model candidate in §10 is a recommendation,
not an accepted shipped default. Implementation must not begin until the operator accepts this ADR
and chooses the default.

## Context

### 1. Current configuration and lifetime

The API persists Settings changes today, but it does not recompose the running model:

1. `SettingsEndpoints` writes the active provider to `SettingsStore`, a provider's non-secret
   `BaseUrl`, `Model`, `SupportsNativeToolCalling` and `ExtraParameters` to its `ProviderProfile`,
   and the credential to `VaultStore`.
2. `ProviderResolution.ResolveEffectiveModelOptions` joins the configured default, Settings profile
   and credential at model construction time.
3. `Program.cs` registers `IChatModel` as a singleton. Its factory calls that resolver once, then
   `IChatModelRegistry.Create` constructs one provider adapter. `AgentRunner`, `DelegationRunner`,
   `AgentTaskLauncher` and `DelegationLauncher` are also singletons rooted in that same model.
4. Therefore a successful Settings write changes durable state and the Settings view immediately,
   but every task still uses the old adapter, endpoint, model, tool-calling flag and resolved key
   until the API restarts. ADR-0029 and `docs/operator-configuration.md` currently document this as
   deliberate.

The affected values are not identical:

| Value | Persisted by Settings now | Used by the singleton now | Current effect |
|---|---:|---:|---|
| Active provider | Yes | Resolved once | Restart required |
| `BaseUrl` | Yes | Captured in `ChatModelOptions`/adapter | Restart required |
| `Model` | Yes | Captured in `ChatModelOptions`/adapter descriptor and requests | Restart required |
| `SupportsNativeToolCalling` | Yes | Captured by the adapter | Restart required |
| API key | Yes, encrypted | Resolved to plaintext once and captured by the adapter | Restart required |
| `ApiKeySecret` reference | Configuration only | Resolved once | Configuration reload/restart required |
| `RequestTimeout` | Configuration only | Captured once | Configuration reload/restart required |
| `ExtraParameters` | Yes | Not passed to `ChatModelOptions` or any provider | Never effective today, even after restart |

`ExtraParameters` must not be represented as effective merely because it is persisted. HARDEN-13
does not invent provider-specific semantics for it: the Settings contract will label it
`persistedOnly` until a provider package declares and consumes a supported parameter. It is neither
live nor restart-required because it currently controls nothing.

### 2. Current execution and resume boundary

`AgentRunner` captures one `IChatModel` in its constructor and uses it for planning, steps,
replanning and final-answer calls. Ordinary task starts and resumes both enter the singleton runner.
`DelegationRunner` uses that same runner for Discovery and Diagnostic; Remediation and Verification
make no model calls today, but they are part of the same durable delegated objective and future role
changes must not create a provider boundary. A delegated run may wait for plan approval and can be
resumed after a process restart.

`TaskState` and `DelegationRun` persist no provider configuration. After a restart, a resume therefore
uses whichever process-global model the new process constructed, even when the original execution
used another provider or model.

### 3. Existing retry and audit contracts

HARDEN-2 / ADR-0039 makes one adapter invocation one attempt. `AgentRunner` owns the bounded retry
loop and retries only `Transient`, `RateLimited`, `Timeout` and `Unreachable`. The complete failure
enum also contains `Authentication`, `QuotaExceeded`, `InvalidRequest`, `ContextOverflow`,
`MalformedResponse` and `Unknown`; none of those is generically retryable.

`ModelCallRecord` already stores `Provider`, `RequestedModel`, `ActualModel`, attempt number, failure
kind and retry decision. `ModelCallAuditEvent` stores the same concepts as `Provider`, `Model` and
`ActualModel`. With one configured model, `Provider` is simultaneously the configured provider and
the provider actually called. There is no distinct pinned primary, actual/fallback provider,
fallback ordinal or per-provider attempt number. A fallback must make those distinctions explicit;
it must never disappear behind one successful final event.

### 4. HARDEN-13 requirement

A Settings change to provider, model, endpoint, tool-calling capability or credential must affect a
subsequently started execution without restarting the API. It must not change an execution already
in progress. The optional fallback feature must be explicitly configured, ordered, bounded and
host-owned. Router mode remains supported but is not a reproducible troubleshooting default.

## Decision

### 5. Three states: persisted, effective and pinned

The product will name and expose three different states:

- **Persisted configuration** is what Settings and configuration sources store: active provider,
  profiles, fallback entries and credential metadata. A persisted value can be shadowed by an
  environment override and is not therefore necessarily in use.
- **Effective configuration** is one immutable, validated `EffectiveProviderConfiguration` published
  by the API host. It contains a primary plus an ordered fallback chain, field provenance, a monotonic
  generation and credential bindings. One atomic reference is the only source used when a new
  execution starts.
- **Pinned execution configuration** is the safe durable projection of one effective generation,
  plus the execution's current sticky fallback ordinal. It belongs to one top-level durable execution
  and never follows subsequent Settings changes.

The Settings API will return all persisted values, the effective primary/fallback descriptors, the
effective generation, per-field source (`environment`, `settings`, `default`), and whether a
persisted value is shadowed. It returns credential presence, source and an opaque version only;
never plaintext. A successful mutation response states whether it was published for new executions
or persisted-but-shadowed. The UI must not use “active” for a shadowed value.

### 6. Consistency and publication

An API-host singleton `ProviderConfigurationCoordinator` owns mutations and publication. It is an
internal host service, not a Runtime service and not part of the plugin SDK.

For each mutation it:

1. takes a short configuration write gate;
2. creates a complete proposed state from one persisted settings revision and one vault revision;
3. validates it without making a provider network call;
4. persists the changed store atomically;
5. builds the complete effective configuration, including credential bindings;
6. publishes it with one atomic reference replacement and increments its generation; and
7. releases the gate.

A task/delegation start reads that reference exactly once. It consequently receives wholly A or
wholly B, never provider from B with model or key from A. No gate is held while constructing prompts,
waiting for approval, calling a provider or running a task. `SettingsStore` gains a revision and
in-process writer serialization equivalent to `VaultStore`; stale writes return conflict rather than
silently overwriting another change. The coordinator is the only API write path to either provider
store while the process is running. The CLI vault-rotation procedure remains an offline maintenance
operation requiring the API to be stopped.

Persistence precedes publication. If persistence fails, nothing is published. If the process stops
after persistence and before publication, startup deterministically rebuilds the effective snapshot
from the persisted state. There is no silent rollback to the preceding configuration.

### 7. Environment precedence

ADR-0029's explicit environment precedence is preserved and made visible:

1. If the real `ModelProvider__Provider` variable is set, environment-owned mode applies. The entire
   merged `ModelProvider` primary block (provider, endpoint, model, tool-calling capability, secret
   reference and request timeout) is effective and the Settings primary selection/profile is not
   consulted. Settings writes are accepted and persisted for later use, but are reported as
   shadowed and do not increment the effective generation unless they also change an unshadowed
   fallback.
2. Without that provider override, Settings selects the provider/profile. A real environment variable
   for an individual field (`ModelProvider__BaseUrl`, `ModelProvider__Model`,
   `ModelProvider__SupportsNativeToolCalling` or `ModelProvider__RequestTimeout`) overrides only that
   field. This closes the current ambiguity where a merged configuration value can be mistaken for a
   JSON default. The immutable result remains one atomic tuple even when its fields have different
   declared sources.
3. The configured `ApiKeySecret` is resolved first. A non-empty result is the effective credential
   and shadows the provider's vault entry. If the reference resolves to null/empty, the active
   provider's vault credential is used, as ADR-0029 specifies. The Settings view says which source
   wins and whether a stored vault key is shadowed.
4. Environment fallback entries, when explicitly present, own the whole ordered fallback list.
   Otherwise the Settings-stored list wins, then the shipped empty default.

This precedence is evaluated at every publication, not at every model call. Environment variables
are not polled in the background; changing the process environment outside Settings requires a host
configuration reload/restart. The live guarantee applies to Settings mutations.

### 8. Pinning scope and model lifetime

The **execution scope** is the largest durable aggregate that represents one operator objective:

- one ordinary `TaskState`; or
- one complete `DelegationRun`, including Discovery, Diagnostic, the approval wait, Remediation,
  Verification, reconciliation and every role task it owns.

It is not an individual model call, `AgentRunner` invocation, role or execution attempt. A role-level
scope would let Discovery and Diagnostic disagree; an attempt-level scope would let Resume change the
model mid-task; a call-level scope is the reported defect.

At admission, an internal host `IExecutionChatModelFactory` receives one effective snapshot, creates
the underlying provider models and returns one execution-scoped `IChatModel`. The host creates an
execution-scoped `AgentRunner` with that model using the existing constructor. For delegation it
creates one execution-scoped `DelegationRunner` over that runner. Runtime still sees exactly one
`IChatModel`; it does not resolve services, provider ids, profiles or secrets.

Provider model objects are lightweight execution-scoped adapters over factory-managed `HttpClient`
instances. Making `IChatModel` transient by itself is not the design: the factory and durable pin are
the consistency boundary. Existing public `AgentRunner(IChatModel, ...)` and `IChatModel` contracts
remain usable by tests and non-API hosts.

### 9. Durable pin and credential binding

`TaskState` and `DelegationRun` gain an additive optional `PinnedProviderConfiguration`. Old records
without one use a migration rule: the first accepted resume captures the then-effective snapshot,
audits that migration, persists it before any model call, and remains pinned thereafter.

The durable projection contains only safe data:

- schema version and effective generation;
- primary and ordered fallback provider ids, base URLs, requested models, native-tool flags and
  request timeouts;
- effective non-secret supported parameters;
- field provenance and a snapshot hash;
- an opaque credential binding id/version for each candidate;
- current sticky fallback ordinal.

No credential plaintext, authorization header or reversible unkeyed credential fingerprint is stored
in task, delegation, audit or Settings data.

To make restart/resume deterministic, the vault maintains immutable encrypted credential versions.
Resolving an environment credential or current provider vault entry for a new execution creates or
references a provider-bound encrypted credential lease. The durable pin stores only its opaque id and
version. A lease is bound by authenticated data to its provider id and cannot be resolved for another
provider. Rotation creates a new current version; it does not mutate leases held by resumable
executions. Terminal/non-resumable executions release their lease, and retention cleanup may delete an
unreferenced retired version. Clearing a current Settings key never destroys a still-referenced lease.

If the exact pinned non-secret provider package or credential lease cannot be reconstructed after a
restart, Resume fails closed before a model call with an actionable “pinned provider configuration is
unavailable” result. It never substitutes the current Settings provider or credential. A deployment
without an active encrypted vault can run and pin credentials in memory, but such an execution is
explicitly non-resumable across process restart; the API exposes that limitation before start.

Consequences for required scenarios:

| Scenario | Rule |
|---|---|
| A/X task runs; Settings changes to B/Y | Running task remains A/X; the next task pins B/Y. |
| Task resumes after a model-call failure | It reuses its durable snapshot and sticky fallback ordinal. |
| Delegation waits for plan approval; Settings changes | The whole run, including post-approval roles, keeps its original snapshot. |
| Delegation resumes after approval or interruption | It reconstructs the stored delegation snapshot, never current Settings. |
| API restarts before a resumable task continues | It reconstructs the exact non-secret snapshot and credential lease; if either is unavailable, Resume is refused. |
| Administrator rotates a key during an active execution | Existing execution keeps its leased version; new executions lease the new version. |

### 10. Fixed shipped default — operator decision required

Both API and CLI shipped configuration should use the same fixed provider/model to avoid host drift.
The model must support the Chat Completions or native Messages adapter that bOps currently implements,
reliable native tools, and a stable model id. A router alias is not a candidate. Availability must be
rechecked against the provider's official model list in the implementation session.

| Candidate | Native tools | Determinism/stability | Credential | Operational implications | Assessment |
|---|---|---|---|---|---|
| **OpenAI / `gpt-4.1-2025-04-14`** | Yes; supported by the existing OpenAI-compatible function-call path | Dated snapshot, so behavior is more reproducible than a rolling alias | OpenAI API key | Hosted dependency and account quota; exact snapshot can later be retired and must be reviewed during upgrades | **Recommended**: fixed, tool-capable, existing adapter, least ambiguity for shipped troubleshooting behavior |
| **Anthropic / `claude-sonnet-4-6`** | Yes; covered by the native Anthropic tool-use adapter | Active direct model id, but not a dated immutable snapshot | Anthropic API key | Uses the distinct native adapter, so it is a strong compatibility choice but makes that adapter the fresh-install path | Suitable alternative, less reproducible than the dated recommendation |
| **DeepSeek / `deepseek-flash`** | Tool calls are supported by the existing OpenAI-compatible path | Direct provider but a rolling model name, so less reproducible than a dated snapshot | DeepSeek API key | Hosted dependency; changes behind the name require closer troubleshooting records | Supported but not suitable as the deterministic shipped default |

Candidate status was checked on 2026-10-06 against the providers' official documentation:
[OpenAI GPT-4.1 model and snapshot support](https://developers.openai.com/api/docs/models/gpt-4.1),
[Anthropic model status](https://docs.anthropic.com/en/docs/about-claude/model-deprecations), and
[DeepSeek API change log](https://api-docs.deepseek.com/updates/). These are
time-sensitive facts, which is why implementation must recheck them before changing shipped config.

**Recommendation: OpenAI / `gpt-4.1-2025-04-14`. OPERATOR DECISION REQUIRED.** Cost is not the
architectural criterion. A fresh install still requires the selected provider's credential, as it
does today.

OpenRouter remains supported. `openrouter/free` and other router-selection models are classified as
supported, non-deterministic exploration modes and not recommended for reproducible troubleshooting.
When such a model is effective, Settings will display: “The actual model may change on every call;
not recommended for troubleshooting sessions.” Router selection is performed inside a provider;
the fallback chain below is an administrator-owned cross-candidate recovery policy. They are not the
same mechanism and must not share a name.

### 11. Ordered fallback chain

The one canonical name is **fallback chain**. An entry is a **fallback candidate** and the host
implementation is `FallbackChatModel`. “Router” refers only to router-mode provider behavior;
“provider chain”, “fallback provider” and “composite provider” are not used as names for this
feature.

The ordered configuration shape is:

```json
{
  "ModelProvider": {
    "Fallbacks": [
      { "Provider": "Anthropic", "Model": "claude-sonnet-4-6" },
      { "Provider": "OpenAI", "Model": "gpt-4.1-2025-04-14" }
    ]
  }
}
```

Each entry names an already registered provider and a non-blank model. Endpoint, tool capability and
credential come only from that provider's own effective profile/binding. The list is explicit,
ordered and bounded (maximum three fallback entries). Duplicate provider/model tuples and the
primary tuple are rejected; there is no cycle, discovery, implicit vendor or health-based insertion.
A different model on the same provider is allowed only as an explicit distinct tuple. An empty or
absent list disables fallback.

`FallbackChatModel` lives in `bOps.Hosting` and implements one `IChatModel` for the pinned primary and
chain. It is execution-scoped and serial: `AgentRunner` does not make concurrent model calls within
one execution. It owns only candidate selection and sticky state. Provider-neutral retry timing,
attempt timeout, audit and global model-call budget stay in `AgentRunner`.

To compose precisely with HARDEN-2 without duplicating retries, the execution model carries an
internal routing directive on a `ModelProtocolException`: continue the same candidate, advance to
the next candidate, or stop because the chain is exhausted. Runtime reads that provider-neutral
directive through the same `IChatModel` call; it gains no provider names or vendor rules. The
directive is a narrow additive contract (or an equivalent internal contract if implementation can
keep it out of `bOps.Abstractions`); it cannot make a permanent failure retryable. A stable logical
call id is likewise required so candidate attempt counts cannot depend on `ModelRequest` object
identity. The implementation design review must prefer an internal contract, but must not use
`AsyncLocal` service location or ambient mutable provider state to avoid an honest additive type.

The exact sequence for one logical call is:

```text
primary attempt 1 .. N
  -> if N ends in an allowed failure, fallback ordinal 1 attempt 1 .. N
     -> if N ends in an allowed failure, fallback ordinal 2 attempt 1 .. N
        -> stop at success, a forbidden failure, exhausted chain, global budget or deadline
```

`N` is the existing same-provider maximum. The original `ModelCallBudget` stopwatch, task attempt
deadline, task token/step budget and delegated role budget are never reset on transition. The host
sets an absolute attempt ceiling of `N × (1 + configured fallback count)`, while the execution model
can stop earlier when starting from a sticky later ordinal. No candidate is revisited and no loop is
possible. A fallback transition does not wait for the failed provider's `Retry-After`; that delay was
already honored during the provider's same-provider attempts and is not authority to delay another
provider. The global remaining time must still leave the minimum attempt window.

Fallback is permitted only when the candidate exhausted its same-provider retries with:

- `Transient`
- `RateLimited`
- `Timeout`
- `Unreachable`

Fallback is forbidden for every other current kind:

- `Authentication`
- `QuotaExceeded`
- `InvalidRequest`
- `ContextOverflow`
- `MalformedResponse`
- `Unknown`

Future enum values default to forbidden until an ADR/classification explicitly allows them. No HTTP
status is interpreted by the fallback chain.

### 12. Sticky behavior

Three semantics were considered:

- per-call fallback would retry the primary at the next call and can oscillate throughout one
  troubleshooting task;
- task/delegation sticky fallback keeps the first candidate that recovers for the rest of the
  durable objective;
- process-global stickiness would leak one task's outage observation into unrelated tasks.

The selected semantic is **sticky for the execution scope**. When a candidate transition occurs,
the later ordinal becomes the starting candidate for all subsequent calls in that task or delegated
run. It is persisted before the next candidate is invoked. A later transient exhaustion may advance
again, but the execution never moves backward. This allows recovery without alternating providers
and preserves a coherent troubleshooting model. A new task starts again from its newly effective
primary.

### 13. Validation and failure behavior

Save-time validation is local and deterministic:

- provider ids must be registered and use exact canonical casing;
- model is non-blank and bounded;
- `BaseUrl` is an absolute HTTP/HTTPS URI;
- supported non-secret parameters have valid names/types/bounds;
- primary and fallback tuples are unique and chain length is bounded;
- a candidate requiring a credential has one resolvable through its own binding;
- the provider package can construct the adapter from the proposed options.

Saving does not make a live network request and does not prove account validity, model availability
or provider health. An inactive profile may be stored without a credential, but it cannot be made
effective or added to the fallback chain until its required credential is present. Provider-package
metadata must distinguish local/no-credential providers from providers that require one; it belongs
to registration/host composition, not Runtime vendor logic.

A malformed URL, blank model, unregistered provider, missing required secret, unsupported parameter
or adapter-construction failure rejects the mutation before publication. A syntactically valid
provider that is offline is accepted and fails at execution as `Unreachable`; a valid-looking key
that receives 401 fails as `Authentication` and never falls back. The system never silently keeps
using the preceding configuration while claiming the new one is active.

### 14. Audit and persisted call records

Every underlying provider attempt remains one `ModelCallRecord` and one `ModelCallAuditEvent`. The
existing fields retain their meaning for compatibility: `Provider`/`Model` identify the configured
candidate actually called, and `ActualModel` is what that provider reports.

Additive optional fields record:

- `PrimaryProvider` and `PrimaryModel` — the pinned requested primary;
- `ActualProvider` — the actual fallback/provider candidate invoked (equal to `Provider` for new
  direct records, but explicit so a fallback is queryable without interpreting legacy fields);
- `FallbackOrdinal` — 0 for primary, 1..N for configured fallbacks;
- `ProviderAttempt` — attempt number within that candidate;
- `ConfigurationGeneration` and safe snapshot hash;
- the existing normalized `FailureKind`, model-attempt ordinal and retry decision; a fallback
  transition is distinguishable from same-provider retry and chain exhaustion.

`RequestedModel`/`Model` remains the model requested from that attempt's candidate;
`ActualModel` remains nullable when the provider does not report one. Router upstream provider is
not guessed: `ActualProvider` is the configured provider bOps called (for example `OpenRouter`), not
an inferred upstream vendor. If a provider later reports a safe upstream provider id, it requires a
separate explicitly named field.

No event or record contains the credential binding id if that id could be used to resolve a secret,
and no secret value, hash or header is audited. Configuration mutation audit additionally records
the resulting generation and whether the mutation was effective or shadowed, never the value.

### 15. Security controls

| Threat | Architectural control |
|---|---|
| Switch to an unconfigured provider | Registry validation; explicit primary/fallback tuples only; fail before publication. |
| Secret cross-routing | Provider-bound authenticated credential lease; each candidate resolves only its own binding. |
| TOCTOU across provider/profile/key | One coordinator gate for mutation and one immutable atomic publication; execution reads once. |
| Fallback after authentication/permanent failure | Allow-list of four transient kinds; every other/current/future kind fails closed. |
| Fallback loops | Ordered bounded list, duplicate rejection, monotonic sticky ordinal, no backward transition. |
| Stale or misleading Settings UI | Persisted/effective/pinned terminology, generation, per-field provenance and shadow state. |
| Partially applied configuration | Validate complete proposal, atomically persist changed store, atomically publish complete snapshot. |
| API key in task/audit snapshot | Only opaque lease/version in durable pin; ciphertext stays in the vault; plaintext is execution memory only. |
| Authority/task semantics change | Snapshot selection is host composition only; AgentRunner policy, delegation envelope and approval paths are unchanged. |
| Retry/resource multiplication | Maximum three fallbacks, per-candidate attempt cap, one unchanged global call budget/deadline and no cycling. |
| Settings change during approval wait | Entire `DelegationRun` owns one durable snapshot across every role and wait. |
| Malicious provider tries to widen retries | Routing directive may only narrow/advance within the host-pinned chain; it cannot make a forbidden kind retryable. |

### 16. Required implementation tests

Block B/C must prove at least:

1. provider A is used, Settings changes to B without restart, a new task uses B, and the running task remains A;
2. same provider model X to Y affects the next task without restart;
3. key rotation affects the next execution, while the active execution keeps its old encrypted lease and no plaintext reaches task/audit persistence;
4. environment-owned provider/configuration continues to win and Settings reports the persisted value as shadowed;
5. a concurrent mutation/start observes wholly A or wholly B, never a mixed tuple;
6. primary 429 exhausts same-provider retries, then fallback runs, with every attempt and transition audited;
7. timeout and unreachable can fallback;
8. authentication, quota exceeded, invalid request, context overflow, malformed response and unknown never fallback;
9. fallback order is deterministic, sticky and never cycles;
10. Settings changes do not mutate an in-flight primary or fallback chain;
11. ordinary and delegated resume reconstruct the pin across approval, interruption and restart; unavailable exact leases refuse resume;
12. router models remain configurable and the English/Italian warning appears;
13. fresh API and CLI configuration resolves to the operator-approved fixed model;
14. stale Settings writes conflict and invalid proposals never replace the published generation.

Gating E2E uses deterministic fake/local providers:

- E2E-4 variant: scripted transient primary exhaustion reaches the ordered fallback inside the
  existing deadline and produces attempt-level audit evidence;
- live-settings E2E: one API process stays running, task A proves A, Settings changes to B/Y, task B
  proves B/Y, and audit proves A never changed and B never used A.

Live external-provider smoke remains optional and non-gating.

## Alternatives considered

- **Reload the process-global singleton after Settings saves.** Rejected: replacing an object while
  running tasks retain or reacquire it creates call-level switching and unclear disposal; it has no
  durable resume rule.
- **Make every model transient.** Rejected: lifetime alone does not define an atomic tuple or a
  deterministic task/delegation boundary, and can mix calls in one execution.
- **Resolve current Settings before every model call.** Rejected: directly violates execution
  determinism and makes approval waits a provider-switch boundary.
- **Pin per role or execution attempt.** Rejected: Discovery/Diagnostic or initial/resume calls could
  use different models in one objective.
- **Pin only provider/model and resolve endpoint/key live.** Rejected: it permits mixed tuples,
  secret cross-routing and key changes midway through a run.
- **Persist credential plaintext in the task row.** Rejected: expands secret exposure into task
  databases, API views, backups and serialization contracts.
- **Persist an unkeyed credential hash.** Rejected: it creates an offline verification oracle for
  low-entropy secrets and still cannot reconstruct the old credential.
- **Resume under current Settings after restart.** Rejected: silently changes the execution that
  HARDEN-3 says is being resumed. Exact reconstruction or refusal is deterministic; substitution is not.
- **Fallback on all failures or directly on status codes.** Rejected: authentication/configuration
  errors must be fixed, and provider packages already own status/body normalization.
- **Per-call or process-global fallback stickiness.** Rejected for oscillation and cross-task leakage,
  respectively.
- **Automatic discovery, health routing, cheapest-provider selection or vendor scoring.** Rejected as
  out of scope and contrary to explicit administrator configuration.

## Consequences

- Settings changes become live for subsequently admitted API executions with no restart.
- Active ordinary and delegated work remains deterministic across calls, approvals and resumes.
- The host gains an immutable configuration coordinator, execution factory, encrypted credential
  leases and `FallbackChatModel`; Runtime remains vendor-agnostic and is constructed with one model.
- `TaskState`, `DelegationRun` and model-call/audit records receive additive optional fields. A narrow
  routing directive/logical-call identity may require an additive `bOps.Abstractions` change if an
  internal equivalent cannot preserve the one-attempt audit contract cleanly.
- Fallback support is implemented but disabled by an empty list. No provider is ever implicit.
- Router mode stays supported and visible as non-deterministic.
- An exact resume can fail closed when its registered provider package or credential lease is no
  longer available. That is intentionally safer than silently changing provider or key.
- ADR-0029's statement that Settings changes require restart is superseded by this decision only
  after operator acceptance and implementation.

## Operator acceptance required

1. Accept or reject this execution-pinning, encrypted credential-lease and fallback-chain architecture.
2. Select the shipped default provider/model; the recommendation is OpenAI / `gpt-4.1-2025-04-14`.
