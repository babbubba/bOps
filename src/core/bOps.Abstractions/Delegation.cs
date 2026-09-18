// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;

namespace bOps.Abstractions;

/// <summary>
/// Identifies one logical agent of a delegated run (ADR-0030). Assigned by the runtime when it
/// creates the agent, never supplied by a package or a model — the same reasoning as
/// <see cref="PackageId"/> (rule A11): an agent must not be able to claim another agent's identity.
/// </summary>
[JsonConverter(typeof(AgentIdJsonConverter))]
public readonly record struct AgentId(Guid Value)
{
    /// <summary>True for <c>default</c>, which is never a valid identity.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <summary>Creates a fresh, unique agent id. Called by the runtime, never by a package.</summary>
    public static AgentId New() => new(Guid.NewGuid());

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}

/// <summary>Converts <see cref="AgentId"/> to and from a bare JSON string. Public so source-generated <see cref="JsonSerializerContext"/> types in other assemblies can reference it.</summary>
public sealed class AgentIdJsonConverter : JsonConverter<AgentId>
{
    /// <inheritdoc />
    public override AgentId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetGuid());

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, AgentId value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.Value);
    }
}

/// <summary>
/// The fixed roles of a delegated run, in pipeline order (ADR-0030 section 2). The order and the set
/// are not configurable: they cannot be changed by a model, by tool output or by configuration. A
/// role is a generic class of work, never a product — what each role may do is a role profile, which
/// is host configuration and not part of this contract. The numeric values are persisted and must
/// never be reordered.
/// </summary>
public enum AgentRoleKind
{
    /// <summary>Read-only. Gathers <see cref="Evidence"/> through Read tools visible under its envelope.</summary>
    Discovery,

    /// <summary>Read-only. Turns evidence into <see cref="Finding"/>s and may prepare an immutable <see cref="ExecutionPlan"/>, which it may not execute.</summary>
    Diagnostic,

    /// <summary>Executes exactly the plan a human approved through the ordinary step pipeline. Makes no model call.</summary>
    Remediation,

    /// <summary>A distinct identity that reads the system itself and evaluates the declared verification. Makes no model call.</summary>
    Verification,
}

/// <summary>
/// Which logical agent acted (ADR-0030 section 1). Additional to <see cref="ActorIdentity"/>, never a
/// replacement: the actor is the operator on whose authority an objective runs, the agent is the role
/// that acted for them. An agent identity can never be an approver.
/// </summary>
public sealed record AgentIdentity
{
    /// <summary>Creates an agent identity.</summary>
    /// <param name="Id">The runtime-assigned id. Must not be empty.</param>
    /// <param name="Role">The role this agent performs.</param>
    /// <param name="ParentAgentId">
    /// The agent that delegated to this one. <c>null</c> when the parent is the orchestrator, which is
    /// deterministic runtime code and has no agent identity of its own; the roles of V1.2 are all
    /// children of the orchestrator, so the depth is 1 and no agent parents another. The field exists
    /// so a later ADR can lift that deliberately.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="Id"/> is empty, <paramref name="Role"/> is not a defined role, or the agent is its own parent.</exception>
    public AgentIdentity(AgentId Id, AgentRoleKind Role, AgentId? ParentAgentId = null)
    {
        if (Id.IsEmpty)
        {
            throw new ArgumentException("An agent must have a non-empty id.", nameof(Id));
        }

        if (!Enum.IsDefined(Role))
        {
            throw new ArgumentException("Unknown agent role.", nameof(Role));
        }

        if (ParentAgentId is { } parent && (parent.IsEmpty || parent == Id))
        {
            throw new ArgumentException("An agent's parent must be a different, non-empty agent.", nameof(ParentAgentId));
        }

        this.Id = Id;
        this.Role = Role;
        this.ParentAgentId = ParentAgentId;
    }

    /// <summary>The runtime-assigned id.</summary>
    public AgentId Id { get; init; }

    /// <summary>The role this agent performs.</summary>
    public AgentRoleKind Role { get; init; }

    /// <summary>The delegating agent, or <c>null</c> when the parent is the orchestrator.</summary>
    public AgentId? ParentAgentId { get; init; }
}

/// <summary>
/// Where a piece of <see cref="Evidence"/> came from within a delegated run (ADR-0030 section 4), so the
/// chain from observation to verdict can be audited. Only the runtime stamps it: the setter on
/// <see cref="Evidence.Provenance"/> is not public.
/// </summary>
/// <param name="DelegationId">The delegated run the evidence was gathered in.</param>
/// <param name="AgentId">The agent that gathered it.</param>
/// <param name="Role">That agent's role.</param>
public sealed record EvidenceProvenance(Guid DelegationId, AgentId AgentId, AgentRoleKind Role);

/// <summary>
/// The correlation block carried by an <see cref="AuditEvent"/> of a delegated run (ADR-0030 section 8).
/// <c>null</c> on every event of a run that is not delegated, and then absent from the serialized event.
/// Envelope contents are never audited, only their hash: the value is reproducible from the envelope
/// with <see cref="DelegationHasher.ComputeEnvelopeHash"/>.
/// </summary>
public sealed record DelegationCorrelation
{
    /// <summary>Creates a correlation block.</summary>
    /// <param name="DelegationId">The delegated run (<see cref="DelegationRun.Id"/>).</param>
    /// <param name="EnvelopeHash">The hash of the envelope in force for the event: the acting agent's, or the root envelope for an event of the orchestrator.</param>
    /// <param name="Agent">The agent that acted, or <c>null</c> for an event of the orchestrator itself (the run being requested or ending, an envelope being denied).</param>
    /// <exception cref="ArgumentException"><paramref name="DelegationId"/> is empty or <paramref name="EnvelopeHash"/> is blank.</exception>
    public DelegationCorrelation(Guid DelegationId, string EnvelopeHash, AgentIdentity? Agent)
    {
        if (DelegationId == Guid.Empty)
        {
            throw new ArgumentException("A delegation must have a non-empty id.", nameof(DelegationId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(EnvelopeHash);

        this.DelegationId = DelegationId;
        this.EnvelopeHash = EnvelopeHash;
        this.Agent = Agent;
    }

    /// <summary>The delegated run.</summary>
    public Guid DelegationId { get; init; }

    /// <summary>The hash of the envelope in force for the event.</summary>
    public string EnvelopeHash { get; init; }

    /// <summary>The agent that acted, or <c>null</c> for an event of the orchestrator itself.</summary>
    public AgentIdentity? Agent { get; init; }
}
