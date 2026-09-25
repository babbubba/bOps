# ADR-0037 — Safe local plugin lifecycle

Status: Accepted
Date: 2026-09-24
Accepted: 2026-09-24 by the operator
Builds on: ADR-0020

## Context

ADR-0020 establishes one plugin boundary: `PluginManager`, `PluginStore`,
`PluginManifestValidator`, `PluginPackageSignature`, `PluginPublisherTrustStore`, and the
collectible `PluginHost` load context. This ADR defines archive intake without creating another
loader, manifest interpretation, signature scheme, trust store, registry, or arbitrary-package
installer. The existing read-only catalog remains the only catalog surface to extend.

An archive is untrusted data until complete validation and commit succeed. No assembly may be
loaded, reflected as executable code, constructed, or registered from an upload body, temporary
archive, staging directory, quarantine, or rejected candidate. Metadata-only PE inspection is
permitted for structural validation; it must not load an assembly into an `AssemblyLoadContext`.

## Decision

### Authority and persisted state

All upload/install, replace, enable, disable, and recovery-affecting actions are
administrator-only. API/backend authorization is authoritative; the UI and archive never decide it.

M5 extends the existing atomically written `PluginStore` record additively. The record retains
the manifest snapshot and verified provenance, and gains an opaque monotonic lifecycle version
(ETag), `CurrentGeneration`, `ActivationLkgGeneration`, lifecycle state, and a bounded sanitized
failure stage/reason. An active lifecycle transaction also records its
`TransactionRollbackGeneration` and `CandidateGeneration` in its recovery marker; these are
transaction roles, not aliases for activation LKG.

| Persisted state | Meaning |
|---|---|
| `InstalledDisabled` | A fully validated, committed generation is installed but not loaded or registered. Every install/replacement ends here. |
| `Enabled` | The committed generation is registered through the existing `PluginManager`/`PluginHost` boundary and is persisted for startup activation. |
| `ActivationFailed` | Enable/startup activation failed; the generation is not registered and its sanitized failure is recorded. |
| `RecoveryRequired` | Recovery cannot prove a single safe current generation. Nothing is registered until an administrator recovers. |

Persisted `Enabled` intent and actual in-process loaded state remain distinct, as they are in the
current catalog. A bounded operation result may report `Received`, `Validating`, `Rejected`,
or `Quarantined`; these are transient stages, not public plugin states and not second install
records. A rejected archive never makes a plugin record.

### Intake, staging, extraction, and bounds

M5 accepts one configured local archive type: ZIP. It accepts no URL, remote repository, nested
installer, or generic package format. The API request-body limit and backend streaming limit are
both applied. Limits are configuration with hard ceilings; a configured value above its ceiling
fails configuration, rather than weakening intake.

| Limit | Default | Hard ceiling |
|---|---:|---:|
| Compressed archive bytes | 64 MiB | 256 MiB |
| Entry count | 2,048 | 10,000 |
| Total uncompressed bytes | 256 MiB | 1 GiB |
| Single-entry uncompressed bytes | 64 MiB | 256 MiB |
| Directory depth | 16 | 32 |
| Normalized relative path length | 240 characters | 240 characters |
| Per-entry and aggregate compression ratio | 100:1 | 100:1 |
| Manifest bytes | existing 1 MiB | existing 1 MiB |

Missing, negative, overflowing, inconsistent, or unavailable size metadata rejects the archive.
Streaming extraction independently counts actual entries/bytes and stops before an uncompressed or
ratio limit is crossed. The path limit deliberately fits Windows; implementation uses the stricter
platform limit when lower.

The configured plugin root owns a same-volume lifecycle work area, for example
`<plugin-root>/.lifecycle/{uploads,staging,quarantine,lkg}`. Temporary archive, staging,
quarantine, activation-LKG, transaction-rollback, and journal material are lifecycle-owned and
restrictive to the host owner where supported. M5 proves work and install roots are on the same volume before promotion. If atomic
rename cannot be guaranteed, it fails safely; it never substitutes copy-and-delete replacement.

Before creating an extracted path, M5 enumerates and canonicalizes every entry. It rejects:

- empty, rooted, drive-qualified, UNC, or absolute paths;
- `..` traversal with either separator;
- paths escaping staging after normalization;
- duplicate entries, case-insensitive collisions, Unicode-normalization collisions, or
  separator-normalization collisions;
- file/directory type collisions;
- symbolic links, junction/reparse-point entries, hard-link-like, device, or other unsupported
  special entries;
