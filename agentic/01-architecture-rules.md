# 01 — Architecture rules

Invariants first, then the authoritative contract. Where this file and any roadmap or task
disagree, this file wins. Historical-plan reasons remain in
[`07-plan-corrections.md`](07-plan-corrections.md).

---

## A. Structural invariants

### A1 — The core never names a package, a tool or a provider

`bOps.Runtime`, `bOps.Policy`, `bOps.Memory` and `bOps.Audit` must contain **zero** literal
references to `"docker.restart"`, `"service.status"`, `"OpenRouter"`, `/proc`, `systemctl`,
`ServiceController`, or any other package-specific identifier.

This is mechanically checkable and it is checked in CI. If a core class needs to know
something about a tool, that knowledge belongs in `ToolManifest`, not in the core.

The one and only exception: `bOps.Abstractions` may define *enums and shapes* that name
concepts (`RiskLevel.Critical`, `ChatRole.Tool`), never instances.

### A2 — The tool boundary is serializable, always

The runtime receives from a tool exactly one thing: a `ToolCallResult`. Never a `Stream`,
never a `FileInfo`, never a live `Process`, never a `DbConnection`, never a package-defined
type. A tool reads a stream — it does not hand one back.

This single rule is what makes remote execution a future DI swap
(`LocalToolExecutor` → `RemoteToolExecutor`) instead of a rewrite. It costs nothing today.
Violating it costs the entire remote story.

Everything crossing the boundary must round-trip through `System.Text.Json` with source
generation. Write a round-trip test for every contract type.

### A3 — Everything carries a `NodeId`

`TaskState`, `PlanStep`, every `AuditEvent`, and every registry lookup are scoped to a node.
Today there is one node and its id is the constant `NodeId.Local`. Nothing in the code may
*assume* that — no `Environment.MachineName` shortcuts, no implicit "the local machine".

### A4 — Registries and capability probes are per-node, never global singletons

A Linux node exposes different tools than a Windows node. `IToolRegistry` and
`ICapabilityProbe` are resolved for a node, not injected as process-wide singletons. Today
that resolution always returns the local one.

### A5 — Policy is enforced where execution happens

Policy evaluation happens on the node that will execute, not only where the plan is made.
Today those are the same process, but the call must go through the node-scoped path, so that
a future remote worker never has to trust a controller that says "this was approved".

### A6 — Audit is written locally, then shipped

An `IAuditSink` writes to node-local storage. Aggregation is a separate concern layered on
top. No code path writes an audit event directly to a remote destination.

### A7 — Packages depend only on `bOps.Abstractions`

A package may reference third-party NuGet packages freely (`Docker.DotNet`, `Npgsql`, …) and
may reference a **shared library of its own family** (see A8). It may never reference
`bOps.Runtime`, `bOps.Policy`, `bOps.Memory` or `bOps.Audit`.

### A8 — Operating systems are packages; shared code between them is a plain library

`bOps.Packages.System.Windows` and `bOps.Packages.System.Linux` each contribute their **own
complete tools**, each declaring its own `Platforms`. The registry already filters by
platform, so selection is automatic. There is no `ISystemProvider`, no `IServiceProvider2`,
no cross-package service resolution.

Duplication of the tool shell is avoided with `bOps.Packages.System.Core`: a normal class
library holding manifests, argument parsing, result DTOs and output formatting as abstract
base classes. Each OS package implements only data collection.

```
bOps.Packages.System.Core      (manifests, parsing, formatting, result DTOs — no OS calls)
  ├── bOps.Packages.System.Windows   (PerformanceCounter, WMI, DriveInfo)
  ├── bOps.Packages.System.Linux     (/proc, /sys)
  └── bOps.Packages.System.MacOS     (someone else, some day, without touching the core)
```

**Output format is part of the contract.** The LLM reads tool output, so two OS packages
producing `system.cpu` must produce the *same shape*. Formatting therefore lives in
`.Core`, and both packages must pass the shared conformance suite
(see [`04-testing-rules.md`](04-testing-rules.md)).

The same pattern applies to `Service`, `Network`, `Process` and `Filesystem`.

