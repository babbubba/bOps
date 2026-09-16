# 07 — Corrections to the archived original plan

`agentic/obsolete/plans/piano-bops.md` is a historical document: rich in rationale, valuable for
understanding *why* bOps is shaped the way it is, and **not a specification**. It is excluded from
normal agent bootstrap. Everything below is superseded by
[`01-architecture-rules.md`](01-architecture-rules.md), [`06-decisions.md`](06-decisions.md) and
the active consolidated roadmap.

An agent finding a conflict between the plan and these documents follows these documents, and
does not need to ask.

---

## Internal contradictions

| Plan | Problem | Resolution |
|---|---|---|
| §3 / §3.1 / §5.1 | `IChatModel` is in `bOps.Abstractions`, but `IModelProviderPackage`, `ChatModelOptions` and `IChatModelRegistry` are declared in a `bOps.Models` namespace that does not exist in the layout — while §5.1 says a package references *only* `bOps.Abstractions`. A third-party provider could not compile. | All of them move to `bOps.Abstractions`. There is no `bOps.Models` project (rule B6). |
| §3.1 | Three provider packages share `OpenAiCompatibleChatModel`, but it cannot live in `bOps.Abstractions` (dependency-free) nor in one of the three (mutual references). | It lives in `bOps.Packages.Providers.OpenAiCompatible`, a shared library (rules A8, B6). |
| §8 vs §0 principle 8 | "Every non-READ tool declares how it is verified", implemented as a `Dictionary` hardcoded in `bOps.Runtime` naming `service.restart`, `docker.restart`, `process.kill`. The core knows package tool names, and third-party packages can never have verification. | `VerificationSpec` in the manifest plus `IVerifiableTool`, enforced at registration (rule B3, D-006). |
| §5.3 vs §3 | The per-package ceiling needs to know which package supplied the tool, but `IPolicyEngine.Evaluate(ToolManifest, args)` receives no package id and `ToolManifest` has none. | `PolicyContext` carries `PackageId`, `PackageTrustLevel`, `NodeId` and `ActorIdentity`; `ToolManifest.Package` is stamped by the registry (rules A11, B7). |
| §5.3 vs §3 | §5.3 requires `packageId` on audit events and says model calls must be traced; the `AuditEvent` record has neither. | Audit event hierarchy with `ToolCallAuditEvent`, `ModelCallAuditEvent`, `PolicyDecisionAuditEvent` (rule B8, D-008). |
| §2 vs §11 | `LinuxSystemProvider`, an `ISystemProvider`, is placed in `bOps.Packages.Service.Linux`. The package is named "Service" and contains System. | `ISystemProvider` is removed entirely; `bOps.Packages.System.Linux` / `.Windows` contribute their own tools (D-005). |
| §7 | The YAML comment on `fs.delete` says "elsewhere it inherits Critical → forbidden", but §11 declares `fs.delete` as `RiskLevel.High`. Elsewhere it inherits High → approval. | The manifest is authoritative; the comment was wrong. |
| §11 | `system.services` and `service.list` are the same tool under two names, in two packages. | `service.*` only. `system.services` does not exist. |

## Defects in the reference implementation