- count, size, depth, path-length, or compression-bomb limit excess;
- lifecycle control/marker/store names and attempts to overwrite lifecycle metadata.

Only after the complete structural pass does M5 materialize approved relative paths under a unique
staging directory. It verifies every parent is an ordinary directory, opens without following links
where supported, and rechecks containment/reparse attributes before and after writing.
`Path.GetFullPath` is a containment check, not the extraction defense.

Invalid material is never installed/enabled. Quarantine is transient diagnostic state: normally the
candidate is deleted immediately. A configured incident mode may retain it only in a non-loadable
restricted directory, for at most 24 hours and at most ten candidates. It retains only sanitized
metadata publicly; it never exposes archive bytes or paths and never becomes an unbounded corpus.

### Exact validation sequence

M5 stops at the first failed stage and records a neutral stage/result only:

1. Authorize the administrator and apply request/body/key limits; stream to a bounded temporary
   archive.
2. Parse archive structure and reject every unsafe path, special type, collision, metadata issue,
   or configured archive limit before extraction.
3. Safely extract approved entries to unique staging while enforcing actual byte/count/ratio and
   containment/reparse checks.
4. Require exactly one expected plugin root/layout and `bops-plugin.json`; reject ambiguous
   roots and lifecycle-owned metadata.
5. Read the bounded manifest and invoke the existing `PluginManifestValidator` for
   schema/version and required-field validation.
6. Validate stable manifest identity/version; compare operation/route id when supplied, never the
   archive filename.
7. Require and parse existing detached `bops-plugin.sig.json`.
8. Invoke existing `PluginPackageSignature` canonical-content verification over staged accepted
   content; do not add a digest or signature format.
9. Require signature publisher equals manifest publisher and query existing local publisher/key
   trust.
10. Fail closed for missing/invalid signature, mismatch, unknown publisher/key, revoked key, or
    untrusted publisher. Revocation is an additive local trust-store status, not a second store.
11. Run existing host/SDK/manifest compatibility: supported manifest format,
    `bOps.Abstractions` floor, entry-assembly presence, and declared dependency consistency.
    Declared dependencies remain informational contract data, not a resolver.
12. Validate declared capabilities and maximum risk against present manifest/risk semantics and
    host policy. Installation cannot widen package trust, actual tool `RiskLevel`, host policy,
    or delegation authority. Declarations are not grants or a sandbox.
13. Inspect entry assembly/type structurally with metadata-only APIs: it is a managed assembly
    within staging, contains the declared type, and has the existing allowable provider-role shape.
    Construction, provider methods, and registration occur only at explicit enable.
14. Under the per-plugin lock, recheck ETag, identity, publisher continuity, allowed version
    transition, collision, and state eligibility.
15. Only then declare the candidate commit-eligible.

Replacement requires the same manifest id as its target. Route/manifest id mismatch, replacing
another id, or publisher/key change rejects unless local trust policy has an explicit configured
key-rotation continuity record. New versions must differ. A target must be
`InstalledDisabled`, `ActivationFailed`, or `RecoveryRequired`; an enabled plugin must first
be explicitly disabled. This keeps the existing single-active-generation load-context model safe,
prevents silent execution of the new version, and avoids unsupported force unload/simultaneous
versions.

### Atomic install, generation roles, and recovery

The atomically replaced `PluginStore` record is the authoritative commit marker. A committed
record references final generation path, digest, state, and generation id. The generation roles
are distinct:

- `CurrentGeneration` is the generation committed as the installed version. It can be enabled,
  disabled, activation-failed, or in recovery state; current does not imply it ever activated.
- `ActivationLkgGeneration` is the most recent generation that successfully completed the accepted
  activation boundary through the existing registry. It changes only after successful activation;
  validation, installation, commit, disable, or an activation failure do not make a generation
  activation LKG.
- `TransactionRollbackGeneration` is a transient reference/location for one install/replace
  transaction. It identifies the generation that was `CurrentGeneration` immediately before the
  replacement commit began, solely to restore that pre-transaction current generation if the
  transaction fails or is interrupted before successful commit reconciliation.
- `CandidateGeneration` is the validated staging/promotion candidate for that transaction.

A lifecycle journal, atomically written before each non-idempotent rename, records operation id,
plugin id, each generation/digest with its explicit role, and phase (`oldMoved`,
`candidatePromoted`, `storeCommitted`). It is removed only after store commit. This local recovery
journal is not a distributed transaction, and M5 never infers generation roles from directory
timestamps or version numbers.

