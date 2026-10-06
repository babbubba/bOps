# ADR-0045 — Execution-pinned provider configuration and ordered fallback chain

Status: Accepted (2026-10-06, operator decision after the independent architecture review and the targeted B-1 delta review PASS — blockers 0)
Date: 2026-10-06

Supersedes only the restart-to-apply consequence of
[ADR-0029](0029-encrypted-local-vault-and-master-key.md). ADR-0029's vault, masking,
master-key, provider-profile and environment-precedence decisions otherwise remain in force. This
ADR also governs HARDEN-13 of the V1.3.x reliability train
([packet](../../../agentic/_tasks/2026-09-25-v1.3x-harden-13-provider-defaults-fallback.md);
[plan](../../../agentic/_plans/2026-09-25-v1.3x-reliability-hardening.md), finding F-17).

Architecture accepted; implementation not started. The operator selected the shipped bootstrap default and approved
the fallback chain, and the independent architecture review (including the targeted B-1 delta review) passed
with no blockers. Block B implementation may now begin.

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

A Settings change to provider, model, endpoint or tool-calling capability must affect a subsequently
started execution without restarting the API. It must not change an execution already in progress.
A credential change is the deliberate exception: it must affect the next model attempt, including an
attempt in an already-admitted execution, because rotation may revoke or replace a compromised key.
The approved fallback feature must be explicitly configured, ordered, bounded and host-owned. The
shipped `OpenRouter` / `openrouter/free` bootstrap remains intentionally non-deterministic and must
never be presented as suitable for reproducible troubleshooting or remediation.

## Decision

### 5. Three states: persisted, effective and pinned

The product will name and expose three different states:

- **Persisted configuration** is what Settings and configuration sources store: active provider,
  profiles, fallback entries and credential metadata. A persisted value can be shadowed by an
  environment override and is not therefore necessarily in use.
- **Effective configuration** is one immutable, validated `EffectiveProviderConfiguration` published
  by the API host. It contains a primary plus an ordered fallback chain, field provenance, a monotonic
  generation and safe credential source/availability metadata. It contains no credential value or
  historical credential reference. One atomic reference is the only source used when a new execution
  starts.
- **Pinned execution configuration** is the safe durable projection of one effective generation,
  plus the execution's current sticky fallback ordinal. It contains only non-secret provider and
  request configuration, belongs to one top-level durable execution and never follows subsequent
  provider/profile Settings changes. Credentials are deliberately not pinned.

The Settings API will return all persisted values, the effective primary/fallback descriptors, the
effective generation, per-field source (`environment`, `settings`, `default`), and whether a
persisted value is shadowed. It returns credential presence and source only, never plaintext, a
credential hash or a historical-secret version. A successful mutation response states whether it
was published for new executions or persisted-but-shadowed. The UI must not use “active” for a
shadowed value.

### 6. Consistency and publication

An API-host singleton `ProviderConfigurationCoordinator` owns mutations and publication. It is an
internal host service, not a Runtime service and not part of the plugin SDK.

For each mutation it:

1. takes a short configuration write gate;
2. creates a complete proposed state from one persisted settings revision and one vault revision;
3. validates it without making a provider network call;
4. persists the changed store atomically;
5. builds the complete effective non-secret configuration and current credential availability/source
   metadata;
6. publishes it with one atomic reference replacement and increments its generation; and
7. releases the gate.

A task/delegation start reads that reference exactly once. It consequently receives wholly non-secret
configuration A or wholly B, never provider from B with model or endpoint from A. The credential is
intentionally outside this pinned tuple and is resolved from the current authority for the pinned
candidate at each attempt. No gate is held while constructing prompts, waiting for approval, calling
a provider or running a task. `SettingsStore` gains a revision and
in-process writer serialization equivalent to `VaultStore`; stale writes return conflict rather than
silently overwriting another change. The coordinator is the only API write path to either provider
store while the process is running. The CLI vault-rotation procedure remains an offline maintenance
operation requiring the API to be stopped.

