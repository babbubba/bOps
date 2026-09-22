# bOps consolidated roadmap — V0.1 through V2.0

Status: **active and authoritative for roadmap scope, sequencing and delivery gates**
Consolidated: 2026-09-16
Current implementation milestone: **V1.3** (V1.2-A through V1.2-M are complete and `v1.2.0-preview.8` is released as a pre-release)
Next implementation batch: **V1.3-F — filesystem troubleshooting** (V1.3-A through V1.3-E are implemented); V1.3 then completes the local senior-operator diagnostic surface through V1.3-L before V1.3-M entitlement/plugin lifecycle

## 1. Authority and precedence

This is the single active bOps development plan. It consolidates the original roadmap, the
V0.9.1–V2.0 evolutionary plan, the 2026-09-16 consolidation, the pending-specification register,
the repository's implemented state, and the operator decisions recorded on 2026-09-16.

When sources disagree, use this order from highest to lowest authority:

1. an accepted ADR for its subject;
2. `agentic/00-*.md` through `agentic/07-*.md`;
3. this consolidated roadmap;
4. the active task file for the current batch, which may add execution detail but may not widen
   scope or weaken a gate;
5. code-adjacent documentation.

Everything under `agentic/obsolete/` is historical and non-normative. Coding agents must not read
or implement from it unless an active task explicitly requests historical research.

## 2. Product direction and invariants

bOps is a .NET 10 agent runtime that operates Windows and Linux machines through typed,
declarative tools, policy, approval, post-action verification and complete audit. The model
proposes; the runtime decides and executes.

The roadmap never permits:

- generic shell, command, process-start or arbitrary SQL execution;
- model-controlled policy, approval, entitlement or verification;
- unverified success for a side effect;
- secrets in prompts, logs, telemetry, audit payloads or browser persistence;
- package or product identities in the generic core;
- remote control that bypasses policy and execution on the managed node;
- commercial source, playbooks or knowledge in the public `bOps` repository.

All existing architecture and security rules remain binding, including package isolation being
dependency isolation rather than a security sandbox.

## 3. Verified baseline on 2026-09-16

| Version | State | Evidence or remaining gate |
|---|---|---|
| V0.1–V0.9 | Complete | Runtime, agent loop, policy, verification, cross-platform packages, persistence, providers, API and Angular UI are present. |
| V0.9.1 | Complete | Repository integrity, licensing and contribution readiness are implemented. |
| V0.10 | Complete | Dynamic plugin loader, manifest, trust boundary and CLI lifecycle are implemented. |
| V0.11 | Complete | Previously promised operational tools and Service packages are implemented. Closed milestones are not reopened. |
| V1.0 implementation | Complete | Authentication/authorization, secret references, bounded execution, plugin provenance, audit verification and release workflow are present. |
| V1.0 formal release | Closed via `v1.1.0-preview.2` | The release workflow was proven on tag `v1.1.0-preview.2` (`c81ffab`, run `35383901325`) with verified archives, package, SBOMs, checksums and attestations. No stable `v1.0.x` tag exists or will be created; the first stable tag is decided at V1.1 GA. |
| V1.1 | Complete (preview tag `v1.1.0-preview.2`) | V1.1-A through V1.1-H are complete; the H gate is green on Windows/Linux CI (runs `35365098294`, `35372748588`). The preview artifacts are workflow artifacts only, not a GitHub Release or NuGet publication. |
| V1.2 | Complete (preview) | Sub-tasks A–M are implemented, each with its pull request green on Windows and Linux CI: contracts, authority reduction, the orchestrator, budgets, durable state, separation of duties, audit provenance, CLI, API, dashboard view and documentation. M (integration and release gate) is closed: Release build with zero warnings, the non-live suite, the Angular build and tests, the SDK pack, an end-to-end objective with real tools and a crash-and-resume scenario, and a permanent snapshot of the frozen 1.0 surface. Tag `v1.2.0-preview.8` (`5cd9046`) passed the release workflow on Windows and Linux (run `35516050493`) and is published as a GitHub pre-release, not a NuGet publication or a stable release. |
| V1.3 | In progress | V1.3-A through V1.3-E are implemented: system events, Docker management, advanced process diagnostics, sockets/routes/network diagnostics and storage diagnostics. Ordered public batches A–L complete the senior-operator local diagnostic surface; M adds the neutral entitlement boundary and local plugin lifecycle. |
| V1.4–V2.0 | Not started | They remain gated by completion of all preceding milestones. |

The state above describes the repository, not a production endorsement. A milestone is not
formally released until its release gate is satisfied.

## 4. Repository topology and ownership

The target topology contains three repositories:

