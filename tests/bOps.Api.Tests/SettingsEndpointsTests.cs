// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace bOps.Api.Tests;

/// <summary>
/// Drives <c>/api/settings</c> (ADR-0029) against the real composition root, including its real
/// <see cref="bOps.Runtime.VaultStore"/>/<see cref="bOps.Runtime.SettingsStore"/> wiring — the
/// test-configured master key is real, so this exercises genuine AES-256-GCM round trips, not a
/// stub.
/// </summary>
public sealed class SettingsEndpointsTests
{
    private static readonly JsonSerializerOptions ResponseJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] AdministratorRoles = ["viewer", "operator", "approver", "administrator"];

    [Fact]
    public async Task GetSettings_RequiresAdministrator_RejectsPlainOperator()
    {
        using var factory = new TestAppFactory { Roles = ["viewer", "operator", "approver"] };
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/api/settings", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetSettings_RequiresAuthentication()
    {
        using var factory = new TestAppFactory { Roles = AdministratorRoles };
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync(new Uri("/api/settings", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetSettings_StartsWithNoStoredKeysOrProfiles()
    {
        using var factory = new TestAppFactory { Roles = AdministratorRoles };
        using var client = factory.CreateClient();

        var view = await client.GetFromJsonAsync<SettingsView>("/api/settings", ResponseJsonOptions);

        Assert.NotNull(view);
        Assert.Equal(0, view!.VaultVersion);
        Assert.Contains(view.Providers, provider => provider.ProviderId == "Anthropic" && !provider.HasStoredKey);
    }

    [Fact]
    public async Task SetProviderKey_ThenGetSettings_ShowsTheMask_NeverThePlaintext()
    {
        using var factory = new TestAppFactory { Roles = AdministratorRoles };
        using var client = factory.CreateClient();

        var setResponse = await client.PutAsJsonAsync(
            new Uri("/api/settings/providers/Anthropic/key", UriKind.Relative),
            new { apiKey = "sk-ant-abcdefghijklmnopqrstuvwxyz", expectedVersion = 0 });
        Assert.Equal(HttpStatusCode.NoContent, setResponse.StatusCode);

        var raw = await client.GetStringAsync(new Uri("/api/settings", UriKind.Relative));
        Assert.DoesNotContain("sk-ant-abcdefghijklmnopqrstuvwxyz", raw, StringComparison.Ordinal);

        var view = JsonSerializer.Deserialize<SettingsView>(raw, ResponseJsonOptions);
        var anthropic = view!.Providers.Single(provider => provider.ProviderId == "Anthropic");
        Assert.True(anthropic.HasStoredKey);
        Assert.Equal("sk-ant", anthropic.KeyMaskPrefix);
        Assert.Equal("wxyz", anthropic.KeyMaskSuffix);
        Assert.Equal(1, view.VaultVersion);
    }

    [Fact]
    public async Task SetProviderKey_NeverWritesThePlaintextToTheAuditLog()
    {
        using var factory = new TestAppFactory { Roles = AdministratorRoles };
        using var client = factory.CreateClient();

        await client.PutAsJsonAsync(
            new Uri("/api/settings/providers/Anthropic/key", UriKind.Relative),
            new { apiKey = "sk-ant-abcdefghijklmnopqrstuvwxyz", expectedVersion = 0 });

        var auditPath = Path.Combine(factory.TempDirectory, "audit.jsonl");
        var auditLog = await File.ReadAllTextAsync(auditPath);
        Assert.Contains("settingsChanged", auditLog, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-ant-abcdefghijklmnopqrstuvwxyz", auditLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetProviderKey_WithAStaleExpectedVersion_ReturnsConflict()
    {
        using var factory = new TestAppFactory { Roles = AdministratorRoles };
        using var client = factory.CreateClient();
        await client.PutAsJsonAsync(
            new Uri("/api/settings/providers/Anthropic/key", UriKind.Relative),
            new { apiKey = "sk-ant-abcdefghijklmnopqrstuvwxyz", expectedVersion = 0 });

        var conflict = await client.PutAsJsonAsync(
            new Uri("/api/settings/providers/Anthropic/key", UriKind.Relative),
            new { apiKey = "sk-ant-replacementvalue0123456789", expectedVersion = 0 });

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task ClearProviderKey_RemovesTheStoredKey()
    {
        using var factory = new TestAppFactory { Roles = AdministratorRoles };
        using var client = factory.CreateClient();
        await client.PutAsJsonAsync(
            new Uri("/api/settings/providers/Anthropic/key", UriKind.Relative),
            new { apiKey = "sk-ant-abcdefghijklmnopqrstuvwxyz", expectedVersion = 0 });

        var clearResponse = await client.DeleteAsync(new Uri("/api/settings/providers/Anthropic/key?expectedVersion=1", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NoContent, clearResponse.StatusCode);

        var view = await client.GetFromJsonAsync<SettingsView>("/api/settings", ResponseJsonOptions);
        Assert.False(view!.Providers.Single(provider => provider.ProviderId == "Anthropic").HasStoredKey);
    }

    [Fact]
    public async Task SetProviderProfile_ThenGetSettings_ShowsTheProfile()
    {
        using var factory = new TestAppFactory { Roles = AdministratorRoles };
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(
            new Uri("/api/settings/providers/Anthropic/profile", UriKind.Relative),
            new { baseUrl = "https://api.anthropic.com", model = "claude-sonnet-4-5", supportsNativeToolCalling = true, extraParameters = (Dictionary<string, string>?)null });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var view = await client.GetFromJsonAsync<SettingsView>("/api/settings", ResponseJsonOptions);
        var anthropic = view!.Providers.Single(provider => provider.ProviderId == "Anthropic");
        Assert.Equal("https://api.anthropic.com", anthropic.BaseUrl);
        Assert.Equal("claude-sonnet-4-5", anthropic.Model);
        Assert.True(anthropic.SupportsNativeToolCalling);
    }

    [Fact]
    public async Task SetProviderProfile_RejectsANonHttpBaseUrl()
    {
        using var factory = new TestAppFactory { Roles = AdministratorRoles };
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(
            new Uri("/api/settings/providers/Anthropic/profile", UriKind.Relative),
            new { baseUrl = "not-a-url", model = "claude-sonnet-4-5", supportsNativeToolCalling = true, extraParameters = (Dictionary<string, string>?)null });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task StaleProfileWrite_ConflictsWithoutPublishingOrReplacingTheStoredProfile()
    {
        using var factory = new TestAppFactory { Roles = AdministratorRoles };
        using var client = factory.CreateClient();
        var first = await client.PutAsJsonAsync(new Uri("/api/settings/providers/Anthropic/profile", UriKind.Relative),
            new { baseUrl = "https://api.anthropic.test", model = "sonnet-x",
                supportsNativeToolCalling = true, extraParameters = (Dictionary<string, string>?)null, expectedRevision = 0 });
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        var before = await client.GetFromJsonAsync<SettingsView>("/api/settings", ResponseJsonOptions);

        var stale = await client.PutAsJsonAsync(new Uri("/api/settings/providers/Anthropic/profile", UriKind.Relative),
            new { baseUrl = "https://api.anthropic.changed", model = "sonnet-y",
                supportsNativeToolCalling = false, extraParameters = (Dictionary<string, string>?)null, expectedRevision = 0 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var after = await client.GetFromJsonAsync<SettingsView>("/api/settings", ResponseJsonOptions);
        Assert.Equal(before!.SettingsRevision, after!.SettingsRevision);
        Assert.Equal(before.ConfigurationGeneration, after.ConfigurationGeneration);
        Assert.Equal("sonnet-x", after.Providers.Single(provider => provider.ProviderId == "Anthropic").Model);
    }

    [Fact]
    public async Task Fallbacks_SaveReadClear_PreserveOrder_AndNeverExposeSecrets()
    {
        using var factory = new TestAppFactory { Roles = AdministratorRoles };
        using var client = factory.CreateClient();
        await SetAnthropicProfile(client);
        await client.PutAsJsonAsync(new Uri("/api/settings/providers/Anthropic/key", UriKind.Relative),
            new { apiKey = "sk-ant-fallback-secret-value", expectedVersion = 0 });

        var save = await client.PutAsJsonAsync(new Uri("/api/settings/fallbacks", UriKind.Relative), new
        {
            fallbacks = new[] { new { provider = "anthropic", model = "model-b" }, new { provider = "Anthropic", model = "model-c" } },
        });
        Assert.Equal(HttpStatusCode.NoContent, save.StatusCode);
        Assert.Equal("published", Assert.Single(save.Headers.GetValues("X-bOps-Settings-Effect")));

        var raw = await client.GetStringAsync(new Uri("/api/settings", UriKind.Relative));
        Assert.DoesNotContain("sk-ant-fallback-secret-value", raw, StringComparison.Ordinal);
        var view = JsonSerializer.Deserialize<SettingsView>(raw, ResponseJsonOptions)!;
        Assert.Equal(["model-b", "model-c"], view.PersistedFallbacks.Select(f => f.Model));
        Assert.Equal(["model-b", "model-c"], view.EffectiveFallbacks.Select(f => f.Model));
        Assert.Equal([1, 2], view.EffectiveFallbacks.Select(f => f.Ordinal));
        Assert.All(view.EffectiveFallbacks, f => Assert.True(f.CredentialAvailable));
        Assert.Equal("settings", view.EffectiveFallbackSource);

        var clear = await client.DeleteAsync(new Uri($"/api/settings/fallbacks?expectedRevision={view.SettingsRevision}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NoContent, clear.StatusCode);
        var cleared = (await client.GetFromJsonAsync<SettingsView>("/api/settings", ResponseJsonOptions))!;
        Assert.Empty(cleared.PersistedFallbacks);
        Assert.Empty(cleared.EffectiveFallbacks);
        Assert.Equal("default", cleared.EffectiveFallbackSource);
        Assert.True(cleared.ConfigurationGeneration > view.ConfigurationGeneration - 1);
    }

    [Fact]
    public async Task Fallbacks_RejectMoreThanThree_Duplicates_Primary_BlankAndUnregistered_WithoutChangingState()
    {
        using var factory = new TestAppFactory { Roles = AdministratorRoles };
        using var client = factory.CreateClient();
        await SetAnthropicProfile(client);
        var before = (await client.GetFromJsonAsync<SettingsView>("/api/settings", ResponseJsonOptions))!;
        var primary = new { provider = before.ActiveProviderId, model = before.EffectiveModel };

        object[] bad =
        [
            new { fallbacks = new[] { new { provider = "Anthropic", model = "a" }, new { provider = "Anthropic", model = "b" },
                new { provider = "Anthropic", model = "c" }, new { provider = "Anthropic", model = "d" } } },
            new { fallbacks = new[] { new { provider = "Anthropic", model = "a" }, new { provider = "anthropic", model = "A" } } },
            new { fallbacks = new[] { primary } },
            new { fallbacks = new[] { new { provider = "Anthropic", model = " " } } },
            new { fallbacks = new[] { new { provider = "", model = "a" } } },
            new { fallbacks = new[] { new { provider = "not-a-provider", model = "a" } } },
        ];
        foreach (var body in bad)
        {
            var response = await client.PutAsJsonAsync(new Uri("/api/settings/fallbacks", UriKind.Relative), body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        var after = (await client.GetFromJsonAsync<SettingsView>("/api/settings", ResponseJsonOptions))!;
        Assert.Equal(before.SettingsRevision, after.SettingsRevision);
        Assert.Equal(before.ConfigurationGeneration, after.ConfigurationGeneration);
        Assert.Empty(after.PersistedFallbacks);
    }

    [Fact]
    public async Task Fallbacks_StaleWrite_ConflictsAndKeepsTheEffectiveChain()
    {
        using var factory = new TestAppFactory { Roles = AdministratorRoles };
        using var client = factory.CreateClient();
        await SetAnthropicProfile(client);
        var revision = (await client.GetFromJsonAsync<SettingsView>("/api/settings", ResponseJsonOptions))!.SettingsRevision;
        var first = await client.PutAsJsonAsync(new Uri("/api/settings/fallbacks", UriKind.Relative),
            new { fallbacks = new[] { new { provider = "Anthropic", model = "model-b" } }, expectedRevision = revision });
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        var stale = await client.PutAsJsonAsync(new Uri("/api/settings/fallbacks", UriKind.Relative),
            new { fallbacks = new[] { new { provider = "Anthropic", model = "model-x" } }, expectedRevision = revision });
        var staleDelete = await client.DeleteAsync(new Uri($"/api/settings/fallbacks?expectedRevision={revision}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, staleDelete.StatusCode);
        var view = (await client.GetFromJsonAsync<SettingsView>("/api/settings", ResponseJsonOptions))!;
        Assert.Equal(["model-b"], view.EffectiveFallbacks.Select(f => f.Model));
        Assert.Equal(["model-b"], view.PersistedFallbacks.Select(f => f.Model));
    }

    [Fact]
    public async Task Fallbacks_WithoutACredential_AreAcceptedAndReportedUnavailable()
    {
        using var factory = new TestAppFactory { Roles = AdministratorRoles };
        using var client = factory.CreateClient();
        await SetAnthropicProfile(client);

        var save = await client.PutAsJsonAsync(new Uri("/api/settings/fallbacks", UriKind.Relative),
            new { fallbacks = new[] { new { provider = "Anthropic", model = "model-b" } } });

        Assert.Equal(HttpStatusCode.NoContent, save.StatusCode);
        var view = (await client.GetFromJsonAsync<SettingsView>("/api/settings", ResponseJsonOptions))!;
        Assert.False(Assert.Single(view.EffectiveFallbacks).CredentialAvailable);
    }

    private static async Task SetAnthropicProfile(HttpClient client)
    {
        var response = await client.PutAsJsonAsync(new Uri("/api/settings/providers/Anthropic/profile", UriKind.Relative),
            new { baseUrl = "https://api.anthropic.test", model = "sonnet-x",
                supportsNativeToolCalling = true, extraParameters = (Dictionary<string, string>?)null });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task SetActiveProvider_RejectsAnUnregisteredProviderId()
    {
        using var factory = new TestAppFactory { Roles = AdministratorRoles };
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(
            new Uri("/api/settings/active-provider", UriKind.Relative), new { providerId = "not-a-real-provider" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SetActiveProvider_RejectsAProviderWithNoStoredProfile_AndIsNotTheConfiguredDefault()
    {
        using var factory = new TestAppFactory { Roles = AdministratorRoles };
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(
            new Uri("/api/settings/active-provider", UriKind.Relative), new { providerId = "Anthropic" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SetActiveProvider_SucceedsOnceAProfileAndCredentialAreStored()
    {
        using var factory = new TestAppFactory { Roles = AdministratorRoles };
        using var client = factory.CreateClient();
        await client.PutAsJsonAsync(
            new Uri("/api/settings/providers/Anthropic/profile", UriKind.Relative),
            new { baseUrl = "https://api.anthropic.com", model = "claude-sonnet-4-5", supportsNativeToolCalling = true, extraParameters = (Dictionary<string, string>?)null });
        await client.PutAsJsonAsync(
            new Uri("/api/settings/providers/Anthropic/key", UriKind.Relative),
            new { apiKey = "anthropic-test-key", expectedVersion = 0 });

        var response = await client.PutAsJsonAsync(
            new Uri("/api/settings/active-provider", UriKind.Relative), new { providerId = "Anthropic" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var view = await client.GetFromJsonAsync<SettingsView>("/api/settings", ResponseJsonOptions);
        Assert.Equal("Anthropic", view!.ActiveProviderId);
        Assert.Equal("Settings", view.ActiveProviderSource);
        Assert.True(view.Providers.Single(provider => provider.ProviderId == "Anthropic").IsActive);
    }
}
