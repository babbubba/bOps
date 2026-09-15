# Handoff — V0.5 (Windows + Linux parity, Filesystem and Network packages, Aspire AppHost, CI on both OSes) complete

Written at the end of the session that implemented V0.5 on top of the completed V0.4
post-action-verification runtime. Everything below is exact, not a summary — follow it literally
to resume.

## State right now

**`dotnet build bOps.sln` builds clean end to end — 0 warnings, 0 errors** in both `Debug` and
`Release` (verified with a full clean of every `bin`/`obj` and a from-scratch rebuild of both
configurations). **`dotnet test bOps.sln`: 132 passing, 7 skipped, 0 failed** (both
configurations) — the 7 skips are 6 pre-existing Linux-only conformance tests
(`bOps.Packages.System.Linux.Tests`, no Linux host in this dev environment) plus one new
symlink-resolution test (`bOps.Packages.Filesystem.Tests` — this Windows account cannot create
symbolic links without Developer Mode or elevation; skipped visibly, not silently, same pattern as
the Linux tests). 23 projects in `bOps.sln` now, up from 18 at the end of V0.4.

CLI smoke test (`cd src/core/bOps.Cli && dotnet run -- "how is this machine doing?"`): DI wiring,
config binding, and tool registry construction all succeed — including registering the five new
`fs.*` tools and four new `network.*` tools, which would have thrown `ToolRegistrationException`
immediately at startup had the V0.3 rule-B3 registration guard found anything wrong with
`fs.write`'s or `fs.delete`'s declared `VerificationSpec`/`IVerifiableTool` — the task then fails
cleanly at the planning call's HTTP 401 (`ApiKey` empty, rule S6), exactly as every prior session's
smoke test did. This confirms the new packages' wiring holds end to end; it does not confirm a
model was ever actually called with `fs.*`/`network.*` in its tool list, since no provider is
configured in this dev environment.

## What this session did

Implemented V0.5 per `agentic/00-project-spec.md`'s roadmap: **"Windows + Linux parity, Filesystem
and Network packages, Aspire AppHost, CI on both OSes."** Four pieces:

### 1. Windows + Linux parity — already true, verified, not re-done

`bOps.Packages.System.{Core,Windows,Linux}` already contributed matching `system.info`/`cpu`/
`memory`/`disk` and `process.list` tools since V0.1, sharing manifests via `SystemToolManifests`
and passing the same `bOps.Packages.System.Conformance` suite on both OS packages. There was
nothing to build here — parity was already structural. No new `system.*`/`process.*` tool was
added (the roadmap line names parity, not catalog expansion; `piano-bops.md`'s fuller catalog —
`system.swap`, `system.io`, `process.inspect`/`start`/`stop`/`kill` — is scope for whichever future
version actually calls for it, not implied by this one).

### 2. `bOps.Packages.Filesystem` — the first non-`Read` tool this repository ships for real

Cross-platform via `System.IO` (rule A8 does not require an OS split when the BCL already
abstracts the difference — unlike `system.*`, there is no per-OS data-collection code to share).
Five tools, all under `PackageId("bops.packages.filesystem")`:

- **`fs.list`**, **`fs.read`**, **`fs.stat`** — `RiskLevel.Read`. `fs.read` truncates deterministically
  (head-only, marked explicitly) at a configurable `maxBytes` (default 65536), mirroring the agent
  loop's own history-truncation rule (C3). `fs.stat` reports existence as data, not as
  `ToolOutcome.Failure` — a missing path is a successful observation of a negative fact; `Failure`
  is reserved for "could not check at all" (policy denial, a real I/O error). Its output is a
  single-line JSON object (`FsStatOutput`), because it doubles as the verification target for the
  two tools below and JSON is what lets that be read back reliably.