| Repository | Visibility | Responsibility |
|---|---|---|
| `bOps` | Public, Apache-2.0 | Standalone runtime, SDK, generic packages, providers, local API/UI, Managed Agent host, public protocol/entitlement contracts and demonstration-only Skills. |
| `bOps.Commercial` | Private | Official Community Coordinator (including proprietary free components), commercial Skills and knowledge, entitlement providers, Account Portal, on-prem/SaaS Control Plane and Operations Portal. |
| `bOps.Workspace` | Private | Pins both repositories as Git submodules and carries cross-repository bootstrap, coordination plans and validation orchestration; it contains no duplicated product source. |

`bOps.Workspace` allows an authorized agent to clone one private repository recursively and
work on both codebases. It does not blur their licenses, history, CI, release artifacts or access
controls. Changes remain committed and reviewed in the owning submodule first; the root then pins
the approved commits. The approved GitHub owner is `babbubba`; the submodule paths are
`repos/bOps` and `repos/bOps.Commercial`, both on `main`.

The topology bootstrap is an administrative workstream. It may run while V1.1 is active, but it
must be complete before V1.3 starts private implementation. See
`agentic/_tasks/2026-09-16-repository-topology-bootstrap.md`.

## 5. Effort scale and task contract

Every executable task declares one recommended model effort:

- **basso** — bounded documentation or mechanical alignment with no design choice;
- **medio** — contained multi-file work following an established design;
- **alto** — cross-layer or cross-platform work with meaningful failure modes;
- **molto alto** — public contracts, security boundaries, cryptography, destructive operations,
  distributed systems or milestone integration.

An agent starts only the first task whose dependencies and operator gates are satisfied. It updates
the checklist with evidence as work completes and does not pre-check future work.

## 6. Delivery chain

```text
V1.0 formal release gate (independent operator-authorized release workstream)

V1.1-A Skill SDK completion
  -> V1.1-B system inventory
  -> V1.1-C bounded filesystem inventory
  -> V1.1-D governed recursive deletion
  -> V1.1-E Web package
  -> V1.1-F read-only plugin catalog UI
  -> V1.1-G writable secure Settings
  -> V1.1-H integration and release gate
  -> V1.2 multi-agent
  -> V1.3-A bounded cross-platform system events
  -> V1.3-B Docker image/build/volume management
  -> V1.3-C advanced process diagnostics
  -> V1.3-D sockets/routes/network diagnostics
  -> V1.3-E storage topology/latency/health
  -> V1.3-F filesystem troubleshooting helpers
  -> V1.3-G service configuration + scheduler
  -> V1.3-H identity + time + reboot state
  -> V1.3-I firewall diagnostics
  -> V1.3-J TLS + certificate diagnostics
  -> V1.3-K updates + crashes + drivers/modules
  -> V1.3-L senior-operator diagnostic integration gate
  -> V1.3-M entitlement + local plugin lifecycle UI
  -> V1.4-A secure Managed Agent / Coordinator protocol
  -> V1.4-B private Coordinator core + tenant/RBAC/enrollment
  -> V1.4-C private PostgreSQL+pgvector persistence baseline
  -> V1.4-D Semantic Knowledge Store + Semantic Operational Memory
  -> V1.4-E official packaging + signed update/pack distribution
  -> V1.5 PostgreSQL read-only
  -> V1.6 PostgreSQL remediation
  -> V1.7 SQL Server read-only
  -> V1.8 SQL Server remediation
  -> V1.9 enterprise Portal
  -> V2.0 enterprise GA
  -> post-V2.0 bilingual public documentation
```

Repository-topology bootstrap is parallel administrative work, but it gates V1.3. No later
implementation is pulled forward merely because its repository exists.

## 7. V1.0 formal release gate

**Goal.** Prove the existing V1.0 implementation through the real release path.

**Required outcome.** From the approved V1.0 commit, create the operator-selected release-candidate
tag, execute `.github/workflows/release.yml`, and verify signed/reproducible Windows and Linux
artifacts, NuGet packages, SBOMs, checksums and attestations. Tag creation and push always require
explicit operator authorization at execution time.

**Task.** `agentic/_tasks/2026-09-16-release-v1.0-formal-gate.md` — effort **medio**.

## 8. V1.1 — complete the public operational platform

V1.1 is one SemVer milestone delivered in ordered batches. The additional capabilities are not
retroactively assigned to V0.11. Every batch must leave the build and non-live tests green.

### V1.1-A — Skill/Capability SDK completion

Complete the work deliberately deferred by ADR-0023 and ADR-0024:

- follow-up ADR for `ICapability`, `ISkillProvider`, activation and restricted `IToolInvoker`;
- same-package, read-only evidence collection through the existing governed tool path;
- Skill provider registration through package trust and activation;
- contextual policy over Skill, capability, target, environment and blast radius;
- explicit persistence/resume semantics for partial Skill runs;
- end-to-end OSS sample Skill producing evidence, findings, an immutable plan, execution,
  verification, report and audit.

