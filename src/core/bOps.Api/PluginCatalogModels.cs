// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;
using bOps.Abstractions;
using bOps.PluginHost;

namespace bOps.Api;

/// <summary>
/// The read-only, operator-facing projection of a <see cref="bOps.PluginHost.PluginRecord"/>
/// (V1.1-F). Deliberately narrower than the record it projects: never carries
/// <c>InstallPath</c>, a raw exception stack, or trust-store content — only what an operator can
/// act on. <c>enabled</c> (persisted intent) and <c>loaded</c> (actually registered in this
/// process right now) are kept as two distinct fields on purpose — a plugin can be enabled but not
/// loaded (see <see cref="LoadError"/>), and must never be presented as one merged "status".
/// <see cref="LifecycleState"/> and <see cref="LifecycleETag"/> (V1.3-M6) are additive: the authoritative
/// ADR-0037 lifecycle state and the opaque ETag a mutation must present in <c>If-Match</c>.
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
    string? LoadError,
    [property: JsonConverter(typeof(JsonStringEnumConverter<PluginLifecycleState>))]
    PluginLifecycleState? LifecycleState = null,
    string? LifecycleETag = null,
    string? LifecycleFailure = null,
    bool RecoveryAvailable = false);

/// <summary>One informational dependency, as shown to an operator — name and version only, matching <see cref="bOps.Abstractions.PluginDependency"/>.</summary>
internal sealed record PluginCatalogDependency(string Name, string Version);

/// <summary>A bounded page of <see cref="PluginCatalogEntry"/> results, deterministically ordered by <see cref="PluginCatalogEntry.Id"/>.</summary>
internal sealed record PluginCatalogPage(IReadOnlyList<PluginCatalogEntry> Entries, int TotalCount);
