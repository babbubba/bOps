// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using bOps.Abstractions;
using bOps.Runtime;

namespace bOps.Api;

/// <summary>One immutable non-secret generation, published for new executions only (ADR-0045).</summary>
internal sealed record EffectiveProviderConfiguration(
    PinnedProviderConfiguration Pin, bool PrimaryCredentialAvailable, string PrimaryCredentialSource);

/// <summary>Serializes API provider writes and publishes one complete effective tuple after persistence.</summary>
internal sealed class ProviderConfigurationCoordinator(
    IConfiguration configuration, SettingsStore settings, IChatModelRegistry registry,
    ISecretProvider environmentSecrets, VaultStore? vault = null)
{
    private readonly object _gate = new();
    private EffectiveProviderConfiguration? _current;

    internal EffectiveProviderConfiguration Current
    {
        get
        {
            var published = Volatile.Read(ref _current);
            if (published is not null) return published;
            lock (_gate)
            {
                return _current ?? Publish(Build());
            }
        }
    }

    internal int SettingsRevision => settings.Revision;

    internal T ReadConsistent<T>(Func<T> read)
    {
        lock (_gate) return read();
    }

    internal void EnsureAdmission(PinnedProviderConfiguration pin)
    {
        ValidatePin(pin);
        RequireCredential(pin.ProviderId);
    }

    internal bool SelectProvider(string providerId, int? expectedRevision = null)
    {
        lock (_gate)
        {
            var previousHash = Current.Pin.SnapshotHash;
            var canonical = Canonical(providerId);
            var proposed = Build(activeOverride: canonical);
            // A shadowed selection may have no profile yet. An effective selection must be usable.
            if (proposed.Pin.ProviderId.Equals(canonical, StringComparison.OrdinalIgnoreCase))
                RequireCredential(canonical);
            settings.SetActiveProviderId(canonical, expectedRevision);
            return Publish(proposed).Pin.SnapshotHash != previousHash;
        }
    }

    internal bool SetProfile(string providerId, SetProviderProfileRequest request)
    {
        lock (_gate)
        {
            var previousHash = Current.Pin.SnapshotHash;
            var canonical = Canonical(providerId);
            var profile = new ProviderProfile(canonical, request.BaseUrl, request.Model,
                request.SupportsNativeToolCalling, request.ExtraParameters ?? [], DateTimeOffset.UtcNow);
            ValidateShape(profile.BaseUrl, profile.Model);
            registry.Create(new ChatModelOptions(canonical, profile.BaseUrl, null, profile.Model,
                profile.SupportsNativeToolCalling));
            var proposed = Build(profileOverride: profile);
            settings.SetProviderProfile(canonical, request.BaseUrl, request.Model,
                request.SupportsNativeToolCalling, request.ExtraParameters, request.ExpectedRevision);
            return Publish(proposed).Pin.SnapshotHash != previousHash;
        }
    }

    internal void SetKey(string providerId, string key, int expectedVersion)
    {
        lock (_gate)
        {
            (vault ?? throw new InvalidOperationException("The provider vault is not configured."))
                .Set(Canonical(providerId), key, expectedVersion);
            // Credential-only writes do not alter the non-secret generation or snapshot hash.
            RefreshCredentialMetadata();
        }
    }

    internal void RemoveKey(string providerId, int expectedVersion)
    {
        lock (_gate)
        {
            (vault ?? throw new InvalidOperationException("The provider vault is not configured."))
                .Clear(Canonical(providerId), expectedVersion);
            RefreshCredentialMetadata();
        }
    }

    internal string Canonical(string providerId) =>
        registry.RegisteredProviderIds.FirstOrDefault(id => id.Equals(providerId, StringComparison.OrdinalIgnoreCase))
        ?? throw new ProviderNotSupportedException(providerId);

    internal void ValidatePin(PinnedProviderConfiguration pin)
    {
        if (pin.SchemaVersion != 1 || pin.Generation < 1 ||
            !string.Equals(Canonical(pin.ProviderId), pin.ProviderId, StringComparison.Ordinal) ||
            !string.Equals(Hash(pin), pin.SnapshotHash, StringComparison.Ordinal))
            throw new InvalidOperationException("The stored provider configuration pin is invalid.");
        ValidateShape(pin.BaseUrl, pin.Model);
        if (pin.RequestTimeout is { } timeout && timeout <= TimeSpan.Zero)
            throw new InvalidOperationException("The stored provider request timeout is invalid.");
        registry.Create(new ChatModelOptions(pin.ProviderId, pin.BaseUrl, null, pin.Model,
            pin.SupportsNativeToolCalling) { RequestTimeout = pin.RequestTimeout });
    }

    private EffectiveProviderConfiguration Publish(EffectiveProviderConfiguration candidate)
    {
        var prior = _current;
        var generation = prior is null ? 1 :
            prior.Pin.SnapshotHash == candidate.Pin.SnapshotHash ? prior.Pin.Generation : prior.Pin.Generation + 1;
        var published = candidate with { Pin = candidate.Pin with { Generation = generation } };
        Volatile.Write(ref _current, published);
        return published;
    }

    private EffectiveProviderConfiguration Build(string? activeOverride = null, ProviderProfile? profileOverride = null)
    {
        var configured = configuration.GetSection("ModelProvider").Get<ChatModelOptions>()
            ?? throw new InvalidOperationException("Missing 'ModelProvider' configuration section.");
        var environmentOwned = HasEnvironmentField("Provider");
        var storedId = activeOverride ?? settings.ActiveProviderId;
        var selected = environmentOwned ? configured.Provider : storedId ?? configured.Provider;
        var id = Canonical(selected);
        var profile = environmentOwned ? null :
            profileOverride is not null && profileOverride.ProviderId.Equals(id, StringComparison.OrdinalIgnoreCase)
                ? profileOverride : settings.FindProviderProfile(id);
        if (!environmentOwned && !id.Equals(configured.Provider, StringComparison.OrdinalIgnoreCase) && profile is null)
            throw new ArgumentException($"'{id}' has no stored profile yet; set its endpoint and model before making it active.");

        var baseUrl = Field("BaseUrl", profile?.BaseUrl, configured.BaseUrl, environmentOwned);
        var model = Field("Model", profile?.Model, configured.Model, environmentOwned);
        var native = BoolField("SupportsNativeToolCalling", profile?.SupportsNativeToolCalling,
            configured.SupportsNativeToolCalling, environmentOwned);
        var timeout = configured.RequestTimeout;
        ValidateShape(baseUrl.Value, model.Value);
        if (timeout is { } value && value <= TimeSpan.Zero)
            throw new ArgumentException("The provider request timeout must be positive.");
        var pin = new PinnedProviderConfiguration(1, 0, id, baseUrl.Value, model.Value, native.Value,
            timeout, environmentOwned ? "environment" : storedId is null ? "default" : "settings",
            baseUrl.Source, model.Source, native.Source,
            HasEnvironmentField("RequestTimeout") ? "environment" : "default", string.Empty);
        pin = pin with { SnapshotHash = Hash(pin) };
        // Local validation: a package must be able to construct the adapter without a network call.
        (configuration.GetSection("Agent").Get<AgentRunnerOptions>() ?? new AgentRunnerOptions())
            .Validate(pin.RequestTimeout ?? ChatModelOptions.DefaultRequestTimeout);
        registry.Create(new ChatModelOptions(id, pin.BaseUrl, null, pin.Model, pin.SupportsNativeToolCalling)
            { RequestTimeout = pin.RequestTimeout });
        var credential = new ProviderCredentialResolver(configuration, environmentSecrets, vault).Availability(id);
        return new EffectiveProviderConfiguration(pin, credential.Available, credential.Source);
    }

    private void RefreshCredentialMetadata()
    {
        var current = _current ?? Publish(Build());
        var credential = new ProviderCredentialResolver(configuration, environmentSecrets, vault)
            .Availability(current.Pin.ProviderId);
        Volatile.Write(ref _current, current with
        {
            PrimaryCredentialAvailable = credential.Available,
            PrimaryCredentialSource = credential.Source,
        });
    }

    private static (T Value, string Source) Field<T>(string name, T? profile, T configured, bool environmentOwned)
    {
        if (environmentOwned || HasEnvironmentField(name)) return (configured, "environment");
        if (profile is not null) return (profile, "settings");
        return (configured, "default");
    }

    private static (bool Value, string Source) BoolField(string name, bool? profile, bool configured, bool environmentOwned) =>
        environmentOwned || HasEnvironmentField(name) ? (configured, "environment") :
        profile is { } value ? (value, "settings") : (configured, "default");

    private static bool HasEnvironmentField(string field) =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable($"ModelProvider__{field}"));

    private static void ValidateShape(string baseUrl, string model)
    {
        if (string.IsNullOrWhiteSpace(model) || model.Length > 256)
            throw new ArgumentException("The provider model must be nonblank and at most 256 characters.");
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("The provider BaseUrl must be an absolute HTTP/HTTPS URI.");
    }

    private void RequireCredential(string providerId)
    {
        if (ProviderCredentialResolver.RequiresCredential(providerId) &&
            string.IsNullOrEmpty(new ProviderCredentialResolver(configuration, environmentSecrets, vault).Resolve(providerId)))
            throw new ArgumentException($"'{providerId}' has no usable current credential.");
    }

    private static string Hash(PinnedProviderConfiguration pin)
    {
        var content = string.Join('\u001f', pin.SchemaVersion, pin.ProviderId, pin.BaseUrl, pin.Model,
            pin.SupportsNativeToolCalling, pin.RequestTimeout?.Ticks, pin.ProviderSource, pin.BaseUrlSource,
            pin.ModelSource, pin.SupportsNativeToolCallingSource, pin.RequestTimeoutSource);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }
}