V1.2 multi-agent behavior and commercial playbooks remain excluded.

**Task.** `agentic/_tasks/2026-09-16-v1.1-a-skill-sdk-completion.md` — effort **molto alto**.

**Current state.** Complete. The real signed sample covers preparation through verified execution;
GitHub Actions run `35178863698` passed on Windows and Linux, including .NET and Angular checks.

### V1.1-B — system and hardware inventory

Add cross-platform, read-only operational visibility without creating a new package family:

- `system.apps`: bounded installed-software inventory with name, version, publisher/source and
  platform-specific identity where available;
- `system.devices`: bounded hardware/device inventory with stable cross-platform fields and
  explicit unknown/unavailable values;
- an additive hardware model field in `system.info`, preserving backward compatibility;
- Windows and Linux implementations in the existing System package pattern, conformance tests and
  real-platform fixtures.

No driver-update action or dedicated “check latest driver” tool is added. Later Skills combine
`system.devices` evidence with `web.search` when freshness research is requested.

**Task.** `agentic/_tasks/2026-09-16-v1.1-b-system-inventory.md` — effort **alto**.

**Current state.** Complete. Bounded native Windows/Linux collectors, shared conformance and
explicit source-completeness semantics are implemented; GitHub Actions run `35180430313` passed on
Windows and Linux, including .NET, Angular and Linux SBOM checks.

### V1.1-C — bounded filesystem size and inventory

Add `fs.size` as a Risk `Read` capability. It must support files and directories, cancellation,
bounded recursion, deterministic ordering, depth/entry/time/output budgets, file and directory
counts, total bytes and a configurable top-N view. Symbolic links are never followed implicitly.

The same internal enumerator produces an exact immutable inventory manifest when a later destructive
workflow needs one. The full manifest is stored outside the model context and outside ordinary audit
payloads. Tool output contains only bounded summaries plus an opaque manifest reference and hash.

**Task.** `agentic/_tasks/2026-09-16-v1.1-c-filesystem-inventory.md` — effort **alto**.

**Current state.** Complete. Bounded streaming summaries, host-owned contextual scope and expiring
SQLite exact manifests are implemented; GitHub Actions run `35200996310` passed on Windows and
Linux, including .NET, Angular, Linux permission/symlink coverage and SBOM checks.

### V1.1-D — governed permanent recursive and batch deletion

Add a distinct `fs.delete_tree` tool rather than silently widening the existing single-file
`fs.delete` semantics. It is Risk `High`, permanent, and requires approval for every execution.

The approved object is an immutable `DeletionManifest` containing the exact canonical target set,
entry identity metadata, total size, counts, deterministic order and a canonical hash. The operator
sees counts, total bytes, roots, warnings and a paginated/filterable exact list. Large sets such as
5,000–10,000 files must remain usable: neither the browser nor the LLM receives the entire list in
one response. The server retains the complete manifest, exposes cursor-based pages and an optional
download, and binds approval to the full hash.

Immediately before deletion the executor re-resolves paths under rule S11 and rejects stale or
changed manifests. It deletes children before parents, reports partial completion precisely, never
claims atomicity across files, and verifies every approved entry after execution. Audit stores the
manifest identity/hash/counts/bytes and outcome, not thousands of path strings. Enumeration and
deletion have configurable hard ceilings; exceeding one fails closed before approval and performs
no deletion. The task's ADR selects safe defaults and proves at least a 10,000-file scenario.

**Task.** `agentic/_tasks/2026-09-16-v1.1-d-governed-recursive-delete.md` — effort **molto alto**.

**Current state.** Complete. `fs.delete_tree.prepare`, `fs.delete_tree` and `fs.delete_tree.verify`
are implemented with a complete hash-bound manifest, mandatory approval, children-first execution
and per-entry verification; GitHub Actions run `35230568622` passed on Windows and Linux, including
the Linux container attack-test suite (symlink, permissions, case sensitivity, long path,
concurrent mutation and a 10,000-file bounded scenario).

### V1.1-E — Web package with SearXNG search and safe fetch

Create `bOps.Packages.Web` with two Risk `Read` tools:

- `web.search`: query a configured SearXNG instance through its JSON search API. No API key is
  required or accepted by the tool contract. The package never falls back to scraping a public
  search page. If JSON output is disabled by the chosen instance, discovery fails with an
  actionable diagnostic.
- `web.fetch`: fetch an HTTP/HTTPS resource with strict SSRF, redirect, DNS-rebinding, protocol,
  timeout, size, content-type and decoding controls. It accepts no arbitrary headers, credentials,
  cookies or request bodies.

