// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Api;

/// <summary>
/// The read-only, operator-facing projection of a <see cref="bOps.PluginHost.PluginRecord"/>
/// (V1.1-F). Deliberately narrower than the record it projects: never carries
/// <c>InstallPath</c>, a raw exception stack, or trust-store content — only what an operator can
/// act on. <c>enabled</c> (persisted intent) and <c>loaded</c> (actually registered in this
/// process right now) are kept as two distinct fields on purpose — a plugin can be enabled but not
/// loaded (see <see cref="LoadError"/>), and must never be presented as one merged "status".
/// </summary>
internal sealed record PluginCatalogEntry(
    string Id,
    string Version,
    string Publisher,
    DateTimeOffset InstalledAtUtc,
    bool Enabled,
    bool Loaded,
    bool Compatible,
    bool SignaturePresent,
    bool Verified,
    PackageTrustLevel Trust,
    string? KeyId,
    IReadOnlyList<string> DeclaredCapabilities,
    IReadOnlyList<PluginCatalogDependency> Dependencies,
    RiskLevel? DeclaredMaxRisk,
    RiskLevel? EffectiveMaxRisk,
    string? LoadError);

/// <summary>One informational dependency, as shown to an operator — name and version only, matching <see cref="bOps.Abstractions.PluginDependency"/>.</summary>
internal sealed record PluginCatalogDependency(string Name, string Version);

/// <summary>A bounded page of <see cref="PluginCatalogEntry"/> results, deterministically ordered by <see cref="PluginCatalogEntry.Id"/>.</summary>
internal sealed record PluginCatalogPage(IReadOnlyList<PluginCatalogEntry> Entries, int TotalCount);