For first install: atomically rename validated staging into the final install path, then atomically
write `InstalledDisabled` into the store. Failure before store commit leaves unreferenced material
that recovery deletes/quarantines; it never activates it.

For replacement: validate completely; acquire lock and recheck ETag; record the old final as
`TransactionRollbackGeneration`; atomically move it to the transaction rollback location;
atomically promote `CandidateGeneration` to final; then atomically write the new
`InstalledDisabled` record with that candidate as `CurrentGeneration`. Cleanup is subsequent.
Failure before candidate promotion restores/keeps `TransactionRollbackGeneration` as current.
Failure after candidate promotion but before store commit restores
`TransactionRollbackGeneration`, treats `CandidateGeneration` as uncommitted, and preserves
`ActivationLkgGeneration`; if restoration cannot be proved, mark `RecoveryRequired` and register
neither. Failure after store commit leaves the new generation committed and disabled. A successful
install/replace never enables or promotes the candidate to activation LKG.

After a successful replacement commit, `CurrentGeneration` is the candidate and
`ActivationLkgGeneration` remains unchanged until that current generation activates successfully.
The completed transaction's `TransactionRollbackGeneration` is no longer authoritative and is
cleanup-eligible under the bounded retention/recovery rules. On a later successful activation,
`CurrentGeneration` and `ActivationLkgGeneration` both name that generation; the prior activation
LKG then becomes cleanup-eligible when retention permits, without deleting material needed by an
active transaction or recovery state.

For example, let A be the previously successfully activated `ActivationLkgGeneration`, B be a
later installed but disabled and never-activated `CurrentGeneration`, and C be a replacement
candidate. Before B -> C promotion, current is B, activation LKG is A, and transaction rollback
is B. If C promotion or metadata commit fails, recovery restores current B, preserves activation
LKG A, and discards, quarantines, or cleans C under the normal failure semantics. It must not
restore A merely because A is activation LKG.

Retention is bounded to the logical roles of current committed generation, activation LKG when
distinct, one active transaction rollback generation, and one active candidate/staging generation.
A single physical generation may satisfy more than one logical role (for example,
`CurrentGeneration == ActivationLkgGeneration`) and does not require duplicate copies. Retain an
activation LKG for 30 days; do not delete it until a later generation activated successfully and
retention permits.

At startup and before any new mutation, M5 serializes recovery with that plugin's lock:

| Observation | Required recovery |
|---|---|
| Unreferenced old upload/staging | Delete or bounded-quarantine; never load. |
| Final directory matching store digest/generation | Current committed install; normal startup considers only persisted `Enabled` for activation. |
| Interrupted replacement journal with no committed current marker | Restore `TransactionRollbackGeneration` as `InstalledDisabled`; preserve `ActivationLkgGeneration`; do not auto-enable. |
| Candidate final exists while the journal/store identifies a prior current | Restore `TransactionRollbackGeneration`; delete/quarantine `CandidateGeneration`; preserve `ActivationLkgGeneration`. |
| Store references absent/mismatched material or two plausible currents | `RecoveryRequired`; preserve bounded evidence; register neither. |
| Store committed before activation completed | `InstalledDisabled`; activation needs a fresh explicit enable. |

### Enable, disable, and activation failure

Enable is a separate administrator transaction. It accepts only a verified compatible committed
`InstalledDisabled` generation and requires explicit confirmation that the named version will
execute in-process. It uses the existing `PluginManager`/`PluginHost` activation and restricted
service boundary. Success atomically records `Enabled` and increments ETag.

Disable is administrator-only and idempotent. It uses existing registry unregistration and
collectible context release, then persists `InstalledDisabled`; artifacts remain installed. It
does not invent force unload. When current registry behavior cannot genuinely unregister a provider,
it returns sanitized refusal and keeps it enabled rather than lying.

If construction, discovery, registration, or activation persistence fails, the existing activation
path unregisters every plugin-owned tool/Skill it added, unloads the context, records sanitized
`ActivationFailed`, increments ETag, and never reports enabled. There is no automatic re-enable
of the activation LKG: it would execute code without fresh confirmation. Explicit administrator
recovery restores the selected `ActivationLkgGeneration` as `InstalledDisabled`; a separate
explicit enable is required.

| Operation | Preconditions | Result |
|---|---|---|
| Install | Valid candidate; absent id; creation precondition | `InstalledDisabled` |
| Replace | Valid same id/publisher continuity; target not enabled; matching ETag | New `InstalledDisabled`, activation LKG retained |
| Enable | Disabled verified compatible generation; matching ETag; confirmation | `Enabled` or `ActivationFailed` |
| Disable | Matching ETag | `InstalledDisabled` |
| Enable already enabled | Matching ETag, same generation | Idempotent success; no reload |
| Disable already disabled | Matching ETag | Idempotent success |
| Recover | Failed/recovery state; matching ETag; confirmation | Activation LKG restored disabled |