**C# namespace is `bOps.Packages.Sys.*`, not `bOps.Packages.System.*`.** A namespace segment
literally named `System` breaks every unqualified `System.*` reference inside it (the compiler
resolves `System.Console` etc. against the enclosing namespace first, ahead of the global
`System` namespace). Project and assembly names keep the `bOps.Packages.System.*` spelling above
— it matches this document and the README — but the `RootNamespace` MSBuild property and every
`namespace` declaration in code use `bOps.Packages.Sys.*` instead. This applies to every package
in the `System.*` family, present and future.

`bOps.Packages.System.Windows` targets plain `net10.0`, not `net10.0-windows`: `bOps.Cli` (also
plain `net10.0`, cross-platform) references both the Windows and Linux packages and picks one at
runtime by OS, and a project on a plain TFM cannot reference one on a platform-specific TFM.
Platform intent is instead declared with `[assembly: SupportedOSPlatform("windows")]`. A test
project that only ever calls the Windows package (`bOps.Packages.System.Windows.Tests`) is free
to target `net10.0-windows` itself, since it has no such cross-platform reference to make.

### A9 — Packages never resolve services from other packages

There is no mechanism for package A to consume a service contributed by package B, and none
will be added. If two packages need shared code, they share a library (A8). If a package
needs a host service, it asks for one of the explicitly published host services (A10).

### A10 — The host publishes a closed set of services to packages

A package's `IToolProvider` / `IModelProviderPackage` is constructed with
`ActivatorUtilities`, not `Activator.CreateInstance`, from a container that exposes **only**
this set:

`ILoggerFactory` · `IHttpClientFactory` · `TimeProvider` · `IConfigurationSection` (the
package's own section, nothing else) · `ICapabilityProbe` · `IToolInvoker` (read-only tools
of the same package only)

Anything else is out of reach by design. This list changes only by ADR.

### A11 — `PackageId` is assigned by the host, never self-declared

A package states its id in `bops-plugin.json`, but the *effective* `PackageId` stamped onto a
`ToolManifest` is assigned by the registry at registration time, from the loading context.
A package cannot claim to be another package and inherit its trust level.

### A12 — `bOps.Abstractions` stays on `0.x` until V1.0

Semantic versioning is rigorous from `1.0.0`. Until then the contract is explicitly unstable
and the README of the NuGet package says so. Do not publish `1.0.0` before the V1.0
milestone, and do not promise stability the project cannot yet keep.

---

## B. The contract (`bOps.Abstractions`)

Normative in **shape**. Member-level details may still evolve until V0.3; the structural
decisions below may not.

### B1 — Identity and arguments

```csharp
namespace bOps.Abstractions;

public readonly record struct NodeId(string Value)
{
    public static NodeId Local => new("local");
}

public readonly record struct PackageId(string Value);

public sealed record ActorIdentity(string Kind, string Id, string? DisplayName);
// Kind: "os-user" in Phase 1, "api-user" | "service" in Phase 2.
```

`ToolArguments` is a JSON-native, round-trippable wrapper — **not**
`IReadOnlyDictionary<string, object?>`, which does not survive serialization (`object?`
comes back as `JsonElement`) and therefore breaks audit, replay and any future transport.

```csharp
public sealed class ToolArguments   // backed by JsonObject
{
    public static ToolArguments Empty { get; }
    public bool TryGet<T>(string name, out T value);
    public T GetRequired<T>(string name);          // throws only on a validation bug
    public ToolArguments Redact(IEnumerable<string> parameterNames);
    public JsonObject ToJson();
}
```

### B2 — Tools

```csharp
public enum RiskLevel { Read, Low, Medium, High, Critical }

public enum ToolParameterType { String, Integer, Number, Boolean, Path, Duration, Enum }

public sealed record ToolParameter(
    string Name,
    ToolParameterType Type,
    string Description,
    bool Required = true,
    bool Sensitive = false,                       // redacted before it reaches the audit log
    IReadOnlyList<string>? AllowedValues = null);

public sealed record VerificationSpec(
    string VerifyToolName,                        // must resolve in the same registry
    IReadOnlyList<string> ArgumentsFrom,          // original argument names carried over
    string Description);                          // shown to the operator before approval

public sealed record ToolManifest(
    string Name,
    string Description,
    RiskLevel Risk,
    IReadOnlyList<string> Platforms,
    IReadOnlyList<string> Requires,
    IReadOnlyList<ToolParameter> Parameters,
    VerificationSpec? Verification = null)
{
    public PackageId Package { get; init; }       // stamped by the registry (A11)
}

public sealed record ToolCallRequest(string ToolName, ToolArguments Arguments);

public enum ToolOutcome { Success, Failure, Denied, Timeout }

public sealed record ToolCallResult(ToolOutcome Outcome, string? Output, string? ErrorMessage)
{
    public bool Succeeded => Outcome is ToolOutcome.Success;
}
// Duration is measured by the runtime. A tool never reports its own timing.

public interface ITool
{
    ToolManifest Manifest { get; }
    Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default);
}

public interface IToolProvider
{
    IEnumerable<ITool> GetTools();
}
```