- **`fs.write`**, **`fs.delete`** — `RiskLevel.High`, `IVerifiableTool`, verified via `fs.stat` on
  the same path (`ArgumentsFrom: ["path"]`). `fs.write`: `exists:true` afterwards → `Confirmed`,
  `exists:false` → `Refuted`. `fs.delete`: the mirror image. `fs.delete` deliberately never deletes
  a directory (a materially larger blast radius, out of scope for this version) — it fails with an
  explicit message instead of silently refusing or recursing.

**`FilesystemPathPolicy`** implements rule S11 for real — the path policy the architecture and
security rules named since V0.1 but nothing needed until now, since no non-`Read` filesystem tool
existed to enforce it for:

- `Resolve(path)`: `Path.GetFullPath` normalization, then follows every symlink in the longest
  *existing* ancestor chain to its final target — a requested path that does not yet exist (the
  normal case for `fs.write` creating a new file) still has its existing parent directories fully
  resolved, closing the time-of-check/time-of-use gap S11 names explicitly (a symlinked allowed
  directory pointing somewhere outside policy must not become a bypass). Verified by an actual test
  that creates a real symlink and confirms a request routed through it resolves to — and is denied
  at — the real, unlisted target (`FilesystemPathPolicyTests.Resolve_FollowsASymlinkedDirectoryToItsRealTarget`,
  `[SymlinkCapableFact]`, skips visibly where this process cannot create a symlink).
- `AllowsRead(resolvedPath)` / `AllowsWrite(resolvedPath)`: two independently configured glob-style
  pattern lists (`Filesystem:ReadPatterns` / `Filesystem:WritePatterns` in `appsettings.json`, both
  empty by default — **deny by default**, rule S11, verbatim). Matching is a small hand-written
  matcher (`FilesystemPathGlob`), not a general-purpose glob library — see "Design choices" below
  for why.
- `fs.stat` is allowed when *either* list covers the path, not just `ReadPatterns` — see "Design
  choices."

This is genuinely exercised: 25 passing tests (1 skipped) in `bOps.Packages.Filesystem.Tests`,
against a real temp directory, never a mocked filesystem (agentic/04-testing-rules.md — Filesystem
is a platform package).

### 3. `bOps.Packages.Network` — four `Read`-risk tools, no verification needed

Cross-platform via `System.Net.NetworkInformation`, under `PackageId("bops.packages.network")`:
`network.interfaces` (every NIC, type, status, addresses), `network.dns` (hostname → addresses),
`network.ping` (one ICMP echo, `timeoutMs` capped at 10000), `network.connections` (active TCP
connections). All `RiskLevel.Read`, so `Manifest.Verification` is `null` on every one — nothing
here has a side effect. `network.route` and `network.port_check` (named in `piano-bops.md`'s
catalog) were **not** built: `network.route` needs a per-OS shell-out (`route print` / `ip route`)
that adds real complexity for no test that currently needs it, and `port_check` is redundant with
what `ping`/`connections` already cover for this version. Six tests in
`bOps.Packages.Network.Tests`, against the real network stack (loopback, real DNS, RFC 2606's
reserved `.invalid` TLD for the negative case) — `network.ping`'s test deliberately does not assert
`Success`, only that it never throws and always returns a real observation, since some sandboxed CI
containers restrict unprivileged ICMP even to loopback.

### 4. `bOps.AppHost` — Aspire, dev/test orchestration only

