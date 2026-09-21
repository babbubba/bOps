// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Packages.Sys.Core;

/// <summary>Events read from the operating system plus the explicit status of every source that was consulted.</summary>
/// <param name="Events">The records the collector found, in any order.</param>
/// <param name="Sources">One entry per log source, so a gap is never mistaken for an empty log.</param>
/// <param name="CollectionTruncated">True when the collector stopped at its scan ceiling before reaching the start of the window.</param>
public sealed record SystemEventSnapshot(
    IReadOnlyList<SystemEventRecord> Events,
    IReadOnlyList<InventorySourceResult> Sources,
    bool CollectionTruncated = false);
