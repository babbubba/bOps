// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;

namespace bOps.Api.Tests;

[CollectionDefinition("ProviderEnvironment", DisableParallelization = true)]
#pragma warning disable CA1515 // xUnit requires public collection definitions.
public sealed class ProviderEnvironmentGroup;
#pragma warning restore CA1515

[Collection("ProviderEnvironment")]
public sealed class ProviderEnvironmentSettingsTests
{
    [Fact]
    public async Task EnvironmentPrimary_WinsWhileSettingsSelectionPersistsAndIsReportedShadowed()
    {
        var prior = Environment.GetEnvironmentVariable("ModelProvider__Provider");
        Environment.SetEnvironmentVariable("ModelProvider__Provider", "OpenRouter");
        try
        {
            using var factory = new TestAppFactory { Roles = ["viewer", "operator", "approver", "administrator"] };
            using var client = factory.CreateClient();
            await client.PutAsJsonAsync("/api/settings/providers/Anthropic/profile",
                new { baseUrl = "https://api.anthropic.test", model = "sonnet-x",
                    supportsNativeToolCalling = true, extraParameters = (Dictionary<string, string>?)null });
            await client.PutAsJsonAsync("/api/settings/providers/Anthropic/key",
                new { apiKey = "ANTHROPIC_KEY", expectedVersion = 0 });
            var selected = await client.PutAsJsonAsync("/api/settings/active-provider",
                new { providerId = "Anthropic" });
            Assert.Equal(HttpStatusCode.NoContent, selected.StatusCode);
            Assert.Equal("persisted-but-shadowed", selected.Headers.GetValues("X-bOps-Settings-Effect").Single());

            var view = await client.GetFromJsonAsync<SettingsView>("/api/settings");
            Assert.Equal("Anthropic", view!.PersistedActiveProviderId);
            Assert.Equal("OpenRouter", view.ActiveProviderId);
            Assert.Equal("EnvironmentOverride", view.ActiveProviderSource);
            Assert.True(view.PersistedActiveProviderShadowed);
            Assert.True(view.Providers.Single(provider => provider.ProviderId == "Anthropic").IsPersistedSelectionShadowed);
            Assert.Equal("vault", view.Providers.Single(provider => provider.ProviderId == "Anthropic").CredentialSource);
            Assert.Equal(1, view.ConfigurationGeneration);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ModelProvider__Provider", prior);
        }
    }
}