Per `agentic/06-decisions.md`, D-002: never used to host bOps in production. `src/bOps.AppHost`
(`Aspire.Hosting.AppHost` 13.5.3 — the version this NuGet feed currently serves; note the jump from
the 9.x line, not a typo) declares one container resource (`mcr.microsoft.com/dotnet/sdk:10.0`,
bind-mounted to the repo root, kept alive with `tail -f /dev/null`) as a real Linux target for
`bOps.Packages.System.Linux.Tests` and `bOps.Packages.Filesystem.Tests` when developing on a
non-Linux machine — the same real-target requirement 04-testing-rules.md has stated since V0.1
("From V0.5 the Aspire AppHost provides these targets"). **Only `dotnet build` was run against this
project in this session — `dotnet run` (which would actually pull the image and start a
container) was not.** That's a genuine gap in verification, stated plainly rather than implied:
the project compiles and references a real, resolvable NuGet package at a real version, but nobody
has watched it actually bring up a container yet. Ollama/llama.cpp orchestration, also named in
D-002 for "V0.5/V0.6," was deliberately left out — nothing in this codebase yet needs a live model
target for any test to pass, and pulling that forward would be exactly the "add the next version's
scope early" rule S5/workflow discipline warns against.

### 5. CI — `.github/workflows/ci.yml`, and the rule-A1 check it was missing

`windows-latest` / `ubuntu-latest` matrix, `dotnet restore` → `dotnet build --configuration
Release` (warnings-as-errors already on solution-wide, so this is the same gate as local) →
`dotnet test --filter "Category!=LiveModel"`. Platform-specific tests need no extra filtering — they
already skip themselves visibly on the wrong OS (`LinuxOnlyFactAttribute` and friends). **Only
verified locally** (`dotnet build`/`dotnet test` in `Release` config, both green, 132/7/0) — the
workflow file itself has not run on an actual GitHub Actions runner, since that requires pushing,
which this session was not asked to do.

`agentic/04-testing-rules.md` states rule A1 ("the core names no package") "is enforced by an
automated test, not by review" — that test did not exist before this session. Added
`tests/bOps.Architecture.Tests`: it opens the compiled `bOps.Runtime.dll`/`bOps.Policy.dll`/
`bOps.Audit.dll` via `System.Reflection.Metadata`, walks every method body's IL for `ldstr`
instructions, resolves each string literal, and fails if any contains a package/tool/provider-
specific term (`docker.restart`, `/proc`, `systemctl`, `ServiceController`, `openrouter`, `ollama`,
every `fs.*`/`network.*` tool name, and more — the full list is `ForbiddenTerms` in
`CoreNamesNoPackageTests.cs`). This is not a test that trivially passes: verified by temporarily
adding a literal that genuinely exists in `AgentRunner.cs` (`BOPS_TOOL_OUTPUT`, the tool-output
delimiter) to the forbidden list and confirming the test failed and named exactly that literal,
then reverting — see the diff history if you want the receipts; nothing about that round-trip is
left in the working tree.

## Design choices worth knowing before extending this further

- **`fs.stat` accepts either `ReadPatterns` or `WritePatterns` covering a path, not `ReadPatterns`
  only.** `fs.write`/`fs.delete` only need `WritePatterns` to run at all; if `fs.stat`'s own S11
  check required `ReadPatterns` specifically, verification would come back `Inconclusive` for every
  operator who configured write access without also duplicating every path into read access — safe
  under rule S4 (`Inconclusive` is never treated as success), but needlessly useless by default.
  `fs.stat` only ever reveals existence/type/size/mtime, not content, so the lower bar is a
  deliberate, considered call, not a loosening of S11 — S11 itself is still enforced in full on the
  fully resolved path either way.
- **The path glob matcher (`FilesystemPathGlob`) is hand-written, not a general-purpose library.**
  Two forms only: an exact path, or a path ending in `/**` (recursive, any depth) whose final
  segment may carry one `*` wildcard within that segment. `Microsoft.Extensions.FileSystemGlobbing`
  was considered and rejected: its matching model is built for relative patterns under a project
  root, not absolute-path allow-listing with drive letters and backslashes, and a security gate is
  safer built from three auditable rules than from a general library's full semantics this project
  has no need for.