### B3 — Verification is declared *and* evaluated by the package

```csharp
public enum VerificationStatus { Confirmed, Refuted, Inconclusive, NotApplicable }

public sealed record VerificationOutcome(VerificationStatus Status, string? Detail);

public interface IVerifiableTool : ITool
{
    Task<VerificationOutcome> EvaluateVerificationAsync(
        ToolArguments originalArguments,
        ToolCallResult verificationToolResult,
        CancellationToken ct = default);
}
```

**Enforced at registration, not at execution:** a tool whose `Risk != Read` must declare a
`VerificationSpec` *and* implement `IVerifiableTool`, or `IToolRegistry.Register` rejects it
with a clear error. Principle 3 becomes structural rather than aspirational, and a
third-party package gets verification for free instead of being unable to have it.

The declarative half exists so the operator can be told, *before* approving, what will be
checked afterwards. The code half exists so non-trivial checks (an absent PID, a hash
comparison) remain expressible.

`Inconclusive` is never treated as success. See [`03-security-rules.md`](03-security-rules.md).

### B4 — Registry

```csharp
public interface IToolRegistry
{
    void Register(PackageId package, ITool tool);      // validates B3, stamps Package (A11)
    Task RefreshCapabilitiesAsync(CancellationToken ct = default); // see note below
    IReadOnlyList<ToolManifest> GetAvailableManifests(); // filtered by platform + capabilities
    ITool? Resolve(string toolName);
    void SetEnabled(PackageId package, bool enabled);   // package enable/disable, no restart
}

public interface ICapabilityProbe
{
    Task<bool> IsAvailableAsync(string capability, CancellationToken ct = default);
}
```

Capability results are cached with a TTL, not resolved once at startup — a Docker daemon that
starts later must become visible without restarting bOps. `GetAvailableManifests()` stays
synchronous (the planner calls it on every step), so the registry cannot probe capabilities
inline; `RefreshCapabilitiesAsync` is the explicit, host-driven point where it does. The host
calls it once at startup and, from the version that actually needs live discovery (Docker at
V0.6), on a timer.

### B5 — Model contract

`ChatTurn` must be able to represent native OpenAI-style tool calling, which requires a tool
call id and an assistant turn carrying the calls. A flat `(Role, Content)` pair cannot, and
would silently degrade every provider in native mode.

```csharp
public enum ChatRole { System, User, Assistant, Tool }

public sealed record ModelToolCall(string Id, string ToolName, ToolArguments Arguments);

public sealed record ChatTurn
{
    public required ChatRole Role { get; init; }
    public string? Content { get; init; }
    public IReadOnlyList<ModelToolCall>? ToolCalls { get; init; }  // Assistant turns
    public string? ToolCallId { get; init; }                        // Tool turns
}

public sealed record ModelUsage(int PromptTokens, int CompletionTokens, decimal? EstimatedCostUsd);

public sealed record ModelRequest(
    string SystemPrompt,
    IReadOnlyList<ChatTurn> History,
    IReadOnlyList<ToolManifest> AvailableTools);

public sealed record ModelResponse(
    string? TextResponse,
    IReadOnlyList<ModelToolCall> ToolCalls,     // 0..N — models emit parallel calls
    bool IsFinal,
    ModelUsage? Usage);

public interface IChatModel
{
    ChatModelDescriptor Descriptor { get; }     // provider id + model id, for audit
    Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default);
}
```

The contract accepts N tool calls per turn. **The V0.1 runtime executes one per iteration**
and feeds the rest back as unexecuted — sequential execution is the safe default for an ops
agent, and parallel execution would need its own policy story. That is a runtime choice,
reversible; the contract shape is not.

