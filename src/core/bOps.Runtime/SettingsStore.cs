// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace bOps.Runtime;

/// <summary>
/// Tracks the administrator's chosen active provider id and every provider's non-secret profile
/// in one plain JSON file, kept separate from the encrypted vault (ADR-0029) since none of it is
/// secret. Same atomic temp-file-then-rename write pattern as <see cref="VaultStore"/>.
/// </summary>
public sealed class SettingsStore(string settingsFilePath, TimeProvider timeProvider)
{
    private const int CurrentSchemaVersion = 1;
    private const int MaxFallbacks = 3;
    private readonly object _writeGate = new();

    /// <summary>Optimistic revision of the persisted settings file.</summary>
    public int Revision => LoadAll().Revision;

    /// <summary>The administrator's chosen active provider id, or <c>null</c> if none has been set.</summary>
    public string? ActiveProviderId => LoadAll().ActiveProviderId;

    /// <summary>Every stored provider profile, ordered by provider id.</summary>
    public IReadOnlyList<ProviderProfile> ListProviderProfiles() =>
        LoadAll().Providers.Values.OrderBy(profile => profile.ProviderId, StringComparer.Ordinal).ToList();

    /// <summary>The stored profile for a provider, or <c>null</c> if none is stored.</summary>
    public ProviderProfile? FindProviderProfile(string providerId) =>
        LoadAll().Providers.GetValueOrDefault(providerId);

    /// <summary>The administrator's ordered fallback list; empty when none is stored (fallback disabled).</summary>
    public IReadOnlyList<FallbackSetting> Fallbacks => LoadAll().Fallbacks ?? [];

    /// <summary>Replaces the ordered fallback list (an empty list clears it). Shape only; the API validates providers.</summary>
    public void SetFallbacks(IReadOnlyList<FallbackSetting> fallbacks, int? expectedRevision = null)
    {
        ArgumentNullException.ThrowIfNull(fallbacks);
        if (fallbacks.Count > MaxFallbacks)
        {
            throw new ArgumentException($"At most {MaxFallbacks} model fallback candidates may be stored.", nameof(fallbacks));
        }

        if (fallbacks.Any(entry => entry is null || string.IsNullOrWhiteSpace(entry.Provider) || string.IsNullOrWhiteSpace(entry.Model)))
        {
            throw new ArgumentException("Every fallback needs a nonblank provider and model.", nameof(fallbacks));
        }

        lock (_writeGate)
        {
            var file = LoadAll();
            CheckRevision(file, expectedRevision);
            SaveAll(file with { Fallbacks = fallbacks.Count == 0 ? null : [.. fallbacks], Revision = checked(file.Revision + 1) });
        }
    }

    /// <summary>Persists the active provider id.</summary>
    public void SetActiveProviderId(string providerId, int? expectedRevision = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        lock (_writeGate)
        {
            var file = LoadAll();
            CheckRevision(file, expectedRevision);
            SaveAll(file with { ActiveProviderId = providerId, Revision = checked(file.Revision + 1) });
        }
    }

    /// <summary>Stores (or replaces) a provider's non-secret profile.</summary>
#pragma warning disable CA1054 // baseUrl is configuration-bound, same as ChatModelOptions.BaseUrl.
    public void SetProviderProfile(
        string providerId, string baseUrl, string model, bool supportsNativeToolCalling,
        IReadOnlyDictionary<string, string>? extraParameters, int? expectedRevision = null)
#pragma warning restore CA1054
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"'{baseUrl}' is not an absolute http/https URL.", nameof(baseUrl));
        }

        lock (_writeGate)
        {
            var file = LoadAll();
            CheckRevision(file, expectedRevision);
            file.Providers[providerId] = new ProviderProfile(
                providerId, baseUrl, model, supportsNativeToolCalling,
                extraParameters is null ? [] : new Dictionary<string, string>(extraParameters),
                timeProvider.GetUtcNow());
            SaveAll(file with { Revision = checked(file.Revision + 1) });
        }
    }

    /// <summary>Removes a provider's stored profile, if any.</summary>
    public void RemoveProviderProfile(string providerId, int? expectedRevision = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        lock (_writeGate)
        {
            var file = LoadAll();
            CheckRevision(file, expectedRevision);
            if (file.Providers.Remove(providerId))
            {
                SaveAll(file with { Revision = checked(file.Revision + 1) });
            }
        }
    }

    private static void CheckRevision(SettingsFile file, int? expectedRevision)
    {
        if (expectedRevision is { } expected && expected != file.Revision)
        {
            throw new SettingsConcurrencyException(expected, file.Revision);
        }
    }

    private SettingsFile LoadAll()
    {
        if (!File.Exists(settingsFilePath))
        {
            return new SettingsFile(CurrentSchemaVersion, null, []);
        }

        try
        {
            var json = File.ReadAllText(settingsFilePath);
            return JsonSerializer.Deserialize(json, SettingsJsonContext.Default.SettingsFile)
                ?? throw new SettingsCorruptedException($"'{settingsFilePath}' (settings.json) is empty.");
        }
        catch (JsonException ex)
        {
            throw new SettingsCorruptedException($"'{settingsFilePath}' (settings.json) is corrupted and could not be parsed.", ex);
        }
    }

    private void SaveAll(SettingsFile file)
    {
        var directory = Path.GetDirectoryName(settingsFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(file, SettingsJsonContext.Default.SettingsFile);
        var tempPath = $"{settingsFilePath}.tmp-{Guid.NewGuid():N}";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, settingsFilePath, overwrite: true);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(settingsFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