Both outputs are untrusted external data under S5, bounded before audit/model insertion and fully
attributed with final URL and retrieval metadata. The SearXNG base endpoint is operator-owned
configuration, not a hard-coded public instance.

Reference: <https://docs.searxng.org/dev/search_api>.

**Task.** `agentic/_tasks/2026-09-16-v1.1-e-web-capabilities.md` — effort **molto alto**.

**Current state.** Complete. `web.search` and `web.fetch` are implemented with connect-time SSRF/
DNS-rebinding validation, a bounded revalidated redirect loop, decompression-bomb-safe reading and
an allowlisted content-type/charset boundary (ADR-0028); GitHub Actions run `35245357524` passed on
Windows and Linux after the package's local test server was moved from `HttpListener` to Kestrel to
fix unreliable request dispatch on Linux.

### V1.1-F — read-only plugin catalog in the local UI

Expose authenticated read-only API endpoints and an Angular page for installed/active plugins,
versions, publisher/signature/trust state, activation state, declared capabilities, effective risk
ceiling, compatibility and sanitized load errors. The API projects PluginManager state into stable
DTOs and never exposes filesystem internals, trust-store secrets or exception stacks.

Enable, disable and upload are explicitly excluded from V1.1-F and scheduled in the public V1.3
companion track after a mutating lifecycle ADR.

**Task.** `agentic/_tasks/2026-09-16-v1.1-f-plugin-catalog-ui.md` — effort **medio** (revised to
**alto** during implementation).

**Current state.** Complete. `GET /api/plugins`/`GET /api/plugins/{id}` project `PluginManager`
state into a narrow DTO (`enabled`/`loaded`/`compatible`/declared-vs-effective risk kept distinct,
never a raw `InstallPath` or exception stack) behind the existing viewer role; the Angular Plugins
page shows the same fields with no enable/disable/upload control. Implementation also closed two
gaps found along the way, neither of which needed an ADR (no `bOps.Abstractions`, risk-model or
audit-schema change): `bOps.Api` had never activated plugins at all (only `bOps.Cli` did) and now
does, the same composition as the CLI; `PluginManager.LoadAllEnabled` previously let one plugin's
activation failure crash the entire host at start-up and now isolates each failure instead.

### V1.1-G — writable Settings with encrypted local vault

Make provider selection and provider API-key insert/update persistent from the authenticated local
UI while preserving CLI environment/user-secret compatibility.

Secrets are stored in a versioned encrypted local vault. The master key is supplied externally to
the host through an environment/service secret and is never stored beside the vault. There is no
plaintext fallback. The implementation uses a standard authenticated-encryption primitive through
.NET cryptography, random nonces, atomic replacement, restrictive permissions, corruption detection,
schema migration and an explicit rotation/recovery procedure. Cryptographic format and concurrency
semantics require an ADR before code.

The API never returns the full secret. It returns only presence, update metadata and the required
mask `first six characters...last four characters`, stored as non-secret display metadata when the
secret is written so a write-only provider never needs to reveal plaintext. Short inputs use a
non-overlapping safe mask defined by the ADR. All mutations require the administrator role and are
audited without values.

Provider selection is stored separately as validated non-secret configuration and updated
atomically. Startup defines deterministic precedence among explicit environment overrides, the
encrypted vault and legacy development user-secrets.

**Task.** `agentic/_tasks/2026-09-16-v1.1-g-secure-settings.md` — effort **molto alto**.

**Current state.** Complete. ADR-0029 (AES-256-GCM, HKDF-SHA256 master-key derivation,
`expectedVersion` optimistic concurrency, fail-closed startup, CLI-only `bops vault rotate-key`).
`bOps.Runtime` gained `VaultStore`/`VaultCipher`/`VaultSecretProvider`/`SecretMask` (provider API
keys) and `SettingsStore`/`ProviderProfile` (non-secret endpoint/model/tool-calling profile and
active-provider selection, kept separate from the vault). `bOps.Api` gained a new `administrator`
role/policy and `GET/PUT/DELETE /api/settings/*`, opt-in only when `Vault:MasterKeySecret` is
configured — absent it, the key-management surface does not exist and every existing CLI/
environment deployment is unaffected. Scope grew once during implementation, by explicit operator
direction: "provider selection" became full UI-managed provider configuration (endpoint, model,
tool-calling support, API key), not just an id toggle plus key — see D-025 for why the narrower
reading was insufficient (every provider needs its own `BaseUrl`, with no package-level default) and
what was deliberately deferred (`ExtraParameters` is persisted but not yet wired into
`ChatModelOptions`; no provider needs it today). Verified end-to-end against a real running
`bOps.Api` and Angular dev server, not only the test suite.

### V1.1-H — integration and release gate

