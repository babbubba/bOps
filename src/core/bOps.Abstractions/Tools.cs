// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

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

    /// <summary>Capability identifiers this tool needs to be available, checked via <see cref="ICapabilityProbe"/>.</summary>
    public required IReadOnlyList<string> Requires { get; init; }

    /// <summary>The parameters this tool accepts.</summary>
    public required IReadOnlyList<ToolParameter> Parameters { get; init; }

    /// <summary>Required when <see cref="Risk"/> is not <see cref="RiskLevel.Read"/>. See agentic/01-architecture-rules.md, rule B3.</summary>
    public VerificationSpec? Verification { get; init; }

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

/// <summary>A package implements this to contribute one or more tools to the registry.</summary>
public interface IToolProvider
{
    /// <summary>The tools this package contributes.</summary>
    IEnumerable<ITool> GetTools();
}
