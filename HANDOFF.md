# Handoff — V1.1 complete (A–H, released as `v1.1.0-preview.2`); V1.2 next

## V1.1-H status (2026-09-18) — closed

The V1.1-H integration gate was run locally on Windows 11 and then on GitHub Actions. CI is green on
Windows and Linux: run `35365098294` for the batch itself and `35372748588` for the follow-up
dashboard history commit. The latter's first attempt failed on Windows in
`WindowsProcessActionToolsTests.ProcessStop_Succeeds_ForAProcessWithAMainWindow` ("has no main window
to close"): an existing race — the test only waits for input idle before `CloseMainWindow()` — in a
commit that touched no .NET code, and it passed on a rerun of the failed job. It is a known flaky
test, not a fixed one; harden it by polling for the main window handle if it recurs.

**Finding, resolved.** `src/core/bOps.Api/appsettings.json` had drifted from the documented and CLI
defaults in `55d9585` (V1.1-F): `Filesystem:ReadPatterns` was `["*","c:\**"]` (read access to the
whole `C:` drive on a fresh install), `MaximumEntries` `900000` and `MaximumOutputBytes` `131072`.
The committed file now carries the safe defaults again (no readable paths, `100000`, `32768`). The
wider values are a local developer setting and live in `src/core/bOps.Api/appsettings.Development.json`,
which is git-ignored and loaded automatically by the Development launch profile. To use `fs.size`
and inventory over real paths, set `Filesystem:ReadPatterns` explicitly (in that file or the
environment); nothing is readable by default.

What the local pass established (details in
`agentic/_tasks/2026-09-16-v1.1-h-integration-release.md`): zero removed or changed public
`bOps.Abstractions` members against the V1.0 close-out commit (`891dae2`), additions only; SDK
packs as `1.1.0-preview.1` (released as `1.1.0-preview.2`, tag `v1.1.0-preview.2`, run `35383901325`); the signed sample plugin validates, installs, enables, disables and
removes through the built CLI; no credential-shaped strings outside test fixtures; every relative
Markdown link resolves; and `Add-SpdxHeaders.ps1` was fixed (it would have doubled every header on
re-run) and now covers `samples/`.

### Validation on 2026-09-18

- `dotnet build bOps.slnx --configuration Release` — 0 warnings, 0 errors.
- `dotnet test bOps.slnx --configuration Release --no-build --filter "Category!=LiveModel"` —
  **548 passed, 32 skipped, 0 failed (580 total)**. Per assembly (passed/skipped): Architecture 4/0,
  Memory 5/0, Audit 9/0, Policy 23/0, Runtime 186/0, PluginHost 49/0, Api 65/0 (45 before this
  batch plus 20 new in `V11ReleaseGateTests`), Providers.Anthropic 9/0, Network 10/0, System.Core
  12/0, System.Windows 22/0, System.Linux 6/16, Service.Windows 7/5, Service.Linux 7/3, Docker 7/3,
  Web 61/0, Filesystem 66/5.
- Skipped tests are platform- or environment-gated and are **not** verified on this machine: Docker
  daemon absent (3), Windows service tests need elevation (5), Linux-only System/Service tests
  (16 + 3), and Filesystem tests needing Linux or symlink privilege (5). They belong to the CI gate.
- Angular: `npm ci`, `npm run build` and `ng test --watch=false --browsers=ChromeHeadless` — 50/50.
- Not exercised: a browser run of the UI in this batch, and any single test chaining a real model,
  Web and an installed Skill together.

## V1.1-G summary

V1.1-A through V1.1-G are complete on public `bOps` `main`. V1.1-G adds writable Settings: an
administrator-only encrypted local vault for provider API keys, a separate non-secret store for
each provider's endpoint/model/tool-calling profile and the active-provider selection, and a
lazy-loaded Angular **Settings** page that fully manages both. See
`docs/architecture/adr/0029-encrypted-local-vault-and-master-key.md` for the full design.

Scope grew once during implementation, by explicit operator direction. The task file's "provider
selection" reads as an id toggle plus key management; exploration surfaced that this cannot work
alone, because every first-party provider package requires its own operator-supplied `BaseUrl` with
no built-in default (confirmed via `AnthropicProviderPackage`'s own doc comment) — switching the
active provider while `BaseUrl`/`Model` stayed pinned to the single `appsettings.json`
`ModelProvider` block would point the newly selected provider's key at the wrong endpoint. Asked
how to resolve this, the operator asked for full UI-managed provider configuration — endpoint,
model, tool-calling support, API key and room for provider-specific extras, all persisted — not
just the key. `agentic/06-decisions.md` D-025 records the decision and what was deliberately
deferred (`ExtraParameters` is persisted and returned by the API but not yet wired into
`ChatModelOptions`; no first-party provider needs it today).

The formal V1.0 release gate remains independently operator-gated. No tag, package publication,
release workflow or commercial-repository product change was made.

## V1.1-G delivered

- **Vault (`bOps.Runtime`).** `VaultCipher` (AES-256-GCM, `System.Security.Cryptography.AesGcm`, no
  new package), keyed by an HKDF-SHA256-derived key from an externally supplied master secret
  (`SecretReference`, resolved through the existing `ISecretProvider` chain). Every sealed value
  carries additional authenticated data binding it to its provider id and schema version, so an
  entry cannot be relabeled or swapped for another undetected. Transient plaintext/key-material
  buffers are zeroed (`CryptographicOperations.ZeroMemory`) after use. `VaultStore` persists entries
  in one atomically-written JSON file (temp-file-then-rename, `chmod 600` on Linux, the same pattern
  `PluginStore` already uses), guarded by an in-process `SemaphoreSlim` plus an `expectedVersion`
  optimistic-concurrency token that every write must echo back — a stale version throws
  `VaultConcurrencyException` (mapped to HTTP 409) rather than silently overwriting a concurrent
  change. `VaultSecretProvider` (provider id `vault`) resolves a stored key through the same
  `ISecretProvider` shape `EnvironmentSecretProvider` already uses.
- **Fail-closed startup.** `bOps.Api/Program.cs` only activates the vault when
  `Vault:MasterKeySecret` is configured; if it is configured but does not resolve to a value at
  least 20 characters, the host refuses to start rather than run with the vault silently
  unprotected. Absent entirely, the Settings key-management surface (`/api/settings/*`) is not
  mapped at all — every existing CLI/environment-only deployment is completely unaffected. (A
  `WebApplicationFactory` testing gotcha surfaced and was fixed along the way: a config value read
  eagerly from `builder.Configuration` before `Build()` is not guaranteed to reflect a test
  factory's `ConfigureAppConfiguration` override yet, only reads via the DI-resolved
  `IConfiguration` — post-`Build()` — are; both the vault-enablement check and the endpoint-mapping
  guard now read that way.)
- **Masking.** `SecretMask.Compute`: `first six...last four` for length ≥ 10; for shorter secrets, a
  formula (`revealed = min(length-1, floor(length*0.4))`, split evenly between prefix/suffix) that
  always leaves `prefix + suffix < length`. Computed once, from plaintext, at write time, and stored
  as ordinary display metadata — rendering a mask never reads plaintext back out of the vault.
- **Non-secret profile (`SettingsStore`/`ProviderProfile`).** Endpoint, model, tool-calling support
  and extras, kept in a separate plain `settings.json`, joined to the vault only by provider id.
  Validates `BaseUrl` as an absolute http/https URL and `Model` as non-empty before persisting.
- **Precedence (`ProviderResolution`).** A real `ModelProvider__Provider` environment variable
  (checked directly, not through the already-merged `IConfiguration`, which cannot distinguish an
  explicit override from the shipped default) always wins outright and bypasses Settings entirely.
  Otherwise, the Settings-selected provider wins over the `appsettings.json` default; for that
  provider, each of `BaseUrl`/`Model`/`SupportsNativeToolCalling` falls back field-by-field to the
  `appsettings.json` default for anything the stored profile has not set. For the API key: the
  existing environment-secret resolution is tried first and wins if non-empty; only then does the
  vault apply, keyed by the effective provider id. Settings changes take effect on next restart, the
  same as `ModelProvider` always has.
- **Endpoints (`SettingsEndpoints.cs`).** `GET /api/settings`, `PUT /api/settings/active-provider`,
  `PUT`/`DELETE /api/settings/providers/{id}/key`, `PUT /api/settings/providers/{id}/profile` — all
  under a new `ApiAuthorization.AdministratorPolicy` (`bops.administrator` role). Every mutation
  writes a `SettingsChangedAuditEvent` (new `bOps.Abstractions/Audit.cs` type — actor, setting name,
  operation, provider id, outcome only, never a value; `TaskId`/`StepIndex` use documented sentinels
  since a Settings mutation is not task-scoped). Activating a provider that has no stored profile and
  is not the `appsettings.json` default is rejected with a clear message rather than left to produce
  a broken `BaseUrl`/`Model` combination later.
- **Angular Settings page.** Per provider: active badge, key mask or "Not set", endpoint/model/
  tool-calling summary, and an expandable form with a write-only password field (verified live —
  never prefilled, even for a provider with a stored key), Set/Replace/Clear, endpoint/model/
  tool-calling inputs (prefilled from the current profile, verified live), and "Make active" (hidden
  once already active). A 409 conflict refreshes to the latest server state and surfaces a retry
  prompt instead of silently overwriting.
- **CLI.** `bops vault rotate-key <new-master-key-environment-variable>` — decrypts every entry
  under the current master key, re-encrypts under the new one, replaces the file; deliberately not
  exposed through the API (ADR-0029: the highest-privilege vault operation stays on the surface that
  already requires direct machine access, the same reasoning as `bops plugin *`).

See `agentic/_tasks/2026-09-16-v1.1-g-secure-settings.md` for the full checklist and
`docs/architecture/adr/0029-encrypted-local-vault-and-master-key.md` for the complete design,
including the documented Windows ACL-hardening gap (Linux gets a real, tested `chmod 600`; Windows
does not get equivalent hardening in this batch).

## Enabling the vault

Opt-in only — see `README.md`'s "Provider credentials" section for the `Vault:MasterKeySecret`
config shape, the `BOPS_VAULT_MASTER_KEY` environment variable, and the `bops vault rotate-key`
usage. Backup/restore is file-level (copy `vault.dat`); because the master key is deliberately never
stored beside it, a copied vault file alone is inert, and losing the vault means re-entering keys
through the UI — the same recovery story `plugins.json` already has.

## Validation on 2026-09-17

- `dotnet build bOps.slnx --configuration Release` — 0 warnings, 0 errors.
- `dotnet test bOps.slnx --configuration Release --no-build --filter "Category!=LiveModel"` — every
  assembly in the solution passed locally (full-suite regression, not just the touched projects),
  including new coverage in `bOps.Runtime.Tests` (`VaultCipherTests`, `VaultStoreTests`,
  `VaultSecretProviderTests`, `SettingsStoreTests`, `SecretMaskTests`, `FakeTimeProvider`) and
  `bOps.Api.Tests` (`SettingsEndpointsTests`, `ProviderResolutionTests`) — `bOps.Api.Tests` now
  45/45, `bOps.Runtime.Tests` now 186/186.
- `npm run build` — Angular production build succeeded; the `settings` route lazy-chunks correctly.
- `npm test -- --watch=false` — 50/50, including `settings.store.spec.ts` and the rewritten
  `settings.spec.ts`.
- Verified end-to-end against a real running `bOps.Api` (real encrypted vault, real master key) and
  a real Angular dev server: set a key, confirmed the mask rendered and the plaintext never appeared
  in the API response or the audit log, set a profile, made a provider active, confirmed the
  precedence source (`Settings`) and the expanded form's endpoint/model fields were correctly
  prefilled while the key field stayed empty — not only the test suite.
- GitHub Actions run [35277152220](https://github.com/babbubba/bOps/actions/runs/35277152220) —
  Windows and Ubuntu restore/build/non-live tests, Angular build/tests, and SBOM generation all
  passed.

## Next action and boundaries

V1.2-A is complete: ADR-0030 is Accepted (2026-09-18) and the operator's decisions are D-026.

V1.2-B (delegation contracts, SDK `1.2.0-preview.1`) is implemented and verified locally on Windows: the
public-surface diff shows zero removed or changed members against the V1.0 close-out, the full non-live
suite passes, and the SDK packs. It waits for Windows and Linux CI and for merge; do not start V1.2-C
before then. Two things to know before C: the diff found that V1.1 had renumbered
`ToolParameterType` (fixed in B, see its task file), and ADR-0030's "empty intersection in any dimension
is a denial" needs a per-role clarification before enforcement (the open question in
`agentic/_tasks/2026-09-18-v1.2-b-contracts.md`).
Next action after B merges: V1.2-C (`agentic/_tasks/2026-09-16-v1.2-multi-agent.md` lists sub-tasks A–M with effort).
The formal release gate is closed via `v1.1.0-preview.2`.

Do not create a release tag or publish packages without separate operator authorization. The private commercial repository remains product-gated and
unchanged; only the workspace submodule pin advances after this public closure.
