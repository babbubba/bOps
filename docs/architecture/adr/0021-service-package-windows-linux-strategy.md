# ADR-0021 — Service package: Windows and Linux collection strategy

Status: Accepted

Written before any code in `bOps.Packages.Service.*`, as required by `piano-bops-v0.9.1-v2.0.md`
§7 note 8 and `agentic/05-workflow.md`'s ADR trigger list (this adds a dependency choice and a new
package family whose Linux half has more than one real design). ADR-0006/D-005 already settled
that operating systems are packages behind the shared `System.*` shell (rule A8); this ADR is
specifically about *how* the Linux half of `Service.*` talks to systemd for V0.11's read-only
tranche (`service.list`, `service.status`).

## Context

`service.list` and `service.status` need to enumerate systemd units and read one unit's state.
Windows has a direct answer: `System.ServiceProcess.ServiceController` is a first-party BCL-shaped
API over the Service Control Manager, the same kind of native wrapper `system.memory`
(`GlobalMemoryStatusEx`) and `process.list` (`Process`) already use. Linux has no such BCL type —
querying systemd means either shelling out to `systemctl`, or speaking D-Bus directly to
`org.freedesktop.systemd1` (the approach `bOps.Packages.Docker` uses for the Docker daemon, via
`Docker.DotNet` rather than shelling to the `docker` CLI). `piano-bops.md` §11 itself already
weighs this exact choice and recommends starting with the shell-out.

This choice matters more than it looks because of rule S1 ("no generic execution tool"): a Linux
Service tool that runs an external process needs to be — and needs to be *seen* to be — nothing
like `shell.run`.

## Decision

**Windows**: `System.ServiceProcess.ServiceController` (`GetServices()` for the list, `new
ServiceController(name).Status` for one). New `PackageReference` to
`System.ServiceProcess.ServiceController` (Microsoft, MIT) — SBOM/THIRD-PARTY-NOTICES regenerated
alongside this ADR's code.

**Linux**: shell out to `systemctl`, not D-Bus, for V0.11. Concretely:

- `Process.Start` with `ProcessStartInfo.ArgumentList`, never `UseShellExecute` and never a
  composed command string. The OS `exec` call receives an argv array directly; there is no shell
  to interpret `;`, `|`, backticks, or anything else even if a unit name contained them.
- Exactly two fixed invocation shapes exist, both hardcoded in the package, neither influenced by
  the model beyond the one `name` argument `service.status` takes:
  `systemctl list-units --type=service --all --no-legend --no-pager --plain` and
  `systemctl show <name> --property=LoadState,ActiveState,SubState,Description --no-pager`. There
  is no path from a tool call to an arbitrary `systemctl` subcommand, let alone an arbitrary
  binary — the model cannot supply flags, and could not reach a shell even if it tried, because
  none is ever invoked.
- `--property=` output (`Key=Value` lines) is parsed, never `systemctl status`'s human-formatted
  text — the same reason `system.cpu` reads `/proc/stat` fields instead of parsing `top`'s output.
- A `name` that doesn't parse as a plausible systemd unit name never reaches `systemctl` at all;
  it is reported as "not found" the same way a genuinely absent unit is (see Consequences).

This does **not** revisit S1. S1 forbids a tool whose *effect* is model-controlled command
execution — an escape hatch. Here the command is fixed by the package; the only external input is
one argv element passed straight through, with no shell to reinterpret it, exactly like
`network.ping`'s `host` parameter or `fs.read`'s `path` parameter are already model-controlled
inputs to an otherwise fixed operation. `Docker.DotNet` was preferred over shelling to `docker`
only because a maintained client library for the daemon protocol already existed; no equivalently
maintained, dependency-light .NET library exists for the systemd D-Bus API.

## Alternatives considered

- **D-Bus via `Tmds.DBus` against `org.freedesktop.systemd1`.** No child process, and it is the
  option that best mirrors `Docker.DotNet`'s "talk to the real API" shape. Rejected for V0.11
  specifically: it adds a new NuGet dependency and a materially larger amount of new, unfamiliar
  marshaling code (D-Bus interface proxies, the systemd object/interface surface) that cannot be
  exercised against a real bus from this project's Windows development environment — only CI's
  `ubuntu-latest` runner can, which means every mistake in that marshaling code is discovered
  after the fact, in CI, for a package this session cannot iterate on locally the way `/proc`
  parsing (plain file reads) can be sanity-checked in isolation. `systemctl`'s plain-text
  `--property=` output is a much smaller, better-documented surface with an obvious manual sanity
  check (`systemctl show <unit> --property=... --no-pager`) on any systemd machine, including a
  GitHub Actions `ubuntu-latest` runner, which really does run systemd as PID 1 (it is a full VM,
  not a container), so this tranche's tests exercise a real `systemctl` process, never a mock.
- **A generic `service.exec` / `systemctl.run` tool taking arbitrary arguments.** Never considered
  seriously — this is exactly rule S1's forbidden shape, and unlike the two options above it would
  put the model in control of *which* `systemctl` invocation runs at all.

## Consequences

- `service.list`/`service.status` report a status **normalized** to `"running"` / `"stopped"` /
  `"failed"` / `"unknown"` on both platforms (rule A8: two OS packages producing the same tool
  must produce the same shape) — Windows's `ServiceControllerStatus` and systemd's `ActiveState`
  use different vocabularies neither the manifest nor the LLM should have to know apart.
- An invalid or absent unit name and a syntactically implausible one are reported identically
  (`exists: false`) — from an operator's-question point of view ("is `ngnix` running?", a typo)
  they are the same fact, and neither should cause a raw `systemctl` parse error to reach the
  model.
- `service.status`'s JSON shape (`name`, `exists`, `status`, `description`) is deliberately the
  same kind of structured, parseable output `fs.stat` produces, because `piano-bops-v0.9.1-v2.0.md`
  §7 note 10 already names it as the verification target for V0.11's second tranche
  (`service.start`/`stop`/`restart`) — designing that shape now, while it is still Read-only and
  therefore low-stakes to get right, avoids a breaking change to it later.
- If `systemctl`-shelling proves unreliable or too slow in practice, migrating the Linux
  implementation to D-Bus is a change confined entirely to `bOps.Packages.Service.Linux`'s
  internals — the manifest, the shared `Service.Core` shapes, and every other package are
  unaffected. This ADR does not need to be revisited to make that change; a new one only if the
  decision itself is reversed.
- This ADR says nothing about `service.start`/`stop`/`restart` (V0.11's second tranche). Starting
  or stopping a unit is a materially different risk profile (non-`Read`, needs
  `VerificationSpec`/`IVerifiableTool`, needs policy/approval) and is deliberately left for its own
  design pass when that tranche starts, per `piano-bops-v0.9.1-v2.0.md` §7's own sequencing.
