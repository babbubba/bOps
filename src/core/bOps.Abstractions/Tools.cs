// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace bOps.Abstractions;

/// <summary>
/// The risk a tool's execution carries. Policy decides the execution mode from this level
/// (agentic/00-project-spec.md, principle 2) — it is a declarative model, never a command
/// blacklist. <see cref="Critical"/> is always forbidden and cannot be configured otherwise
/// (agentic/03-security-rules.md, rule S3).
/// </summary>
public enum RiskLevel
{
    /// <summary>Reads state. Never has a side effect.</summary>
    Read,

    /// <summary>A minor, easily reversible side effect.</summary>
    Low,

    /// <summary>A meaningful side effect, typically reversible with another action.</summary>
    Medium,

    /// <summary>A significant or hard-to-reverse side effect.</summary>
    High,

    /// <summary>Never executed by the agent, with or without approval. See agentic/03-security-rules.md, rule S3.</summary>
    Critical,
}

/// <summary>The declared type of a tool parameter, used for validation before a tool ever runs.</summary>
#pragma warning disable CA1720 // String/Integer/Number/Boolean/Enum are exactly the right names for a JSON-Schema-like parameter type enum; see docs/architecture/suppressions.md.
public enum ToolParameterType
{
    /// <summary>A text value.</summary>
    String,

    /// <summary>A whole number.</summary>
    Integer,

    /// <summary>A number, integer or floating-point.</summary>
    Number,

    /// <summary>A true/false value.</summary>
    Boolean,

    /// <summary>A filesystem path, subject to the path policy (agentic/03-security-rules.md, rule S11).</summary>
    Path,

    /// <summary>A span of time.</summary>
    Duration,

    /// <summary>One of a fixed set of values, listed in <see cref="ToolParameter.AllowedValues"/>.</summary>
    Enum,

    /// <summary>
    /// A JSON array of filesystem paths, each subject to the path policy. Added in 1.1, so it is last:
    /// the C# compiler inlines an enum member's value into every package built against the SDK, so a
    /// member inserted in the middle would silently change what a package built against 1.0 declares.
    /// </summary>
    PathList = 7,
}
#pragma warning restore CA1720

/// <summary>Describes one parameter a tool accepts.</summary>
public sealed record ToolParameter
{
    /// <summary>Creates a tool parameter description.</summary>
    /// <param name="Name">The argument name, as the model must supply it.</param>
    /// <param name="Type">The parameter's declared type, used for validation before execution.</param>
    /// <param name="Description">A human- and model-readable explanation of what this argument means.</param>
    /// <param name="Required">Whether the call is invalid without this argument.</param>
    /// <param name="Sensitive">
    /// When true, the runtime redacts this argument's value before it reaches the audit log
    /// (agentic/03-security-rules.md, rule S6). Set this on connection strings, passwords,
    /// tokens — anything that must never appear in an append-only log.
    /// </param>
    /// <param name="AllowedValues">For <see cref="ToolParameterType.Enum"/>, the values the argument may take.</param>
    public ToolParameter(
        string Name,
        ToolParameterType Type,
        string Description,
        bool Required = true,
        bool Sensitive = false,
        IReadOnlyList<string>? AllowedValues = null)
    {
        this.Name = Name;
        this.Type = Type;
        this.Description = Description;
        this.Required = Required;
        this.Sensitive = Sensitive;
        this.AllowedValues = AllowedValues;
    }

    /// <summary>The argument name, as the model must supply it.</summary>
    public string Name { get; init; }

    /// <summary>The parameter's declared type, used for validation before execution.</summary>
    public ToolParameterType Type { get; init; }

    /// <summary>A human- and model-readable explanation of what this argument means.</summary>
    public string Description { get; init; }

    /// <summary>Whether the call is invalid without this argument.</summary>
    public bool Required { get; init; }

    /// <summary>
    /// When true, the runtime redacts this argument's value before it reaches the audit log
    /// (agentic/03-security-rules.md, rule S6).
    /// </summary>
    public bool Sensitive { get; init; }

    /// <summary>For <see cref="ToolParameterType.Enum"/>, the values the argument may take.</summary>
    public IReadOnlyList<string>? AllowedValues { get; init; }

    /// <summary>Inclusive numeric lower bound for Integer or Number parameters.</summary>
    public double? Minimum { get; init; }

    /// <summary>Inclusive numeric upper bound for Integer or Number parameters.</summary>
    public double? Maximum { get; init; }

    /// <summary>Minimum character length for String or Path parameters.</summary>
    public int? MinLength { get; init; }