- **Verification runs on `Failure`/`Timeout` too (V0.4's decision, ADR-0016) — this version is the
  first time a real tool actually exercises that.** `fs.write`/`fs.delete`'s `EvaluateVerificationAsync`
  never receives the original call's own outcome, only `fs.stat`'s result, so this falls out of the
  existing contract for free; nothing needed to change to make it correct.
- **`bOps.AppHost` lives at `src/bOps.AppHost`, not under `src/core` or `src/packages`.** It is
  neither core runtime nor a package under the tool-provider contract — it is dev-time
  orchestration only, so it gets its own top-level slot.

## What V0.5 deliberately does NOT have yet

- **`fs.search`, `fs.hash`, `fs.move`** (named in `piano-bops.md`'s catalog) — not built. Nothing in
  this version's roadmap line names them specifically, and five real, tested `fs.*` tools already
  exercise everything V0.3/V0.4 built (policy, approval, verification) for real. Natural next
  additions whenever a task actually needs them.
- **`network.route`, `network.port_check`** — not built; see "What this session did," part 3.
- **No `Service` or `Docker` package.** `Service` is not named in V0.5's roadmap line at all.
  `Docker` is explicitly V0.6 ("Docker package with conditional capability discovery").
- **`bOps.AppHost` has not actually been run.** Only `dotnet build` was verified — see part 4 above.
  The first session that actually needs a live Linux container target (running the Linux system/fs
  tests against it, not just on native CI Linux) should run it and fix whatever the first `dotnet
  run` surfaces; Aspire APIs this session guessed at from NuGet metadata, not from a live trial.
- **No Ollama/llama.cpp orchestration in the AppHost.** D-002 names it for "V0.5/V0.6" — deferred to
  whichever version first needs a live local model target for a test to pass.
- **No `bOps.Memory` project / SQLite** — V0.7. Still in-memory only.
- **No dynamic plugin loading** — V0.10. Still direct `ProjectReference`s, including the two new
  packages.
- **No CLI command to run `AuditChainVerifier` on demand** — carried forward again, still small,
  still not done.

## Next steps

V0.5 is genuinely done: the platform-parity claim is verified (it already held), two real
cross-platform packages ship with `fs.write`/`fs.delete` finally giving V0.3's policy/approval flow
and V0.4's verification flow a real non-`Read` tool to run against, a real (if only build-verified)
Aspire AppHost exists for local Linux test targets, a CI workflow exists for both OSes, and the
rule-A1 check the testing rules already claimed existed now actually does.

**V0.6 — "Docker package with conditional capability discovery" — has not been started.** Per the
scope-discipline rule, the next session should begin by reading `agentic/00-project-spec.md`'s
roadmap entry for V0.6, architecture rule B4 (`ICapabilityProbe`, `RefreshCapabilitiesAsync` — the
mechanism this version needs for real: a Docker daemon that isn't running yet must make
`docker.*` tools disappear from what the planner sees, not fail at call time), and the `Docker`
row of architecture rule A8's "same pattern applies to Service, Network, Process and Filesystem"
before writing any code. `Docker.DotNet` is the first genuinely new third-party NuGet dependency a
package in this repository will take (rule A7 — packages may reference third-party NuGet freely;
the core still may not) — worth reading rule A9/A10 again before deciding how `docker.restart`'s
verification (`docker.inspect` checking `State.Status == "running"`, per `piano-bops.md` §11) is
wired, since it is the first verification target that is itself a third-party API call rather than
a BCL one.

Before starting V0.6, whoever picks this up should also actually run `dotnet run` on `bOps.AppHost`
at least once and fix whatever the first real container start surfaces — this session verified the
project compiles against a real NuGet package, nothing more.

Two smaller, non-urgent items carried forward again from every prior handoff, still real, still
judged out of scope for a session implementing code rather than backfilling documentation:

1. The six pre-existing ADRs `agentic/05-workflow.md` lists as "the first ADRs to exist" (0001,
   0002, 0005, 0006, 0011, 0012) are still unwritten.
2. No CLI subcommand runs `AuditChainVerifier`. Small, real, not done.
