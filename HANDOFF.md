# Handoff — V1.0 complete, uncommitted at session start; committed by this session

V1.0 (`piano-bops-v0.9.1-v2.0.md`'s gate after V0.11 — "publishable as a reliable base for
commercial extensions") is fully implemented: `bOps.Abstractions` is frozen at `1.0.0`, the API
and plugin loader no longer rely on informal local trust, and release artifacts are reproducible
and attested. **This session found the implementation already done in the working tree** (left
by a prior agent run that was interrupted mid-session) and spent its own time verifying it for
real, closing out documentation, and committing it in scoped commits — not re-implementing it.

## What V1.0 delivers

- **Secret references** (`bOps.Abstractions/Secrets.cs`) — `SecretReference` (provider id +
  opaque name, never a value) and `ISecretProvider`, resolved only at the host boundary.
  `ChatModelOptions.ApiKeySecret` replaces the old raw `ApiKey` string; `ResolvedApiKey` is
  populated once, at the last responsible moment, and is provably excluded from JSON
  serialization and from `ToString()` (`JsonRoundTripTests.ChatModelOptions_RoundTrips` asserts
  the resolved value never appears in either). `EnvironmentSecretProvider`
  (`bOps.Runtime/EnvironmentSecretProvider.cs`) is the one host-side implementation shipped.
- **Host-assigned package trust** (`Registry.cs`, `ToolRegistry.cs`, `AgentRunner.cs`) —
  `IToolRegistry.Register` gained a `PackageTrustLevel` overload and a `GetTrust` accessor;
  `AgentRunner`'s policy evaluation now reads the registry's real trust instead of the
  V0.3-era hardcoded `PackageTrustLevel.Official` for every package. This is what makes plugin
  signature verification (below) actually load-bearing instead of decorative.
- **API authentication and authorization** (`bOps.Api/ApiAuthenticationOptions.cs`,
  `ApiAuthorization.cs`, `ApiKeyAuthenticationHandler.cs`, `IdentityEndpoints.cs`) — bearer
  API-key auth with `viewer`/`operator`/`approver` roles read from configuration as one atomic,
  comma-separated scalar per key (see the merge-semantics bug below). Anonymous access is
  rejected; approval-endpoint actor identity comes only from the authenticated principal, never
  from a client-supplied field.
- **Bounded, idempotent task execution** (`AgentTaskLauncher.cs`, `AgentTaskLauncherOptions.cs`,
  `TaskIdempotencyStore.cs`) — rate limiting, a configurable max-concurrent-tasks bound,
  actor-scoped idempotent start, and observable cancellation.
- **Plugin provenance** (`bOps.PluginHost/PluginPackageSignature.cs`, `PluginProvenance.cs`,
  `PluginPublisherTrustStore.cs`, `PluginSecurityJsonContext.cs`) — a detached RSA-PSS/SHA-256
  signature over a deterministic inventory of the full package directory, a local
  publisher/key-id trust store, install-time provenance recording, and a re-verification of the
  actual bytes before every activation. Unsigned, unknown-key, invalidly-signed or
  tampered-after-signing packages stay disabled or are rejected outright — this is a fail-closed
  policy, not a default-allow with logging.
- **Operator-facing audit verification and file permission hardening**
  (`bOps.Audit/JsonLinesAuditSink.cs`, `bOps.Memory/SqliteTaskStore.cs`) — `bops audit verify`
  is now a real CLI command; the audit log and the SQLite task store both get `0600`-equivalent
  permissions on Unix via `File.SetUnixFileMode` (a no-op on Windows, which has no portable
  POSIX-mode primitive — ACL hardening there is out of scope for V1.0, recorded honestly in the
  threat model rather than glossed over).
- **CLI wiring** (`bOps.Cli/Program.cs`) — `bops plugin sign`, `bops plugin validate` (now
  reporting provenance), and `bops audit verify`.
- **Angular UI authentication** (`web/bops-ui/src/app/core/auth/`, `features/login/`) — the
  credential lives in memory only, an `HttpInterceptor` attaches it to every request, and task
  status uses authenticated polling instead of an unauthenticated `EventSource` (native
  `EventSource` cannot carry a bearer header — the authenticated SSE endpoint stays available for
  header-capable clients, per ADR-0022).
- **Release pipeline** (`.github/workflows/release.yml`, `scripts/Compare-ReproducibleTrees.ps1`,
  `scripts/New-ReproducibleZip.ps1`) — tag-triggered, `dotnet restore --locked-mode`, publishes
  the CLI twice per RID and diffs the trees to prove determinism, packs the SDK, generates SBOMs,
  produces a deterministic zip + `SHA256SUMS`, and attests every artifact via `actions/attest`.
  `Directory.Build.props` now sets `RestorePackagesWithLockFile`, and every project has a
  committed `packages.lock.json`.
- **ADR-0022** (`docs/architecture/adr/0022-bops-abstractions-1.0-security-boundaries.md`) and
  the **V1.0 threat model** (`docs/security/threat-model.md`) record the decisions above and their
  explicitly-scoped limits — most importantly that in-process plugins remain trusted code:
  signatures prove byte provenance, not sandboxing, and nothing in this release changes that.

## A real regression this session found and fixed

The gate run surfaced a genuine authorization bug, not a flaky test: a key configured with only
the `viewer` role could still start a task. Root cause was ASP.NET configuration's array-merge
semantics — overriding a lower-priority source's `Roles` array element-by-element left that
source's `operator`/`approver` entries in place instead of replacing them, so a "reduced"
privilege set silently kept its old, broader one. Fixed by making the roles configuration key a
single comma-separated scalar (`"viewer,operator,approver"`) instead of an array, which
configuration sources can only replace atomically, never merge. The authorization test that
caught this is now permanent regression coverage.

## Verified for real, this session

- `dotnet build bOps.slnx --configuration Release`: **0 warnings, 0 errors**, full solution.
- `dotnet test bOps.slnx --configuration Release --no-build --filter "Category!=LiveModel"`:
  **all 15 test assemblies green, 0 failures.** Skips are the expected, visible ones —
  Linux-only tests on this Windows dev box, the five `[RequiresElevationFact]` Windows service
  tests (this session's process is not elevated, same constraint as V0.11), one symlink test
  skipped for its own documented platform reason, two Docker tests skipped because this host's
  Docker daemon cannot run Linux containers.
- `npm run build` (Angular production build): succeeds, no errors.
- `npx ng test --watch=false --browsers=ChromeHeadless`: **17 of 17 green.**
- `bOps.Architecture.Tests`: 4/4 — rule A1 still holds after every V1.0 change.
- Read every `appsettings.json` diff and the plugin/`SecretReference` code paths directly to
  confirm no literal secret value was committed anywhere; `specifiche-pendenti.md` (the user's
  pre-existing untracked file, outside this plan's scope) has no diff and was never staged.

## Left for CI, not verified locally — same trust model as every prior version

- **The five `[RequiresElevationFact]` Windows service-lifecycle tests** and **Linux
  `process.stop`/`kill`'s real execution** — unchanged from V0.11's own note; this dev session is
  still not elevated and still has no Linux host.
- **`release.yml` itself was not executed.** It is written and reasoned through (dependency
  ordering, `--locked-mode` restore, double-publish-and-diff for reproducibility, SBOM/checksum/
  attestation), but nothing in this session pushed a `v*` tag or ran it via `workflow_dispatch`.
  Its first real execution — including whether `dotnet publish`'s output is actually
  byte-reproducible across two runs on GitHub's own runners — is a genuinely open question until
  it runs there. This is the single biggest unverified claim in this handoff; flagging it
  explicitly rather than asserting reproducibility works.
- **The `dependency-review` job added to `ci.yml`** only runs on `pull_request` events, so it has
  never executed against this branch (`main`, direct pushes only) either.

## Scope boundaries — deliberate, not gaps to silently fill later

- **In-process plugins remain trusted code.** Signatures prove a publisher's bytes were not
  modified after signing; they are not a sandbox, and ADR-0022 says so explicitly to prevent this
  from being misread later as "plugins are now safe to run untrusted."
- **`ISecretProvider` is host infrastructure, not exposed to the restricted plugin activation
  container.** A package receives only the single resolved credential for its own configured
  model, never a general secret-resolution capability. A broader, scoped resolver is future work,
  not a V1.0 gap.
- **Windows gets no file-permission hardening equivalent to Unix's `0600`.** There is no portable
  POSIX-mode primitive on Windows; an ACL-based equivalent was judged out of scope for V1.0 and is
  recorded as residual risk in the threat model, not silently skipped.
- **In-flight approvals are not restored across a restart, by design (ADR-0022).** A resumed task
  requests a fresh approval from a currently authenticated approver rather than replaying a
  point-in-time human decision as a reusable capability.
- **No V1.1 Skill/Capability or remote Node–Control Plane contract was introduced.** Everything
  above is local-only, exactly as the plan requires at this gate.

## Exact next steps, in order

1. Ask the user before pushing (standing rule, `agentic/05-workflow.md`). No `Co-Authored-By:
   Claude` trailer — carried forward from this project's own standing correction.
2. After pushing, confirm CI is green on both `ubuntu-latest` and `windows-latest`.
3. Once satisfied with the ordinary CI run, consider pushing a `v1.0.0-rc.1` tag (or running
   `release.yml` via `workflow_dispatch`) to get the release pipeline's **first real execution** —
   this is the one part of V1.0 that is written but genuinely unproven, per the note above. This
   is a new decision for the user to make explicitly, not something to do automatically.

## Next: V1.1 and beyond

V1.0 was the last gate before `piano-bops-v0.9.1-v2.0.md`'s V1.1 (Skill/Capability/evidence
contracts and the immutable execution plan). Per this project's own scope-discipline rule, that is
a new decision for the user to make explicitly, not something to begin automatically because V1.0
closed out clean.