### Concurrency, ETag, and idempotency

One keyed asynchronous lifecycle lock serializes install/replace, enable, disable, and recovery for
the same manifest id. Different ids may proceed independently; no global process lock is needed.
Archive intake may precede identity discovery, but no commit occurs before identity is known and
this lock is held. Multi-process access is unsupported unless a later `PluginStore` adds a
cross-process primitive.

Every mutable plugin resource exposes opaque monotonic ETag. M6 requires `If-Match` for replace,
enable, disable, and recovery; creation uses identity-bound `If-None-Match: *` (or equivalent)
once manifest id is known. M5 performs the final check while holding the key, before promotion or
activation. Stale/missing required preconditions fail with deterministic conflict/precondition
response, delete/quarantine the already-validated staging candidate, and make no mutation,
promotion, or activation.

M6 reuses the API idempotency pattern, adding a durable lifecycle operation record atomically
reserved by the provisional uniqueness scope `(NodeId, ActorIdentity, IdempotencyKey)`. Plugin id
and operation kind are intent, not key uniqueness: within the configured bounded idempotency
retention window, one actor/node/key identifies one logical lifecycle mutation. The existing
128-character key bound remains; records do not create permanent actor-global reservations.

At request admission, before archive parsing or plugin identity discovery, the backend reserves
that one record in bounded `Pending`/`InProgress` state. Only its owner executes the mutation.
Any concurrent request with the same scope joins, waits for, or replays that operation according
to the existing API idempotency infrastructure and never begins a second lifecycle transaction.

The record stores a deterministic canonical intent fingerprint. It includes operation kind,
route/request plugin identity when already known, expected ETag/version or `If-Match`
precondition when semantically part of the mutation, activation confirmation where applicable,
and normalized mutation-body fields. For upload/install it also includes a cryptographic digest of
the received archive bytes and, after safe manifest parsing, the validated canonical manifest
plugin identity/version. The archive digest is a request-identity fingerprint, not a signature and
not a substitute for artifact signature verification; raw archive bytes are not retained for
idempotency comparison.

The same record transitions as follows: reserve `(NodeId, ActorIdentity, IdempotencyKey)`; record
immediately-known intent; compute the archive digest during bounded upload; then atomically bind
the canonical intent after safe manifest parsing. It is never moved to a plugin-scoped key.
Same scoped key plus the same canonical intent joins or replays the one logical result, with no
duplicate staging, commit, activation, or state transition. The same scoped key with a different
operation kind, archive digest, plugin identity/version, ETag/precondition, activation intent, or
other canonical mutation input is a deterministic idempotency conflict and performs no second
lifecycle mutation. Thus the same actor/node/key cannot be reused for Enable after Upload merely
because the operation differs.

If a follower arrives while an upload owner is still receiving or parsing its archive and the full
fingerprint is unknown, it still joins/waits on the in-progress reservation and starts no mutation.
Once the owner's intent and result are known, a matching follower replays that result and a
different follower receives deterministic conflict. If comparison requires consuming a follower
body, M6 may bound and hash it, but it must not start a second backend mutation.

ETag optimistic concurrency and idempotency are separate controls. An ETag asks whether a mutation
is based on current plugin state; idempotency asks whether this logical request was submitted
already. A same-key, same-intent replay returns the original logical result under these semantics.
A new key with stale `If-Match` fails normal optimistic concurrency; idempotency does not bypass an
unrelated stale-state mutation.

### Cleanup and audit

Cleanup removes temporary archives after extraction outcome, staging after commit/rejection,
quarantine after 24 hours/ten candidates, and obsolete role material only after the applicable
activation-LKG or transaction/recovery retention permits. Cleanup failure does not un-install a
committed generation or change its state; it is observable/auditable for retry. Retention caps
prevent accumulation.

Audit records upload/install attempt, validation stage/result, signature/trust result,
install/replace commit, enable, disable, activation failure, recovery, stale precondition refusal,
and idempotent replay where convention does. Fields are plugin id/version, safe publisher/key
category, operation, prior/new state, outcome, actor, node, correlation, and idempotency metadata.
Never audit archive bytes, signatures/private keys, trust-store contents, credentials, arbitrary
payloads, stacks, or absolute local paths.

### M6 API responsibility

