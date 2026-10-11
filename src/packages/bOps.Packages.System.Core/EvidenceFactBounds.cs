// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using bOps.Abstractions;

namespace bOps.Packages.Sys.Core;

/// <summary>
/// The bound every high-density fact projection of this package keeps (ADR-0050; ADR-0052 AF-04), below the runtime's own limit of
/// 16 facts and 4096 serialized bytes: whole facts are dropped from the end of an already priority-ordered list, never cut.
/// </summary>
internal static class EvidenceFactBounds
{
    internal const int MaximumFacts = 16;
    internal const int MaximumSerializedBytes = 4096;

    /// <summary>The longest fact type or key (the runtime's identifier bound).</summary>
    internal const int MaximumIdentifierCharacters = 128;

    /// <summary>The longest text value kept whole; a longer value is skipped, never truncated.</summary>
    internal const int MaximumValueCharacters = 256;

    /// <summary>Keeps the leading facts that fit both bounds.</summary>
    internal static IReadOnlyList<EvidenceFact> Fit(List<EvidenceFact> facts)
    {
        while (facts.Count > 0
            && (facts.Count > MaximumFacts || JsonSerializer.SerializeToUtf8Bytes(facts).Length > MaximumSerializedBytes))
        {
            facts.RemoveAt(facts.Count - 1);
        }

        return facts;
    }
}