    /// <summary>Maximum character length for String or Path parameters.</summary>
    public int? MaxLength { get; init; }

    /// <summary>Minimum item count for PathList parameters.</summary>
    public int? MinItems { get; init; }

    /// <summary>Maximum item count for PathList parameters.</summary>
    public int? MaxItems { get; init; }
}

/// <summary>
/// Declares how a non-<see cref="RiskLevel.Read"/> tool's effect is verified after execution.
/// A tool with this risk must declare one and implement <see cref="IVerifiableTool"/>, or the
/// registry refuses to register it (agentic/01-architecture-rules.md, rule B3) — principle 3
/// ("every side-effecting action is verified") is enforced structurally, not by convention.
/// </summary>
public sealed record VerificationSpec
{
    /// <summary>Declares how a tool's effect is verified.</summary>
    /// <param name="VerifyToolName">The name of a <see cref="RiskLevel.Read"/> tool, resolved in the same registry, that observes the effect.</param>
    /// <param name="ArgumentsFrom">Names of the original call's arguments to carry over into the verification call (e.g. a service name).</param>
    /// <param name="Description">Shown to the operator before approval, so they know what will be checked afterwards.</param>
    public VerificationSpec(string VerifyToolName, IReadOnlyList<string> ArgumentsFrom, string Description)
    {
        this.VerifyToolName = VerifyToolName;
        this.ArgumentsFrom = ArgumentsFrom;
        this.Description = Description;
    }

    /// <summary>The name of a <see cref="RiskLevel.Read"/> tool, resolved in the same registry, that observes the effect.</summary>
    public string VerifyToolName { get; init; }

    /// <summary>Names of the original call's arguments to carry over into the verification call (e.g. a service name).</summary>
    public IReadOnlyList<string> ArgumentsFrom { get; init; }

    /// <summary>Shown to the operator before approval, so they know what will be checked afterwards.</summary>
    public string Description { get; init; }
}

/// <summary>
/// The full, serializable description of a tool: what it does, how risky it is, where it runs,
/// what it needs, and how its effect is verified. This is also the tool discovery protocol —
/// the planner only ever sees manifests, never implementations.
/// </summary>
public sealed record ToolManifest
{
    /// <summary>The tool's name, in <c>lowercase.dotted</c> form: <c>system.cpu</c>, <c>docker.logs</c>.</summary>
    public required string Name { get; init; }

    /// <summary>A human- and model-readable explanation of what this tool does.</summary>
    public required string Description { get; init; }

    /// <summary>How risky this tool's execution is.</summary>
    public required RiskLevel Risk { get; init; }

    /// <summary>Platform identifiers this tool runs on: <c>"windows"</c>, <c>"linux"</c>.</summary>
    public required IReadOnlyList<string> Platforms { get; init; }

    /// <summary>
    /// Prerequisite ids this tool <b>requires</b>, checked via <see cref="ICapabilityProbe"/>. While any is not satisfied the
    /// tool stays registered but is unavailable: never offered to a model and never resolved for execution (ADR-0049).
    /// </summary>
    public required IReadOnlyList<string> Requires { get; init; }

    /// <summary>
    /// Prerequisite ids this tool uses when present but runs without (ADR-0049). An unsatisfied optional prerequisite
    /// leaves the tool available and marks it degraded. Empty by default; an id may not also appear in <see cref="Requires"/>.
    /// </summary>
    public IReadOnlyList<string> OptionalRequires { get; init; } = [];

    /// <summary>The parameters this tool accepts.</summary>
    public required IReadOnlyList<ToolParameter> Parameters { get; init; }

    /// <summary>Required when <see cref="Risk"/> is not <see cref="RiskLevel.Read"/>. See agentic/01-architecture-rules.md, rule B3.</summary>
    public VerificationSpec? Verification { get; init; }

    /// <summary>
    /// Whether this tool always requires a human approval even when policy would otherwise allow
    /// automatic execution. This can tighten policy but never override a forbidden decision.
    /// </summary>
    public bool RequiresExplicitApproval { get; init; }

    /// <summary>
    /// The bounded execution timeout this tool requests from the host. <c>null</c> uses the host default; a declared value
    /// must be positive and finite, and the host may lower it to its configured maximum.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TimeSpan? RequestedExecutionTimeout { get; init; }

    /// <summary>
    /// The package that contributed this tool. Stamped by the registry at registration time —
    /// see agentic/01-architecture-rules.md, rule A11. A package cannot set this on its own
    /// manifest and inherit another package's trust level.
    /// </summary>
    public PackageId Package { get; internal set; }
}

