# Changelog

All notable changes to bOps are documented here. Versions follow Semantic Versioning.

## [Unreleased]

### Added

- V1.1 preview contracts for Capability, Evidence, Finding, SkillReport and immutable, canonically
  hashed ExecutionPlan artifacts (ADR-0023).
- Governed ExecutionPlan orchestration through the existing policy, approval, verification and
  audit pipeline (ADR-0024).
- Executable `ICapability`/`ISkillProvider` contracts, a node-scoped Skill registry and
  invocation-scoped same-package Read-only `IToolInvoker` evidence collection (ADR-0025).
- Exact contextual Skill policy, correlated Skill-run audit events and an end-to-end signed sample
  Skill covering evidence, findings, hash-bound approval, action and independent verification.
- Persistent V1.1 plan/task tracking and a stable `agentic/00-bootstrap.md` entry point.
- Bounded cross-platform `system.apps` and `system.devices` inventories with deterministic JSON,
  explicit source completeness and native Windows registry/Linux dpkg+sysfs collectors.
- Additive hardware-model reporting in `system.info`, with an explicit `unknown` fallback.
- Bounded cross-platform `fs.size` summaries plus scoped, expiring SQLite exact manifests with
  deterministic content hashes and no full entry list in model, telemetry or audit payloads.
- Additive host-owned `ToolExecutionContext` / `IContextualTool` dispatch for tools that persist
  task- and actor-scoped derived state without breaking existing `ITool` implementations.
- Governed permanent recursive/batch deletion with complete hash-bound manifests, mandatory
  approval, one-shot children-first execution, durable per-entry reconciliation and independent
  verification (ADR-0027).
- Scoped deletion-manifest API paging/search/NDJSON download and an Angular approval preview with
  expiry/hash invalidation and explicit permanent-deletion acknowledgement.
- Optional bounded tool audit summaries, used by deletion to retain hash/count/outcome evidence
  without copying complete path lists into the append-only audit log.
- `bOps.Packages.Web`: `web.search` against one operator-configured SearXNG JSON endpoint (no API
  key, no HTML-scraping fallback) and a hardened `web.fetch` that resolves and validates every
  connection's destination address at connect time, denying loopback/link-local/private/
  carrier-grade-NAT/multicast/unspecified addresses by default and closing the DNS-rebinding TOCTOU
  window by construction (ADR-0028).
- Bounded redirect following, decompression-bomb-safe response reading (the byte cap applies to
  decompressed output, not wire bytes), and an allowlisted textual content-type/charset boundary
  for `web.fetch`.
- Read-only plugin catalog: `GET /api/plugins` and `GET /api/plugins/{id}`, and a lazy-loaded
  Angular Plugins page showing installed/enabled/loaded/compatible state, signature/trust,
  declared capabilities and dependencies, and declared-vs-effective maximum risk as distinct
  values — no enable, disable or upload control in this batch.
- `bOps.Api` now activates the operator's already-enabled plugins at start-up, the same way
  `bOps.Cli` already did — it runs its own `AgentRunner` and needs the same plugin-contributed
  tools/Skills visible to it.
- Writable Settings: an administrator-only encrypted local vault (AES-256-GCM, HKDF-SHA256-derived
  key from an externally supplied master secret) for provider API keys, a separate non-secret store
  for each provider's endpoint/model/tool-calling profile and the active-provider selection, and
  `GET/PUT/DELETE /api/settings/*` endpoints with optimistic-concurrency version checks on every key
  write (ADR-0029). The vault is opt-in — absent `Vault:MasterKeySecret` configuration, the Settings
  key-management surface does not exist and every existing CLI/environment deployment is unaffected.
  A configured-but-unresolvable master key refuses to start rather than run unprotected. A new
  `administrator` role/policy gates every mutation; a new `bops vault rotate-key` CLI command
  rotates the master key without exposing rotation through the API. The Angular Settings page lets
  an administrator choose the active provider and fully manage each provider's endpoint, model,
  tool-calling support and API key — the key input is always write-only and a stored key is never
  returned in plaintext, only as a `first six...last four` display mask captured at write time.
- New `bops.administrator` role/policy (`bOps.Api.ApiAuthorization`), required by every Settings
  mutation endpoint; the shipped local-dev credential now carries it alongside the three existing
  roles.
