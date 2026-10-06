// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Runtime.Tests;

/// <summary>
/// The non-secret Settings store (ADR-0029) — active provider selection and each provider's full
/// non-secret profile (endpoint, model, tool-calling support, extras). Kept separate from the
/// encrypted vault, which holds only each provider's API key. Same atomic temp-file-then-rename
/// write pattern as <see cref="VaultStore"/>.
/// </summary>
public sealed class SettingsStoreTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("bops-settings-store-");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero));
    private string SettingsPath => Path.Combine(_dir.FullName, "settings.json");

    public void Dispose() => _dir.Delete(recursive: true);

    private SettingsStore CreateStore() => new(SettingsPath, _time);

    [Fact]
    public void LegacyFileWithoutRevision_RemainsReadableAndGetsAnOptimisticRevisionOnWrite()
    {
        File.WriteAllText(SettingsPath,
            "{\"SchemaVersion\":1,\"ActiveProviderId\":\"Anthropic\",\"Providers\":{}}");
        var store = CreateStore();
        Assert.Equal(0, store.Revision);
        Assert.Equal("Anthropic", store.ActiveProviderId);
        store.SetActiveProviderId("OpenRouter", expectedRevision: 0);
        Assert.Equal(1, CreateStore().Revision);
        Assert.Throws<SettingsConcurrencyException>(() =>
            store.SetActiveProviderId("Anthropic", expectedRevision: 0));
        Assert.Equal("OpenRouter", store.ActiveProviderId);
    }

    [Fact]
    public void Fallbacks_AreAdditive_OrderedBoundedAndClearable_AndGuardedByRevision()
    {
        File.WriteAllText(SettingsPath, "{\"SchemaVersion\":1,\"ActiveProviderId\":\"Anthropic\",\"Providers\":{},\"Revision\":4}");
        var store = CreateStore();
        Assert.Empty(store.Fallbacks);

        store.SetFallbacks([new("Anthropic", "b"), new("OpenRouter", "a")], expectedRevision: 4);
        Assert.Equal([("Anthropic", "b"), ("OpenRouter", "a")], CreateStore().Fallbacks.Select(f => (f.Provider, f.Model)));
        Assert.Equal(5, store.Revision);
        Assert.Throws<SettingsConcurrencyException>(() => store.SetFallbacks([new("Anthropic", "x")], expectedRevision: 4));
        Assert.Throws<ArgumentException>(() => store.SetFallbacks([new("A", "1"), new("A", "2"), new("A", "3"), new("A", "4")]));
        Assert.Throws<ArgumentException>(() => store.SetFallbacks([new("A", " ")]));
        Assert.Equal(5, store.Revision);

        store.SetFallbacks([], expectedRevision: 5);
        Assert.Empty(CreateStore().Fallbacks);
        Assert.Equal("Anthropic", CreateStore().ActiveProviderId);
    }

    [Fact]
    public void ActiveProviderId_IsNull_WhenNoSettingsFileExistsYet()
    {
        Assert.Null(CreateStore().ActiveProviderId);
    }

    [Fact]
    public void SetActiveProviderId_ThenANewStoreInstance_SeesThePersistedSelection()
    {
        CreateStore().SetActiveProviderId("anthropic");

        Assert.Equal("anthropic", CreateStore().ActiveProviderId);
    }

    [Fact]
    public void SetActiveProviderId_RejectsAnEmptyProviderId()
    {
        Assert.Throws<ArgumentException>(() => CreateStore().SetActiveProviderId(string.Empty));
    }

    [Fact]
    public void SetProviderProfile_ThenANewStoreInstance_SeesThePersistedProfile()
    {
        CreateStore().SetProviderProfile("anthropic", "https://api.anthropic.com", "claude-sonnet-4-5",
            supportsNativeToolCalling: true, extraParameters: new Dictionary<string, string> { ["apiVersion"] = "2023-06-01" });

        var profile = CreateStore().FindProviderProfile("anthropic");

        Assert.NotNull(profile);
        Assert.Equal("anthropic", profile!.ProviderId);
        Assert.Equal("https://api.anthropic.com", profile.BaseUrl);
        Assert.Equal("claude-sonnet-4-5", profile.Model);
        Assert.True(profile.SupportsNativeToolCalling);
        Assert.Equal("2023-06-01", profile.ExtraParameters["apiVersion"]);
        Assert.Equal(_time.GetUtcNow(), profile.UpdatedUtc);
    }

    [Fact]
    public void SetProviderProfile_RejectsABaseUrlThatIsNotAnAbsoluteHttpUri()
    {
        var store = CreateStore();

        Assert.Throws<ArgumentException>(() =>
            store.SetProviderProfile("anthropic", "not-a-url", "claude-sonnet-4-5", true, null));
    }

    [Fact]
    public void SetProviderProfile_RejectsAnEmptyModel()
    {
        var store = CreateStore();

        Assert.Throws<ArgumentException>(() =>
            store.SetProviderProfile("anthropic", "https://api.anthropic.com", string.Empty, true, null));
    }

    [Fact]
    public void SetProviderProfile_Replacing_OverwritesThePreviousProfile()
    {
        var store = CreateStore();
        store.SetProviderProfile("anthropic", "https://api.anthropic.com", "claude-sonnet-4-5", true, null);

        _time.Advance(TimeSpan.FromMinutes(5));
        store.SetProviderProfile("anthropic", "https://api.anthropic.com", "claude-opus-5", false, null);

        var profile = store.FindProviderProfile("anthropic");
        Assert.Equal("claude-opus-5", profile!.Model);
        Assert.False(profile.SupportsNativeToolCalling);
        Assert.Equal(_time.GetUtcNow(), profile.UpdatedUtc);
    }

    [Fact]
    public void RemoveProviderProfile_DropsTheProfile()
    {
        var store = CreateStore();
        store.SetProviderProfile("anthropic", "https://api.anthropic.com", "claude-sonnet-4-5", true, null);

        store.RemoveProviderProfile("anthropic");

        Assert.Null(store.FindProviderProfile("anthropic"));
    }

    [Fact]
    public void ListProviderProfiles_OrdersByProviderId()
    {
        var store = CreateStore();
        store.SetProviderProfile("openai", "https://api.openai.com/v1", "gpt-5", true, null);
        store.SetProviderProfile("anthropic", "https://api.anthropic.com", "claude-sonnet-4-5", true, null);

        var ids = store.ListProviderProfiles().Select(profile => profile.ProviderId).ToArray();

        Assert.Equal(["anthropic", "openai"], ids);
    }

    [Fact]
    public void FindProviderProfile_ReturnsNull_ForAnUnknownProvider()
    {
        Assert.Null(CreateStore().FindProviderProfile("nonexistent"));
    }

    [Fact]
    public void SetActiveProviderId_AndSetProviderProfile_PersistIndependently()
    {
        var store = CreateStore();

        store.SetActiveProviderId("anthropic");
        store.SetProviderProfile("anthropic", "https://api.anthropic.com", "claude-sonnet-4-5", true, null);

        var reopened = CreateStore();
        Assert.Equal("anthropic", reopened.ActiveProviderId);
        Assert.NotNull(reopened.FindProviderProfile("anthropic"));
    }

    [Fact]
    public void SetProviderProfile_RestrictsSettingsFilePermissions_OnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        CreateStore().SetProviderProfile("anthropic", "https://api.anthropic.com", "claude-sonnet-4-5", true, null);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(SettingsPath));
    }

    [Fact]
    public void Load_OfACorruptedSettingsFile_ThrowsAClearError_RatherThanSilentlyIgnoringTheStoredSelection()
    {
        File.WriteAllText(SettingsPath, "{ this is not valid json");

        var ex = Assert.Throws<SettingsCorruptedException>(() => CreateStore().ActiveProviderId);
        Assert.Contains("settings.json", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