/// <summary>A request to call one tool with a set of arguments.</summary>
public sealed record ToolCallRequest
{
    /// <summary>Creates a tool call request.</summary>
    /// <param name="ToolName">The name of the tool to call.</param>
    /// <param name="Arguments">The arguments to call it with.</param>
    public ToolCallRequest(string ToolName, ToolArguments Arguments)
    {
        this.ToolName = ToolName;
        this.Arguments = Arguments;
    }

    /// <summary>The name of the tool to call.</summary>
    public string ToolName { get; init; }

    /// <summary>The arguments to call it with.</summary>
    public ToolArguments Arguments { get; init; }
}

/// <summary>
/// How a tool call ended. <see cref="Timeout"/> is distinct from <see cref="Failure"/> because
/// "it did not finish" and "it failed" call for different replanning, and because a
/// side-effecting action that timed out may have partially happened and still needs
/// verification (agentic/01-architecture-rules.md, rule C2). <see cref="Denied"/> is a policy
/// outcome, not an execution outcome — it is recorded here so a denied call and an executed one
/// share the same result shape end to end.
/// </summary>
public enum ToolOutcome
{
    /// <summary>The tool executed and its effect (if any) is as expected.</summary>
    Success,

    /// <summary>The tool executed but reported an expected error condition.</summary>
    Failure,

    /// <summary>The call was not executed because policy denied it.</summary>
    Denied,

    /// <summary>The tool did not finish within its allotted time.</summary>
    Timeout,
}

/// <summary>Why a tool invocation did not yield its requested result.</summary>
public enum ToolFailureKind
{
    /// <summary>No structured classification was supplied.</summary>
    Unspecified = 0,
    /// <summary>The call violated the declared tool contract.</summary>
    Validation = 1,
    /// <summary>The host environment could not provide required evidence.</summary>
    Environment = 2,
    /// <summary>An unexpected runtime or tool exception occurred.</summary>
    Internal = 3,
    /// <summary>The invocation exceeded its runtime timeout.</summary>
    Timeout = 4,
    /// <summary>Policy, approval, envelope, or entitlement denied the call.</summary>
    Authorization = 5,
}

/// <summary>How complete the evidence in a tool result is.</summary>
public enum ToolResultCompleteness
{
    /// <summary>The tool did not declare evidence completeness.</summary>
    Unspecified = 0,
    /// <summary>All applicable evidence was collected.</summary>
    Complete = 1,
    /// <summary>Useful evidence was collected but one or more sources were incomplete.</summary>
    Partial = 2,
    /// <summary>No applicable evidence was available.</summary>
    Unavailable = 3,
}

/// <summary>
/// One opaque, package-defined fact emitted with a tool result. Its origin is the containing
/// <see cref="ToolCallResult"/>, which is durably associated with a <see cref="PlanStep"/>.
/// Runtime treats <see cref="Type"/> and <see cref="Key"/> as ordinal identifiers and never
/// interprets their vocabulary (ADR-0050).
/// </summary>
/// <param name="Type">A stable, package-defined fact type identifier.</param>
/// <param name="Key">A stable key within <paramref name="Type"/>.</param>
/// <param name="ValueType">The declared type of <paramref name="Value"/>.</param>
/// <param name="Value">One JSON-native scalar or path-list value.</param>
public sealed record EvidenceFact(string Type, string Key, ToolParameterType ValueType, JsonNode Value);

/// <summary>
/// The result of a tool call. Duration is measured by the runtime, around the call — a tool
/// never reports its own timing (agentic/07-plan-corrections.md).
/// </summary>
public sealed record ToolCallResult
{
    /// <summary>Creates a tool call result.</summary>
    /// <param name="Outcome">How the call ended.</param>
    /// <param name="Output">The tool's output, fed back to the model as an observation.</param>
    /// <param name="ErrorMessage">Present when <paramref name="Outcome"/> is not <see cref="ToolOutcome.Success"/>.</param>
    public ToolCallResult(ToolOutcome Outcome, string? Output, string? ErrorMessage)
    {
        this.Outcome = Outcome;
        this.Output = Output;
        this.ErrorMessage = ErrorMessage;
    }

    /// <summary>How the call ended.</summary>
    public ToolOutcome Outcome { get; init; }

    /// <summary>The tool's output, fed back to the model as an observation.</summary>
    public string? Output { get; init; }

    /// <summary>Present when <see cref="Outcome"/> is not <see cref="ToolOutcome.Success"/>.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Structured failure classification. Defaults preserve existing tool results.</summary>
    public ToolFailureKind FailureKind { get; init; }

    /// <summary>Structured evidence completeness. Defaults preserve existing tool results.</summary>
    public ToolResultCompleteness Completeness { get; init; }

