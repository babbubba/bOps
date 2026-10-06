// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace bOps.Abstractions;

/// <summary>
/// How the runtime read the delegated Diagnostic role's final reply (ADR-0044 sections 16 and 17). Every outcome other than
/// <see cref="DiagnosticReplyStatus.Valid"/> yields zero findings; a reply is never guessed at or partly accepted. The raw reply
/// stays in the role's persisted task and is not copied here.
/// </summary>
public sealed record DiagnosticReplyOutcome
{
    /// <summary>Whether the reply was a valid findings object, absent, or malformed.</summary>
    public required DiagnosticReplyStatus Status { get; init; }

    /// <summary>Why a reply was not valid; <see cref="DiagnosticReplyProblem.None"/> for a valid or absent one.</summary>
    public required DiagnosticReplyProblem Problem { get; init; }

    /// <summary>Entries of a valid reply dropped by the per-finding rules: no summary, or a missing or unrecorded evidence id.</summary>
    public int DiscardedFindings { get; init; }
}

/// <summary>
/// The status of a Diagnostic reply. The numeric values are persisted and must never be reordered; new members are only appended.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<DiagnosticReplyStatus>))]
public enum DiagnosticReplyStatus
{
    /// <summary>The reply was one valid findings object.</summary>
    Valid = 0,

    /// <summary>The role gave no final reply, or an empty one.</summary>
    Absent = 1,

    /// <summary>The reply was not a valid findings object; see <see cref="DiagnosticReplyOutcome.Problem"/>.</summary>
    Malformed = 2,
}

/// <summary>
/// Why a Diagnostic reply was not valid. The numeric values are persisted and must never be reordered; new members are only appended.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<DiagnosticReplyProblem>))]
public enum DiagnosticReplyProblem
{
    /// <summary>No problem.</summary>
    None = 0,

    /// <summary>The reply contains no <c>{…}</c> slice.</summary>
    NoJsonObject = 1,

    /// <summary>The slice is not valid JSON, or nests deeper than the bound.</summary>
    InvalidJson = 2,

    /// <summary>An object at some depth repeats a property name; ambiguous JSON is rejected, never first- or last-wins.</summary>
    DuplicateProperty = 3,

    /// <summary>The reply is longer than the bound and was not parsed.</summary>
    TooLarge = 4,

    /// <summary>The root of the slice is not a JSON object.</summary>
    NotAnObject = 5,

    /// <summary>The object has no <c>findings</c> array.</summary>
    MissingFindingsArray = 6,

    /// <summary>The <c>findings</c> array has more entries than the bound.</summary>
    TooManyFindings = 7,
}
