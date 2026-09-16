// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http.Json;

namespace bOps.Api.Tests;

/// <summary>Drives <c>GET /api/providers</c> (ADR-0019) against the real composition root.</summary>
public sealed class ProvidersEndpointsTests
{
    [Fact]
    public async Task GetProviders_ListsEveryRegisteredProviderPackage()
    {
        using var factory = new TestAppFactory { ChatModel = new QueueChatModel() };
        using var client = factory.CreateClient();

        var providers = await client.GetFromJsonAsync<ProvidersResponse>("/api/providers");

        Assert.NotNull(providers);
        Assert.Contains("OpenRouter", providers!.RegisteredProviderIds);
        Assert.Contains("Anthropic", providers.RegisteredProviderIds);
    }

    [Fact]
    public async Task GetProviders_ReportsTheConfiguredActiveProvider_WithoutTheApiKeyValue()
    {
        // TestAppFactory preserves the shipped provider/model but replaces the secret reference
        // with a unique unset environment variable so developer credentials cannot affect this test.
        using var factory = new TestAppFactory { ChatModel = new QueueChatModel() };
        using var client = factory.CreateClient();

        var providers = await client.GetFromJsonAsync<ProvidersResponse>("/api/providers");

        Assert.NotNull(providers!.Active);
        Assert.Equal("OpenRouter", providers.Active!.Provider);
        Assert.Equal("openrouter/free", providers.Active.Model);
        Assert.False(providers.Active.HasApiKey);
    }
}
