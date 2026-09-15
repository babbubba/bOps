using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace bOps.Abstractions;

/// <summary>How a tool call came to be authorized, recorded on every <see cref="ToolCallAuditEvent"/>.</summary>
public enum AuthorizationKind
{
    /// <summary>Policy placed the call in <see cref="PolicyMode.Automatic"/>.</summary>
    Automatic,

    /// <summary>An operator approved the call.</summary>
    UserApproved,

    /// <summary>An operator rejected the call.</summary>
    UserRejected,

    /// <summary>Policy placed the call in <see cref="PolicyMode.Forbidden"/>.</summary>
    PolicyDenied,

    /// <summary>The model requested a tool name that does not resolve to any registered tool.</summary>
    UnknownTool,
}

/// <summary>
/// Something that happened, worth recording forever. Every tool call, every model call, and
/// every policy decision produces one of these — including denials, rejections and timeouts,
/// which are often the most interesting events in the log (agentic/00-project-spec.md,
/// principle 4; agentic/03-security-rules.md, rule S9).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "eventType")]
[JsonDerivedType(typeof(ToolCallAuditEvent), "toolCall")]
[JsonDerivedType(typeof(ModelCallAuditEvent), "modelCall")]
[JsonDerivedType(typeof(PolicyDecisionAuditEvent), "policyDecision")]
public abstract record AuditEvent
{
    /// <summary>When this event occurred, in UTC.</summary>
    public required DateTimeOffset TimestampUtc { get; init; }

    /// <summary>The node this event happened on.</summary>
    public required NodeId Node { get; init; }

    /// <summary>The task this event belongs to.</summary>
    public required Guid TaskId { get; init; }

    /// <summary>The step, within the task, this event belongs to.</summary>
    public required int StepIndex { get; init; }

    /// <summary>Who caused this event: the operator who launched the task, or who approved/rejected the step.</summary>
    public required ActorIdentity Actor { get; init; }
}

/// <summary>
/// A tool was called (or its call was denied before execution). <see cref="Arguments"/> must
/// already be redacted per <see cref="ToolParameter.Sensitive"/> before this event is
/// constructed — redaction happens at the <see cref="ToolArguments"/> boundary, not here
/// (agentic/03-security-rules.md, rule S6).
/// </summary>
public sealed record ToolCallAuditEvent : AuditEvent
{
    /// <summary>The package that contributed the tool.</summary>
    public required PackageId Package { get; init; }

    /// <summary>The tool's name.</summary>
    public required string Tool { get; init; }

    /// <summary>Already redacted. Never the raw <see cref="ToolArguments"/>.</summary>
    public required JsonObject Arguments { get; init; }

    /// <summary>The tool's declared risk.</summary>
    public required RiskLevel Risk { get; init; }

    /// <summary>How this call came to be authorized.</summary>
    public required AuthorizationKind Authorization { get; init; }

    /// <summary>How the call ended.</summary>
    public required ToolOutcome Outcome { get; init; }

    /// <summary>How long the call took, measured by the runtime.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary><c>null</c> only for a <see cref="RiskLevel.Read"/> tool, which has nothing to verify.</summary>
    public VerificationStatus? Verification { get; init; }
}

/// <summary>
/// How a call to an LLM provider ended (agentic/architecture/adr/0013). A model call can fail —
/// unreachable provider, a non-success HTTP status, a response that cannot be parsed — and rule
/// S9 requires that failure to be audited exactly like a successful call, not silently dropped.
/// </summary>
public enum ModelCallOutcome
{
    /// <summary>The provider returned a usable response.</summary>
    Success,

    /// <summary>The call could not be completed. See the audited event's <see cref="ModelCallAuditEvent.ErrorMessage"/>.</summary>
    Failure,
}

/// <summary>A call was made to an LLM provider. Distinct from a tool call — see plan §5.3.</summary>
public sealed record ModelCallAuditEvent : AuditEvent
{
    /// <summary>The provider id that served this call.</summary>
    public required string Provider { get; init; }

    /// <summary>The specific model that served this call.</summary>
    public required string Model { get; init; }

    /// <summary>Whether the call succeeded. See ADR-0013.</summary>
    public required ModelCallOutcome Outcome { get; init; }

    /// <summary>Present when <see cref="Outcome"/> is <see cref="ModelCallOutcome.Failure"/>.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Token and cost accounting, when the provider reports it.</summary>
    public ModelUsage? Usage { get; init; }
}

/// <summary>A policy decision was made for a tool call, whatever the outcome that followed.</summary>
public sealed record PolicyDecisionAuditEvent : AuditEvent
{
    /// <summary>The package that contributed the tool.</summary>
    public required PackageId Package { get; init; }

    /// <summary>The tool's name.</summary>
    public required string Tool { get; init; }

    /// <summary>The mode policy decided.</summary>
    public required PolicyMode Mode { get; init; }

    /// <summary>Why this mode was decided.</summary>
    public required string Reason { get; init; }
}

/// <summary>
/// Writes audit events to node-local, append-only storage. Aggregation across nodes is a
/// separate concern layered on top — no implementation writes directly to a remote destination
/// (agentic/01-architecture-rules.md, rule A6).
/// </summary>
public interface IAuditSink
{
    /// <summary>Writes one audit event. Must not throw for a well-formed event — audit failures are themselves an operational concern, not a reason to lose the event silently.</summary>
    /// <param name="evt">The event to write.</param>
    /// <param name="ct">Cancelled if the write should be abandoned.</param>
    Task WriteAsync(AuditEvent evt, CancellationToken ct = default);
}