M6 extends `/api/plugins`, not a parallel surface, with administrator-only archive
install/replace, explicit-confirmation enable, disable, recovery, and bounded operation/status
projection. API owns authorization, existing CSRF/antiforgery handling for browser mutations,
request-body bounds, `Idempotency-Key`, ETag transport, rate limit, and sanitized HTTP mapping.
It maps validation/trust/compatibility/risk errors to stable sanitized categories and stale
preconditions to the established conflict/precondition category.

Lifecycle/backend owns archive validation, signature/trust, compatibility, in-lock concurrency
check, atomic commit, activation cleanup, and recovery. API must not duplicate trust logic. Public
projection excludes paths, journal data, raw exceptions, signature material, and trust-store data.

### M7 UI responsibility

M7 extends the existing catalog. It may show persisted/actual loaded state, bounded validation
outcome, publisher/signature/trust, compatibility, dependencies, capabilities, declared/effective
risk, recovery availability, and sanitized failure stage. It may upload, initiate enable/disable/
recovery, send ETag/idempotency key, prevent obvious double-submit, and refresh stale state.

The UI cannot decide authorization, signature validity, trust, compatibility, risk ceiling, or
transition legality. It must show an explicit confirmation naming plugin and version before enable,
including after update. Stale state refreshes the authoritative catalog; the UI never blindly
retries. Installation never implies activation.

### Security property and public boundary

An enabled plugin executes in-process with bOps host process privileges. Signature and local
publisher trust establish publisher/artifact provenance; they are **not a sandbox**.
`AssemblyLoadContext`, manifest capabilities, declared risk, and restricted service construction
do not confine arbitrary malicious assembly code. This ADR does not claim process isolation.

This lifecycle is product-neutral and local. It contains no edition, billing, SKU, token format,
private provider, private Skill, or business distribution rule.

## Threat and bypass analysis

| Threat | Controlling boundary |
|---|---|
| Zip slip/rooted paths | Pre-materialization canonical entry validation and containment checks |
| Symlink/junction/reparse escape | Special-entry rejection and parent/file reparse checks |
| Duplicate/case/separator collision | Canonical collision set before extraction |
| Archive bomb/missing metadata | Explicit limits plus streaming counters |
| Malformed manifest | Bounded parser and existing validator before code load |
| Signature substitution | Existing canonical signature verification over staged content |
| Unknown/revoked publisher | Existing local trust boundary, fail-closed revocation |
| SDK/dependency confusion | Compatibility/declared-dependency validation; no resolver |
| Privilege/risk widening | Host-assigned identity; unchanged policy/trust/envelope boundaries |
| Lifecycle race/stale ETag | Keyed lock, in-lock condition, journal |
| Idempotency replay/conflict | Provisional actor/node/key reservation, canonical intent fingerprint, and durable operation record |
| Crash during install | Atomic same-volume renames, journal, store marker, recovery |
| Partial activation | Existing unregister/unload cleanup and failed state |
| Malicious trusted code | Explicit confirmation and host-privilege warning |
| Audit secret/path leakage | Narrow audit/projection and sanitized diagnostics |

## Alternatives considered

- A second upload loader, signature validator, or trust store: rejected because ADR-0020 owns this
  boundary.
- Automatic activation after install/update: rejected because it executes third-party in-process
  code without explicit administrator action.
- In-place replacement of enabled plugin: rejected because current registry/load-context behavior
  does not safely promise it. Explicit disable first is deterministic.
- Automatic activation-LKG re-enable after failure: rejected because it executes code without fresh
  confirmation.
- Permanent rejected-archive storage: rejected as unbounded hostile-content retention.
- Validating paths only after extraction: rejected because unsafe writes may already occur.

## Implementation contract for M5

M5 implements only the lifecycle service as an extension of `PluginManager`/`PluginStore` and
the current manifest, signature, trust, registry, and load-context boundaries. It implements this
state model, lifecycle-owned same-volume paths, exact validation sequence and numeric limits,
metadata-only structural inspection, atomic promotion/journal/store marker, generation-role/recovery rules,
per-id locking, ETag/idempotency behavior, transition table, cleanup, and failure behavior.

It does not implement M6/M7, remote sources, deletion, a new signing/trust format, generic
dependency resolution, sandboxing, or force unload.

Later additive public/API contract work is lifecycle state/version/ETag and sanitized operation
projection; `If-Match`, `Idempotency-Key`, and activation-confirmation transport; and a local
publisher-key revocation status interpreted by the existing trust store. No breaking plugin SDK or
public contract change is required.