Persistence precedes publication. If persistence fails, nothing is published. If the process stops
after persistence and before publication, startup deterministically rebuilds the effective snapshot
from the persisted state. There is no silent rollback to the preceding configuration.

A credential-only Settings mutation atomically replaces or removes the current vault value under the
same short coordinator gate and refreshes the safe presence/source view. It does not rewrite an
execution pin or require a new non-secret configuration hash: subsequent attempts resolve the new
current value by pinned provider id. A combined profile-and-key operation is serialized, validates
the complete non-secret proposal plus credential availability, persists both changes before
publication and never routes one provider's key to another provider.

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
3. **The block-level `ModelProvider:ApiKeySecret` is provider-bound.** Let
   `ConfiguredPrimaryProviderId` be the merged `IConfiguration` value of `ModelProvider:Provider`
   after ordinary ASP.NET configuration/environment precedence and *before* any `SettingsStore`
   active-provider selection (appsettings `Provider = openrouter` with no override → `openrouter`;
   `ModelProvider__Provider = anthropic` → `anthropic`). The block-level `ApiKeySecret` belongs
   **only** to `ConfiguredPrimaryProviderId`. On every attempt the credential for candidate
   provider `P` is resolved as follows:
   - **`P == ConfiguredPrimaryProviderId`:** resolve `ModelProvider:ApiKeySecret` if configured;
     a non-empty result is the current credential and shadows that provider's vault entry. If it
     resolves to null/empty, use `P`'s current vault entry, as ADR-0029 specifies.
   - **`P != ConfiguredPrimaryProviderId`:** `ModelProvider:ApiKeySecret` is categorically
     ineligible and is never read for `P`. Only the credential belonging to `P` in the
     provider-keyed vault (the provider-keyed configured authority) is used.

   This applies equally to a Settings-selected active provider, every fallback candidate and every
   resumed execution. A credential belonging to provider A is never sent to provider B. This
   **narrows** ADR-0029's ambiguous "effective active provider" wording, under which the block-level
   reference was applied to whichever provider was active; the shipped
   `BOPS_MODELPROVIDER_API_KEY` reference is therefore the OpenRouter bootstrap key and is not
   offered to a Settings-selected Anthropic. Environment-only deployments are unchanged: with
   `ModelProvider__Provider = anthropic` and an Anthropic credential behind `ApiKeySecret`, the
   secret is bound to Anthropic and works as before.

   **Settings provenance.** The Settings view reports per provider whether a usable credential
   exists and its source (`environment` only for `ConfiguredPrimaryProviderId`, otherwise `vault`
   or none). If Settings selects B while the configured provider is A, B never appears to use A's
   environment credential: it shows its own vault credential or "no usable credential". If an
   environment provider override makes A effective, B is shown as persisted/shadowed, not
   effective. The view never exposes plaintext, a secret hash, a secret value or a reusable
   credential identifier. The secret value is never part of the effective or pinned snapshot.
4. Environment fallback entries, when explicitly present, own the whole ordered fallback list.
   Otherwise the Settings-stored list wins, then the shipped empty default.

Non-secret provider/profile/fallback precedence is evaluated at every publication, not at every model
call. Credential precedence is the explicit exception and is evaluated for the pinned provider on
every attempt. Environment variables are not polled in the background; changing the process
environment outside Settings requires a host configuration reload/restart. Settings vault
rotation/removal is immediately visible to the next attempt without restart.

### 8. Pinning scope and model lifetime

The **execution scope** is the largest durable aggregate that represents one operator objective:

- one ordinary `TaskState`; or
- one complete `DelegationRun`, including Discovery, Diagnostic, the approval wait, Remediation,
  Verification, reconciliation and every role task it owns.

It is not an individual model call, `AgentRunner` invocation, role or execution attempt. A role-level
scope would let Discovery and Diagnostic disagree; an attempt-level scope would let Resume change the
model mid-task; a call-level scope is the reported defect.

