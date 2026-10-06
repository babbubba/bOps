// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Api;

/// <summary>Maps <c>/api/providers</c> — which LLM providers are registered and which one is active (ADR-0019).</summary>
internal static class ProvidersEndpoints
{
    internal static void MapProvidersEndpoints(this WebApplication app)
    {
        app.MapGet("/api/providers", (IChatModelRegistry registry, ProviderConfigurationCoordinator coordinator) =>
        {
            var effective = coordinator.Current;
            var active = new ActiveProviderInfo(effective.Pin.ProviderId, effective.Pin.Model,
                effective.Pin.BaseUrl, effective.PrimaryCredentialSource != "none");

            return Results.Ok(new ProvidersResponse(registry.RegisteredProviderIds, active));
        }).RequireAuthorization(ApiAuthorization.ViewerPolicy);
    }
}
