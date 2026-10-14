# ADR-0055 — Trusted Knowledge Packs, provider admission and tenant isolation (KNOW-01A)

Status: **Proposed — 2026-10-12** (implements ADR-0054 G2-3, G2-4 and G2-5; ADR-0054 itself is still Proposed on `docs/arch-03-maf-convergence-decision`)
Date: 2026-10-12
Refines: ADR-0054 §4–§5 · ADR-0053 §3 row 11
Preserves: ADR-0052, the ContextGate of AF-CONTEXT-01 / AF-TURN-01B, invariants I-1…I-10 of ADR-0053.
Base: AF-TURN-01B `851ef31`. Report: [`know-01a-trusted-knowledge-foundation.md`](../know-01a-trusted-knowledge-foundation.md).

## 1. Context

AF-TURN-01B serves skill content to the model through `runtime.knowledge_read`, but only from a bOps-owned in-memory fixture
and an admission that is a host-constructed test object. G2-3 (host-assigned trust), G2-4 (no elevation) and G2-5 (tenant and cache
isolation) remain open for any real source. KNOW-01B will wire the bridge into the runner; this decision fixes what a pack is, who
is believed, and where isolation is enforced first.

## 2. Decision

1. **One OSS pack contract, independent of MAF.** A new assembly `bOps.Knowledge` (Abstractions only, no MAF type) owns the pack
   manifest, signature verification, publisher trust store, content-addressed snapshots, serializable pins, provider admission and
   tenant scope. `bOps.AgentFramework` consumes a verified snapshot and serves it with the framework's own
   `AgentFileSkillsSource` (SKILL.md parsing, discovery, path and symlink checks) and `AgentSkillsProvider`. bOps writes no SKILL.md
   parser and no second skills engine.
2. **Trust comes from the host, never from the pack.** A pack is `bops-pack.json` (manifest), `bops-pack.sig.json` (detached
   signature) and payload files. The signature is RSA-PSS/SHA-256 over the exact manifest bytes (the same standard primitive as the
   plugin signatures of ADR-0020; keys of at least 2048 bits). The verifying public key is looked up in the operator-owned
   `KnowledgeTrustStore` by `(publisher, keyId)`; a revoked key or publisher, an unknown key and a bad signature are refused. The
   manifest commits to every payload file by SHA-256, so the manifest hash is the content identity. Private keys exist only in
   publisher tooling and tests; no runtime type loads one.
3. **A pack is data, never code or authority.** Allowed payload extensions are a closed list of text formats; any other file, any
   `scripts` path segment, any file not in the manifest, a missing file, a hash mismatch, a link, a reparse point, an unsafe path or
   non-UTF-8 text rejects the whole pack. Frontmatter `allowed-tools` is never a grant: a skill that declares one is rejected.
   A valid signature proves who published the pack, not that its text is safe; served content stays an attributed, delimited
   `Tool` turn that can never be `System`.
   > **Refined by [ADR-0057](0057-knowledge-discovery-index.md) (§G.2):** served content stays a `Tool` turn; the pre-read index is the
   > single exception, a `User`-role data turn governed by S5 (external reference data). The prohibition on `System` is unchanged.
4. **Immutable snapshot and pin.** After verification bOps writes the bytes it already holds in memory to a content-addressed
   directory partitioned by tenant (`<root>/<tenant key>/<manifest hash>`), re-verifies the directory, and serves only from it. The
   source is never read again. A `KnowledgePin` (tenant, id, version, kind, publisher, key id, content hash, per-file hashes) is
   serializable, so a resumed task can re-acquire exactly that content or fail. The bridge additionally hashes everything the
   framework returns and compares it with the hash taken at admission. Alteration of a snapshot file, read or not yet read, is
   detected before the model sees it (fail closed, audited). Immutability is by verification, not by operating-system ACLs: a
   process with the host's own privileges can still delete or replace files (S8).
5. **Provider admission is a registry of instances.** The only thing a task may use as a source is an `IKnowledgePackSource`
   instance the host registered with `KnowledgeProviderRegistry`. The registry assigns the provider id; trust (`Trusted` or
   `Untrusted`), the allowed pack kinds and the allowed tenants are properties of the registration, looked up by object reference.
   A name, type, interface member or metadata a source declares about itself is never read. An `Untrusted` provider can serve
   `Knowledge` and `Experience` packs but not `Skill` packs. A source returns bytes only: it never composes, injects or runs
   anything in the MAF context pipeline, and the single bOps-authored `AIContextProvider` stays the only one.
6. **Tenant scope is host authority.** `KnowledgeTaskScope` is created only from an `ITenantAuthority` (or `ForLocalAgent`), has no
   public constructor and cannot be copied with `with`. The model's `runtime.knowledge_read` arguments contain no tenant and unknown
   arguments are rejected. Visibility comes from host `KnowledgeGrants` of exact `(tenant, id, version, kind)`, not from pack
   metadata; there is no wildcard tenant. Snapshots, pins and the provider tenant check are partitioned by tenant; there is no
   process-wide or shared cache and no fallback when a tenant has no entry. The single-tenant Local Agent uses tenant `local` through
   the same path, so it cannot become a way to read another tenant's data.
7. **Audit.** Every admission attempt, accepted or refused, writes a `KnowledgePackAdmissionAuditEvent` (code, tenant, provider id,
   identifiers that passed a strict shape check, manifest hash); every read event gains `TenantId` and `ProviderId`; served turns
   carry `TenantId` in `ContextProvenance` and `Knowledge`/`Experience`/`Skill` origin follows the signed pack kind. Never content,
   paths outside the pack, keys or signatures. All additions to `bOps.Abstractions` are optional members or new types.

## 3. Alternatives considered

- **Pack code in `bOps.AgentFramework`.** Rejected: ADR-0053 §4 separates the Knowledge engine (4.5) from the MAF adapter (4.2); trust
  and snapshots do not need MAF and should be testable without it.
- **A SKILL.md parser in bOps** so that snapshots can stay in memory. Rejected (stop condition of the task): a second skills
  engine. The framework parser reads the verified on-disk snapshot, and hash checks on its output close the remaining window.
- **Wrap arbitrary third-party `AIContextProvider`s with a decorator (ADR-0054 G2-3 wording).** Deferred: admitting executable
  providers into the MAF cycle is a larger attack surface than reading bytes from a restricted source. No such provider is admitted.
- **Trust levels derived from the pack's publisher string, or a `tenant` field in the manifest.** Rejected: self-declared.
- **Ed25519.** Not offered by the .NET base library on every supported platform; RSA-PSS is already the platform standard here.

## 4. Consequences

- G2-3, G2-4 and G2-5 are closed for the pack source path; they remain open for any third-party MAF provider (none is admitted) and for
  Experience search (no store exists yet). Nothing is composed by a production host; there is still no configuration key.
- Compatibility with the engine is declared by `engine.min`/`engine.max` in the manifest and checked against a version the host passes
  to the verifier; KNOW-01B supplies it.
- The `tenant` concept is a knowledge-isolation scope only. It is not the multi-tenant model of V1.4 and adds no tenant to
  `TaskState`, policy or the runtime.
- Revocation takes effect for new admissions and resumes, not for a task already holding a verified snapshot. *(Superseded by [ADR-0056](0056-governed-knowledge-read-and-durable-resume.md) §2.4: every read now asks the current authority again.)*
- Not decided here: publisher tooling to sign packs, pack distribution, snapshot garbage collection, Experience write path and search.