At admission, an internal host `IExecutionChatModelFactory` receives one effective non-secret
snapshot and returns one execution-scoped `IChatModel`. That model owns pinned candidate descriptors
and uses a narrow host-owned current-credential resolver when each provider attempt is made. The host
creates an execution-scoped `AgentRunner` with that model using the existing constructor. For
delegation it creates one execution-scoped `DelegationRunner` over that runner. Runtime still sees
exactly one `IChatModel`; it does not resolve services, provider ids, profiles or secrets.

Provider model objects are lightweight execution-scoped adapters over factory-managed `HttpClient`
instances, but they must not capture a credential for their entire execution lifetime. The host
resolves the current credential and binds it only for the individual attempt, either through an
attempt-scoped adapter or an equivalent narrow provider-factory contract. Making `IChatModel`
transient by itself is not the design: the factory and durable non-secret pin are the configuration
consistency boundary. Existing public `AgentRunner(IChatModel, ...)` and `IChatModel` contracts remain
usable by tests and non-API hosts.

### 9. Durable non-secret pin and late-bound credentials

`TaskState` and `DelegationRun` gain an additive optional `PinnedProviderConfiguration`. Old records
without one use a migration rule: the first accepted resume captures the then-effective snapshot,
audits that migration, persists it before any model call, and remains pinned thereafter.

The durable projection contains only safe data:

- schema version and effective generation;
- primary and ordered fallback provider ids, base URLs, requested models, native-tool flags and
  request timeouts;
- relevant effective first-party non-secret request configuration; `ExtraParameters` remains
  persisted-only and is not added to the HARDEN-13 pin or acceptance criteria;
- field provenance and a snapshot hash;
- current sticky fallback ordinal.

No credential plaintext, authorization header, credential hash, historical secret version or
credential correlation identifier is stored in task, delegation, audit or Settings data.

Credentials are late-bound for every model attempt. The host resolves the **current** credential from
ADR-0029's existing authority for the pinned candidate provider, and that credential lives only in
attempt memory. It is never copied into `EffectiveProviderConfiguration` or
`PinnedProviderConfiguration`. Provider/profile pinning therefore remains deterministic while key
rotation is immediately effective for the next attempt in both new and already-running executions.
This exception is intentional: a rotated key may be compromised or revoked, so preserving its use
for reproducibility would be a security defect.

After restart, Resume reconstructs the exact pinned non-secret provider/model/endpoint/tool/fallback
configuration and resolves the current credential only when an attempt needs it. If the pinned
provider package can no longer instantiate that safe configuration, or the current credential for a
pinned candidate is unavailable, Resume or the next attempt fails explicitly. It never substitutes
the current Settings provider/model and never requires a historical credential lease. An invalid
current credential produces `Authentication` and cannot trigger fallback.

Consequences for required scenarios:

| Scenario | Rule |
|---|---|
| A/X task runs; Settings changes to B/Y | Running task remains A/X; the next task pins B/Y. |
| Task resumes after a model-call failure | It reuses its durable snapshot and sticky fallback ordinal. |
| Delegation waits for plan approval; Settings changes | The whole run, including post-approval roles, keeps its original snapshot. |
| Delegation resumes after approval or interruption | It reconstructs the stored delegation snapshot, never current Settings. |
| API restarts before a resumable task continues | It reconstructs the exact non-secret snapshot and late-binds the current credential; unavailable provider configuration or credential fails/refuses resume. |
| Administrator rotates a key during an active execution | The execution remains on its pinned provider/model, and its next model attempt uses the new current key. |
| Administrator removes a key during an active execution | The execution remains pinned, but its next required model attempt fails explicitly; missing credentials are not transient and cannot activate fallback. |

### 10. Shipped bootstrap default — operator decision recorded

