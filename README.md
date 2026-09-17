<div align="center">

<img src="logo.png" alt="bOps logo" width="160" height="160">

# bOps

**An open-source agent runtime for safely operating Windows and Linux machines
through declarative tools, policies, planning and verification.**

[![Status](https://img.shields.io/badge/status-v1.0%20release%20candidate-orange)](#status)
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
| **System** | `system.info` `system.apps` `system.devices` `system.cpu` `system.memory` `system.disk` `system.swap` `system.io` |
| **Process** | `process.list` `process.inspect` `process.stop` `process.kill` |
| **Filesystem** | `fs.list` `fs.stat` `fs.read` `fs.write` `fs.delete` `fs.search` `fs.hash` `fs.move` `fs.size` `fs.delete_tree.prepare` `fs.delete_tree` `fs.delete_tree.verify` |
| **Network** | `network.interfaces` `network.connections` `network.dns` `network.ping` `network.port_check` `network.route` |
| **Service** | `service.list` `service.status` `service.start` `service.stop` `service.restart` (Windows via `ServiceController`, Linux via a fixed `systemctl` invocation — ADR-0021) |
| **Docker** | `docker.containers` `docker.inspect` `docker.logs` `docker.images` `docker.networks` `docker.start` `docker.stop` `docker.restart` |
| **Web** | `web.search` `web.fetch` |

V0.11 is fully registered. `system.apps`, `system.devices`, `fs.size`, governed permanent recursive
deletion and the Web package are implemented for the V1.1 preview; their bounded output, supported
native sources and explicit completeness semantics are documented in
[`docs/system-inventory.md`](docs/system-inventory.md) and
[`docs/filesystem-inventory.md`](docs/filesystem-inventory.md). Recursive/batch deletion requires a
complete hash-bound manifest and explicit approval; see
[`docs/governed-recursive-deletion.md`](docs/governed-recursive-deletion.md).
`web.search` is invisible until an operator configures a SearXNG endpoint, and `web.fetch` denies
loopback/private/link-local/metadata network destinations by default; see
[`docs/security/web-network-policy.md`](docs/security/web-network-policy.md).

The Angular UI's **Plugins** page (`GET /api/plugins`, `GET /api/plugins/{id}`) is a read-only
catalog of installed plugins — id, version, publisher, signature/trust, installed/enabled/loaded/
compatible state (kept distinct, never merged into one "status"), declared capabilities and
dependencies, and declared-vs-effective maximum risk. Enable, disable, install and remove stay
`bops plugin *`-only; no mutation path exists through the API in this batch. `bOps.Api` now
activates the operator's already-enabled plugins at start-up exactly like `bOps.Cli` always has —
it runs its own `AgentRunner` for tasks started from the dashboard and needs the same
plugin-contributed tools/Skills.

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
bops "permanently delete these trees" # exact manifest; fs.delete_tree always requires approval
bops "search the web for the current LTS .NET version and fetch its release notes"
```

```bash
bops resume <task-id>          # resume a persisted task (V0.7, SQLite-backed) from where it left off
```

```bash
bops audit verify [audit-file]   # verify the complete append-only audit hash chain

bops plugin install <directory>   # install a local plugin build — disabled until you enable it
bops plugin list                  # every installed plugin, enabled or not
bops plugin enable <id>           # activate now, and on every future run, until disabled
bops plugin disable <id>          # unload it; its files stay on disk
bops plugin remove <id>           # disable (if enabled) and delete it
bops plugin validate <directory>  # check a bops-plugin.json without installing anything
bops plugin sign <directory> <publisher> <key-id> <private-key.pem>
```

`bops diagnose` is **not implemented** — there is no such subcommand, planned or otherwise.

Provider credentials are references, not committed values. The CLI resolves its default model
credential from `BOPS_MODEL_API_KEY`; the API resolves its model credential from
`BOPS_MODELPROVIDER_API_KEY`. The API additionally requires `BOPS_API_KEY` and accepts it only as
`Authorization: Bearer <key>`; query-string credentials are never supported. The local UI keeps
the API credential in memory and loses it on refresh by design. Bind the API to loopback, or put
TLS and an authenticated reverse proxy in front of it.

## Roadmap

**V0.1 through V1.0 are implemented, and V1.1 is in progress.** The formal V1.0 release workflow
still needs its first operator-authorized tagged run. V1.1-A through V1.1-F are complete;
V1.1-G writable Settings backed by an encrypted local vault is the active next batch, while the
remaining operational and local-management batches stay in their fixed order.

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
| `V1.0` | Stable `bOps.Abstractions` 1.0 SDK; API authentication/roles; secret references; bounded/idempotent/cancellable execution; verified plugin provenance; audit verification; locked, reproducible SBOM/provenance release pipeline (ADR-0022) |
| `V1.1-A` *(complete)* | Skill provider interfaces, restricted tool invocation, contextual policy, terminal-run semantics and end-to-end OSS sample Skill |
| `V1.1-B–F` *(complete)* | Add bounded system/device inventory, filesystem sizing, hash-bound recursive deletion, SearXNG-backed Web search/safe fetch, and a read-only plugin catalog API/UI |
| `V1.1-G` | Writable Settings backed by an encrypted local vault |
| `V1.1-H` | Cross-platform integration, documentation and release gate |
| `V1.2` | In-process multi-agent orchestration with privilege-reducing delegation |
| `V1.3` | Neutral entitlement boundary and safe local plugin enable/disable/upload |
| `V1.4` | Outbound secure node protocol and private Control Plane foundation |
| `V1.5–V1.9` | Private commercial PostgreSQL/SQL Server Skills and enterprise Portal |
| `V2.0` | Enterprise GA, recovery, compatibility, security and release readiness |

The [consolidated roadmap](agentic/_plans/2026-09-16-consolidated-roadmap.md) is the single active
plan and links every executable public task with its recommended model effort. Historical plans and
migration inputs are archived under `agentic/obsolete/` and are intentionally ignored by coding
agents. `bOps` itself stays Apache-2.0 forever, including commercial use — see
[Licensing](#license) and [`docs/licensing.md`](docs/licensing.md).

## Extending bOps

A plugin is one or more .NET assemblies referencing only the published `bOps.Abstractions`
package, with an entry type implementing `IToolProvider`, `ISkillProvider` or
`IModelProviderPackage`, plus a `bops-plugin.json` manifest naming it.
[`samples/bops-sample-plugin/`](samples/bops-sample-plugin/) is a real, working Skill/Tool plugin —
build it, then `bops plugin install`/`enable` it, as a starting point:

```json
{
  "SchemaVersion": 1,
  "Id": "acme.sample-plugin",
  "Publisher": "Acme",
  "Version": "1.0.0",
  "MinHostAbstractionsVersion": "1.1.0",
  "EntryAssembly": "Acme.SamplePlugin.dll",
  "EntryType": "Acme.SamplePlugin.SampleToolProvider",
  "DeclaredCapabilities": ["sample.echo-marker"],
  "Dependencies": [],
  "MaxDeclaredRisk": "Low"
}
```

Packages are never trusted at their word. For a Skill provider, `DeclaredCapabilities` must
exactly match the activated Capability names or activation fails; `Dependencies` and
`MaxDeclaredRisk` remain operator-facing declarations, while the policy engine's own per-package
risk ceiling in `policy.yaml` is what is actually enforced.
A plugin is loaded in an isolated, collectible `AssemblyLoadContext` sharing a single copy of
`bOps.Abstractions` with the host (ADR-0020) and stays disabled until an operator enables it
explicitly. V1.0 also requires a detached RSA-PSS/SHA-256 signature whose publisher key and trust
level appear in the operator-owned `publisher-trust.json`; unsigned or unknown-key packages may
be inspected but cannot be enabled. See [`docs/plugins/getting-started.md`](docs/plugins/getting-started.md)
for the full walkthrough, and rule S8: this isolation is dependency isolation, not a security
sandbox — a loaded plugin runs with the host's own privileges.

## Status

V1.1-A is complete and GitHub Actions run `35178863698` is green on Windows and Linux, including
.NET and Angular checks. V1.1-B system inventory is now active. V1.0 implementation and security
hardening are complete, but its formal release gate remains open: no release-candidate tag has been
created and the release workflow has not yet produced and attested the reproducible Windows/Linux
artifacts, SBOMs and checksums. This is not yet a production endorsement.

## Documentation

| | |
|---|---|
| [`agentic/00-bootstrap.md`](agentic/00-bootstrap.md) | Stable agent bootstrap, required read order and validation baseline |
| [`agentic/`](agentic/) | Binding specification, active plans and task tracking |
| [`agentic/06-decisions.md`](agentic/06-decisions.md) | Decision register: what was chosen, what was rejected, why |
| [`agentic/_plans/2026-09-16-consolidated-roadmap.md`](agentic/_plans/2026-09-16-consolidated-roadmap.md) | Single active roadmap — actual status, ordered gates, open-core boundary and task index |
| [`agentic/_tasks/README.md`](agentic/_tasks/README.md) | Detailed executable public tasks with model-effort guidance |
| [`docs/licensing.md`](docs/licensing.md) | What Apache-2.0 does and doesn't grant, the open-core repository split, CLA policy |
| [`docs/architecture/`](docs/architecture/) | Architecture decision records |
| [`docs/security/`](docs/security/) | Risk model, default policies, threat model |
| [`docs/governed-recursive-deletion.md`](docs/governed-recursive-deletion.md) | Exact manifest, mandatory approval, bounds and partial-failure recovery |
| [`docs/security/web-network-policy.md`](docs/security/web-network-policy.md) | SearXNG setup, `web.fetch` SSRF/redirect/decompression policy and configuration |
| [`docs/plugins/getting-started.md`](docs/plugins/getting-started.md) | Write your first bOps plugin |

## License

Apache-2.0 — see [LICENSE](LICENSE), always and for everyone, including commercial use.
Packages are separately licensed: bOps does not require third-party packages to be open
source. `bOps` follows an **open-core** model: the core, SDK, first-party packages and this
local UI stay Apache-2.0 in this public repository; official commercial Skills, a Control
Plane and an enterprise Portal live in a separate private repository and are never merged
here. The private `bOps.Workspace` coordination repository pins the public and commercial repositories
as Git submodules without duplicating their source or changing their licenses. See
[`docs/licensing.md`](docs/licensing.md) for the full policy, including what
Apache-2.0 does not grant (no trademark rights — `bOps`/`bSoft` are not registered marks) and
how third-party packages and contributions (via CLA) are handled.
