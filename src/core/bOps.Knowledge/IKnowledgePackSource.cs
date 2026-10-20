// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Knowledge;

/// <summary>
/// A restricted external place packs are read from. A source returns bytes and nothing else: it composes nothing into a model
/// context, runs nothing in the agent loop, and what it returns is verified like any other input. A source bounds what it reads by
/// <see cref="KnowledgePackLimits"/> (and sets <see cref="RawKnowledgePack.ExceededLimits"/> when it stops), never follows a link,
/// and does not read outside the one pack it was asked for. A source is used only after the
/// host registered that exact instance with <see cref="KnowledgeProviderRegistry"/>; what a source says about itself (its name, its
/// type, any member it exposes) is never read to decide trust, tenant or kind.
/// </summary>
public interface IKnowledgePackSource
{
    /// <summary>
    /// Reads the pack a task asked for, for the tenant the host derived. The tenant is passed so that a source can partition by it;
    /// it is not a filter the source is trusted to apply, because the host's grants and the tenant-partitioned snapshots decide.
    /// </summary>
    /// <returns>The raw pack, or <c>null</c> when the source has none.</returns>
    Task<RawKnowledgePack?> OpenAsync(PackReference reference, TenantId tenant, CancellationToken ct = default);
}