The operator selected **OpenRouter / `openrouter/free`** as the shared API and CLI bootstrap default.
It is a zero-cost getting-started, evaluation and non-critical exploration mode, and requires an
OpenRouter API key. It is intentionally a router alias: the actual model can change on every call and
must remain visible through the existing requested/actual model-call and audit information. bOps must
never imply that this bootstrap is deterministic.

For important troubleshooting, reproducible analysis and especially remediation, documentation and
Settings must recommend selecting a specific model explicitly. Settings will display: “The actual
model may change on every call; not recommended for troubleshooting sessions.” Router selection is
performed inside a provider; the fallback chain below is an administrator-owned cross-candidate
recovery policy. They are not the same mechanism and must not share a name.

Block B Getting Started/operator documentation must state:

1. the shipped default requires an OpenRouter API key;
2. how to configure that key through Settings;
3. `openrouter/free` may select a different actual model on different calls;
4. important troubleshooting, reproducible analysis and remediation should use an explicitly selected
   specific model; and
5. model-call/audit information exposes the actual model when the provider reports it.

C-20 is therefore mitigated through explicit disclosure, actual-model visibility, easy live
provider/model selection and the serious-work recommendation. HARDEN-13 does not attempt to make the
operator-selected bootstrap deterministic.

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

Each entry names an already registered provider and a non-blank model. Endpoint and tool capability
come only from that provider's own pinned effective profile. Entries contain no secrets. For
fallback candidate N, only the current credential belonging to that candidate's provider is
resolved, per §7.3: if the candidate is not `ConfiguredPrimaryProviderId`, the block-level
`ModelProvider:ApiKeySecret` is categorically ineligible and never consulted. The list is explicit,
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

**Ownership of the transition.** `AgentRunner` remains the single owner of same-provider retry
policy: attempt count, backoff delay, `Retry-After` handling, `ModelCallBudget` and
`ModelRetryDecision`. `FallbackChatModel` owns only candidate advancement: the ordered chain, the
current candidate and the sticky ordinal. It does not run, duplicate or reimplement a retry loop,
and no retry policy moves into provider adapters. The narrow provider-neutral handshake is the
routing directive above, in both directions: the runner, when same-candidate retry processing ends
for a reason listed below, tells the model "same-candidate retry is exhausted; advance"; the model
selects the next configured candidate (or reports the chain exhausted) without the runner knowing
which provider follows. Runtime gains no provider names, no HTTP-status routing and no service
location. The handshake is an additive provider-neutral contract (or an equivalent internal
control surface), not a routing subsystem.

**Retry-to-fallback handoff.** For an allowed kind (list below), when `DecideModelRetry` would
end the call with:

- `AttemptsExhausted` or `RetryAfterExceedsLimit` — the candidate advances, provided another
  configured candidate exists and the remaining global `ModelCallBudget`/execution budget still
  leaves room for a permitted attempt (`MinimumModelAttemptWindow`). The failed candidate's
  `Retry-After` is not waited out (see above);
- `BudgetExhausted` — remains terminal; fallback never advances, as that would spend an already
  exhausted global budget;
- a non-allowed kind — terminal regardless of the retry decision.

**Two independent ordinals.** `ProviderAttempt` is the attempt number within the current
candidate; `FallbackOrdinal` is 0 for the primary, 1 for the first fallback, and so on. One counter
is never used for both. The global model-call budget is shared across all candidates; no
transition resets `ModelCallBudget`, execution duration, task/delegation budgets or role budgets,
and the total attempt count stays bounded by the ceiling above.

**Missing fallback credential.** If the execution has advanced to a fallback candidate and that
candidate has no usable current credential, the attempt fails as `Authentication`, which
terminates the logical model call. The chain does not silently skip to a later fallback, never
supplies another provider's credential, never returns to an earlier candidate, and the sticky
ordinal stays where it advanced. Admission requires a usable primary credential only: fallback
credentials may be unavailable at admission because they might never be needed. A readiness view
may show an unavailable fallback credential as a warning; it never blocks an otherwise usable
primary execution, and no background provider health checking is added.

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
- a candidate newly activated or added to a chain and requiring a credential has one currently
  resolvable through its own provider-scoped authority;