Streaming arrives in Phase 2 as `IStreamingChatModel : IChatModel`. A separate optional
interface is additive and therefore not a breaking change — which is precisely why it can
wait.

### B6 — Providers

```csharp
public sealed record ChatModelOptions(
    string Provider, string BaseUrl, string? ApiKey, string Model,
    bool SupportsNativeToolCalling = true);

public interface IModelProviderPackage
{
    IReadOnlyList<string> SupportedProviderIds { get; }
    IChatModel Create(ChatModelOptions options);
}

public interface IChatModelRegistry
{
    void Register(PackageId package, IModelProviderPackage provider);
    IChatModel Create(ChatModelOptions options);   // resolved by id; unknown id is an error
}
```

`IModelProviderPackage`, `ChatModelOptions` and `IChatModelRegistry` live in
`bOps.Abstractions`, not in a `bOps.Models` project. There is no `bOps.Models` project.

`OpenAiCompatibleChatModel` — shared by the OpenRouter, Ollama and llama.cpp packages — lives
in `bOps.Packages.Providers.OpenAiCompatible`, a plain shared library (A8), because it cannot
live in `bOps.Abstractions` (which stays dependency-free) and must not create a dependency
between sibling packages.

### B7 — Policy

```csharp
public enum PolicyMode { Automatic, Approval, Forbidden }

public enum PackageTrustLevel { Unverified, Community, Verified, Official }

public sealed record PolicyContext(
    NodeId Node,
    PackageId Package,
    PackageTrustLevel Trust,
    ToolManifest Manifest,
    ToolArguments Arguments,
    ActorIdentity Actor);

public sealed record PolicyDecision(PolicyMode Mode, string Reason);  // Reason is audited

public interface IPolicyEngine
{
    PolicyDecision Evaluate(PolicyContext context);
}

public interface IApprovalProvider
{
    Task<ApprovalDecision> RequestApprovalAsync(
        ToolManifest manifest, ToolArguments arguments, VerificationSpec? verification,
        string reason, CancellationToken ct = default);
}

public sealed record ApprovalDecision(bool Approved, ActorIdentity Actor, string? Note);
```

The package identity and trust level are inputs, because the per-package risk ceiling
(§5.3 of the plan) cannot be applied without them. The approval carries the actor, because
an audit log that cannot say *who approved* is not an audit log.

**`bOps.Policy`** (V0.3, ADR-0015) is the concrete implementation: `PolicyEngine : IPolicyEngine`
evaluates a `PolicyConfig` (loaded from `policy.yaml` by `PolicyConfigLoader`, via `YamlDotNet` —
a dependency on `bOps.Policy`, never on `bOps.Abstractions`). It depends only on
`bOps.Abstractions` (rule A7); `bOps.Runtime` depends only on `IPolicyEngine`/`IApprovalProvider`
and never references `bOps.Policy` directly, so the concrete engine stays swappable at the host's
composition root. `ConsoleApprovalProvider` (`bOps.Cli`) is the first `IApprovalProvider` — a
blocking console prompt, because the CLI is the primary interface (principle 6); a future host
implements its own without `AgentRunner` or `PolicyEngine` changing.

`PackageTrustLevel` is passed by `AgentRunner` as `Official` for every call in V0.3 — every
package loaded today is first-party, shipped in this repository, and there is no real
per-package trust assignment mechanism until dynamic loading arrives at V0.10 (D-003). This is a
known, accepted simplification, not an oversight.

### B8 — Audit