Run the full cross-platform, API, Angular, architecture, security, documentation and packaging
matrix after all V1.1 batches. Verify that the new public surface remains SemVer-compatible, that
the sample Skill and every new tool work through the governed path, and that README, CHANGELOG,
HANDOFF, OpenAPI, SDK docs and task evidence agree.

**Task.** `agentic/_tasks/2026-09-16-v1.1-h-integration-release.md` — effort **alto**.

## 9. V1.2 — multi-agent orchestration in one process

Introduce logical Discovery, Diagnostic, Remediation and independent Verification agents without
microservices or implicit privilege inheritance. An ADR defines agent identity, delegation,
reduced privilege context, budgets, timeouts, cancellation and parent/child persistence. Execution
is sequential by default. A sub-agent cannot increase capability, target or risk and cannot approve
its own action. Evidence provenance and the complete delegation chain are audited.

**Design (ADR-0030, decisions D-026).** A fixed, deterministic pipeline — Discovery, Diagnostic, human
approval of the plan hash, Remediation, independent Verification. The model reasons only in Discovery
and Diagnostic; Remediation and Verification make no model call. A child's authority envelope is the
intersection of the parent's, the role profile's and the request's, and an empty intersection is a
denial. Approvals are human only. Only side-effecting steps are journaled (intent before, outcome
after); an ambiguous step is reconciled by its declared verification or fails closed with no automatic
retry. Surfaces: runtime, CLI, API and UI.

**Definition of Done.** One objective is delegated, diagnosed, planned, authorized, executed and
independently verified by distinct roles with deterministic tests, resumability and privilege
isolation.

**Task.** `agentic/_tasks/2026-09-16-v1.2-multi-agent.md` — effort **molto alto** for planning and the
security-boundary sub-tasks; split into sub-tasks A–M with individual efforts (basso to molto alto).

## 10. V1.3 — senior-operator local diagnostics and entitlement/plugin lifecycle

V1.3 is completed locally before remote-node work: the node should already expose the typed,
bounded evidence expected from an experienced Windows/Linux administrator. Closed milestones are
not reopened.

### V1.3-A — system events
Windows Event Log + Linux journald through one bounded read-only `system.events`.
**Status.** Implemented (ADR-0032, D-028); the task file holds the evidence.
**Task.** `agentic/_tasks/2026-09-21-v1.3-a-system-events.md` — **alto**.

### V1.3-B — Docker management
Preserve existing container lifecycle and add governed image/build/volume operations.
**Status.** Implemented (ADR-0033, D-029); the task file holds the evidence.
**Task.** `agentic/_tasks/2026-09-21-v1.3-b-docker-management.md` — **molto alto**.

### V1.3-C — process diagnostics
Additive process inspection plus metrics/tree/modules; no environment dump or process start.
**Status.** Implemented (ADR-0034, D-030); the task file holds the evidence.
**Task.** `agentic/_tasks/2026-09-21-v1.3-c-process-diagnostics.md` — **medio**.

### V1.3-D — network diagnostics
PID-owned sockets, complete routes, neighbors, interface stats, DNS query, traceroute, NTP probe.
**Status.** Implemented (ADR-0035, D-031); the task file holds the evidence.
**Task.** `agentic/_tasks/2026-09-21-v1.3-d-network-diagnostics.md` — **medio**.

### V1.3-E — storage diagnostics
Disks, partitions, mounts, IOPS/latency/queue/utilization and health/SMART evidence.
**Status.** Implemented locally; the task file holds validation and CI evidence.
**Task.** `agentic/_tasks/2026-09-21-v1.3-e-storage-diagnostics.md` — **medio**.

### V1.3-F — filesystem troubleshooting
grep/tail/permissions/locks plus verified single-file copy and mkdir under existing path policy.
**Task.** `agentic/_tasks/2026-09-21-v1.3-f-filesystem-troubleshooting.md` — **medio**.

### V1.3-G — service configuration and scheduler
Service config/dependencies/enable-state plus Task Scheduler/systemd timers/cron diagnostics.
**Task.** `agentic/_tasks/2026-09-21-v1.3-g-service-scheduler.md` — **medio**.

### V1.3-H — identity, time and reboot state
Local principals/groups/sessions, system time/sync state and pending-reboot evidence.
**Task.** `agentic/_tasks/2026-09-21-v1.3-h-identity-time-reboot.md` — **medio**.

### V1.3-I — firewall diagnostics
Read-only host firewall status and normalized rules; mutation deferred.
**Task.** `agentic/_tasks/2026-09-21-v1.3-i-firewall-diagnostics.md` — **medio**.

### V1.3-J — TLS and certificates
Handshake-only TLS probe and local X.509 list/inspect; no private-key export or install/remove.
**Task.** `agentic/_tasks/2026-09-21-v1.3-j-tls-certificates.md` — **medio**.