- the provider package can construct the adapter from the proposed options.

Provider ids are validated against the registry with the existing case-insensitive semantics and
stored in the registered canonical spelling; comparisons, including the `ConfiguredPrimaryProviderId`
binding of §7.3, are case-insensitive. The save-time credential check applies to a newly added
fallback, but execution admission checks the primary credential only (§11): a fallback credential
removed or never present later is an attempt-time `Authentication` failure, not an admission failure.

Saving does not make a live network request and does not prove account validity, model availability
or provider health. An inactive profile may be stored without a credential, but it cannot be newly
activated or added to the fallback chain until its required credential is present. Explicit removal
of a current credential is nevertheless always allowed as a security revocation: availability becomes
false immediately, new execution admission and the next attempt fail explicitly, and the old value is
not retained. Provider-package metadata must distinguish local/no-credential providers from providers
that require one; it belongs to registration/host composition, not Runtime vendor logic.

A malformed URL, blank model, unregistered provider, missing required secret during activation/chain
addition, unsupported parameter or adapter-construction failure rejects that configuration mutation
before publication. Credential removal follows the explicit revocation rule above rather than this
activation rule. A syntactically valid provider that is offline is accepted and fails at execution as
`Unreachable`; a valid-looking key that receives 401 fails as `Authentication` and never falls back.
The system never silently keeps using the preceding configuration or credential while claiming the
new state is active.

### 14. Audit and persisted call records

Every underlying provider attempt remains one `ModelCallRecord` and one `ModelCallAuditEvent`. The
existing fields retain their meaning for compatibility: `Provider` identifies the configured
provider candidate actually called, `RequestedModel`/`Model` identifies the model requested from that
candidate, and `ActualModel` is what that provider reports.

Additive optional fields record:

- `PrimaryProvider` and `PrimaryModel` — the pinned requested primary;
- `FallbackOrdinal` — 0 for primary, 1..N for configured fallbacks;
- `ProviderAttempt` — attempt number within that candidate;
- `ConfigurationGeneration` and safe snapshot hash;
- the existing normalized `FailureKind`, model-attempt ordinal and retry decision. A fallback
  transition is represented by a new **append-only** `ModelRetryDecision` value, `Fallback` (no
  existing value is renumbered; an equivalent additive provider-neutral representation is
  acceptable if it keeps the distinction explicit and testable). The audit must distinguish:
  `Retry` (same candidate again), `Fallback` (advance to the next candidate), `AttemptsExhausted`
  (no candidate remains) and `BudgetExhausted` (the global budget prevents another attempt).
  `ProviderAttempt` and `FallbackOrdinal` are separate fields (§11).

No separate `ActualProvider` field is added because existing `Provider` already records the actual
configured provider candidate bOps invoked; duplicating it would create competing semantics. For
router mode that value is `OpenRouter`, while `ActualModel` remains the provider-reported upstream
model when available. bOps does not guess an upstream vendor. If a provider later reports a safe
upstream provider id, that would require a separately named field outside this decision.

No event or record contains a secret value, historical secret version, credential correlation id,
credential hash, header or Authorization data. Configuration mutation audit additionally records the
resulting generation and whether the mutation was effective or shadowed, never the value.

### 15. Security controls

