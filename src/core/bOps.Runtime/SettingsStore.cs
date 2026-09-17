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

    /// <summary>The administrator's chosen active provider id, or <c>null</c> if none has been set.</summary>
    public string? ActiveProviderId => LoadAll().ActiveProviderId;

    /// <summary>Every stored provider profile, ordered by provider id.</summary>
    public IReadOnlyList<ProviderProfile> ListProviderProfiles() =>
        LoadAll().Providers.Values.OrderBy(profile => profile.ProviderId, StringComparer.Ordinal).ToList();

    /// <summary>The stored profile for a provider, or <c>null</c> if none is stored.</summary>
    public ProviderProfile? FindProviderProfile(string providerId) =>
        LoadAll().Providers.GetValueOrDefault(providerId);

    /// <summary>Persists the active provider id.</summary>
    public void SetActiveProviderId(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        var file = LoadAll();
        SaveAll(file with { ActiveProviderId = providerId });
    }

    /// <summary>Stores (or replaces) a provider's non-secret profile.</summary>
#pragma warning disable CA1054 // baseUrl is configuration-bound, same as ChatModelOptions.BaseUrl.
    public void SetProviderProfile(
        string providerId, string baseUrl, string model, bool supportsNativeToolCalling,
        IReadOnlyDictionary<string, string>? extraParameters)
#pragma warning restore CA1054
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"'{baseUrl}' is not an absolute http/https URL.", nameof(baseUrl));
        }

        var file = LoadAll();
        file.Providers[providerId] = new ProviderProfile(
            providerId, baseUrl, model, supportsNativeToolCalling,
            extraParameters is null ? [] : new Dictionary<string, string>(extraParameters),
            timeProvider.GetUtcNow());
        SaveAll(file);
    }

    /// <summary>Removes a provider's stored profile, if any.</summary>
    public void RemoveProviderProfile(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        var file = LoadAll();
        if (file.Providers.Remove(providerId))
        {
            SaveAll(file);
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
