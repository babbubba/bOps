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
    public async Task SetActiveProvider_SucceedsOnceAProfileIsStored()
    {
        using var factory = new TestAppFactory { Roles = AdministratorRoles };
        using var client = factory.CreateClient();
        await client.PutAsJsonAsync(
            new Uri("/api/settings/providers/Anthropic/profile", UriKind.Relative),
            new { baseUrl = "https://api.anthropic.com", model = "claude-sonnet-4-5", supportsNativeToolCalling = true, extraParameters = (Dictionary<string, string>?)null });

        var response = await client.PutAsJsonAsync(
            new Uri("/api/settings/active-provider", UriKind.Relative), new { providerId = "Anthropic" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var view = await client.GetFromJsonAsync<SettingsView>("/api/settings", ResponseJsonOptions);
        Assert.Equal("Anthropic", view!.ActiveProviderId);
        Assert.Equal("Settings", view.ActiveProviderSource);
        Assert.True(view.Providers.Single(provider => provider.ProviderId == "Anthropic").IsActive);
    }
}
