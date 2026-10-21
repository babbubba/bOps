// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace bOps.Abstractions;

/// <summary>A runtime projection of evidence limitations; no value certifies the truth of a diagnosis.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DiagnosticAssuranceLevel>))]
public enum DiagnosticAssuranceLevel
{
    /// <summary>A collected result, observation, execution or correction carries a known limitation.</summary>
    Qualified,

    /// <summary>The runtime cannot establish complete typed-fact and grounding coverage.</summary>
    Unchecked,

    /// <summary>No runtime limitation was detected and all collected typed facts were shown to the model check; its judgement is not proof.</summary>
    Checked,

    /// <summary>No operational evidence was collected or required by an executable plan.</summary>
    NotApplicable,
}
