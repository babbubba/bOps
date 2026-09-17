// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Packages.Web;

/// <summary>
/// The capability name <c>web.search</c> declares in <c>Requires</c>, and the check the host
/// registers against <c>ICapabilityProbe</c> (agentic/01-architecture-rules.md, rule A8) — an
/// unconfigured SearXNG endpoint makes <c>web.search</c> disappear from
/// <see cref="bOps.Abstractions.IToolRegistry.GetAvailableManifests"/> entirely, rather than
/// failing only once the model tries to call it (mirrors <c>DockerCapability</c>).
/// </summary>
public static class WebCapabilities
{
    /// <summary>The capability identifier: <c>"web.searxng"</c>.</summary>
    public const string Searxng = "web.searxng";

    /// <summary>A well-formed absolute http/https <see cref="WebSearchOptions.BaseUrl"/> is a local, synchronous check — never a live network probe.</summary>
    public static Task<bool> IsSearxngConfiguredAsync(WebSearchOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(options.IsConfigured);
    }
}