### V1.3-K — updates, crashes and drivers/modules
Read-only pending updates/history, crash metadata and loaded drivers/modules.
**Task.** `agentic/_tasks/2026-09-21-v1.3-k-os-maintenance-crash-drivers.md` — **medio**.

### V1.3-L — senior-operator integration gate
End-to-end troubleshooting scenarios and the stable OSS evidence baseline future commercial Skills
consume.
**Task.** `agentic/_tasks/2026-09-21-v1.3-l-ops-diagnostic-integration.md` — **medio**.

### V1.3-M — entitlement and local plugin lifecycle
The previously planned neutral entitlement boundary and administrator-safe plugin lifecycle follows
the operational-completeness gate. Commercial identities/providers remain private.
**Public task.** `agentic/_tasks/2026-09-16-v1.3-oss-entitlement-plugin-lifecycle.md` — **molto alto**.

## 11. V1.4 — Managed Agent, Community Coordinator and secure node transport

V1.4 adds a third runtime profile without degrading the existing standalone OSS product:

- **Standalone OSS** keeps the current local model/runtime/tools/policy path and requires no bSoft
  account or Community entitlement.
- **Managed Agent** is the public node-side host. It needs no LLM, commercial Skill knowledge,
  provider key or direct Internet access; it executes only typed capabilities under node-local
  entitlement/lease, policy, approval, verification, SQLite state and authoritative local audit.
- **Official Coordinator** is private, may contain proprietary free and commercial components, and
  owns reasoning, logical-agent orchestration, Skills/knowledge, fleet, approvals, entitlement,
  reporting and central persistence. It never executes operational tools directly: every real
  action, including actions on its own host, crosses the enrolled Agent protocol.

The public repository owns the Managed Agent host/profile, minimal versioned protocol, outbound
Agent-to-Coordinator connector, node/enrollment identities, delegated Node Lease contract,
audit-synchronization contract and native Windows Service/systemd packaging. Mutual authentication,
explicit registration, rotation, revocation, freshness, replay/downgrade protection, bounded
reconnect/buffering and protocol negotiation are mandatory. No protocol field carries a raw
command, shell, script or arbitrary SQL. The node independently checks Coordinator identity,
signature, target, freshness/replay, delegated lease, local entitlement/policy, approval hash,
capability risk/prerequisites and post-action verification; it writes audit locally before
idempotent synchronization.

The private repository owns the Community Coordinator, enrollment authority, Account integration,
Control Plane storage/identity/RBAC, Coordinator-side providers/Skills, PostgreSQL plus pgvector,
Operations Portal, entitlement refresh and Coordinator-mediated update distribution. V1.4 is split
into ordered private/public companion batches: **A** public Managed Agent/protocol, **B** private
Coordinator core and tenant/RBAC/enrollment, **C** private PostgreSQL+pgvector persistence baseline,
**D** Semantic Knowledge Store + Semantic Operational Memory + versioned Knowledge/Experience Pack
lifecycle, and **E** official packaging plus signed application/pack distribution. V1.4-D is the
first point at which semantic memory may be implemented because it requires the authenticated
Coordinator and the V1.4-C persistence baseline. Community is
data-driven but initially grants one active Coordinator installation, at most three enrolled
`Remote` nodes, and one Coordinator-assigned `Local` Agent excluded from that remote count. An Agent
cannot self-assert `Local`; node removal/revocation immediately releases a remote slot. Agents
receive Coordinator-signed leases that cannot amplify the vendor-signed entitlement.

Coordinator packaging and update delivery require their own ADRs. Aspire remains dev/test only.
The first official distribution evaluates a native bootstrapper managing Coordinator containers or
native services plus PostgreSQL/pgvector on Linux x64 and Windows 10/11 x64; it must not silently
install a container runtime. Agent updates are approved by an administrator, downloaded and
verified by the Coordinator, delivered over the existing secure channel, reverified by the Agent
and support staged rollback. Air-gapped commercial entitlement and update bundles remain private.

**Definition of Done.** A registered node processes a typed objective under stricter local policy,
survives Internet-denied/offline/retry/reconciliation scenarios and synchronizes tenant-safe audit
without opening an inbound administrative execution surface. Community enforcement proves 0→3
remote enrollments, rejects the fourth, permits one authenticated Local Agent in addition, rejects
pseudo-local/cross-account enrollment and reuses a slot immediately after remote revocation. The
Coordinator has no dependency path that permits direct operational execution.

