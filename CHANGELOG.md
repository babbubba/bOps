# Changelog

All notable changes to bOps are documented here. Versions follow Semantic Versioning.

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
