// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.RegularExpressions;
using bOps.Abstractions;

namespace bOps.PluginHost;

/// <summary>
/// Validates a <see cref="PluginManifest"/> before anything from the plugin is loaded (ADR-0020).
/// Fails loud with a specific reason — mirrors <c>bOps.Policy.PolicyConfigLoader</c>'s pattern —
/// and never coerces a malformed or incompatible manifest into something the loader proceeds
/// with anyway.
/// </summary>
public static partial class PluginManifestValidator
{
    /// <summary>The only manifest schema version this loader currently understands.</summary>
    public const int SupportedSchemaVersion = 1;

    [GeneratedRegex("^[a-z0-9]+(?:[.-][a-z0-9]+)*$")]
    private static partial Regex IdPattern();

    /// <summary>
    /// Validates <paramref name="manifest"/> for a plugin installed at <paramref name="pluginDirectory"/>.
    /// Throws <see cref="PluginValidationException"/> on the first failure found.
    /// </summary>
    /// <param name="manifest">The parsed manifest.</param>
    /// <param name="pluginDirectory">The plugin's installed folder — <see cref="PluginManifest.EntryAssembly"/> is resolved relative to this.</param>
    /// <param name="hostAbstractionsVersion">The running host's <c>bOps.Abstractions</c> version. Defaults to the version actually loaded, and is a parameter only so tests can simulate a mismatch without publishing multiple assembly versions.</param>
    public static void Validate(PluginManifest manifest, string pluginDirectory, Version? hostAbstractionsVersion = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);

        if (manifest.SchemaVersion != SupportedSchemaVersion)
        {
            throw new PluginValidationException(
                $"Unsupported manifest schema version {manifest.SchemaVersion}; this loader understands version {SupportedSchemaVersion} only.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Id) || !IdPattern().IsMatch(manifest.Id))
        {
            throw new PluginValidationException(
                $"Manifest id '{manifest.Id}' is invalid: it must be lowercase, dot- or hyphen-separated (e.g. 'acme.sample-plugin').");
        }

        if (manifest.Id.StartsWith("bops.", StringComparison.Ordinal))
        {
            throw new PluginValidationException(
                $"Manifest id '{manifest.Id}' is reserved: the 'bops.' prefix is for first-party packages only " +
                "(rule A11 — a package cannot claim to be another package's identity).");
        }

        if (string.IsNullOrWhiteSpace(manifest.Publisher))
        {
            throw new PluginValidationException("Manifest publisher must not be empty.");
        }

        if (!TryParseAtLeastMajorMinor(manifest.Version, out _))
        {
            throw new PluginValidationException($"Manifest version '{manifest.Version}' is not a valid version (expected at least 'Major.Minor').");
        }

        if (!TryParseAtLeastMajorMinor(manifest.MinHostAbstractionsVersion, out var minHostVersion))
        {
            throw new PluginValidationException(
                $"Manifest MinHostAbstractionsVersion '{manifest.MinHostAbstractionsVersion}' is not a valid version.");
        }

        var effectiveHostVersion = hostAbstractionsVersion ?? typeof(PackageId).Assembly.GetName().Version ?? new Version(0, 0, 0);
        if (effectiveHostVersion < minHostVersion)
        {
            throw new PluginValidationException(
                $"This plugin requires bOps.Abstractions >= {minHostVersion}, but the running host has {effectiveHostVersion}.");
        }

        if (string.IsNullOrWhiteSpace(manifest.EntryAssembly))
        {
            throw new PluginValidationException("Manifest EntryAssembly must not be empty.");
        }

        var entryAssemblyPath = Path.Combine(pluginDirectory, manifest.EntryAssembly);
        if (!File.Exists(entryAssemblyPath))
        {
            throw new PluginValidationException($"Entry assembly '{manifest.EntryAssembly}' was not found under '{pluginDirectory}'.");
        }

        if (string.IsNullOrWhiteSpace(manifest.EntryType))
        {
            throw new PluginValidationException("Manifest entry type must not be empty.");
        }

        var declaredVersionsByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dependency in manifest.Dependencies)
        {
            if (declaredVersionsByName.TryGetValue(dependency.Name, out var existingVersion) &&
                !string.Equals(existingVersion, dependency.Version, StringComparison.Ordinal))
            {
                throw new PluginValidationException(
                    $"Manifest declares conflicting versions for dependency '{dependency.Name}': '{existingVersion}' and '{dependency.Version}'.");
            }

            declaredVersionsByName[dependency.Name] = dependency.Version;
        }

        if (manifest.MaxDeclaredRisk is { } maxDeclaredRisk && !Enum.IsDefined(maxDeclaredRisk))
        {
            throw new PluginValidationException($"Manifest MaxDeclaredRisk '{maxDeclaredRisk}' is not a recognized risk level.");
        }
    }

    /// <summary>
    /// Reads and deserializes <c>bops-plugin.json</c> from <paramref name="directory"/>. Does
    /// not validate it — call <see cref="Validate"/> afterward. Shared by
    /// <see cref="PluginManager.Install"/> and the CLI's <c>bops plugin validate</c>, so both go
    /// through exactly the same parsing.
    /// </summary>
    public static PluginManifest ReadManifest(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var manifestPath = Path.Combine(directory, "bops-plugin.json");
        if (!File.Exists(manifestPath))
        {
            throw new PluginValidationException($"No bops-plugin.json found under '{directory}'.");
        }

        try
        {
            var json = File.ReadAllText(manifestPath);
            return JsonSerializer.Deserialize(json, PluginManifestJsonContext.Default.PluginManifest)
                ?? throw new PluginValidationException($"'{manifestPath}' deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new PluginValidationException($"'{manifestPath}' is not valid JSON.", ex);
        }
    }

    private static bool TryParseAtLeastMajorMinor(string? value, out Version version)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.Contains('.', StringComparison.Ordinal) || !Version.TryParse(value, out var parsed))
        {
            version = new Version(0, 0);
            return false;
        }

        version = parsed;
        return true;
    }
}
