// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.PluginHost;

namespace bOps.Api;

/// <summary>
/// Maps the read-only <c>GET /api/plugins</c> catalog (V1.1-F). It projects the authoritative lifecycle
/// state and ETag (V1.3-M6) but never mutates; the administrator-only install/replace/enable/disable/
/// recover operations live in <see cref="PluginLifecycleEndpoints"/> under the same prefix.
/// </summary>
internal static class PluginCatalogEndpoints
{
    private const int DefaultLimit = 50;
    private const int MaximumLimit = 200;

    internal static void MapPluginCatalogEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/plugins").RequireAuthorization(ApiAuthorization.ViewerPolicy);

        group.MapGet("/", (
            bool? enabled,
            PackageTrustLevel? trust,
            int? limit,
            int? offset,
            PluginManager pluginManager,
            PluginLifecycleService lifecycle,
            IToolRegistry toolRegistry) =>
        {
            var boundedLimit = Math.Clamp(limit ?? DefaultLimit, 1, MaximumLimit);
            var boundedOffset = Math.Max(offset ?? 0, 0);

            var filtered = pluginManager.List()
                .Where(record => enabled is null || record.Enabled == enabled)
                .Where(record => trust is null || (record.Provenance?.Trust ?? PackageTrustLevel.Unverified) == trust)
                .OrderBy(record => record.Id, StringComparer.Ordinal)
                .ToList();

            var page = filtered.Skip(boundedOffset).Take(boundedLimit)
                .Select(record => Project(record, pluginManager, lifecycle, toolRegistry))
                .ToList();

            return Results.Ok(new PluginCatalogPage(page, filtered.Count));
        });

        group.MapGet("/{id}", (string id, PluginManager pluginManager, PluginLifecycleService lifecycle, IToolRegistry toolRegistry, HttpContext http) =>
        {
            var record = pluginManager.List().FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal));
            if (record is null)
            {
                return Results.NotFound(new { message = $"No installed plugin with id '{id}'." });
            }

            var entry = Project(record, pluginManager, lifecycle, toolRegistry);
            if (entry.LifecycleETag is not null)
            {
                // The validator a mutation of this plugin must present in If-Match.
                http.Response.Headers.ETag = entry.LifecycleETag;
            }

            return Results.Ok(entry);
        });
    }

    private static PluginCatalogEntry Project(PluginRecord record, PluginManager pluginManager, PluginLifecycleService lifecycle, IToolRegistry toolRegistry)
    {
        var loaded = pluginManager.IsActivated(record.Id);
        var effectiveMaxRisk = loaded
            ? toolRegistry.GetAvailableManifests()
                .Where(manifest => manifest.Package.Value == record.Id)
                .Select(manifest => (RiskLevel?)manifest.Risk)
                .DefaultIfEmpty()
                .Max()
            : null;

        var loadError = pluginManager.StartupLoadErrors.TryGetValue(record.Id, out var startupError) ? startupError : null;
        var status = lifecycle.GetStatus(record.Id);

        return new PluginCatalogEntry(
            record.Id,
            record.Manifest.Version,
            record.Manifest.Publisher,
            record.InstalledAtUtc,
            record.Enabled,
            loaded,
            IsCompatible(record),
            record.Provenance?.SignaturePresent ?? false,
            record.Provenance?.Verified ?? false,
            record.Provenance?.Trust ?? PackageTrustLevel.Unverified,
            record.Provenance?.KeyId,
            record.Manifest.DeclaredCapabilities,
            [.. record.Manifest.Dependencies.Select(dependency => new PluginCatalogDependency(dependency.Name, dependency.Version))],
            record.Manifest.MaxDeclaredRisk,
            effectiveMaxRisk,
            loadError,
            status?.State,
            status?.ETag,
            status?.LifecycleFailure,
            status?.RecoveryAvailable ?? false);
    }

    /// <summary>
    /// Re-runs the same manifest validation enforced at install time, live, so a plugin that
    /// became incompatible since (a host upgrade without reinstalling it) is caught instead of
    /// silently assumed fine forever. A <see cref="PluginValidationException"/> here is an
    /// expected outcome for an installed-but-now-invalid plugin, not a broken invariant — caught
    /// and converted to a boolean per the error model in agentic/02-coding-standards.md.
    /// </summary>
    private static bool IsCompatible(PluginRecord record)
    {
        try
        {
            PluginManifestValidator.Validate(record.Manifest, record.InstallPath);
            return true;
        }
        catch (PluginValidationException)
        {
            return false;
        }
    }
}