```csharp
public abstract record AuditEvent
{
    public required DateTimeOffset TimestampUtc { get; init; }
    public required NodeId Node { get; init; }
    public required Guid TaskId { get; init; }
    public required int StepIndex { get; init; }
    public required ActorIdentity Actor { get; init; }
}

public enum AuthorizationKind { Automatic, UserApproved, UserRejected, PolicyDenied, UnknownTool }

public sealed record ToolCallAuditEvent : AuditEvent
{
    public required PackageId Package { get; init; }
    public required string Tool { get; init; }
    public required JsonObject Arguments { get; init; }   // already redacted (B2, Sensitive)
    public required RiskLevel Risk { get; init; }
    public required AuthorizationKind Authorization { get; init; }
    public required ToolOutcome Outcome { get; init; }
    public required TimeSpan Duration { get; init; }
    public VerificationStatus? Verification { get; init; }
}

public enum ModelCallOutcome { Success, Failure }   // ADR-0013

public sealed record ModelCallAuditEvent : AuditEvent
{
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public required ModelCallOutcome Outcome { get; init; }   // ADR-0013
    public string? ErrorMessage { get; init; }                // ADR-0013 — set when Outcome is Failure
    public ModelUsage? Usage { get; init; }
}

public sealed record PolicyDecisionAuditEvent : AuditEvent
{
    public required PackageId Package { get; init; }
    public required string Tool { get; init; }
    public required PolicyMode Mode { get; init; }
    public required string Reason { get; init; }
}

public sealed record ApprovalAuditEvent : AuditEvent   // V0.3, ADR-0015
{
    public required PackageId Package { get; init; }
    public required string Tool { get; init; }
    public required bool Approved { get; init; }
    public required ActorIdentity Approver { get; init; }
    public string? Note { get; init; }
}

public interface IAuditSink
{
    Task WriteAsync(AuditEvent evt, CancellationToken ct = default);
}
```

`AuthorizationKind` has a fifth value, `UnknownTool`, beyond the four principle 4 originally
named — for auditing a tool-call attempt that never resolved to any registered tool (a model
hallucinating a tool name). It is recorded with `PackageId.Unknown`, a static value added to
`PackageId` for exactly this case: there is no real package to blame for a name the runtime
never registered.

`ModelCallAuditEvent` gained `Outcome` and `ErrorMessage` in ADR-0013
(`docs/architecture/adr/0013-model-call-audit-outcome.md`): a call to an `IChatModel` that throws
must still be audited, per rule S9, exactly like a denied or timed-out tool call — the original
shape had no way to record that a call failed at all.

`ApprovalAuditEvent` (V0.3, ADR-0015) is distinct from `PolicyDecisionAuditEvent`:
`PolicyDecisionAuditEvent` records what policy decided (`Approval` is required, and why);
`ApprovalAuditEvent` records what the human actually decided, and by whom (`Approver`, which is
not always the task's launching `Actor` on the base type — not today, in the CLI, but once
remote approval exists). `AuthorizationKind.UserApproved`/`UserRejected` on the following
`ToolCallAuditEvent` record which one happened, exactly like `PolicyDenied`/`UnknownTool` already
did for the other rejection paths.

Principle 4 says *everything*. A denied call, a rejected approval and an unknown tool name
are the most interesting events in the log, so they are events, not `continue` statements.

**Hash-chaining** (V0.3, ADR-0015; D-008) is implemented in `bOps.Audit`'s `JsonLinesAuditSink`,
not in this contract: `IAuditSink` itself is unchanged, because tamper evidence is a storage
detail a given sink either provides or does not, not something every implementation must agree
on the shape of. Each line is `{Seq, PrevHash, Hash, EventJson}`, `Hash = SHA256(PrevHash +
EventJson)`; `AuditChainVerifier` re-derives and checks it independently. This is
tamper-*evident*, not tamper-*proof* — see rule S9.

### B9 — Planning

```csharp
public sealed record PlannedStep(int Index, string Description, string? ExpectedTool);

public sealed record AgentPlan(int Revision, string Rationale, IReadOnlyList<PlannedStep> Steps);
```

A `PlannedStep` is a stated intention, never a tool call: it has no arguments, because the model
still must be asked, at execution time, for the concrete `ModelToolCall` that step needs —
auto-executing `ExpectedTool` from the plan would weaken principle 1 into "the model approves a
checklist once and the runtime free-runs it." `AgentPlan.Revision` starts at 0 for the plan made
before the first step and increases by one on each replan (rule C8). See ADR-0014.

`TaskState` carries the resulting shapes:

```csharp
public sealed record PlanStep(
    int Index, string? Description, ModelToolCall? ToolCall, ToolCallResult? Result,
    string? Observation, int? PlanRevision = null);

public sealed record TaskState(
    Guid Id, NodeId Node, string Goal, AgentTaskStatus Status,
    IReadOnlyList<PlanStep> Steps, IReadOnlyList<AgentPlan> Plans, DateTimeOffset CreatedAtUtc);
```

