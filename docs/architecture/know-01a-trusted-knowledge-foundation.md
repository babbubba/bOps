# KNOW-01A — Trusted Knowledge Packs and provider isolation

Base: AF-TURN-01B `851ef31` (MAF `Microsoft.Agents.AI` pinned `[1.24.0]`, unchanged). Decision: [ADR-0055](adr/README.md#adr-0055)
(Proposed), implementing G2-3, G2-4 and G2-5 of ADR-0054 (Proposed on `docs/arch-03-maf-convergence-decision`). Offline only: no live model, no
MAF upgrade, no subagent, nothing composed by a production host, no configuration key.

## What exists

| Piece | Where | Role |
|---|---|---|
| Pack format v1 | `bOps.Knowledge` (new, Abstractions only, no MAF type) | `bops-pack.json` (id, version, kind `Knowledge`/`Experience`/`Skill`, engine `min`..`max`, publisher, key id, content hash, per-file SHA-256 and size), `bops-pack.sig.json` (RSA-PSS/SHA-256 over the exact manifest bytes), text payload under `skill/…` |
| Verifier | `KnowledgePackVerifier` | Signature first, then manifest, engine range, path policy, closed text-extension list, no script, no link, no extra or missing file, byte-exact hashes, strict UTF-8. Works on its own copy of the bytes. Refusals are results with an audit code |
| Trust | `KnowledgeTrustStore` | Operator-owned public keys, key and publisher revocation, ≥ 2048-bit RSA, a private key in it is a configuration error. Trust is never read from the pack |
| Snapshot and pin | `KnowledgeSnapshotStore`, `KnowledgeSnapshot`, `KnowledgePin` | Content-addressed copy of the verified bytes, partitioned by tenant (`root/tenant key/manifest hash`), re-verified on every reuse and by the bridge, serializable pin for a future resume |
| Provider admission | `KnowledgeProviderRegistry`, `IKnowledgePackSource` | Host registers *instances*; the registry assigns the id, trust (`Trusted`/`Untrusted`), kinds and tenants. Matching is by reference. An untrusted provider never serves a Skill pack |
| Tenant scope and entitlement | `KnowledgeTaskScope`, `ITenantAuthority`, `KnowledgeGrants` | Scope issued only by the host authority (no public constructor, not copyable); visibility only by host grant of exact tenant, pack, version and kind; no wildcard, no shared or global cache; Local Agent is tenant `local` through the same path |
| Admission | `KnowledgeAdmissionService` | Order: provider → grant → read → verify → kind/identity/pin → tenant snapshot. Nothing is read before the grant exists |
| MAF serving | `bOps.AgentFramework`: `VerifiedSnapshotSkills`, `KnowledgeReadBridge.CreateFromSnapshotAsync`, `KnowledgeBridgeAdmission` | The framework's own `AgentFileSkillsSource` (SKILL.md parser, discovery, path/symlink checks) reads the verified snapshot with no script runner and no script extension; the existing 01B bridge pins, authorises, verifies and audits every read. No second skills engine, no new tool |
| Audit | `KnowledgePackAdmissionAuditEvent` (new), `TenantId`/`ProviderId` on `KnowledgeReadAuditEvent`, `TenantId` on `ContextProvenance` | Every attempt, accepted or refused, exactly once; only shape-checked identifiers and the manifest hash bOps computed; never content, path, key or signature |

`ContextGate`, `BopsContextProvider`, `MafGovernedTurn`, `AgentRunner` and the tool surface are unchanged. Served content is still a `Tool` turn with
provenance `Skill`/`Knowledge`/`Experience` (by signed kind), tenant, pack id, version, content hash and the registry's provider id.

## Verification matrix (task section 6)

| # | Verification | Result | Evidence (test) |
|---|---|---|---|
| 1 | Valid, altered, unknown and revoked signature | **PASS** | `PackVerifierTests`: valid; manifest/signature altered; unsigned; unsupported algorithm; unknown key; impostor key under a trusted id; key revoked; publisher revoked (other publisher still verifies) |
| 2 | Resource altered after the pin, also one never read | **PASS** | `SnapshotStoreTests` (hash, delete, add, manifest, link); `SnapshotBridgeTests.AResourceAlteredAfterTheTaskStarted…` (checklist and SKILL.md altered on disk → never served, untouched skill still served, audit without content); tampered stored snapshot refuses the next admission |
| 3 | Path traversal and access outside the snapshot | **PASS** | 12 unsafe manifest paths, unlisted `../` file, case aliases; model-proposed `../` skill/resource → `UnknownSkill`/`UnknownResource`; directory source reports and never enters a junction (found and fixed during this task) |
| 4 | Provider not registered; provider that forges its identity | **PASS** | `ProviderAndScopeTests`, `AdmissionServiceTests`, `SnapshotBridgeTests`: refused before the source is opened (`Opens == 0`), forged id not echoed in the audit, two instances of a type get two ids |
| 5 | Attempt to elevate to System | **PASS** | Injection pack through a real governed MAF turn: instructions and tool list identical on every request, no System turn, the pack's own `[/skill-data]` is neutralised so the frame closes once, no gate rejection. Gate-level System elevation is covered by AF-CONTEXT-01/01B suites (not duplicated) |
| 6 | Two concurrent tenants, different packs and caches | **PASS** | Same pack id and version, different content, 40 admissions (service) and 32 full tasks (bridge): each sees only its content, catalogue, provenance tenant and hash, audit and snapshot |
| 7 | Cross-access to Experience | **PASS** | Granted to A only: B and Local get `NotEntitled` although the provider would serve it, even with A's pin; the source is not read; refusals audited |
| 8 | Widening the tool surface | **PASS** | Tool list per request unchanged; the model has no tenant argument (`Malformed`); `allowed-tools`, oversized license, markup or a framework function name in a description refuse the pack (`SkillInvalid`); architecture tests: no script runner or script extension, no caching source, one `AgentFileSkillsSource` site, `Frontmatter.AllowedTools` read once to refuse, one knowledge function |
| 9 | Compatibility with the AF-TURN-01B bridge | **PASS** | All 15 existing bridge tests and the rest of `bOps.AgentFramework.Tests` pass unchanged (210/210) |
| 10 | Pin serializable, resume | **PASS (pin and re-admission)** / **NOT DETERMINED (task-store resume)** | Pin JSON round trip; re-admission with the stored pin yields the same hashes; a republished pack or another tenant's pin is `PinMismatch`. No `TaskState`/store integration exists yet |

Suites: `bOps.Knowledge.Tests` 92, `bOps.AgentFramework.Tests` 210 (16 new), `bOps.Architecture.Tests` 189 (8 new), `bOps.Audit.Tests` +1.
Test keys are generated in memory per run and never written; no credential, token or PEM literal is committed.

## G2 gates

| Gate | After KNOW-01A |
|---|---|
| G2-1 final context check | Unchanged (01A/01B); knowledge flows run through it with no rejection |
| G2-2 typed provenance | Closed for bOps content and for served pack content, now with tenant; pack-claimed ids and metadata ignored (tested in 01B and here) |
| **G2-3 host-assigned trust** | **Closed for pack sources**: allow-list of registered instances, host-assigned ids, trust, kinds and tenants, unregistered sources refused before invocation. **Not applicable / not built for third-party MAF `AIContextProvider`s**: none is admitted, there is no decorator, and the single bOps-authored provider test still holds |
| **G2-4 no elevation** | **Closed for the pack path**: Tool turn, never System; bOps-owned catalogue template; frame markers neutralised; name, description and frontmatter validated and bounded; `allowed-tools` refused; scripts refused at three layers (verifier, snapshot, framework source); `run_skill_script` still has no entry |
| **G2-5 tenant and cache isolation** | **Closed for the pack path**: per-task provider and source, tenant-bound scope, grants and provider tenants, tenant-partitioned snapshots and pins, no static or shared cache (architecture-tested), concurrency tested at two layers. **Open**: Experience *search* (no store or retrieval API exists; ND-5) |
| G2-6 architecture tests | Extended (`KnowledgePackBoundaryTests`); mutation-checked (a static cache and a signing call both fail the build) |

## Integrations not yet demonstrated (NOT DETERMINED)

- **Runner wiring.** `AgentRunner` does not offer or serve `runtime.knowledge_read` (KNOW-01B); no host composes any of this; tenant authority, registry, trust store location, grants source and the engine version passed to the verifier are all host work.
- **CI and a real symlink on Windows.** Windows and Linux CI have not run (nothing is pushed), and CI stays authoritative. Local evidence: Windows (all non-live suites green except one unrelated, pre-existing flaky test, `VaultStoreTests.ConcurrentSets_ForDifferentProviders_BothSucceed_WithoutLosingAnEntry`, an `UnauthorizedAccessException` race in `VaultStore.SaveAll`; it passes in isolation and is outside this change) and Linux from a `git archive` copy in WSL Ubuntu-24.04, with real symlinks: `bOps.Knowledge.Tests` 92/92, `bOps.AgentFramework.Tests` 210/210, `bOps.Architecture.Tests` 187 passed and 2 platform skips, `bOps.Audit.Tests` 29/29. On Windows the link tests use a junction (a reparse point, like a symlink for these checks), not a symbolic link.
- **Third-party MAF providers.** No decorator exists; the design avoids admitting them rather than wrapping them.
- **Experience search and its mandatory tenant filter.** Only the isolation of whole Experience packs is shown.
- **Task-store resume, snapshot garbage collection, signing tooling, key distribution and rotation, time-limited key validity.**
- **MAF upgrades.** Pins are hashes of what the framework returns (as in 01B), now also checked to *contain* the verified file text; a formatting change in a framework upgrade surfaces as a refused pack. Re-run these suites on upgrade.

## Divergences and limits (recorded, not loosened)

- ADR-0054 and ADR-0055 are Proposed; the operator assigned this task on the AF-TURN-01B base ahead of the plan's step order (the consolidated queue is unchanged).
- The `tenant` is a knowledge-isolation scope only, not the V1.4 multi-tenant model; nothing outside knowledge consumes it.
- Immutability is by verification, not OS permission (S8): a process with host privileges can still rewrite a snapshot file; it is detected before anything is served, not prevented.
- The framework's `load_skill` reply for a file skill carries its own `<available_resources>` / `<available_scripts />` envelope (an empty scripts block). The bOps template wraps it and no script can exist or run; stripping it would need a bridge-side reformat and is left out.
- Revocation applies to new admissions and resumes, not to a task already holding a verified snapshot.
- Revoking a publisher or key is a new trust store the host swaps in; there is no live reload in this task.
