# Changelog

All notable changes to bOps are documented here. Versions follow Semantic Versioning.

## [Unreleased]

### Added

- V1.1 preview contracts for Capability, Evidence, Finding, SkillReport and immutable, canonically
  hashed ExecutionPlan artifacts (ADR-0023).
- Governed ExecutionPlan orchestration through the existing policy, approval, verification and
  audit pipeline (ADR-0024).
- Persistent V1.1 plan/task tracking and a stable `agentic/00-bootstrap.md` entry point.

### Changed

- `bOps.Abstractions` now identifies the in-progress additive SDK surface as
  `1.1.0-preview.1` rather than publishing V1.1 contracts under the stable 1.0 version.
- Project status documentation now distinguishes V1.0 implementation completion from the
  still-unexecuted release-candidate workflow.

### Fixed

- API composition tests no longer inherit a developer's model-provider secret from the host
  environment.

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