| Plan | Problem | Resolution |
|---|---|---|
| §6 `AgentPlanner` | `registry.Resolve(...) ?? throw` kills the whole task when the model hallucinates a tool name. Nothing catches a tool that throws either. | Nothing thrown escapes an iteration; every failure becomes an observation (rule C1). |
| §6 | `history` grows without bound. Fifteen steps containing `docker.logs` output overflow the context window. | Deterministic truncation against a configured budget, marked explicitly (rule C3). |
| §7 point 1 | Claims argument validation is "already guaranteed" by `ToolManifest.Parameters`; no validation step exists anywhere, and tools do `(string)args["path"]!` on LLM-produced input. | Validation against the manifest before `ExecuteAsync`, and `ToolArguments.TryGet`/`GetRequired` inside tools (rule S2). |
| §7 point 5 | Requires a timeout per tool; the loop passes the ambient `CancellationToken`. | Linked CTS with a per-tool timeout; `ToolOutcome.Timeout` as a distinct outcome (rule S7). |
| §6 | `Forbidden` does `continue` without writing an audit event and without a counter — the model can retry a forbidden tool for all fifteen steps, and principle 4 is violated for the most interesting case. | `PolicyDecisionAuditEvent` on every denial; N consecutive denials terminate the task as `PolicyBlocked` (rules B8, C4). |
| §8 | `VerifyAsync` returns `Success: true` for any tool not in the map, so an unverified `High` action reports as verified. `process.kill` maps to `output => true`. | `VerificationStatus` with `Inconclusive` never treated as success, and no unverified non-`Read` tool can be registered (rules B3, S4). |
| §5.2 | `PluginLoader` uses `Activator.CreateInstance(type)`, but the plan's own examples have constructor dependencies (`IHttpClientFactory`, `ISqlConnectionFactory`). It would throw on every realistic package. There is also no definition of which host services a package may request. | `ActivatorUtilities` against a restricted container exposing a closed, documented set of services (rule A10). |
| §5.2 | `sharedTypes` lists individual types; every contract type crossing the boundary must be shared or loading breaks in confusing ways. | Share the whole `bOps.Abstractions` assembly. |
| §3 | `ChatTurn(Role, Content)` cannot represent native OpenAI tool calling: no `tool_call_id`, no assistant turn carrying the calls. | `ChatTurn` with `ToolCalls` and `ToolCallId`; `ModelResponse` with 0..N calls (rule B5, D-007). |
| §3 | `IReadOnlyDictionary<string, object?> Arguments` does not round-trip through JSON — `object?` returns as `JsonElement`. This breaks audit and replay today, and any transport later. | `ToolArguments`, JSON-native (rule B1). |
| §3 | `ToolCallResult.Duration` is `TimeSpan.Zero` in every example; each tool would have to time itself. | The runtime measures; `Duration` leaves the result record (rule B2). |
| §3 | `TaskState.Status` is a bare string, and `task with { Status = "completed" }` returns the initial state with no steps. | A status enum, and a `TaskState` reflecting the steps actually executed (rule C6). |
| §11 | `IServiceProvider2` — a numeric suffix to dodge a framework collision. | Removed entirely with `ISystemProvider` (D-005). |

## Security gaps

| Plan | Problem | Resolution |
|---|---|---|
| §5.2, §5.3 | `AssemblyLoadContext` isolation is presented in a security context. It isolates dependencies, not permissions: an in-process package can call `File.Delete` or P/Invoke without passing through `ITool`. Trust levels then imply a protection that does not exist. | Stated plainly in the threat model and the package docs: installing a package equals installing software with the host's privileges (rule S8). |
| §7 point 2 | Prompt injection is declared as a principle but not designed. Tool output is concatenated into a string; a compromised container's logs enter the context undelimited. | Structured tool-result turns, explicit delimiters, neutralization of delimiter sequences, and the guarantee that no tool result can alter runtime state (rule S5). |
| §3 | `AuditEvent` has no actor. `IApprovalProvider` returns `bool` — nobody knows who approved. | `ActorIdentity` on every event; `ApprovalDecision` carries the actor (rules B7, B8). |
| §3 | `AuditEvent.Arguments` logs everything; a connection string or password reaches an append-only log that by definition cannot be cleaned. | `ToolParameter.Sensitive`, redacted at the boundary before the event is built (rules B2, S6). |
| §3.1 | `"ApiKey": ""` in `appsettings.json` from V0.1, with secrets management deferred to V1.0. | Environment variables or user-secrets from V0.1; the committed file always holds an empty string (rule S6). |
| §7 point 4 | `Critical` is "always forbidden", but `policy.yaml` is user-editable and could assign it another mode. Invariant or default is left ambiguous. | Invariant. The policy loader rejects any configuration assigning `Critical` anything but `forbidden` (rule S3). |
| §11 | The filesystem path check happens before the operation but with no requirement to resolve symlinks or normalize `..` — a time-of-check/time-of-use bug. | Check the fully resolved path immediately before the operation; deny by default (rule S11). |

## Technical dead ends

| Plan | Problem | Resolution |
|---|---|---|
| §13 vs §5.2 | `PublishAot=true` and dynamic assembly loading are mutually exclusive. EF Core makes AOT harder still. | AOT dropped (D-003). |
| §5.4 | Rigorous semantic versioning on `bOps.Abstractions` while the contract is still being discovered through V1.0. | `0.x` with an explicit instability notice until V1.0 (D-012, rule A12). |
| §3 | No streaming in `IChatModel`, while the Phase 2 UI wants live reasoning. Adding it after publishing the SDK is breaking. | `IStreamingChatModel : IChatModel`, additive, in Phase 2 (D-007). |
| §5.2 | `McMaster.NETCore.Plugins` is the basis for V0.10 loading, but the library is archived upstream. | Verify its state before V0.10; if unmaintained, implement the loader directly on `AssemblyLoadContext`. Record the outcome as an ADR. |
| everywhere | The .NET version is never stated. | .NET 10, C# latest. |
| §14 | The roadmap does not say where the runtime executes relative to its target. | D-001. |
