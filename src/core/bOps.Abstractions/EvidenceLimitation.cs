// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace bOps.Abstractions;

/// <summary>
/// One step of a delegated role's model loop whose requested result was not obtained, or was only partially available, to the
/// tool or to the model (ADR-0044 section 16, from the ADR-0042 classification). Runtime-authored, deterministic, from typed
/// fields only: it never carries tool output, an error message, an argument or model text, and no model is asked for it. It
/// describes evidence <em>collection</em>; it is not evidence, never a <see cref="Finding"/>, and never creates or changes one.
/// </summary>
/// <remarks>
/// A list of these covers the role's model-loop evidence collection only, not reads made elsewhere (for example inside a
/// Capability's preparation). Enums are serialized by name.
/// </remarks>
public sealed record EvidenceLimitation
{
    /// <summary>The index of the step in the role's task.</summary>
    public required int StepIndex { get; init; }

    /// <summary>
    /// The resolved canonical tool name when it passes the ADR-0042 shape check (letters, digits, <c>.</c>, <c>_</c>, <c>-</c>, at most
    /// 128 characters); <c>null</c> for an unknown-tool rejection or a name that fails the check. Never a caller-supplied name.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolName { get; init; }

    /// <summary>Whether the step was a pre-execution rejection of a name that resolved to no tool.</summary>
    public bool UnknownTool { get; init; }

    /// <summary>How the step's tool call ended.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<ToolOutcome>))]
    public required ToolOutcome Outcome { get; init; }

    /// <summary>The failure classification of the step's result.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<ToolFailureKind>))]
    public required ToolFailureKind FailureKind { get; init; }

    /// <summary>The completeness the tool declared for its result.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<ToolResultCompleteness>))]
    public required ToolResultCompleteness Completeness { get; init; }

    /// <summary>The length, in UTF-16 code units, of the output when the persisted observation was shortened; otherwise <c>null</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ShortenedFromCharacters { get; init; }

    /// <summary>
    /// The id of the <see cref="Evidence"/> this step produced (<c>"&lt;role&gt;-&lt;index&gt;"</c>), when it produced one; <c>null</c>
    /// when it produced none, as for a failed or denied read. An id is never manufactured to create a link.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EvidenceId { get; init; }
}
