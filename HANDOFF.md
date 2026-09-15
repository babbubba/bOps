# Handoff — V0.6 (Docker package with conditional capability discovery) complete

Written at the end of the session that implemented V0.6 on top of the completed V0.5
Filesystem/Network/AppHost/CI work. Everything below is exact, not a summary — follow it
literally to resume.

## State right now

**`dotnet build bOps.sln` builds clean end to end — 0 warnings, 0 errors** in both `Debug` and
`Release` (verified with a full clean of every `bin`/`obj` and a from-scratch rebuild of both
configurations). **`dotnet test bOps.sln`: 142 passing, 7 skipped, 0 failed** (both
configurations, `--filter "Category!=LiveModel"` matching CI exactly) — same 7 pre-existing skips
as the V0.5 handoff (6 Linux-only conformance tests, 1 symlink-privilege test). 24 projects in
`bOps.sln` now, up from 23 at the end of V0.5.

**This dev environment has a real, running Docker daemon (Docker Desktop 4.88.1, engine 29.7.2)**,
unlike every prior package in this repository — the ten new `bOps.Packages.Docker.Tests` all ran
for real against it, not just against a test double: they create a uniquely named (`bops-test-*`)
`alpine:3.20` container, start it, stop it, restart it, inspect it, read its logs, and remove it —
every step actually executed and its result actually asserted, verified clean (no leftover
`bops-test-*` containers) after both the Debug and Release test runs. This is the first session
where "integration tests against real targets" (agentic/04-testing-rules.md) could be honored for
a daemon-backed package rather than only documented and skipped.

CLI smoke test (`cd src/core/bOps.Cli && dotnet run -- "how is this machine doing?"`): DI wiring,
config binding, tool registry construction, **and a real `docker.System.PingAsync()` capability
probe against the live daemon** all succeed during `RefreshCapabilitiesAsync()` — the task then
fails cleanly at the planning call's HTTP 401 (`ApiKey` empty, rule S6), exactly as every prior
session's smoke test did.

## What this session did

Implemented V0.6 per `agentic/00-project-spec.md`'s roadmap: **"Docker package with conditional
capability discovery."**

### `bOps.Packages.Docker` — eight tools, `Docker.DotNet` 3.125.15