`Plans` is every plan revision produced for the task, distinct from `Steps` — `Steps` records
tool-call iterations, `Plans` records the act of planning itself. `PlanStep.PlanRevision` is
nullable for completeness (the type does not assume a plan always exists) though no code path
in the runtime currently leaves it null.

---

## C. The agent loop

`bOps.Runtime/AgentRunner.cs` owns the loop (named `AgentRunner`, not `AgentPlanner` as in
earlier drafts of this document — planning and execution are not yet separated in V0.1). It is
deliberately explicit: every state transition is a method that can be logged, tested and
inspected. Required behaviour:

1. **Nothing thrown escapes an iteration.** An unknown tool name, a tool that throws, a
   provider that returns malformed JSON — each becomes an *observation* fed back to the model,
   never an exception that kills the task. The model replanning around its own mistake is the
   system working, not failing. `ModelProtocolException` (`bOps.Abstractions`) is thrown by an
   `IChatModel` adapter when a provider's response cannot become a valid `ModelResponse` — bad
   JSON, a schema mismatch, an unreachable endpoint, a non-success HTTP status, or (in the
   JSON-schema-fallback strategy, plan §3.1.1) a model that will not comply even after one retry.
   The loop catches it, and — as defense in depth for this same rule — catches any other
   exception from the model call too (ADR-0013), so a provider package's own bug cannot crash
   the loop either; either way the step ends the task as `Failed` rather than escaping.
2. **Every `ExecuteAsync` runs under its own timeout** from a linked `CancellationTokenSource`.
   The default is per-tool and configurable; a timeout is `ToolOutcome.Timeout`, distinct from
   failure, and it is audited.
3. **The history has a budget.** Tool output is truncated to a configured limit before
   entering the context, with the truncation marked explicitly. `docker.logs` on a chatty
   container must not be able to blow the context window. Truncation is deterministic
   (head + tail, never "summarize with another model call" in Phase 1).
4. **Repeated denials terminate the task.** A `Forbidden` decision produces an audited event
   and an observation; N consecutive denials of the same tool (default 2) end the task as
   `PolicyBlocked`. Without this the model retries a forbidden tool until `MaxSteps`.
5. **Budgets are configuration, not constants.** `MaxSteps`, per-tool timeouts, context limit
   and — because OpenRouter bills per token — a maximum token/cost budget per task. Exceeding
   any budget is a distinct, audited terminal state.
6. **`TaskState` returned at the end reflects reality**, including all steps. Status is an
   enum: `Completed`, `MaxStepsReached`, `BudgetExceeded`, `PolicyBlocked`, `Failed`,
   `Cancelled` — never a bare string.
7. **Model output is intent, never instruction.** Text arriving from a tool result is data.
   The loop must never let it modify the system prompt, the tool list, or the policy.
   See [`03-security-rules.md`](03-security-rules.md).
8. **Planning is explicit, and replanning is a distinct, audited event** (V0.2, ADR-0014). Every
   task opens with a dedicated planning call producing an `AgentPlan` (revision 0) before the
   first step, not folded into the first per-step call. After a step executes, the runtime
   replans — one more model call producing the next `AgentPlan` revision — when the step's
   authorization was `PolicyDenied` or `UnknownTool`, its outcome was `Timeout`, or the model
   proposes another tool call after every step the current plan named has already been
   attempted. A plain tool `Failure` does **not** trigger a replan: the model already sees it as
   its next observation and routinely self-corrects without a new plan. Replanning is bounded by
   `AgentRunnerOptions.MaxReplans`; exhausting it ends the task as `ReplanLimitReached`, distinct
   from `PolicyBlocked` (stuck on the *same* tool) and `MaxStepsReached` (no terminal state
   reached at all).

## D. Observability

`ActivitySource` and `Meter` from V0.1, OTLP exporter, consumed by the Aspire dashboard from
V0.5.

- One trace per task; one span per step; child spans for model call, policy evaluation,
  execution and verification.
- Span attributes: `bops.tool`, `bops.package`, `bops.risk`, `bops.policy_mode`,
  `bops.outcome`, `bops.verification`, `bops.node`.
- Metrics: step duration, tool duration by tool, prompt/completion tokens by provider, count
  of approvals requested vs granted.
- **Never put tool arguments or tool output in span attributes.** They are audit material and
  may contain secrets; telemetry has a different retention and a different audience.
