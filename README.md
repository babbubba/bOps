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
│   ├── bOps.Runtime/        # Agent loop, registries, plugin loader
│   ├── bOps.Policy/         # Risk model, policy engine, approval flow
│   ├── bOps.Memory/         # Task state and conversation context (SQLite)
│   ├── bOps.Audit/          # Append-only structured audit log
│   ├── bOps.Cli/            # `bops "..."` — the primary interface
│   ├── bOps.Api/            # Minimal API backing the web UI
│   └── bOps.Worker/         # Windows Service / systemd unit — not built yet
└── packages/                # First-party packages — same contract as third-party ones
    ├── bOps.Packages.System.{Core,Windows,Linux}
    ├── bOps.Packages.{Filesystem,Network,Docker}.*
    ├── bOps.Packages.Service.*      # planned, V0.11 — does not exist yet
    └── bOps.Packages.Providers.*   # OpenRouter, Ollama, llama.cpp, OpenAI, DeepSeek, Anthropic
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
| **System** | `system.info` `system.cpu` `system.memory` `system.disk` |
| **Process** | `process.list` |
| **Filesystem** | `fs.list` `fs.stat` `fs.read` `fs.write` `fs.delete` |
| **Network** | `network.interfaces` `network.connections` `network.dns` `network.ping` |
| **Docker** | `docker.containers` `docker.inspect` `docker.logs` `docker.images` `docker.networks` `docker.start` `docker.stop` `docker.restart` |

**Planned, not yet registered — arriving at V0.11** (see
[`piano-bops-v0.9.1-v2.0.md`](piano-bops-v0.9.1-v2.0.md) §7): `system.swap`, `system.io`,
`process.inspect`, `fs.search`, `fs.hash`, `fs.move`, `network.port_check`, `network.route`, and
a new `Service` package (`service.list`, `service.status`, then — after their read-only
counterparts land — `service.start`/`stop`/`restart` and controlled process-stop operations).

**Never planned, on purpose:** `system.uptime` (`system.info` already reports it — a second tool
for the same data won't be added), `system.environment` as an unfiltered dump (would hand secrets
to the model), and a generic `process.start` (equivalent to a generic execution tool — see rule
S1). None of these are gaps; they're explicit non-goals.

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

`bops diagnose` and `bops plugin list` are **not implemented yet** — the plugin loader (and the
`plugin` subcommand family) arrives at V0.10; do not treat either as available today.

## Roadmap

**V0.1 through V0.9 are done** — runtime, planning/replanning, policy/approval, verification,
Windows+Linux parity, Filesystem/Network/Docker, persistence, five LLM providers, and
`bOps.Api` + the Angular UI.

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

**From V0.9.1 on**, the full backlog — repository/licensing readiness, the dynamic plugin
loader, the remaining operational capabilities, V1.0 hardening, and the open-core commercial
roadmap beyond it (Skills/Evidence, multi-agent, entitlement, a private Control Plane and
Portal, and commercial DBA Skills) — lives in
[`piano-bops-v0.9.1-v2.0.md`](piano-bops-v0.9.1-v2.0.md). `bOps` itself stays Apache-2.0,
forever, for anyone, including commercial use — see [Licensing](#license) below and
[`docs/licensing.md`](docs/licensing.md).

## Extending bOps

A package is one or more .NET assemblies referencing only `bOps.Abstractions`,
implementing `IToolProvider` and/or `IModelProviderPackage`, plus a `bops-plugin.json`
manifest declaring what it contributes.

```json
{
  "id": "bops-plugin-sqlserver",
  "displayName": "SQL Server DBA Toolkit",
  "publisher": "AcmeCorp",
  "version": "1.2.0",
  "minHostAbstractionsVersion": "1.0.0",
  "maxDeclaredRisk": "High",
  "requires": ["sqlserver"],
  "contributes": { "tools": ["sqlserver.wait_stats", "…"], "modelProviders": [] }
}
```

Packages are never trusted at their word: the policy engine applies a per-package risk
ceiling independent of what the manifest declares, packages are opt-in rather than
auto-discovered, and every call they make flows through the same audit pipeline.

## Status

Pre-alpha. The architecture is settled and documented; V0.1 through V0.9 are built and tested;
V0.9.1 onward is repository/licensing readiness, then the plugin loader and remaining
capabilities. Not yet suitable for production use.

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
| [`docs/plugins/`](docs/plugins/) | Write your first bOps package |

## License

Apache-2.0 — see [LICENSE](LICENSE), always and for everyone, including commercial use.
Packages are separately licensed: bOps does not require third-party packages to be open
source. `bOps` follows an **open-core** model: the core, SDK, first-party packages and this
local UI stay Apache-2.0 in this public repository; official commercial Skills, a Control
Plane and an enterprise Portal live in a separate private repository and are never merged
here. See [`docs/licensing.md`](docs/licensing.md) for the full policy, including what
Apache-2.0 does not grant (no trademark rights — `bOps`/`bSoft` are not registered marks) and
how third-party packages and contributions (via CLA) are handled.
