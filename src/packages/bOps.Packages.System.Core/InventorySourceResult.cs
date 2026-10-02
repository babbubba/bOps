// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Packages.Sys.Core;

/// <summary>The explicit outcome of reading one inventory source.</summary>
public sealed record InventorySourceResult(
    string Name,
    InventorySourceStatus Status,
    string? Detail)
{
    /// <summary>
    /// For a time-windowed evidence source: the oldest instant this call's scan of the source actually reached — the start of the
    /// window when the scan ran to it, the time of the oldest record read when a ceiling or time bound stopped it, and <c>null</c> when
    /// the source was not read or stopped before its first record (ADR-0032 HARDEN-7 amendment §5).
    /// </summary>
    public DateTimeOffset? ExaminedFromUtc { get; init; }
}