No OS split (rule A8 does not require one when the transport already abstracts the difference —
`Docker.DotNet` talks to the daemon over a named pipe on Windows or a Unix socket on Linux, picked
by `DockerClientFactory`'s platform check). All under `PackageId("bops.packages.docker")`, all
declaring `Requires: ["docker"]`:

- **`docker.containers`**, **`docker.images`**, **`docker.networks`**, **`docker.inspect`**,
  **`docker.logs`** — `RiskLevel.Read`. `docker.inspect` reports id/name/image/status/running as
  single-line JSON (`DockerInspectOutput`) — it is the verification target for the three tools
  below, so its output needs to be read back reliably, same reasoning as V0.5's `fs.stat`.
  `docker.logs` uses the `MultiplexedStream`/`ReadOutputToEndAsync` API (log output is
  stdout/stderr-multiplexed by the daemon unless the container has a TTY) rather than reading the
  raw stream, which would otherwise return frame-header bytes mixed into the text.
- **`docker.start`**, **`docker.stop`**, **`docker.restart`** — `RiskLevel.Medium`
  (`piano-bops.md` §11's own worked example), `IVerifiableTool`, verified via `docker.inspect` on
  the same container (`ArgumentsFrom: ["container"]`). `start`/`restart`: `status == "running"`
  afterwards → `Confirmed`; `stop`: anything *other than* `"running"` → `Confirmed`. Either way, an
  inspect call that itself fails (denied, container renamed away, daemon hiccup) → `Inconclusive`,
  never a guess.

**Not built**: `docker.pause`/`unpause`, `docker.exec`, `docker.build`/`pull`/`push`, container
creation/removal as agent-facing tools. None of these are named in V0.6's roadmap line or in
`piano-bops.md`'s catalog for this package; the eight tools built are exactly that catalog.

### Conditional capability discovery — the actual point of this version

`DockerCapability.IsAvailableAsync` pings the daemon (`client.System.PingAsync()`); any exception
(`DockerApiException`, `HttpRequestException`, `TimeoutException`, `IOException`) means
unavailable, never a thrown error the caller must handle. `bOps.Cli/Program.cs` registers this
check on the concrete `CachingCapabilityProbe` — resolved from DI by pattern-matching
`ICapabilityProbe` (the interface has no `RegisterCheck`; only the concrete type does, by design —
see `bOps.Runtime.CachingCapabilityProbe`, which has existed, fully generic and unused by anything
until now, since V0.1) — **before** the existing `RefreshCapabilitiesAsync()` call. No change was
needed to `bOps.Runtime`, `bOps.Abstractions`, or `ToolRegistry` at all: `IsVisible`'s capability
check (`ToolRegistry.cs`) was already exactly what rule B4 describes, and this version is simply
the first package that actually exercises it with a real, sometimes-absent daemon. This is the
same shape V0.4 was for verification and V0.5 was for a real non-`Read` tool: the contract was
already there; a real package now exercises it.

### Tests

`bOps.Packages.Docker.Tests`, ten tests: a comprehensive round-trip (`docker.containers` sees a
freshly created container → `docker.inspect` reports it not running → `docker.start` + inspect
confirms `Confirmed` → `docker.stop` + inspect confirms `Confirmed` → `docker.restart` + inspect
confirms `Confirmed` → `docker.logs` succeeds), `docker.images`/`docker.networks` not throwing,
`docker.inspect` failing cleanly for a container that never existed, the three verified tools'
`EvaluateVerificationAsync` going `Inconclusive` when the inspect call itself fails (no daemon
needed — a synthetic failed `ToolCallResult`), and two manifest-shape checks (all eight tools
present and `Requires: ["docker"]`; the three action tools are `Medium` risk with
`docker.inspect`-based `VerificationSpec`s and implement `IVerifiableTool`).

**`DockerAvailableFactAttribute`** (mirrors `LinuxOnlyFactAttribute` from V0.1 and V0.5's
`SymlinkCapableFactAttribute`): probes via the `docker` CLI (`docker version --format ...`,
synchronous `Process.Start`/`WaitForExit`), not via `IDockerClientFactory` — a `FactAttribute`
constructor cannot be `async`, and blocking on the async client call
(`.GetAwaiter().GetResult()`) is exactly what `agentic/02-coding-standards.md`'s async rules
forbid. Shelling out to a *diagnostic* CLI command from test infrastructure has nothing to do with
rule S1 (no generic execution tool exposed to the *model*) — worth keeping distinct in your head if
you touch this again. Skips visibly, never silently, wherever no daemon responds within 5s.

**`TestContainer`** creates and removes a uniquely named (`bops-test-{guid}`) `alpine:3.20`
container per test that needs one, deliberately *not* via xunit's `IAsyncLifetime` — that would run
before a `[DockerAvailableFact]` skip is known to the runner, risking a Docker call from a test
meant to be skipped entirely on a machine with no daemon. Every test that touches the daemon
creates its own container inside its own body and disposes it there.

### Rule A1 test updated

`docker.images` and `docker.networks` (the two tool names not already present from V0.5's
forward-looking list) added to `bOps.Architecture.Tests`' `ForbiddenTerms` — confirmed clean
against `bOps.Runtime`/`Policy`/`Audit` (V0.6 touched none of those three assemblies at all; this
was a completeness update, not a response to a real finding).

## Design choices worth knowing before extending this further

- **`DockerClientConfiguration` is disposed immediately after `CreateClient()`** (`using var
  configuration = ...; return configuration.CreateClient();`) — CA1062/CA2000 flagged the
  one-liner version. The returned `DockerClient` does not depend on the configuration object
  staying alive (no credentials were supplied here; only an endpoint `Uri`), so this is safe, not
  a workaround.
- **`docker.logs` combines stdout and stderr into one string**, not two separate fields. Nothing
  downstream (the model, the observation text) currently benefits from keeping them apart, and
  `ToolCallResult.Output` is a single string by contract (rule A2) — splitting them would need a
  formatting convention this version has no consumer for yet.
- **The capability check lives in the package (`DockerCapability`), the registration call lives in
  the host (`bOps.Cli/Program.cs`)** — not the other way around, and not inside `bOps.Runtime`.
  `CachingCapabilityProbe.RegisterCheck` takes a bare `Func<CancellationToken, Task<bool>>`
  precisely so the core never has to know what "docker" means (rule A1); only the composition root,
  which is allowed to know package specifics (it already picks `WindowsSystemToolProvider` vs.
  `LinuxSystemToolProvider` by name), wires the two together.
- **Restarting a stopped container is not a no-op or an error** — Docker's own `restart` semantics
  start it if it was already stopped, which is exactly what the round-trip test exercises
  (`docker.stop` → `docker.restart` → running again) and exactly why `docker.restart`'s
  verification predicate is identical to `docker.start`'s.

## What V0.6 deliberately does NOT have yet

- **No `docker.pause`/`unpause`, `docker.exec`, `docker.build`/`pull`/`push`.** Not named in this
  version's roadmap line or in `piano-bops.md`'s catalog for this package.
- **No `Service` package** (`service.list`/`status`/`start`/`stop`/`restart`) — not named in V0.5
  or V0.6's roadmap lines at all. The next version that needs it should say so explicitly rather
  than assume it slots in here because the shape is similar to Docker's.
- **The Docker capability check is a plain ping, not the richer discovery `RefreshCapabilitiesAsync`
  ultimately supports** (per-capability TTL is already there from V0.1's `CachingCapabilityProbe`;
  nothing about *this* capability's check needed anything beyond it).
- **No CI verification that `docker.*` actually passes on GitHub's runners.** `windows-latest` and
  `ubuntu-latest` GitHub-hosted runners both ship Docker, but this was verified only against this
  session's local Docker Desktop — `[DockerAvailableFact]` will skip cleanly rather than fail if a
  future CI run finds no responsive daemon, but nobody has watched that actually happen on a real
  runner yet.
- **`bOps.AppHost` (V0.5) still has not been run** — carried forward again; still only
  `dotnet build`-verified.
- **No `bOps.Memory` project / SQLite** — V0.7, the next roadmap line, and genuinely next.
- **No dynamic plugin loading** — V0.10. Still direct `ProjectReference`s, including this package.
- **No CLI command to run `AuditChainVerifier` on demand** — carried forward again, still small,
  still not done.

## Next steps

V0.6 is genuinely done, and unusually well-verified for a first pass: the Docker package's full
verified action lifecycle (start → verify → stop → verify → restart → verify) ran for real against
a real daemon, not just against test doubles, in both Debug and Release. Conditional capability
discovery — the actual point of this version — is now exercised by a real capability that is
genuinely sometimes absent, closing the loop `ICapabilityProbe`/`RefreshCapabilitiesAsync` opened
at V0.1 and left unused since.

**V0.7 — "Persistent, resumable tasks (SQLite)" — has not been started.** Per the scope-discipline
rule, the next session should begin by reading `agentic/00-project-spec.md`'s roadmap entry for
V0.7 and re-reading `agentic/01-architecture-rules.md`'s `TaskState`/`PlanStep`/`AgentPlan` shapes
(rule B9) before writing any code: V0.7 is the first version where `TaskState` needs to survive the
process, which touches how `AgentRunner.RunAsync` currently builds and returns state entirely
in-memory. Check `06-decisions.md` for whether persistence architecture was already decided (it
was not named in the initial decision register read this session — read it again, don't assume).
This is also the first version that needs a real `bOps.Memory` project to exist at all — currently
there is none in `src/core/`, only named in the roadmap and in `00-project-spec.md`'s list of what
the core consists of.

Two smaller, non-urgent items carried forward again from every prior handoff, still real, still
judged out of scope for a session implementing code rather than backfilling documentation:

1. The six pre-existing ADRs `agentic/05-workflow.md` lists as "the first ADRs to exist" (0001,
   0002, 0005, 0006, 0011, 0012) are still unwritten.
2. No CLI subcommand runs `AuditChainVerifier`. Small, real, not done.