- Dashboard task history: a status selector over every `AgentTaskStatus` (default `Running`) lets an
  operator browse and reopen completed, failed and other terminal tasks; history is fetched on demand,
  never polled, newest first and capped at 50 rows. UI-only; no API change.
- V1.1-H release-gate tests (`V11ReleaseGateTests`): the API role matrix for the plugin catalog and
  Settings with each role on its own, a guarantee that the catalog exposes no mutation route,
  Settings persistence across a genuine host restart on the same state directory and vault master
  key, and a governed system-evidence-then-Web-research workflow proving an unsafe fetch
  destination stays denied and audited.

### Changed

- `bOps.Abstractions` now identifies the in-progress additive SDK surface as
  `1.1.0-preview.1` rather than publishing V1.1 contracts under the stable 1.0 version.
- Project status documentation now distinguishes V1.0 implementation completion from the
  still-unexecuted release-candidate workflow.
- Planning is consolidated into one active `agentic` roadmap with granular effort-rated task files;
  superseded plans and specification inputs are preserved in an agent-ignored historical archive.
- Dynamic plugin activation now registers combined Skill/Tool providers atomically and requires
  their declared Capability names to match the activated provider exactly.
- V1.1 Skill runs are explicitly terminal and non-resumable; interruption requires fresh
  preparation and approval rather than unsafe partial-plan replay.
- Filesystem inventory limits, output ceilings, manifest path and retention are host configuration;
  out-of-range requests fail instead of being silently clamped.
- `PluginManager.LoadAllEnabled` isolates each plugin's activation failure instead of letting one
  bad plugin crash the whole host at start-up; it now returns the sanitized per-plugin failures
  (install path scrubbed from the message) instead of throwing past the first one. `bOps.Cli` logs
  these as warnings instead of ignoring them.
- Runtime argument validation now rejects undeclared names and JSON type mismatches before policy,
  approval or execution; tool manifests can require explicit human approval even when policy would
  otherwise allow automatic execution.

### Fixed

- `bOps.Api`'s committed `appsettings.json` again ships the documented safe filesystem defaults (no
  readable paths, `MaximumEntries` 100000, `MaximumOutputBytes` 32768); broader local values had
  crept in with V1.1-F and now live in the git-ignored `appsettings.Development.json`.
- `scripts/Add-SpdxHeaders.ps1` recognised an existing header only at the very start of a file, so
  re-running it would have prepended a second header to every source file; it now detects the SPDX
  identifier in the file's first lines and also covers `samples/`, whose four files lacked the
  header.
- API composition tests no longer inherit a developer's model-provider secret from the host
  environment.
- The AppHost dependency lock now includes the centrally configured SourceLink dependency, so the
  release workflow can restore the complete solution in locked mode.

## [1.0.0-rc.1] - 2026-09-16

### Added

- Stable `bOps.Abstractions` 1.0 contracts, including host-resolved `SecretReference` and
  `ISecretProvider` types.
- Bearer-key API authentication with `viewer`, `operator` and `approver` roles; approval actors
  are derived from authenticated claims.
- Actor-scoped idempotent task starts, cancellation, API rate limits and bounded concurrent runs.
- Detached RSA-PSS/SHA-256 plugin signatures, operator-owned publisher trust and recorded package
  provenance, re-verified before every activation.
- Operator-facing `bops audit verify`, Unix-restrictive state/audit file permissions and a full
  V1.0 threat model.
- Locked dependency restore and a Windows/Linux release workflow that verifies reproducible
  publish output, creates deterministic archives, SBOMs and checksums, and attests artifacts.

### Changed

- Provider credentials are configured only by secret reference and are excluded from serialized
  options, logs and status responses.
- Provider HTTP adapters retry only bounded transient transport/status failures.
- The Angular UI authenticates requests with an in-memory credential and uses authenticated task
  polling instead of placing credentials in an `EventSource` URL.
- Plugin manifest parsing is size-bounded and fail-closed for malformed or traversal-prone input.

### Security

- Unsigned, unknown-key, invalidly signed or modified plugins cannot be enabled.
- API reads, task operations and approval decisions are separately authorized.
- Pending approvals are intentionally not persisted across crashes; resumed execution requires a
  fresh decision from a currently authenticated approver.
