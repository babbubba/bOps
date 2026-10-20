// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Knowledge;

/// <summary>
/// The size limits of the Knowledge index (ADR-0057 section B.4). They are a host parameter, never a pack's: a pack cannot widen
/// what the model is shown.
/// </summary>
/// <param name="Entries">How many skills a normal rendering lists at most.</param>
/// <param name="ResourcesPerEntry">How many resources a normal rendering lists per skill; the rest are counted in <c>more</c>.</param>
/// <param name="MaxDataChars">The most UTF-16 code units the whole data text may have in a normal rendering.</param>
/// <param name="AggressiveEntries">How many skills the reduced rendering of a request retried after a context overflow lists at most.</param>
/// <param name="AggressiveResources">How many resources per skill the reduced rendering lists.</param>
/// <param name="AggressiveMaxDataChars">The most UTF-16 code units the reduced rendering may have.</param>
/// <param name="DiscoverPageChars">The most UTF-16 code units of one discover page, header line included.</param>
public sealed record KnowledgeIndexLimits(
    int Entries = 8,
    int ResourcesPerEntry = 6,
    int MaxDataChars = 4000,
    int AggressiveEntries = 3,
    int AggressiveResources = 2,
    int AggressiveMaxDataChars = 1500,
    int DiscoverPageChars = 7000)
{
    /// <summary>The ADR-0057 limits.</summary>
    public static KnowledgeIndexLimits Default { get; } = new();

    internal void EnsureValid()
    {
        if (Entries < 1 || ResourcesPerEntry < 0 || MaxDataChars < 1
            || AggressiveEntries < 1 || AggressiveResources < 0 || AggressiveMaxDataChars < 1
            || DiscoverPageChars < 1)
        {
            throw new ArgumentException("knowledge index limits are out of range", nameof(KnowledgeIndexLimits));
        }
    }
}
