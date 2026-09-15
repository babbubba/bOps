# Handoff — V0.11 tranche 1 done, uncommitted; hand off toward V0.11 tranche 2

V0.11's first, read-only tranche (`piano-bops-v0.9.1-v2.0.md` §7, implementation note 2) is
implemented, built, and tested end to end against real Windows APIs — this dev environment has no
Linux host, so the Linux half is built and unit-tested for shape but not run for real here; it
runs on CI's `ubuntu-latest`, a real VM with systemd as PID 1, not a container. **Nothing from this
session is committed yet.**

## What this tranche delivers

Nine new Read-risk tools across four packages, plus a new package family:

- **`bOps.Packages.System.{Core,Windows,Linux}`** — `system.swap`, `system.io`,
  `process.inspect`, following the existing A8 shell pattern exactly (`SystemToolBases.cs`,
  `SystemToolManifests.cs`, `Results.cs`, `SystemToolFormatting.cs` in `.Core`; OS-specific
  collection in `.Windows`/`.Linux`).
  - `system.swap`: Windows via `GlobalMemoryStatusEx`'s page-file fields (already-imported native
    call, no new dependency) — deliberately not literal `pagefile.sys` usage; Linux via
    `/proc/meminfo`'s `SwapTotal`/`SwapFree`.
  - `system.io`: per-device read/write KB/s, sampled over 500ms like `system.cpu` already does.
    Windows via the `PhysicalDisk` performance counter category (one instance per physical disk).
    Linux by differencing two `/proc/diskstats` samples, restricted to whole-disk devices (a
    device counts as one when `/sys/block/<name>` exists) so a single-partition disk isn't
    double-counted between its disk and partition lines.
  - `process.inspect`: single-PID JSON observation (`pid`, `exists`, `name`, `workingSetMb`,
    `threadCount`, `startTimeUtc`), reporting a missing PID as `exists: false` the same way
    `fs.stat` reports a missing path — a successful observation of a negative fact, not a
    failure. Deliberately excludes command-line arguments (could leak another process's secrets
    into model context).
- **`bOps.Packages.Filesystem`** — `fs.search` (glob-by-name, recursive, every candidate's
  resolved path checked against the read policy both before it's reported and before the search
  descends into it — rule S11 — with a visited-set guard against symlink cycles) and `fs.hash`
  (SHA-256 + size, single-line JSON, single algorithm by design).
- **`bOps.Packages.Network`** — `network.port_check` (TCP connect attempt via `TcpClient`, closed/
  refused/timed-out reported as a successful negative observation, not a failure) and
  `network.route` (each active interface's directly connected subnet + default gateway via
  `NetworkInterface`, **not** the full OS routing table — see the Scope boundaries section below).
- **`bOps.Packages.Service.{Core,Windows,Linux}`** (new package family) — `service.list`,
  `service.status`, status normalized to `"running"`/`"stopped"`/`"failed"`/`"unknown"` on both
  platforms (rule A8: two OS packages producing the same tool must produce the same shape).
  - **ADR-0021** (`docs/architecture/adr/0021-service-package-windows-linux-strategy.md`) —
    written before this package's code, as `piano-bops-v0.9.1-v2.0.md` §7 note 8 and
    `agentic/05-workflow.md`'s ADR trigger list both require. Windows: `ServiceController` (new
    `System.ServiceProcess.ServiceController` NuGet dependency, MIT). Linux: a fixed,
    non-composable `systemctl` invocation via `ProcessStartInfo.ArgumentList` (never a shell
    string) — read the ADR for why this doesn't reopen rule S1, and why D-Bus was considered and
    deferred rather than chosen.
  - `service.status`'s JSON shape (`name`, `exists`, `status`, `description`) is deliberately
    already the verification-target shape `piano-bops-v0.9.1-v2.0.md` §7 note 10 names for V0.11's
    second tranche (`service.start`/`stop`/`restart` will verify against it) — designing it now,
    while still Read-only, avoids a breaking change to it later.
- **CLI wiring** (`bOps.Cli/Program.cs`) — the Service package registers exactly like the System
  package does: OS-picked `IToolProvider`, `PackageId("bops.packages.service.{windows|linux}")`.
  No policy.yaml entry needed (Read tools use the built-in default).
- **README.md** — tool table, architecture tree, roadmap table, Status section all updated for
  what's now actually registered, including the `network.route`/`fs.hash` scope notes.
- **SBOM/THIRD-PARTY-NOTICES regenerated** — one new component,
  `System.ServiceProcess.ServiceController@10.0.0` (MIT, auto-resolved by CycloneDX, no manual
  override needed). 143 .NET components now (was 142).

## Verified for real, this session

- `dotnet build bOps.slnx --configuration Release`: **0 warnings, 0 errors**, full solution
  including the three new Service projects and their two new test projects.