**Public task.** `agentic/_tasks/2026-09-16-v1.4-node-control-plane-protocol.md` — effort
**molto alto**. Private sub-batches, created only after their gates close, cover entitlement/account
activation, Coordinator core, enrollment/node limits, PostgreSQL/pgvector + Operations Portal,
semantic knowledge/operational memory, and official packaging/update distribution. The semantic
sub-batch is specified by `agentic/_plans/2026-09-21-v1.4-d-semantic-knowledge-operational-memory.md`
and its normative pack/authoring documents under `docs/knowledge/`. V1.4-D must close **before
V1.5 PostgreSQL read-only starts**, so the first commercial Skill consumes semantic knowledge from
day one rather than adding it retrospectively. Executable private tasks belong only in
`bOps.Commercial`; cross-repository pinning and compatibility evidence belong only in
`bOps.Workspace`.

## 12. V1.5–V1.9 — private commercial delivery

These milestones are specified here for cross-repository sequencing, but executable task files are
created in `bOps.Commercial`, not in the public repository. Commercial Skill reasoning and
proprietary playbooks run on the Coordinator; public/generic typed database capabilities execute on
the Managed Agent near the target. Database credentials remain node-side where possible and never
enter model prompts. Every mutation remains locally policy-governed, approved and verified; no
arbitrary SQL protocol or tool is introduced.

### V1.5 — PostgreSQL DBA read-only

Recommended task effort in `bOps.Commercial`: **molto alto**.

Implement version-aware, allowlisted and parameterized evidence collection for discovery/health,
performance, locks/transactions, vacuum/statistics, indexes, capacity, backup/PITR, WAL/replication,
configuration and security. Missing permission yields `UNKNOWN_DUE_TO_PERMISSION`, never `OK`.
`EXPLAIN ANALYZE` is a separate capability because it executes workload. Validate against real
supported PostgreSQL versions and least-privilege accounts.

**DoD.** A production-style read-only analysis produces correlated evidence, root cause, ranked
recommendations and risk without modifying the database.

### V1.6 — PostgreSQL governed remediation

Recommended task effort in `bOps.Commercial`: **molto alto**.

Add capabilities incrementally for cancel/terminate, VACUUM, ANALYZE, REINDEX, index changes and
configuration changes. Each declares preconditions, lock/blast radius, timeout, maintenance window,
effects, rollback and post-metrics. Dangerous destructive SQL is never exposed as a general
capability. Test real targets, partial failure, stale approval, rollback and inconclusive/refuted
verification.

**DoD.** Each supported change requires evidence, entitlement, immutable approval, execution,
specific verification and honest partial-failure reporting.

### V1.7 — SQL Server DBA read-only

Recommended task effort in `bOps.Commercial`: **molto alto**.

Implement version/edition-aware, allowlisted diagnostics for performance, Query Store, waits,
blocking/deadlock, I/O/memory, TempDB, indexes/statistics, capacity, backup/restore, integrity,
security and configuration without forcing PostgreSQL abstractions onto SQL Server. Use real target
and permission matrices; absence of a recent check is `INTEGRITY_NOT_VERIFIED`, not corruption.

**DoD.** An analysis produces evidence, root cause and an actionable report without target changes.

### V1.8 — SQL Server governed remediation

Recommended task effort in `bOps.Commercial`: **molto alto**.

Add separate capabilities for cancel/kill, statistics, index maintenance, plan force/unforce,
integrity checks, backup and configuration. Account for edition, HA, log growth, workload and
maintenance windows. No unrestricted dynamic SQL, sysadmin shortcut or Critical autonomous action.

**DoD.** Every supported remediation crosses evidence, immutable plan, policy, entitlement,
approval, execution, independent verification, post-metrics and audit.

### V1.9 — enterprise Portal

Recommended task effort in `bOps.Commercial`: **molto alto**.

Build the private multi-tenant Operations Portal as a client of the Control Plane, not a second
authority and not a duplicate of the Community Operations Portal foundation delivered in V1.4.
Cover Agents, Tasks, Targets, Skills, Policies, Approvals, Audit, Reports, Licenses and Settings.
Authorization remains server-side; approvals display plan/hash/effects/rollback/verification;
browser persistent storage contains no secrets or license tokens. The separately hosted bOps
Account Portal remains the bounded context for account, activation/transfer, entitlement issuance/
refresh and official downloads and receives no operational payload by default. Test every role,
tenant isolation, session security, stale approvals, accessibility, large audit streams and
reconnect/idempotency.

**DoD.** Administrator, Operator, Approver and Viewer complete only their permitted workflows.

## 13. V2.0 — enterprise GA

Both product repositories complete threat-model delta, independent security review, capacity and
soak tests, retention/data residency, disaster recovery, migrations, canary/rollback, measurable
SLOs, privacy-aware telemetry, compatibility/upgrade matrices, offline/online entitlement revocation,
redacted support bundles, release signing and provenance. GA gates include Community onboarding,
three-remote-node plus Local Agent accounting, 90-day/grace clock simulation, fully Internet-
isolated Agent operation, air-gapped commercial entitlement, Coordinator compromise analysis,
protocol/update compatibility and on-prem/SaaS contract parity. Legal/commercial review covers licenses,
CLA, marks and third-party notices. Pricing and billing remain outside the OSS core.

