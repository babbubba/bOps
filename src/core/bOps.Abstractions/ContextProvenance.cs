// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace bOps.Abstractions;

/// <summary>
/// Where the content of one model-context turn comes from (ADR-0054 G2-2). The origin is assigned by trusted bOps code at the point
/// a turn is admitted into the context, never read back from an external label, and it decides how the content may be treated:
/// only <see cref="Runtime"/> content is instruction; everything else is attributed data.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ContextOrigin>))]
public enum ContextOrigin
{
    /// <summary>Authored by the bOps runtime itself (standing instructions, protocol corrections, plan and step projections).</summary>
    Runtime,

    /// <summary>The operator's goal for the task.</summary>
    Task,

    /// <summary>Retrieved knowledge content. Reserved: no knowledge source is admitted yet.</summary>
    Knowledge,

    /// <summary>Retrieved experience content. Reserved: no experience source is admitted yet.</summary>
    Experience,

    /// <summary>Skill pack content. Reserved: no skill pack source is admitted yet.</summary>
    Skill,

    /// <summary>Output of a tool call. Always data, never instruction (agentic/03-security-rules.md, S5).</summary>
    ToolResult,

    /// <summary>
    /// The model's own text or requested tool calls, replayed as context. It is a proposal, never an instruction: the runtime
    /// decides and executes. Ordered last, as the most data-like origin.
    /// </summary>
    Model,
}

/// <summary>
/// The provenance of one <see cref="ChatTurn"/>: an origin, plus the identity of the admitted provider and the pack that
/// supplied it, when there is one. Instances are created only by bOps code that admits a turn; nothing here is parsed from a
/// MAF tag, a message id, <c>AdditionalProperties</c>, pack metadata or any identifier an external source supplied.
/// </summary>
public sealed record ContextProvenance
{
    /// <summary>Provenance of runtime-authored content.</summary>
    public static ContextProvenance Runtime { get; } = new() { Origin = ContextOrigin.Runtime };

    /// <summary>Provenance of the operator's task goal.</summary>
    public static ContextProvenance Task { get; } = new() { Origin = ContextOrigin.Task };

    /// <summary>Provenance of tool output.</summary>
    public static ContextProvenance ToolResult { get; } = new() { Origin = ContextOrigin.ToolResult };

    /// <summary>Provenance of the model's own output replayed as context.</summary>
    public static ContextProvenance Model { get; } = new() { Origin = ContextOrigin.Model };

    /// <summary>Where the content comes from.</summary>
    public required ContextOrigin Origin { get; init; }

    /// <summary>
    /// <c>true</c> when the content was produced by compacting other turns (a placeholder or a summary) rather than authored by the
    /// <see cref="Origin"/> itself. <see cref="Origin"/> is then the most data-like origin among the turns it may have been built
    /// from, never a more trusted one. Omitted when <c>false</c>, so a non-derived turn serializes as before.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Derived { get; init; }

    /// <summary>The id the host assigned to the admitted provider instance that supplied the content, when applicable.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AdmittedProviderId { get; init; }

    /// <summary>The pack the content came from, when applicable.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PackId { get; init; }

    /// <summary>The pack version, when applicable.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PackVersion { get; init; }

    /// <summary>The pack content hash, when applicable.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PackHash { get; init; }

    /// <summary>
    /// The tenant scope the host bound to the pack the content came from, when applicable (KNOW-01A). Assigned by bOps from the
    /// host's tenant authority, never from pack metadata or a model argument.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TenantId { get; init; }
}

/// <summary>
/// How many turns of one model request carry a given provenance, as written into the model-call audit event. A
/// <c>null</c> <see cref="Provenance"/> counts the turns that carry none (model output replayed as history, or turns older than
/// provenance), which are deliberately not given a trust level.
/// </summary>
public sealed record ContextProvenanceCount
{
    /// <summary>The provenance, or <c>null</c> for turns that carry none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ContextProvenance? Provenance { get; init; }

    /// <summary>The number of turns.</summary>
    public required int Turns { get; init; }

    /// <summary>
    /// Counts the turns of a request by provenance in a deterministic order (origin, provider, pack id, version, hash; the
    /// unattributed group last), so equal requests always produce byte-equal audit output.
    /// </summary>
    public static IReadOnlyList<ContextProvenanceCount> Summarize(IEnumerable<ChatTurn> turns)
    {
        ArgumentNullException.ThrowIfNull(turns);
        return [.. turns
            .GroupBy(turn => turn.Provenance)
            .OrderBy(group => group.Key is null ? 1 : 0)
            .ThenBy(group => group.Key?.Origin)
            .ThenBy(group => group.Key?.Derived)
            .ThenBy(group => group.Key?.AdmittedProviderId, StringComparer.Ordinal)
            .ThenBy(group => group.Key?.PackId, StringComparer.Ordinal)
            .ThenBy(group => group.Key?.PackVersion, StringComparer.Ordinal)
            .ThenBy(group => group.Key?.PackHash, StringComparer.Ordinal)
            .ThenBy(group => group.Key?.TenantId, StringComparer.Ordinal)
            .Select(group => new ContextProvenanceCount { Provenance = group.Key, Turns = group.Count() })];
    }
}