| Threat | Architectural control |
|---|---|
| Switch to an unconfigured provider | Registry validation; explicit primary/fallback tuples only; fail before publication. |
| Secret cross-routing | Per-attempt resolver is scoped to the candidate provider. The block-level `ModelProvider:ApiKeySecret` is bound to `ConfiguredPrimaryProviderId` only and is never read for any other provider (Settings-selected, fallback or resumed); other providers use only their own provider-keyed vault entry (§7.3). |
| TOCTOU across provider/profile configuration | One coordinator gate for mutation and one immutable atomic publication; execution reads non-secret configuration once. Credential rotation is intentionally late-bound and independently atomic in the vault. |
| Fallback after authentication/permanent failure | Allow-list of four transient kinds; every other/current/future kind fails closed. |
| Fallback loops | Ordered bounded list, duplicate rejection, monotonic sticky ordinal, no backward transition. |
| Stale or misleading Settings UI | Persisted/effective/pinned terminology, generation, per-field provenance and shadow state. |
| Partially applied configuration | Validate complete proposal, atomically persist changed store, atomically publish complete snapshot. |
| API key in task/audit snapshot | No credential identifier/version/value is in the durable pin; ciphertext stays in the existing vault and plaintext is attempt memory only. |
| Authority/task semantics change | Snapshot selection is host composition only; AgentRunner policy, delegation envelope and approval paths are unchanged. |
| Retry/resource multiplication | Maximum three fallbacks, per-candidate attempt cap, one unchanged global call budget/deadline and no cycling. |
| Settings change during approval wait | Entire `DelegationRun` owns one durable snapshot across every role and wait. |
| Malicious provider tries to widen retries | Routing directive may only narrow/advance within the host-pinned chain; it cannot make a forbidden kind retryable. |

### 16. Required implementation tests

Block B/C must prove at least:

1. provider A is used, Settings changes to B without restart, a new task uses B, and the running task remains A;
2. same provider model X to Y affects the next task without restart;
3. a task starts pinned to provider A/model X, A's key rotates without restart, and the next attempt in
   that execution remains A/X but uses the new key; the old key is not retained or persisted by
   HARDEN-13 and audit contains no secret;
4. removing A's key while that execution is active makes its next required attempt fail explicitly;
   missing or invalid credentials are not transient, and `Authentication` never triggers fallback;
5. environment-owned provider/configuration continues to win and Settings reports the persisted value as shadowed;
6. a concurrent mutation/start observes wholly non-secret configuration A or wholly B, never a mixed tuple;
7. primary 429 exhausts same-provider retries, then fallback runs, with every attempt and transition audited;
8. timeout and unreachable can fallback;
9. authentication, quota exceeded, invalid request, context overflow, malformed response and unknown never fallback;
10. fallback order is deterministic, sticky and never cycles;
11. Settings changes do not mutate an in-flight primary or fallback chain;
12. ordinary and delegated resume reconstruct the non-secret pin across approval, interruption and
    restart, use the current credential, and refuse when the pinned configuration or current
    credential is unavailable;
13. the `OpenRouter` / `openrouter/free` bootstrap remains configurable, requires a key, shows the
    English/Italian warning, and records actual model information when reported;
14. Getting Started recommends an explicit specific model for important troubleshooting, reproducible
    analysis and remediation;
15. stale Settings writes conflict and invalid proposals never replace the published generation.

Security and handoff regression tests (review B-1, NB-1..NB-3), using fake providers that record the
credential they receive:

- **A — Settings-selected different provider.** Block provider `openrouter` with block-level
  `ApiKeySecret` = `OPENROUTER_KEY`; Settings active provider `anthropic`; vault `anthropic` →
  `ANTHROPIC_KEY`. Anthropic receives `ANTHROPIC_KEY` and never `OPENROUTER_KEY`.
- **B — Fallback cross-provider isolation.** Primary `openrouter` with `OPENROUTER_KEY`; fallback
  `anthropic`; vault `anthropic` → `ANTHROPIC_KEY`; the primary fails with an allowed kind. The
  fallback receives `ANTHROPIC_KEY` and never `OPENROUTER_KEY`.
- **C — Environment primary override.** `ModelProvider__Provider = anthropic` with block-level
  `ApiKeySecret` = `ANTHROPIC_ENV_KEY`; the block-level secret is valid for `anthropic`.
