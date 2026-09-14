# 00 — Project specification

## What bOps is

An agent runtime that operates Windows and Linux machines through declarative tools,
policies, planning and post-action verification.

The shape of the system in one sentence: **the LLM proposes, the runtime decides and
executes, policies authorize, verification confirms, the audit log records.**

## What bOps is not

- Not a chatbot with shell access.
- Not a wrapper that hands model output to a shell. There is no generic "run this command"
  tool, and there never will be — see [`03-security-rules.md`](03-security-rules.md).
- Not a configuration-management system. bOps diagnoses and acts on a live machine; it does
  not converge a machine towards a declared desired state.
- Not a framework consumer. The agent loop is written and owned here, explicitly, so that
  every state transition can be logged, tested and inspected.

## Non-negotiable principles

1. **The LLM never touches the machine.** It returns a structured intent; the runtime decides
   whether that intent becomes an action.
2. **Every tool declares its own risk** (`Read`, `Low`, `Medium`, `High`, `Critical`) and the
   policy engine decides the mode (`automatic`, `approval`, `forbidden`). Declarative model,
   not a command blacklist.
3. **Every side-effecting action is verified after execution.** An action is not reported as
   successful until a read-only tool confirms the effect. Unverifiable is not success.
4. **Everything is audited** — every tool call, every policy decision, every model call,
   whatever the outcome.
5. **The core is agnostic** to LLM provider and operating system.
6. **The CLI is the primary interface.** The web UI is Phase 2 and is a second client of the
   same runtime, never a privileged one.
7. **Everything beyond the minimal runtime is a package.** Tools, tool categories, operating
   systems and LLM providers all load through the same extension contract. The only difference
   between a first-party package and a third-party one is *where it is loaded from*, never
   *how it is built*.

The core is exactly: Runtime (loop, registries, plugin loader), Policy, Memory, Audit,
Abstractions (the contract), and the hosts (CLI, API, Worker). Nothing else.

## Confirmed decisions

Recorded in full, with rationale, in [`06-decisions.md`](06-decisions.md). Summary:

| Subject | Decision |
|---|---|
| Runtime | .NET 10, C# latest |
| Topology | Local-only execution now, designed so remote agents can be added without rewriting (D-001) |
| Target platforms | Windows and Linux. macOS is possible later **as a package**, never as a core change |
| OS model | Each OS is its own package contributing its own tools; no `ISystemProvider`, no `IServiceProvider2` (D-005) |
| Native AOT | Dropped. Dynamic package loading wins (D-003) |
| Dev orchestration | .NET Aspire from V0.5, for dependencies and test targets only — never to host bOps in production (D-002) |
| Verification | Declared in the manifest, evaluated by the package (D-006) |
| Model contract | `tool_call_id` and multiple tool calls per turn from V0.1; streaming later as a separate optional interface (D-007) |
| Audit | Actor identity, secret redaction, `NodeId` and model-call events from V0.1 (D-008) |
| Observability | OpenTelemetry from V0.1, OTLP exporter (D-009) |
| Testing | TDD on the core, real integration targets for packages (D-010) |
| License | Apache-2.0 |

## Roadmap and scope discipline

**Phase 1 — CLI**

| | |
|---|---|
| `V0.1` | Minimal runtime: chat model, tool registry, first read-only System tools |
| `V0.2` | Explicit agent loop with replanning |
| `V0.3` | Policy engine and approval flow; analyzers escalate to `all` |
| `V0.4` | Post-action verification |
| `V0.5` | Windows + Linux parity, Filesystem and Network packages, Aspire AppHost, CI on both OSes |
| `V0.6` | Docker package with conditional capability discovery |
| `V0.7` | Persistent, resumable tasks (SQLite) |

**Phase 2 — Web UI and provider expansion**

| | |
|---|---|
| `V0.8` | Anthropic, OpenAI and DeepSeek provider packages |
| `V0.9` | `bOps.Api` + Angular UI |
| `V0.10` | Dynamic package loading, `bops plugin install`, published plugin SDK |
| `V1.0` | Threat model, package signing and trust levels, secrets management, hardening |

**The rule for agents:** build the current version, not the next one. Do not add the policy
engine while implementing V0.2, do not add a plugin loader while implementing V0.5. The
*contract* must accommodate later versions — the *implementation* must not anticipate them.

If a task seems to require something from a later version, that is a signal to stop and ask,
not to pull the work forward.

## Explicitly out of scope until after V1.0

- Multi-agent supervision. A supervisor is "just" an `IAgentPlanner` that calls other
  planners; if the core is right, it costs almost nothing later. Building it early costs
  everything.
- Vector stores and semantic memory over past tasks.
- Remote execution transport (the *contract* accommodates it from V0.1; the transport does not exist).
- macOS platform packages.
