<div align="center">

<img src="logo.png" alt="bOps logo" width="160" height="160">

# bOps

**An open-source agent runtime for safely operating Windows and Linux machines
through declarative tools, policies, planning and verification.**

[![Status](https://img.shields.io/badge/status-pre--alpha-orange)](#status)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Platforms](https://img.shields.io/badge/platforms-Windows%20%7C%20Linux-informational)](#)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue)](LICENSE)

</div>

---

## What bOps is

bOps is **not a chatbot with shell access**. It is infrastructure:

> the LLM proposes, the runtime decides and executes, policies authorize,
> verification confirms, the audit log records.

The model never touches the machine. It can only return a structured intent —
`{"tool": "...", "arguments": {...}}` — and a .NET runtime decides whether and
how that intent becomes an action.

## Non-negotiable principles

1. **The LLM never touches the machine.** It returns intent; the runtime executes.
2. **Every tool declares its own risk** (`Read`, `Low`, `Medium`, `High`, `Critical`)
   and policy decides the mode (`automatic`, `approval`, `forbidden`) — a declarative
   model, not a command blacklist.
3. **Every side-effecting action is verified afterwards.** "I restarted nginx" is not a
   valid conclusion until `service.status("nginx")` reports `ACTIVE`.
4. **Everything is audited** — every tool call emits a structured event, whatever the outcome.
5. **The core is agnostic** to LLM provider and operating system, both hidden behind interfaces.
6. **The CLI is the primary interface**, not a stopgap. The web UI is Phase 2.
7. **Everything beyond the minimal runtime is a package** — System, Filesystem, Network,
   Docker, Service and the LLM providers themselves load through the exact same extension
   contract a third-party package uses.

## How it works

```
REQUEST → UNDERSTAND → PLAN → EXECUTE → OBSERVE → EVALUATE ─┬─ goal reached? → FINAL
                          ▲                                  │
                          └──────────── REPLAN ──────────────┘
```

Each iteration passes through the same gates:

| Gate | Responsibility |
|---|---|
| **Registry** | Only tools available on this platform, with satisfied capabilities, are visible to the model |
| **Policy** | `automatic` / `approval` / `forbidden`, per risk level, per tool, per package |
| **Approval** | Human-in-the-loop for anything above the automatic ceiling |
| **Execution** | Typed, validated arguments — there is no generic "run this command" tool |
| **Verification** | A read-only tool confirms the effect actually happened |
| **Audit** | Append-only structured event, success or failure |

## Architecture

```
src/
├── core/
│   ├── bOps.Abstractions/   # The contract / plugin SDK — zero dependencies
│   ├── bOps.Runtime/        # Agent loop, registries
│   ├── bOps.PluginHost/     # Dynamic package loader (V0.10, ADR-0020): manifest, isolated
│   │                        # AssemblyLoadContext, install/enable/disable/remove
│   ├── bOps.Policy/         # Risk model, policy engine, approval flow
│   ├── bOps.Memory/         # Task state and conversation context (SQLite)
│   ├── bOps.Audit/          # Append-only structured audit log
│   ├── bOps.Cli/            # `bops "..."` — the primary interface
│   ├── bOps.Api/            # Minimal API backing the web UI
│   └── bOps.Worker/         # Windows Service / systemd unit — not built yet
├── packages/                # First-party packages — same contract as third-party ones
│   ├── bOps.Packages.System.{Core,Windows,Linux}
│   ├── bOps.Packages.Service.{Core,Windows,Linux}   # V0.11, ADR-0021
│   ├── bOps.Packages.{Filesystem,Network,Docker}.*
│   └── bOps.Packages.Providers.*   # OpenRouter, Ollama, llama.cpp, OpenAI, DeepSeek, Anthropic
└── samples/
    └── bops-sample-plugin/  # A real, purely-demonstrative third-party plugin — see docs/plugins/
```

The core is deliberately small: loop, registries, policy, memory, audit, contract.
Everything else is a package. The only difference between a first-party package and a
third-party one is *where it is loaded from*, never *how it is built*.

**Operating systems are packages too.** Each OS package contributes its own complete tools,
declaring the platforms it serves; the registry picks by platform automatically. Adding a
platform means writing a package — never changing the core.

## Tools

Registered today — this table tracks what actually loads, not what is planned; see the note
below it for what's coming and, deliberately, what never will.

| Package | Tools |
|---|---|
| **System** | `system.info` `system.cpu` `system.memory` `system.disk` `system.swap` `system.io` |
| **Process** | `process.list` `process.inspect` `process.stop` `process.kill` |
| **Filesystem** | `fs.list` `fs.stat` `fs.read` `fs.write` `fs.delete` `fs.search` `fs.hash` `fs.move` |
| **Network** | `network.interfaces` `network.connections` `network.dns` `network.ping` `network.port_check` `network.route` |
| **Service** | `service.list` `service.status` `service.start` `service.stop` `service.restart` (Windows via `ServiceController`, Linux via a fixed `systemctl` invocation — ADR-0021) |
| **Docker** | `docker.containers` `docker.inspect` `docker.logs` `docker.images` `docker.networks` `docker.start` `docker.stop` `docker.restart` |

V0.11 is now fully registered — every capability `piano-bops-v0.9.1-v2.0.md` §7 named for this
version is a real, tested tool.

**Never planned, on purpose:** `system.uptime` (`system.info` already reports it — a second tool
for the same data won't be added), `system.environment` as an unfiltered dump (would hand secrets
to the model), and a generic `process.start` (equivalent to a generic execution tool — see rule
S1). None of these are gaps; they're explicit non-goals.

**Scope notes:**

- `network.route` reports each active interface's directly connected subnet and default
  gateway — genuinely useful for "can this host reach the internet from here?" — not the full OS
  routing table (every destination-specific static route), which would need `GetIpForwardTable2`
  on Windows and `/proc/net/route` parsing on Linux for a shape few ops questions actually need.
- `fs.hash` is SHA-256 only, single algorithm, by design.
- `fs.move` never overwrites an existing destination — a deliberate refusal, not a limitation; a
  deliberate overwrite is a separate `fs.delete` then `fs.move`.
- `process.stop` on Windows can only close a process that has a main window
  (`CloseMainWindow()`) — Windows has no generic SIGTERM equivalent for an arbitrary process, and
  this tool reports that honestly as a failure rather than silently escalating to a forced kill.
  On Linux, `process.stop` sends a real `SIGTERM`, which always applies. `process.kill` (forced,
  `TerminateProcess`/`SIGKILL`) works identically on both platforms.
- `service.start`/`stop`/`restart` on Linux are exercised for real in CI against `systemctl`
  (via the same code path `service.list`/`status` already prove works); the full elevated
  create→start→stop→delete lifecycle is exercised for real only on Windows CI, which runs
  administrator-elevated by default — the equivalent on Linux would need root or a polkit rule
  this project does not control, so it is a documented gap (`HANDOFF.md`), not a silent one.

## LLM providers

Providers are packages too, resolved by id at startup — never a hardcoded switch.

| Provider | Transport | Phase |
|---|---|---|
| OpenRouter | OpenAI-compatible | 1 |
| Ollama | OpenAI-compatible (`/v1`) | 1 |
| llama.cpp (`llama-server`) | OpenAI-compatible, JSON-schema fallback | 1 |
| OpenAI · DeepSeek | OpenAI-compatible | 2 |
| Anthropic | native Messages API adapter | 2 |

Anything else — Bedrock, Vertex, Groq — can be added by a third party as a provider
package, without touching bOps.

## Usage

```bash
bops "this server is slow, find the problem"
bops "check every Docker container and tell me if something is wrong"
bops "list the files under this directory and tell me what's taking up the most space"
bops "delete this temp file"        # fs.delete is High-risk — requires approval before it runs
```

```bash
bops resume <task-id>          # resume a persisted task (V0.7, SQLite-backed) from where it left off
```

```bash
bops plugin install <directory>   # install a local plugin build — disabled until you enable it
bops plugin list                  # every installed plugin, enabled or not
bops plugin enable <id>           # activate now, and on every future run, until disabled
bops plugin disable <id>          # unload it; its files stay on disk
bops plugin remove <id>           # disable (if enabled) and delete it
bops plugin validate <directory>  # check a bops-plugin.json without installing anything
```

`bops diagnose` is **not implemented** — there is no such subcommand, planned or otherwise.

## Roadmap

**V0.1 through V0.11 are done** — runtime, planning/replanning, policy/approval, verification,
Windows+Linux parity, Filesystem/Network/Docker, persistence, five LLM providers, `bOps.Api` +
the Angular UI, repository/licensing readiness, the dynamic plugin loader, and now the full
operational capability set: every read-only and side-effecting tool the plan named for V0.11.

| | |
|---|---|
| `V0.1` | Minimal runtime: chat model, tool registry, first read-only System tools |
| `V0.2` | Explicit agent loop with replanning |
| `V0.3` | Policy engine + approval flow |
| `V0.4` | Post-action verification |
| `V0.5` | Windows + Linux parity, Filesystem and Network packages, CI on both OSes |
| `V0.6` | Docker package with conditional capability discovery |
| `V0.7` | Persistent, resumable tasks (SQLite) |
| `V0.8` | Anthropic, OpenAI and DeepSeek provider packages |
| `V0.9` | `bOps.Api` + Angular UI: live agent activity, approvals, settings |
| `V0.9.1` | Repository integrity and licensing readiness — SPDX headers, SBOM, NOTICE, CI fixed |
| `V0.10` | Dynamic plugin loader (`bOps.PluginHost`, ADR-0020): manifest, isolated `AssemblyLoadContext`, `bops plugin *` |
| `V0.11` | Full operational capability set: `system.swap`/`io`, `process.inspect`/`stop`/`kill`, `fs.search`/`hash`/`move`, `network.port_check`/`route`, and the new `Service.{Core,Windows,Linux}` package (`service.list`/`status`/`start`/`stop`/`restart`, ADR-0021) |

**From here, the remaining backlog** — V1.0 hardening, and the open-core commercial roadmap
beyond it (Skills/Evidence, multi-agent, entitlement, a private Control Plane and Portal, and
commercial DBA Skills) — lives in
[`piano-bops-v0.9.1-v2.0.md`](piano-bops-v0.9.1-v2.0.md). `bOps` itself stays Apache-2.0,
forever, for anyone, including commercial use — see [Licensing](#license) below and
[`docs/licensing.md`](docs/licensing.md).

## Extending bOps

A plugin is one or more .NET assemblies referencing only the published `bOps.Abstractions`
package, with an entry type implementing `IToolProvider` or `IModelProviderPackage`, plus a
`bops-plugin.json` manifest naming it. [`samples/bops-sample-plugin/`](samples/bops-sample-plugin/)
is a real, working one — build it, then `bops plugin install`/`enable` it, as a starting point:

```json
{
  "SchemaVersion": 1,
  "Id": "acme.sample-plugin",
  "Publisher": "Acme",
  "Version": "1.0.0",
  "MinHostAbstractionsVersion": "0.10.0",
  "EntryAssembly": "Acme.SamplePlugin.dll",
  "EntryType": "Acme.SamplePlugin.SampleToolProvider",
  "DeclaredCapabilities": ["sample.echo"],
  "Dependencies": [],
  "MaxDeclaredRisk": "Read"
}
```

Packages are never trusted at their word: `DeclaredCapabilities`, `Dependencies` and
`MaxDeclaredRisk` are informational only, shown to the operator before they enable a plugin —
the policy engine's own per-package risk ceiling in `policy.yaml` is what is actually enforced.
A plugin is loaded in an isolated, collectible `AssemblyLoadContext` sharing a single copy of
`bOps.Abstractions` with the host (ADR-0020) and stays disabled until an operator enables it
explicitly — auto-discovery is never silent. See [`docs/plugins/getting-started.md`](docs/plugins/getting-started.md)
for the full walkthrough, and rule S8: this isolation is dependency isolation, not a security
sandbox — a loaded plugin runs with the host's own privileges.

## Status

Pre-alpha. The architecture is settled and documented; V0.1 through V0.11 are built and tested,
including a real dynamic plugin loader and the full operational capability set (read-only and
side-effecting). V1.0 hardening is next. Not yet suitable for production use.

## Documentation

| | |
|---|---|
| [`agentic/`](agentic/) | Binding specification and rules — the authoritative source |
| [`agentic/06-decisions.md`](agentic/06-decisions.md) | Decision register: what was chosen, what was rejected, why |
| [`piano-bops-v0.9.1-v2.0.md`](piano-bops-v0.9.1-v2.0.md) | The active backlog from V0.9.1 onward — versions, gates, scope, open-core boundary |
| [`docs/licensing.md`](docs/licensing.md) | What Apache-2.0 does and doesn't grant, the open-core repository split, CLA policy |
| [`piano-bops.md`](piano-bops.md) | Original development plan through V0.9 (Italian). Historical — see [corrections](agentic/07-plan-corrections.md) |
| [`docs/architecture/`](docs/architecture/) | Architecture decision records |
| [`docs/security/`](docs/security/) | Risk model, default policies, threat model |
| [`docs/plugins/getting-started.md`](docs/plugins/getting-started.md) | Write your first bOps plugin |

## License

Apache-2.0 — see [LICENSE](LICENSE), always and for everyone, including commercial use.
Packages are separately licensed: bOps does not require third-party packages to be open
source. `bOps` follows an **open-core** model: the core, SDK, first-party packages and this
local UI stay Apache-2.0 in this public repository; official commercial Skills, a Control
Plane and an enterprise Portal live in a separate private repository and are never merged
here. See [`docs/licensing.md`](docs/licensing.md) for the full policy, including what
Apache-2.0 does not grant (no trademark rights — `bOps`/`bSoft` are not registered marks) and
how third-party packages and contributions (via CLA) are handled.