**Public task.** `agentic/_tasks/2026-09-16-v2.0-oss-ga-readiness.md` — effort **molto alto**.
Private GA tasks belong in `bOps.Commercial` and the coordination root and use effort
**molto alto**.

**Definition of Done.** Releases are signed and reproducible, recovery is proven, security findings
are closed, license terms are approved, operational documentation is complete and support criteria
are published.

## 14. Post-V2.0 bilingual public documentation

After V2.0 is stable, make the public README available in English and Italian without duplicating
status facts by hand. English remains canonical for code and normative agent files. Translation
must preserve links, security warnings, roadmap state and licensing language, and CI must detect
structural drift.

**Task.** `agentic/_tasks/2026-09-16-post-v2.0-bilingual-readme.md` — effort **medio**.

## 15. Requirement migration matrix

| Source requirement | Normalized destination | State |
|---|---|---|
| Historical Windows EventLog / Linux journald capability | V1.3-A | Implemented; restored as one bounded cross-platform `system.events` tool rather than reopening V0.11. |
| Docker image/build/volume management | V1.3-B | Implemented; existing start/stop/restart and image listing remain baseline, with typed verified extensions. |
| Advanced process diagnostics | V1.3-C | Implemented; `process.inspect` extended additively plus `process.metrics`/`tree`/`modules`, with no `process.start` and no environment dump. |
| Sockets, routes and network diagnostics | V1.3-D | Implemented; `network.sockets`/`routes`/`neighbors`/`interface_stats`/`dns_query`/`traceroute`/`ntp_probe` added via a new native package family, existing `network.*` tools unchanged. |
| SPEC-001 writable Settings/API keys | V1.1-G | Planned; encrypted local vault selected. |
| SPEC-002 rename package to Skill | None | Rejected; package and Skill remain distinct concepts. |
| SPEC-003 README alignment/bilingual docs | Every batch + post-V2.0 task | Ongoing; bilingual delivery gated after V2.0. |
| SPEC-004 plugin UI | V1.1-F read-only; V1.3 lifecycle | V1.1-F implemented and cross-platform CI validated; V1.3 lifecycle still planned. |
| SPEC-005 remote/multi-node | V1.4 | Already covered; not duplicated. |
| SPEC-006 apps/devices/hardware model | V1.1-B | Implemented and cross-platform CI validated. |
| SPEC-007 search/fetch | V1.1-E | Implemented and cross-platform CI validated. |
| SPEC-008 filesystem sizing | V1.1-C | Implemented and cross-platform CI validated. |
| SPEC-009 recursive/batch delete | V1.1-D | Implemented and cross-platform CI validated. |
| Original V0.1–V1.0 plan | Baseline/status sections | Implemented history retained without obsolete code snippets. |
| Evolutionary V1.1–V2.0 plan | Sections 8–13 | Preserved and updated to actual repository state. |

## 16. Required ADRs before code

At minimum, new ADRs are required before:

- Skill execution interfaces and restricted invocation (V1.1-A);
- exact filesystem inventory, hash-bound destructive preflight and retention (V1.1-C/D; one or two
  ADRs depending on the chosen public contract boundary);
- Web package network/SSRF trust boundary (V1.1-E);
- encrypted vault format, master-key handling, precedence and rotation (V1.1-G);
- mutating plugin lifecycle and upload (V1.3);
- multi-agent delegation (V1.2);
- entitlement enforcement (V1.3);
- Community entitlement validity/grace, Coordinator installation identity and transfer (V1.3);
- Coordinator/Agent strict execution separation and Managed Agent host profile (V1.4);
- Local Agent enrollment and remote-slot accounting, and delegated Node Lease (V1.4);
- remote node/Control Plane transport and tenancy (V1.4);
- Coordinator packaging and PostgreSQL/pgvector persistence (V1.4);
- Coordinator-mediated Agent update distribution (V1.4);
- Account Portal/Operations Portal data boundary (V1.4/V1.9).

Accepted ADRs are never edited to retrofit these decisions.

## 17. Session protocol

Before implementation, an agent:

1. reads `CLAUDE.md`, bootstrap, project specification, architecture and security rules;
2. reads this roadmap and only the selected active task file;
3. checks `agentic/06-decisions.md` and relevant accepted ADRs;
4. verifies repository/branch/working-tree state and preserves unrelated changes;
5. stops at the first unmet dependency or operator authorization gate;
6. writes any required ADR and failing tests before implementation;
7. keeps code, tests, README, CHANGELOG, HANDOFF and task evidence synchronized;
8. never commits, pushes, tags, publishes or creates a remote repository without explicit operator
   authorization for that operation.
