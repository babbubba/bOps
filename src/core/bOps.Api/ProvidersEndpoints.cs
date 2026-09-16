// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Api;

/// <summary>Maps <c>/api/providers</c> — which LLM providers are registered and which one is active (ADR-0019).</summary>
internal static class ProvidersEndpoints
{
    internal static void MapProvidersEndpoints(this WebApplication app)
    {
        app.MapGet("/api/providers", (IChatModelRegistry registry, IConfiguration configuration, ISecretProvider secretProvider) =>
        {
            var options = configuration.GetSection("ModelProvider").Get<ChatModelOptions>();
            var active = options is null
                ? null
                : new ActiveProviderInfo(options.Provider, options.Model, options.BaseUrl,
                    options.ApiKeySecret is not null && !string.IsNullOrEmpty(secretProvider.GetSecret(options.ApiKeySecret)));

            return Results.Ok(new ProvidersResponse(registry.RegisteredProviderIds, active));
        }).RequireAuthorization(ApiAuthorization.ViewerPolicy);
    }
}