- `dotnet test bOps.slnx --configuration Release --filter "Category!=LiveModel"`: **all 15 test
  assemblies passed, 0 failures**, including:
  - `bOps.Packages.System.Windows.Tests`: 11/11, including real `system.swap` (real
    `GlobalMemoryStatusEx` page-file fields), real `system.io` (real `PhysicalDisk` performance
    counters, sampled for real over 500ms), real `process.inspect` (this test process's own PID,
    and a deliberately-implausible PID for the missing case).
  - `bOps.Packages.Filesystem.Tests`: 32/33 (1 symlink-privilege skip, pre-existing), including
    `fs.search` recursing a real temp directory tree, refusing to descend into a subdirectory the
    read policy doesn't cover, and `fs.hash` against a known SHA-256 of `"hello world"`.
  - `bOps.Packages.Network.Tests`: 10/10, including `network.port_check` against a real loopback
    `TcpListener` (open) and a bind-then-release port (not reachable), and `network.route`
    against this machine's real interfaces.
  - `bOps.Packages.Service.Windows.Tests`: **4/4, against the real Windows Service Control
    Manager** — `service.list` enumerating every real installed service, `service.status`
    against the real `EventLog` service (exists) and a made-up name (does not).
  - `bOps.Architecture.Tests`: still 4/4 — rule A1 (core never names a package) holds; nothing in
    this tranche touches `bOps.Runtime`/`Policy`/`Memory`/`Audit`.
  - `bOps.Packages.System.Linux.Tests` / `bOps.Packages.Service.Linux.Tests`: skip visibly (no
    Linux host here), as they have since V0.5 — they run for real on CI's `ubuntu-latest`.
- **Manually ran the real `bops.exe`** end to end with a real goal string (no API key configured,
  so it fails at the expected point — a 401 from OpenRouter) specifically to confirm the new
  Service registration in `Program.cs` doesn't throw during composition, the same class of bug
  V0.10's equivalent manual check caught for the plugin loader's relative-path bug. It didn't;
  the failure trace shows the run reaching `AgentRunner.CreatePlanAsync` normally.

## Scope boundaries — deliberate, not gaps to silently fill later

- **`network.route` reports each interface's directly connected subnet and default gateway, not
  the full OS routing table.** The full table needs `GetIpForwardTable2` P/Invoke on Windows
  (a large, union-typed struct with real marshaling risk this environment could not have verified
  against a live Windows box the way the chosen implementation was) and `/proc/net/route` parsing
  on Linux. The reduced scope answers the question most ops queries actually ask ("can this host
  reach the internet from here?") using only already-proven `NetworkInterface` APIs. Documented in
  the tool's own XML doc comment and in README's Scope note.
- **`fs.hash` is SHA-256 only** — no algorithm parameter. Nothing in this tranche needs a second
  algorithm; adding a parameter for a hypothetical future one would be exactly the kind of
  unrequested flexibility `agentic/02-coding-standards.md` argues against.
- **`process.inspect` never reports a process's command-line arguments.** Another process's argv
  can contain secrets (an API key passed as a CLI flag, for instance); this tool reports identity
  and resource usage only.
- **The Linux `Service` package shells to `systemctl`, not D-Bus.** ADR-0021 records the full
  reasoning; short version: `Tmds.DBus` + the systemd D-Bus interface surface is meaningfully more
  new, unfamiliar marshaling code than this session could verify against a real bus (no Linux host
  here), for a plain-text `--property=` parse that is easy to sanity-check by hand on any systemd
  machine, including CI's own runner.
- **This tranche adds no side-effecting tools.** `fs.move`, `service.start`/`stop`/`restart`, and
  a controlled process-stop operation are V0.11's second tranche, explicitly gated by
  `piano-bops-v0.9.1-v2.0.md` §7 on this tranche's read-only verifiers landing first — which they
  now have.

## Exact next steps, in order

1. Commit this tranche in a few well-scoped commits (System extensions, Filesystem extensions,
   Network extensions, the new Service package family + ADR-0021 + CLI wiring, README/SBOM) — the
   same granularity V0.10 committed at.
2. Ask the user before pushing (standing rule, `agentic/05-workflow.md`). No `Co-Authored-By:
   Claude` trailer — the user asked mid-session, in an earlier session on this repository, for
   that to stop, for this repo going forward.
3. After pushing, confirm CI is green on GitHub's own runners — this is specifically where the
   Linux half of `system.swap`/`system.io`/`process.inspect` and the entire `Service.Linux`
   package (including the real `systemctl show`/`list-units` subprocess calls against CI's real
   systemd) gets its first real execution. Treat any Linux-only failure there as expected new
   information, not a surprise — exactly the posture that caught three real bugs when V0.9.1's CI
   was first fixed.

## Next: V0.11 tranche 2 — side-effecting operations

Per the plan (§7, implementation note 3): `fs.move`, a controlled process-stop operation, and
`service.start`/`stop`/`restart`. All non-`Read`, so each needs a `VerificationSpec` and
`IVerifiableTool` before it can even register (rule B3) — `fs.hash` (origin/destination/content
identity) and `service.status`/`process.inspect` (observed state after the action) already exist
as the verification targets this tranche was designed to hand off to, per the note above. Name and
semantics for graceful-stop vs. kill-forced process operations need resolving *before* writing
those two tools' manifests, per the plan's own explicit caution against ambiguous aliases here.
