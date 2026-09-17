# ADR-0029 — Encrypted local vault, master-key handling and rotation

Status: Accepted
Date: 2026-09-17

## Context

V1.1-G lets an authenticated administrator persist the active LLM provider and its API key from
the Angular UI (`agentic/_tasks/2026-09-16-v1.1-g-secure-settings.md`). Today that configuration
is committed as `"ApiKey": ""`-shaped placeholders in `appsettings.json` and resolved only through
`EnvironmentSecretProvider` (`bOps.Runtime/EnvironmentSecretProvider.cs`), the sole
`ISecretProvider` implementation (ADR-0022). A UI write path needs somewhere durable to put a
secret an operator did not, and should not have to, place in an environment variable or a file the
process account can read in plaintext.

D-017 (`agentic/06-decisions.md`) already settles the shape at the decision level: a versioned
encrypted local vault, a master key supplied externally and never stored beside the vault, no
plaintext fallback, a `first6...last4` display mask, and continued support for the existing
environment/user-secret configuration paths with deterministic precedence. This ADR is due at
V1.1-G per the pre-committed subject table (`agentic/05-workflow.md`) because it changes the audit
schema (a new event type) and introduces a new cryptographic mechanism nothing in this codebase
had before.

Implementation surfaced a scope question the task file left implicit: "provider selection" cannot
be just an id. Every first-party provider package requires an operator-supplied `BaseUrl` with no
built-in default (`AnthropicProviderPackage`'s own doc comment: "Configure `ChatModelOptions.BaseUrl`
as `https://api.anthropic.com`") — so switching the active provider while leaving `BaseUrl`/`Model`
pinned to whatever the single `ModelProvider` block in `appsettings.json` happened to configure
would silently point the newly selected provider's key at the wrong endpoint. The operator directed
that provider configuration be fully UI-managed — endpoint, model, tool-calling support, API key,
and room for provider-specific extras — all persisted, not just the key. This ADR's scope is
extended accordingly (see "Provider profile" below); the cryptographic/vault design above is
unchanged by it. — grep of `System.Security.Cryptography` usage today finds only integrity/signature
uses (`SHA256` hash-chaining in `bOps.Audit/JsonLinesAuditSink.cs`, `SHA256` constant-time
comparison in `bOps.Api/ApiKeyAuthenticationHandler.cs`, RSA-PSS package signing in
`bOps.PluginHost/PluginPackageSignature.cs`) — no AEAD, no KDF, no confidentiality primitive
exists yet. This is greenfield, and `bOps.Abstractions` must stay dependency-free regardless
(confirmed: its `.csproj` carries zero `PackageReference`s), so the vault belongs beside
`EnvironmentSecretProvider` in `bOps.Runtime`, not in the SDK.

There is also no `Administrator` role today — only `viewer`/`operator`/`approver`
(`bOps.Api/ApiAuthorization.cs`). The mutation surface this ADR enables requires one, since Viewer
already covers read access to non-secret state (`GET /api/providers`, ADR-0019) and none of the
existing roles are an appropriate gate for writing credentials.

## Decision

### Envelope and cipher

One JSON file (`vault.dat` by default, `Vault:FilePath`) holds a schema-versioned envelope:

```
VaultFile { SchemaVersion: int, Version: int, Entries: { [providerId]: VaultEntry } }
VaultEntry { NonceBase64, CiphertextBase64, TagBase64, CreatedUtc, UpdatedUtc,
             MaskPrefix, MaskSuffix, MaskedLength, PlaintextLength }
```

Each entry is encrypted independently with **AES-256-GCM** (`System.Security.Cryptography.AesGcm`,
part of the BCL — no new package), a standard authenticated cipher, satisfying D-017's "no custom
cipher." A fresh random 96-bit nonce (`RandomNumberGenerator`) is generated on every write of that
entry. Additional authenticated data is `UTF8("{SchemaVersion}:{providerId}")`: this binds each
ciphertext to its own logical slot and schema version, so an attacker who can write to the vault
file cannot relabel or swap two otherwise-valid ciphertexts between provider ids without the GCM
tag failing to verify on decrypt — a real threat for a JSON map keyed by attacker-influenceable
provider ids, and cheap to close with AAD rather than a separate integrity layer.

### Key derivation and master-key bootstrap

The master key is supplied externally through the existing `SecretReference`/`ISecretProvider`
mechanism (`Vault:MasterKeySecret`, defaulting to `{Provider: "environment", Name:
"BOPS_VAULT_MASTER_KEY"}`) — no new resolution path. The resolved secret is not used directly as
the AES key; it is fed as input keying material to **HKDF-SHA256**
(`System.Security.Cryptography.HKDF`, RFC 5869, BCL — satisfies D-017's "no home-grown KDF") with a
fixed, non-secret application salt/info string, producing the 32-byte AES-256 key. Deriving rather
than requiring an exact-length key means an operator can provision any sufficiently long secret
string, not a precisely-formatted base64 blob.

Startup enforces two things, both fail-closed:

- If `Vault:MasterKeySecret` resolves to a value, it must be at least 20 characters. A shorter
  value is rejected at startup with an actionable error — an under-length secret undermines the
  entire envelope regardless of what HKDF does with it.
- If `Vault:MasterKeySecret` is configured but does not resolve (for example, the environment
  variable is unset), the host refuses to start. Starting anyway with the vault silently disabled
  would mean a "secret" a UI write appears to have accepted is not actually protected, which is a
  worse failure mode than refusing to boot.

If `Vault:MasterKeySecret` is absent from configuration entirely, the vault is simply not active:
`VaultSecretProvider` is not registered, and Settings-driven secret writes are unavailable. This
keeps CLI-only, vault-naive deployments unaffected.

### Rotation and recovery

Rotation is a **CLI-only** command (`bops vault rotate-key`, not exposed through the API): it reads
the current master key from `Vault:MasterKeySecret`, decrypts every entry, reads a new master key
from a second operator-supplied `SecretReference`, re-encrypts every entry under the newly derived
key with fresh nonces, and atomically replaces the file. Keeping rotation off the HTTP surface
follows the same reasoning as keeping plugin enable/disable CLI-only pre-V1.1-F (ADR-0020): the
highest-privilege maintenance operations stay on the surface that already requires direct machine
access, rather than widening what a compromised or coerced API session can do.

Corruption or tamper — invalid JSON, a GCM tag that fails to verify, a truncated nonce/tag, an
unrecognized `SchemaVersion` — is fail-closed: the affected read throws
`VaultCorruptedException`/`VaultDecryptionException`, never silently drops the entry or falls back
to treating it as absent. Backup/restore is file-level (copy `vault.dat`); because the master key
is deliberately never stored beside it, a copied vault file alone is inert, which is also the
recovery story if the file is lost — a fresh, empty vault is created on next write and providers
are re-entered through the UI, exactly like `plugins.json`/`publisher-trust.json` today.

### Atomic writes, locking, permissions

`VaultStore` reuses `PluginStore`'s pattern (`bOps.PluginHost/PluginStore.cs`): write to a
`.tmp-{guid}` file, then `File.Move(..., overwrite: true)`, atomic on both Windows and Linux for a
same-volume replace, so a process killed mid-write leaves the previous valid file rather than a
corrupt one. On Linux, `File.SetUnixFileMode(UserRead | UserWrite)` restricts the file to the
running account, the same call `PluginStore` already makes for `plugins.json`. Windows gets no
additional ACL hardening in this batch — there is no existing precedent for
`System.Security.AccessControl` anywhere in this codebase, and the benefit of adding that
dependency is uncertain across the range of Windows filesystems/deployment targets this project
supports; this is a documented, known limitation rather than a silently absent one.

A `SemaphoreSlim(1,1)` inside `VaultStore` serializes concurrent writers within one process —
stronger than `PluginStore`, justified because this task explicitly requires a concurrent-write
test that `PluginStore` was never asked to pass. Across processes (or across serialized writers
that observed a stale read), the root `Version` integer is the optimistic-concurrency token: every
read returns it, every write requires the caller's `expectedVersion` to match the value on disk at
write time, and a mismatch throws `VaultConcurrencyException` rather than silently overwriting a
concurrent change. `SettingsEndpoints` map this to HTTP 409 with the current state attached.

### Configuration precedence

For a provider's **API key**: the host tries the existing `ISecretProvider` resolution of
`ModelProvider:ApiKeySecret` first — today's only path, unchanged. A non-empty result is an
explicit operator override (an environment variable the operator set) and wins outright. Only when
that resolves to null or empty does the host fall back to `VaultSecretProvider` for the effective
active provider id. This preserves every existing CLI/environment deployment unchanged: nothing
about this ADR can make a previously-working environment-variable-backed setup behave differently.

For **which provider is active** (the `Provider` string itself), the ambiguity is different: unlike
a secret reference, `ModelProvider:Provider` always has some value in `appsettings.json`, so
presence alone cannot distinguish "an operator explicitly overrode this" from "this is just the
shipped default." The host checks `Environment.GetEnvironmentVariable("ModelProvider__Provider")`
directly — the real ASP.NET Core double-underscore environment-variable convention for this key —
rather than reading the already-merged `IConfiguration` value. When that variable is actually set,
it wins outright over anything Settings has stored. When it is not set, the Settings-stored active
provider (if any) wins over the `appsettings.json` default. This is a small, explicit check in the
composition root, not a new `IConfigurationSource` — consistent with how `ProvidersEndpoints.cs`
already reads configuration directly rather than through custom configuration-provider machinery.

Settings changes (both the active provider and a provider's stored key) take effect on the next
process restart, matching how `ModelProvider` is already resolved once at composition-root time
today. Live reconfiguration of `IChatModelRegistry` mid-process is out of scope for this batch —
the task's own Definition of Done only requires that selection and secrets *survive restart*, not
that they apply without one, and introducing hot-reload here would be materially larger than what
was asked.

### Provider profile

Each provider's non-secret configuration — `BaseUrl`, `Model`, `SupportsNativeToolCalling`, and an
open `ExtraParameters` string-to-string bag for anything provider-specific without a first-class
field yet — is a `ProviderProfile`, persisted in the same non-secret `SettingsStore`
(`settings.json`) as the active-provider selection, keyed by provider id. The provider's API key
stays exactly where the rest of this ADR puts it: the encrypted vault, under the same provider id.
Splitting secret from non-secret this way means the vault's threat model (D-017: encrypted,
external master key) never has to cover `BaseUrl`/`Model`, which are not secrets and gain nothing
from encryption, while still letting one provider id tie both halves together at read time.

Resolution order at startup, computed once (the same restart-to-apply model as today): if
`ModelProvider__Provider` is set as a real environment variable, the host uses the entire
`appsettings.json` `ModelProvider` block exactly as before and Settings is not consulted at
all — full backward compatibility for an environment-only deployment. Otherwise, the effective
provider id is the Settings-stored active provider if one is set, else the `appsettings.json`
default; for that provider id, each of `BaseUrl`/`Model`/`SupportsNativeToolCalling` comes from the
stored `ProviderProfile` if one exists, falling back field-by-field to the `appsettings.json`
`ModelProvider` block for any field a profile has not (yet) set — a partially-configured profile
(for example, a key saved before an endpoint is set) never leaves the host without a usable
default.

`ExtraParameters` is deliberately not wired into `ChatModelOptions`/`IModelProviderPackage.Create`
in this batch: no first-party provider package's `Create(ChatModelOptions)` accepts anything beyond
the fields `ChatModelOptions` already has, and `ChatModelOptions` is a `bOps.Abstractions` type —
extending it purely speculatively, for no provider that concretely needs it today, is exactly the
"don't design for hypothetical future requirements" the coding standard asks to avoid. It is
stored and returned through the Settings API so the operator can record it and so a future provider
that does need it has somewhere to read it from, without this ADR pretending it already does
something today.

### Masking

For a secret of length 10 or more: `first six characters...last four characters`, the exact format
D-017 specifies. For a shorter secret (length 1–9), a fixed formula keeps the prefix and suffix
from ever overlapping and always leaves at least one masked character:

```
revealed = min(length - 1, floor(length * 0.4))
prefix   = ceil(revealed / 2)
suffix   = revealed - prefix
```

`prefix + suffix < length` holds for every length in range, by construction. The mask
(`MaskPrefix`/`MaskSuffix`/`MaskedLength`) is computed once, from the plaintext, at the moment a
secret is written, and stored as ordinary non-secret display metadata on the `VaultEntry`. It is
never recomputed from a later decryption — the API and UI render the stored mask fields, and
nothing about rendering a mask ever requires reading plaintext back out of the vault (D-017's
"return the stored secret to render the mask" alternative was already rejected there).

### Audit

A new `SettingsChangedAuditEvent : AuditEvent` is added to `bOps.Abstractions/Audit.cs` (one more
`[JsonDerivedType]` entry — the audit-schema change this ADR exists to gate): `SettingName` (e.g.
`"provider.apiKey"`, `"provider.active"`), `Operation` (`Set`/`Replace`/`Clear`/`SelectProvider`),
`ProviderId`, `Outcome` (`Success`/`Denied`/`Failure`) — never a value, never a reversible
fingerprint of one. Every existing `AuditEvent` is implicitly task/step-scoped (`TaskId`,
`StepIndex` are `required` on the base type) because every existing event originates from an
`AgentTask`. A Settings mutation does not: it is an administrator acting directly through the API,
outside any task. Rather than relaxing those fields on the shared base type for every other event,
`SettingsChangedAuditEvent` uses documented sentinels — `TaskId = Guid.Empty`, `StepIndex = -1` —
recorded as such in the type's own doc comment so a reader of the audit log (or of `Audit.cs`)
understands immediately why those fields look empty for this one event type.

## Alternatives considered

- **OS-native credential store (DPAPI/keychain) as the only backend.** Rejected per D-017: strong
  on interactive desktops, inconsistent for containers and headless Linux services, which this
  project must support uniformly.
- **Store the raw AES key as configuration instead of deriving it via HKDF.** Rejected: forces the
  operator to provision an exact 32-byte base64 value and makes rotation more error-prone than
  supplying a new arbitrary-length secret. HKDF from arbitrary input keying material is the
  standard way to avoid that constraint.
- **A per-entry independent master key instead of one derived key for the whole vault.** Rejected:
  multiplies key-management burden (one key to provision and rotate per provider) for no real
  isolation benefit — the trust boundary is already the single host/operator established by
  ADR-0022, not per-provider.
- **Expose rotation through the API.** Rejected: rotation is the highest-privilege operation this
  ADR introduces; putting it behind the same authenticated-session surface as everyday key updates
  widens what a compromised or coerced session could do for no operational need, since rotation is
  rare and machine access is already assumed for other maintenance actions (`bops plugin *`).
- **An advisory file lock instead of a version/ETag concurrency token.** Rejected: a lock only
  serializes writers, it does not produce the clean, testable "the state changed since you read it"
  failure the task explicitly requires a test for. Optimistic concurrency does, and composes with
  the existing atomic-rename write path without adding a new locking primitive.
- **Let a client re-read plaintext to render the mask.** Rejected by D-017 directly: it would make
  the write-only secret model pointless and add a plaintext-retrieval path that does not otherwise
  exist.

## Consequences

- `bOps.Abstractions` gains no new type and no new dependency; `SecretReference`/`ISecretProvider`
  (ADR-0022) are reused exactly as designed — a new provider id (`"vault"`) alongside
  `"environment"`, nothing about the contract changes.
- `bOps.Runtime` gains a new cryptographic surface (`VaultCipher`, `VaultStore`,
  `VaultSecretProvider`) that must carry known-answer, round-trip, tamper, wrong-key and
  concurrency tests before any endpoint depends on it (`agentic/04-testing-rules.md`'s TDD-on-core
  rule).
- `bOps.Api` gains a fourth role (`administrator`) and a new `SettingsEndpoints` surface. Every
  mutation is audited via `SettingsChangedAuditEvent`; no endpoint or log path can expose a stored
  secret's plaintext.
- A host with no `Vault:MasterKeySecret` configured behaves exactly as it does today — this ADR
  changes nothing for a deployment that never opts into Settings-driven secrets.
- Windows restrictive-permission hardening remains a known gap relative to Linux until a concrete
  need justifies the added `System.Security.AccessControl` dependency; this is stated here rather
  than left implicit.
- Provider selection and secret changes require a restart to take effect, consistent with today's
  composition-root-only resolution of `ModelProvider` — the UI must say so rather than imply a live
  switch.
- A provider's endpoint/model/tool-calling support (`ProviderProfile`) and its API key are
  deliberately split across two stores with different threat models (plain JSON vs. encrypted
  vault) but the same provider id as their join key; a caller must read both to fully describe one
  provider.
- `ExtraParameters` is persisted and returned by the Settings API today but affects no running
  behavior until a provider package is extended to read it — a documented, deliberate gap, not a
  silent one.