- **D — Missing fallback credential.** Valid primary credential, no fallback credential; the primary
  exhausts with an allowed kind. Fallback resolution fails as `Authentication`, no later candidate is
  attempted and no foreign credential is supplied.
- **E — `RetryAfterExceedsLimit`.** The primary returns `RateLimited` with `Retry-After` above the
  retry limit, a fallback exists and the global budget permits an attempt; the candidate advances
  rather than the call terminating.
- **F — `BudgetExhausted`.** A primary transient failure leaves insufficient global budget; no
  fallback candidate is attempted.
- **G — Audit transition.** `Retry`, `Fallback`, `AttemptsExhausted` and `BudgetExhausted` are
  distinguishable in the records and events, with `ProviderAttempt` and `FallbackOrdinal` independent.
- **H — Settings provenance.** Settings-selected B never reports A's environment credential; an
  environment override makes A effective and B shadowed.

Gating E2E uses deterministic fake/local providers:

- E2E-4 variant: scripted transient primary exhaustion reaches the ordered fallback inside the
  existing deadline and produces attempt-level audit evidence;
- live-settings E2E: one API process stays running, task A proves A, Settings changes to B/Y, task B
  proves B/Y, and audit proves A never changed and B never used A.

Live external-provider smoke remains optional and non-gating.

### 17. Block B implementation/test obligations (review NB-4..NB-6)

These are obligations on the implementation, not open design questions:

- Direct resume of a delegated role `TaskState` is either refused or inherits the owning
  `DelegationRun` pin; it never captures current Settings as a legacy ordinary task.
- The pin is persisted atomically with `TaskState`/`DelegationRun` admission, before the first
  model call.
- The immutable snapshot hash excludes the mutable sticky fallback ordinal.
- Whether a credential-only mutation increments the configuration generation is defined explicitly
  in the implementation design. Credentials stay outside the immutable snapshot hash either way.
- A legacy unpinned resume under current Settings, if retained, is explicitly identified, audited
  and surfaced in the resume response.
- An invalid or missing persisted provider on resume fails closed; an invalid persisted fallback at
  startup produces an explicit, visible state rather than a silent drop.
- Provider-id canonical casing is documented consistently with the case-insensitive registry (§13).

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
- **Resolve endpoint or other non-secret provider configuration live with the credential.** Rejected:
  it permits mixed provider/model/request tuples. Only the credential is late-bound, as an explicit
  security exception to configuration pinning.
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
- Active ordinary and delegated work keeps deterministic non-secret provider/model/request
  configuration across calls, approvals and resumes.
- Key rotation/removal is effective on the next model attempt, including in an active execution; the
  pinned provider/model/configuration does not change.
- The host gains an immutable configuration coordinator, execution factory, per-attempt current
  credential resolution and `FallbackChatModel`; Runtime remains vendor-agnostic and is constructed
  with one model.
- `TaskState`, `DelegationRun` and model-call/audit records receive additive optional fields. A narrow
  routing directive/logical-call identity may require an additive `bOps.Abstractions` change if an
  internal equivalent cannot preserve the one-attempt audit contract cleanly.
- The approved fallback support is disabled by an empty list. No provider is ever implicit.
- `OpenRouter` / `openrouter/free` remains the disclosed non-deterministic bootstrap default; serious
  work is directed to an explicitly selected specific model.
- An exact resume can fail closed when its registered provider package, pinned configuration or
  current credential is unavailable. That is intentionally safer than silently changing provider or
  model.
- ADR-0029's statement that Settings changes require restart is superseded by this decision only
  after operator acceptance and implementation.

## Review and acceptance gate

The operator decisions are recorded: the bootstrap default is `OpenRouter` / `openrouter/free`, and
the ordered host-level fallback chain is approved in principle. No operator design choice remains
open. Independent architecture review is the only remaining Block A gate; after it passes, the
operator can mark this ADR Accepted and authorize Block B.