    /// <summary>
    /// Opaque typed facts produced by this invocation. An empty list preserves legacy behavior;
    /// Runtime validates the bounded contract before the result is persisted.
    /// </summary>
    public IReadOnlyList<EvidenceFact> Facts { get; init; } = [];

    /// <summary>True only when <see cref="Outcome"/> is <see cref="ToolOutcome.Success"/>.</summary>
    public bool Succeeded => Outcome is ToolOutcome.Success;

    /// <summary>Builds a successful result.</summary>
    public static ToolCallResult Success(string? output) => new(ToolOutcome.Success, output, null);

    /// <summary>Builds a failed result. <paramref name="errorMessage"/> is fed back to the model as an observation.</summary>
    public static ToolCallResult Failure(string errorMessage) => new(ToolOutcome.Failure, null, errorMessage);
}

/// <summary>
/// One executable, typed action a package contributes. There is no generic "run this command"
/// implementation of this interface, and there never will be (agentic/03-security-rules.md, rule S1).
/// </summary>
public interface ITool
{
    /// <summary>This tool's manifest.</summary>
    ToolManifest Manifest { get; }

    /// <summary>
    /// Executes the tool. Arguments have already been validated against <see cref="Manifest"/>
    /// by the runtime. Implementations should return a failed <see cref="ToolCallResult"/> for
    /// any expected error condition; throwing is reserved for a genuine programmer bug
    /// (agentic/02-coding-standards.md — error model).
    /// </summary>
    /// <param name="arguments">The validated arguments to execute with.</param>
    /// <param name="ct">Cancelled when the tool's timeout elapses or the task is cancelled.</param>
    Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default);
}

/// <summary>
/// Host-owned identity for one tool invocation. The runtime constructs this from the task it is
/// executing; tool arguments cannot nominate or replace these values (ADR-0026).
/// </summary>
/// <param name="Node">The node on which the tool executes.</param>
/// <param name="TaskId">The task that caused the invocation.</param>
/// <param name="Actor">The authenticated actor that caused the invocation.</param>
public sealed record ToolExecutionContext(NodeId Node, Guid TaskId, ActorIdentity Actor);

/// <summary>
/// Optional additive tool contract for operations that must bind derived state to the host-owned
/// node, task and actor identity. Existing <see cref="ITool"/> implementations remain unchanged;
/// the runtime uses this overload when a tool implements it (ADR-0026).
/// </summary>
public interface IContextualTool : ITool
{
    /// <summary>Executes with the invocation identity supplied by the host runtime.</summary>
    /// <param name="arguments">The validated arguments to execute with.</param>
    /// <param name="context">Host-owned invocation identity; never sourced from tool arguments.</param>
    /// <param name="ct">Cancelled when the tool timeout elapses or the task is cancelled.</param>
    Task<ToolCallResult> ExecuteAsync(
        ToolArguments arguments,
        ToolExecutionContext context,
        CancellationToken ct = default);
}

/// <summary>
/// Optional contract for a tool whose durable preflight state must be bound to the exact human
/// approval decision before target execution. This callback must not perform the target side effect.
/// </summary>
public interface IApprovalBoundTool : ITool
{
    /// <summary>Records and validates one approval decision against the exact arguments and host-owned context.</summary>
    /// <param name="arguments">The exact arguments shown to the approval provider.</param>
    /// <param name="context">Host-owned invocation identity.</param>
    /// <param name="decision">The human decision returned by the approval provider.</param>
    /// <param name="ct">Cancelled when the task is cancelled or the binding timeout elapses.</param>
    /// <returns>Success when the decision was durably bound or recorded; failure prevents approved execution.</returns>
    Task<ToolCallResult> BindApprovalAsync(
        ToolArguments arguments,
        ToolExecutionContext context,
        ApprovalDecision decision,
        CancellationToken ct = default);
}

/// <summary>
/// Optional additive contract for a tool that contributes a small, structured summary to its
/// <see cref="ToolCallAuditEvent"/>. Implementations must return aggregate, non-sensitive data;
/// the host rejects oversized summaries and never substitutes the full tool output.
/// </summary>
public interface IToolAuditSummaryProvider : ITool
{
    /// <summary>Builds bounded audit metadata from the validated arguments and completed result.</summary>
    JsonObject? CreateAuditSummary(ToolArguments arguments, ToolCallResult result);
}

/// <summary>A package implements this to contribute one or more tools to the registry.</summary>
public interface IToolProvider
{
    /// <summary>The tools this package contributes.</summary>
    IEnumerable<ITool> GetTools();
}
