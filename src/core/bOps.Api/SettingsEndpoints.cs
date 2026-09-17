// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Security.Claims;
using bOps.Abstractions;
using bOps.Runtime;

namespace bOps.Api;

/// <summary>
/// Maps <c>/api/settings</c> — administrator-only provider configuration (ADR-0029): the active
/// provider, each provider's non-secret profile, and each provider's API key. No endpoint here
/// ever returns a stored key's plaintext; the vault's <c>expectedVersion</c> concurrency token
/// guards every key write against a lost update.
/// </summary>
internal static class SettingsEndpoints
{
    internal static void MapSettingsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/settings").RequireAuthorization(ApiAuthorization.AdministratorPolicy);

        group.MapGet("/", (IConfiguration configuration, SettingsStore settingsStore, VaultStore vaultStore, IChatModelRegistry registry) =>
            Results.Ok(BuildView(configuration, settingsStore, vaultStore, registry)));

        group.MapPut("/active-provider", async (
            SetActiveProviderRequest request, IConfiguration configuration, SettingsStore settingsStore,
            IChatModelRegistry registry, IAuditSink audit, TimeProvider timeProvider, ClaimsPrincipal principal) =>
        {
            if (string.IsNullOrWhiteSpace(request.ProviderId))
            {
                return Results.BadRequest(new { message = "'providerId' is required." });
            }

            if (!registry.RegisteredProviderIds.Contains(request.ProviderId, StringComparer.Ordinal))
            {
                return Results.BadRequest(new { message = $"'{request.ProviderId}' is not a registered provider." });
            }

            var hasProfile = settingsStore.FindProviderProfile(request.ProviderId) is not null;
            var isConfiguredDefault = string.Equals(request.ProviderId, configuration["ModelProvider:Provider"], StringComparison.Ordinal);
            if (!hasProfile && !isConfiguredDefault)
            {
                await Audit(audit, timeProvider, principal, "provider.active", SettingsChangeOperation.SelectProvider,
                    request.ProviderId, SettingsChangeOutcome.Failure);
                return Results.BadRequest(new
                {
                    message = $"'{request.ProviderId}' has no stored profile yet; set its endpoint and model before making it active.",
                });
            }

            settingsStore.SetActiveProviderId(request.ProviderId);
            await Audit(audit, timeProvider, principal, "provider.active", SettingsChangeOperation.SelectProvider,
                request.ProviderId, SettingsChangeOutcome.Success);
            return Results.NoContent();
        });

        group.MapPut("/providers/{providerId}/profile", async (
            string providerId, SetProviderProfileRequest request, SettingsStore settingsStore,
            IAuditSink audit, TimeProvider timeProvider, ClaimsPrincipal principal) =>
        {
            try
            {
                settingsStore.SetProviderProfile(
                    providerId, request.BaseUrl, request.Model, request.SupportsNativeToolCalling, request.ExtraParameters);
            }
            catch (ArgumentException ex)
            {
                await Audit(audit, timeProvider, principal, "provider.profile", SettingsChangeOperation.Set,
                    providerId, SettingsChangeOutcome.Failure);
                return Results.BadRequest(new { message = ex.Message });
            }

            await Audit(audit, timeProvider, principal, "provider.profile", SettingsChangeOperation.Set,
                providerId, SettingsChangeOutcome.Success);
            return Results.NoContent();
        });

        group.MapPut("/providers/{providerId}/key", async (
            string providerId, SetProviderKeyRequest request, VaultStore vaultStore,
            IAuditSink audit, TimeProvider timeProvider, ClaimsPrincipal principal) =>
        {
            if (string.IsNullOrEmpty(request.ApiKey))
            {
                return Results.BadRequest(new { message = "'apiKey' is required." });
            }

            var operation = vaultStore.Find(providerId) is null ? SettingsChangeOperation.Set : SettingsChangeOperation.Replace;
            try
            {
                vaultStore.Set(providerId, request.ApiKey, request.ExpectedVersion);
            }
            catch (VaultConcurrencyException ex)
            {
                await Audit(audit, timeProvider, principal, "provider.apiKey", operation, providerId, SettingsChangeOutcome.Failure);
                return Results.Conflict(new { message = ex.Message, currentVersion = ex.ActualVersion });
            }

            await Audit(audit, timeProvider, principal, "provider.apiKey", operation, providerId, SettingsChangeOutcome.Success);
            return Results.NoContent();
        });

        group.MapDelete("/providers/{providerId}/key", async (
            string providerId, int expectedVersion, VaultStore vaultStore,
            IAuditSink audit, TimeProvider timeProvider, ClaimsPrincipal principal) =>
        {
            try
            {
                vaultStore.Clear(providerId, expectedVersion);
            }
            catch (VaultConcurrencyException ex)
            {
                await Audit(audit, timeProvider, principal, "provider.apiKey", SettingsChangeOperation.Clear,
                    providerId, SettingsChangeOutcome.Failure);
                return Results.Conflict(new { message = ex.Message, currentVersion = ex.ActualVersion });
            }

            await Audit(audit, timeProvider, principal, "provider.apiKey", SettingsChangeOperation.Clear,
                providerId, SettingsChangeOutcome.Success);
            return Results.NoContent();
        });
    }

    private static SettingsView BuildView(
        IConfiguration configuration, SettingsStore settingsStore, VaultStore vaultStore, IChatModelRegistry registry)
    {
        var (activeProviderId, source) = ProviderResolution.ResolveActiveProvider(configuration, settingsStore);
        var vaultEntries = vaultStore.List().ToDictionary(entry => entry.ProviderId, StringComparer.Ordinal);
        var profiles = settingsStore.ListProviderProfiles().ToDictionary(profile => profile.ProviderId, StringComparer.Ordinal);

        var ids = registry.RegisteredProviderIds
            .Concat(vaultEntries.Keys)
            .Concat(profiles.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal);

        var providers = ids.Select(id =>
        {
            vaultEntries.TryGetValue(id, out var vaultEntry);
            profiles.TryGetValue(id, out var profile);
            return new SettingsProviderView(
                id,
                string.Equals(id, activeProviderId, StringComparison.Ordinal),
                vaultEntry is not null,
                vaultEntry?.MaskPrefix,
                vaultEntry?.MaskSuffix,
                vaultEntry?.PlaintextLength,
                vaultEntry?.UpdatedUtc,
                profile?.BaseUrl,
                profile?.Model,
                profile?.SupportsNativeToolCalling,
                profile?.ExtraParameters,
                profile?.UpdatedUtc);
        }).ToList();

        return new SettingsView(vaultStore.Version, activeProviderId, source.ToString(), providers);
    }

    private static Task Audit(
        IAuditSink audit, TimeProvider timeProvider, ClaimsPrincipal principal, string settingName,
        SettingsChangeOperation operation, string providerId, SettingsChangeOutcome outcome) =>
        audit.WriteAsync(new SettingsChangedAuditEvent
        {
            TimestampUtc = timeProvider.GetUtcNow(),
            Node = NodeId.Local,
            TaskId = Guid.Empty,
            StepIndex = -1,
            Actor = AgentsEndpoints.ApiActor(principal),
            SettingName = settingName,
            Operation = operation,
            ProviderId = providerId,
            Outcome = outcome,
        });
}
