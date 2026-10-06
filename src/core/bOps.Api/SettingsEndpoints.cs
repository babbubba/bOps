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

        group.MapGet("/", (IConfiguration configuration, SettingsStore settingsStore, VaultStore vaultStore,
            IChatModelRegistry registry, ProviderConfigurationCoordinator coordinator, ProviderCredentialResolver credentials) =>
            Results.Ok(BuildView(configuration, settingsStore, vaultStore, registry, coordinator, credentials)));

        group.MapPut("/active-provider", async (
            SetActiveProviderRequest request, ProviderConfigurationCoordinator coordinator,
            IAuditSink audit, TimeProvider timeProvider, ClaimsPrincipal principal, HttpContext http) =>
        {
            if (string.IsNullOrWhiteSpace(request.ProviderId))
            {
                return Results.BadRequest(new { message = "'providerId' is required." });
            }

            bool published;
            try
            {
                published = coordinator.SelectProvider(request.ProviderId, request.ExpectedRevision);
            }
            catch (SettingsConcurrencyException ex)
            {
                return Results.Conflict(new { message = ex.Message, currentRevision = ex.ActualRevision });
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ProviderNotSupportedException)
            {
                await Audit(audit, timeProvider, principal, "provider.active", SettingsChangeOperation.SelectProvider,
                    request.ProviderId, SettingsChangeOutcome.Failure);
                return Results.BadRequest(new { message = ex.Message });
            }
            var effect = published ? "published" : coordinator.Current.Pin.ProviderSource == "environment"
                ? "persisted-but-shadowed" : "unchanged";
            MutationHeaders(http, coordinator, effect);
            await Audit(audit, timeProvider, principal, "provider.active", SettingsChangeOperation.SelectProvider,
                request.ProviderId, SettingsChangeOutcome.Success, coordinator.Current.Pin.Generation, effect);
            return Results.NoContent();
        });

        group.MapPut("/fallbacks", async (
            SetFallbacksRequest request, ProviderConfigurationCoordinator coordinator, SettingsStore settingsStore,
            IAuditSink audit, TimeProvider timeProvider, ClaimsPrincipal principal, HttpContext http) =>
        {
            if (request.Fallbacks is null || request.Fallbacks.Any(entry => entry is null))
            {
                return Results.BadRequest(new { message = "'fallbacks' is required and may not contain null entries." });
            }

            var operation = request.Fallbacks.Count == 0 ? SettingsChangeOperation.Clear
                : settingsStore.Fallbacks.Count == 0 ? SettingsChangeOperation.Set : SettingsChangeOperation.Replace;
            return await ApplyFallbacks(request.Fallbacks.Select(entry => new FallbackSetting(entry.Provider, entry.Model)).ToList(),
                request.ExpectedRevision, operation, coordinator, audit, timeProvider, principal, http);
        });

        group.MapDelete("/fallbacks", async (
            int? expectedRevision, ProviderConfigurationCoordinator coordinator,
            IAuditSink audit, TimeProvider timeProvider, ClaimsPrincipal principal, HttpContext http) =>
            await ApplyFallbacks([], expectedRevision, SettingsChangeOperation.Clear, coordinator, audit, timeProvider, principal, http));

        group.MapPut("/providers/{providerId}/profile", async (
            string providerId, SetProviderProfileRequest request, ProviderConfigurationCoordinator coordinator,
            IAuditSink audit, TimeProvider timeProvider, ClaimsPrincipal principal, HttpContext http) =>
        {
            try
            {
                var published = coordinator.SetProfile(providerId, request);
                var effective = coordinator.Current.Pin;
                var effect = published ? "published"
                    : !effective.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase)
                        ? effective.ProviderSource == "environment" ? "persisted-but-shadowed" : "persisted-inactive"
                        : effective.BaseUrlSource == "environment" || effective.ModelSource == "environment" ||
                          effective.SupportsNativeToolCallingSource == "environment"
                            ? "persisted-but-shadowed" : "unchanged";
                MutationHeaders(http, coordinator, effect);
                await Audit(audit, timeProvider, principal, "provider.profile", SettingsChangeOperation.Set,
                    providerId, SettingsChangeOutcome.Success, effective.Generation, effect);
            }
            catch (SettingsConcurrencyException ex)
            {
                return Results.Conflict(new { message = ex.Message, currentRevision = ex.ActualRevision });
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ProviderNotSupportedException)
            {
                await Audit(audit, timeProvider, principal, "provider.profile", SettingsChangeOperation.Set,
                    providerId, SettingsChangeOutcome.Failure);
                return Results.BadRequest(new { message = ex.Message });
            }

            return Results.NoContent();
        });

        group.MapPut("/providers/{providerId}/key", async (
            string providerId, SetProviderKeyRequest request, VaultStore vaultStore,
            ProviderConfigurationCoordinator coordinator, IAuditSink audit, TimeProvider timeProvider,
            ClaimsPrincipal principal, HttpContext http) =>
        {
            if (string.IsNullOrEmpty(request.ApiKey))
            {
                return Results.BadRequest(new { message = "'apiKey' is required." });
            }

            var operation = vaultStore.Find(providerId) is null ? SettingsChangeOperation.Set : SettingsChangeOperation.Replace;
            try
            {
                coordinator.SetKey(providerId, request.ApiKey, request.ExpectedVersion);
            }
            catch (VaultConcurrencyException ex)
            {
                await Audit(audit, timeProvider, principal, "provider.apiKey", operation, providerId, SettingsChangeOutcome.Failure);
                return Results.Conflict(new { message = ex.Message, currentVersion = ex.ActualVersion });
            }
            catch (ProviderNotSupportedException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }

            MutationHeaders(http, coordinator, "credential-updated");

            await Audit(audit, timeProvider, principal, "provider.apiKey", operation, providerId,
                SettingsChangeOutcome.Success, coordinator.Current.Pin.Generation, "credential-updated");
            return Results.NoContent();
        });

        group.MapDelete("/providers/{providerId}/key", async (
            string providerId, int expectedVersion, ProviderConfigurationCoordinator coordinator,
            IAuditSink audit, TimeProvider timeProvider, ClaimsPrincipal principal, HttpContext http) =>
        {
            try
            {
                coordinator.RemoveKey(providerId, expectedVersion);
            }
            catch (VaultConcurrencyException ex)
            {
                await Audit(audit, timeProvider, principal, "provider.apiKey", SettingsChangeOperation.Clear,
                    providerId, SettingsChangeOutcome.Failure);
                return Results.Conflict(new { message = ex.Message, currentVersion = ex.ActualVersion });
            }
            catch (ProviderNotSupportedException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }

            MutationHeaders(http, coordinator, "credential-updated");

            await Audit(audit, timeProvider, principal, "provider.apiKey", SettingsChangeOperation.Clear,
                providerId, SettingsChangeOutcome.Success, coordinator.Current.Pin.Generation, "credential-updated");
            return Results.NoContent();
        });
    }

    private static async Task<IResult> ApplyFallbacks(
        List<FallbackSetting> fallbacks, int? expectedRevision, SettingsChangeOperation operation,
        ProviderConfigurationCoordinator coordinator, IAuditSink audit, TimeProvider timeProvider,
        ClaimsPrincipal principal, HttpContext http)
    {
        var subject = string.Join(',', fallbacks.Select(entry => $"{entry.Provider}/{entry.Model}"));
        try
        {
            var published = coordinator.SetFallbacks(fallbacks, expectedRevision);
            var effect = published ? "published"
                : coordinator.Current.FallbackSource == "configuration" ? "persisted-but-shadowed" : "unchanged";
            MutationHeaders(http, coordinator, effect);
            await Audit(audit, timeProvider, principal, "provider.fallbacks", operation, subject,
                SettingsChangeOutcome.Success, coordinator.Current.Pin.Generation, effect);
            return Results.NoContent();
        }
        catch (SettingsConcurrencyException ex)
        {
            await Audit(audit, timeProvider, principal, "provider.fallbacks", operation, subject, SettingsChangeOutcome.Failure);
            return Results.Conflict(new { message = ex.Message, currentRevision = ex.ActualRevision });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ProviderNotSupportedException)
        {
            await Audit(audit, timeProvider, principal, "provider.fallbacks", operation, subject, SettingsChangeOutcome.Failure);
            return Results.BadRequest(new { message = ex.Message });
        }
    }

    private static SettingsView BuildView(
        IConfiguration configuration, SettingsStore settingsStore, VaultStore vaultStore, IChatModelRegistry registry,
        ProviderConfigurationCoordinator coordinator, ProviderCredentialResolver credentials)
        => coordinator.ReadConsistent(() => BuildConsistentView(
            configuration, settingsStore, vaultStore, registry, coordinator, credentials));

    private static SettingsView BuildConsistentView(
        IConfiguration configuration, SettingsStore settingsStore, VaultStore vaultStore, IChatModelRegistry registry,
        ProviderConfigurationCoordinator coordinator, ProviderCredentialResolver credentials)
    {
        var published = coordinator.Current;
        var effective = published.Pin;
        var activeProviderId = effective.ProviderId;
        var (_, source) = ProviderResolution.ResolveActiveProvider(configuration, settingsStore);
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
            var credential = credentials.Availability(id);
            var shadowedSelection = string.Equals(id, settingsStore.ActiveProviderId, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(id, activeProviderId, StringComparison.OrdinalIgnoreCase);
            var shadowedFields = string.Equals(id, activeProviderId, StringComparison.OrdinalIgnoreCase)
                ? new[] {
                    profile?.BaseUrl is not null && effective.BaseUrlSource == "environment" ? "baseUrl" : null,
                    profile?.Model is not null && effective.ModelSource == "environment" ? "model" : null,
                    profile?.SupportsNativeToolCalling is not null && effective.SupportsNativeToolCallingSource == "environment"
                        ? "supportsNativeToolCalling" : null,
                }.Where(field => field is not null).Select(field => field!).ToArray()
                : [];
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
                profile?.UpdatedUtc)
            {
                HasUsableCredential = credential.Available,
                CredentialSource = credential.Source,
                IsPersistedSelectionShadowed = shadowedSelection,
                ShadowedFields = shadowedFields,
            };
        }).ToList();

        return new SettingsView(vaultStore.Version, activeProviderId, source.ToString(), providers)
        {
            SettingsRevision = settingsStore.Revision,
            ConfigurationGeneration = effective.Generation,
            PersistedActiveProviderId = settingsStore.ActiveProviderId,
            PersistedActiveProviderShadowed = settingsStore.ActiveProviderId is { } stored &&
                !string.Equals(stored, activeProviderId, StringComparison.OrdinalIgnoreCase),
            EffectiveBaseUrl = effective.BaseUrl,
            EffectiveModel = effective.Model,
            EffectiveSupportsNativeToolCalling = effective.SupportsNativeToolCalling,
            EffectiveRequestTimeout = effective.RequestTimeout,
            EffectiveBaseUrlSource = effective.BaseUrlSource,
            EffectiveModelSource = effective.ModelSource,
            EffectiveToolCallingSource = effective.SupportsNativeToolCallingSource,
            EffectiveRequestTimeoutSource = effective.RequestTimeoutSource,
            EffectiveCredentialAvailable = published.PrimaryCredentialAvailable,
            EffectiveCredentialSource = published.PrimaryCredentialSource,
            PersistedFallbacks = settingsStore.Fallbacks.Select(entry => new FallbackEntryView(entry.Provider, entry.Model)).ToList(),
            EffectiveFallbackSource = published.FallbackSource,
            PersistedFallbacksShadowed = published.FallbackSource == "configuration" && settingsStore.Fallbacks.Count > 0,
            EffectiveFallbacks = effective.Fallbacks.Select((candidate, index) => new EffectiveFallbackView(
                index + 1, candidate.ProviderId, candidate.Model, candidate.BaseUrl, candidate.SupportsNativeToolCalling,
                index < published.FallbackCredentialAvailable.Count && published.FallbackCredentialAvailable[index])).ToList(),
        };
    }

    private static void MutationHeaders(HttpContext http, ProviderConfigurationCoordinator coordinator, string effect)
    {
        http.Response.Headers["X-bOps-Settings-Effect"] = effect;
        http.Response.Headers["X-bOps-Configuration-Generation"] = coordinator.Current.Pin.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static Task Audit(
        IAuditSink audit, TimeProvider timeProvider, ClaimsPrincipal principal, string settingName,
        SettingsChangeOperation operation, string providerId, SettingsChangeOutcome outcome,
        long? generation = null, string? effect = null) =>
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
            ConfigurationGeneration = generation,
            PublicationEffect = effect,
        });
}
