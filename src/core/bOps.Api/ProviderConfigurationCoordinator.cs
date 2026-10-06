// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using bOps.Abstractions;
using bOps.Runtime;

namespace bOps.Api;

/// <summary>One immutable non-secret generation, published for new executions only (ADR-0045).</summary>
internal sealed record EffectiveProviderConfiguration(
    PinnedProviderConfiguration Pin, bool PrimaryCredentialAvailable, string PrimaryCredentialSource)
{
    /// <summary>Who owns the effective fallback list: <c>configuration</c>, <c>settings</c> or <c>default</c> (empty).</summary>
    public string FallbackSource { get; init; } = "default";

    /// <summary>Safe per-candidate readiness, parallel to <see cref="PinnedProviderConfiguration.Fallbacks"/>. Informational only.</summary>
    public IReadOnlyList<bool> FallbackCredentialAvailable { get; init; } = [];
}

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

    /// <summary>Replaces (or, when empty, clears) the persisted ordered fallback list and publishes the new tuple.</summary>
    internal bool SetFallbacks(IReadOnlyList<FallbackSetting> requested, int? expectedRevision = null)
    {
        ArgumentNullException.ThrowIfNull(requested);
        lock (_gate)
        {
            var previousHash = Current.Pin.SnapshotHash;
            var canonical = requested
                .Select(entry => entry is null ? throw new ArgumentException("A fallback entry is required.")
                    : new FallbackSetting(Canonical(entry.Provider ?? string.Empty), entry.Model ?? string.Empty))
                .ToList();
            // Build validates count, shape, duplicates and the primary tuple before anything is persisted.
            var proposed = Build(fallbackOverride: canonical);
            settings.SetFallbacks(canonical, expectedRevision);
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
            !string.Equals(Hash(pin), pin.SnapshotHash, StringComparison.Ordinal) ||
            pin.Fallbacks.Count > 3 || pin.FallbackOrdinal < 0 || pin.FallbackOrdinal > pin.Fallbacks.Count)
            throw new InvalidOperationException("The stored provider configuration pin is invalid.");
        ValidateShape(pin.BaseUrl, pin.Model);
        if (pin.RequestTimeout is { } timeout && timeout <= TimeSpan.Zero)
            throw new InvalidOperationException("The stored provider request timeout is invalid.");
        registry.Create(new ChatModelOptions(pin.ProviderId, pin.BaseUrl, null, pin.Model,
            pin.SupportsNativeToolCalling) { RequestTimeout = pin.RequestTimeout });
        var tuples = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { $"{pin.ProviderId}\u001f{pin.Model}" };
        foreach (var fallback in pin.Fallbacks)
        {
            if (!string.Equals(Canonical(fallback.ProviderId), fallback.ProviderId, StringComparison.Ordinal) ||
                !tuples.Add($"{fallback.ProviderId}\u001f{fallback.Model}"))
                throw new InvalidOperationException("The stored provider fallback chain is invalid.");
            ValidateShape(fallback.BaseUrl, fallback.Model);
            if (fallback.RequestTimeout is { } fallbackTimeout && fallbackTimeout <= TimeSpan.Zero)
                throw new InvalidOperationException("The stored provider fallback timeout is invalid.");
            registry.Create(new ChatModelOptions(fallback.ProviderId, fallback.BaseUrl, null, fallback.Model,
                fallback.SupportsNativeToolCalling) { RequestTimeout = fallback.RequestTimeout });
        }
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

    private EffectiveProviderConfiguration Build(
        string? activeOverride = null, ProviderProfile? profileOverride = null,
        IReadOnlyList<FallbackSetting>? fallbackOverride = null)
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
        var (fallbackEntries, fallbackSource) = ResolveFallbackEntries(fallbackOverride);
        // A Settings proposal is validated on its own merits even while host configuration shadows it, so an
        // invalid list can never be persisted dormant and later become effective.
        if (fallbackOverride is not null && fallbackSource == "configuration")
            BuildFallbacks(ToEntries(fallbackOverride), id, model.Value, configured.RequestTimeout, profileOverride);
        var pin = new PinnedProviderConfiguration(1, 0, id, baseUrl.Value, model.Value, native.Value,
            timeout, environmentOwned ? "environment" : storedId is null ? "default" : "settings",
            baseUrl.Source, model.Source, native.Source,
            HasEnvironmentField("RequestTimeout") ? "environment" : "default", string.Empty)
        {
            Fallbacks = BuildFallbacks(fallbackEntries, id, model.Value, configured.RequestTimeout, profileOverride),
        };
        pin = pin with { SnapshotHash = Hash(pin) };
        // Local validation: a package must be able to construct the adapter without a network call.
        (configuration.GetSection("Agent").Get<AgentRunnerOptions>() ?? new AgentRunnerOptions())
            .Validate(pin.RequestTimeout ?? ChatModelOptions.DefaultRequestTimeout);
        registry.Create(new ChatModelOptions(id, pin.BaseUrl, null, pin.Model, pin.SupportsNativeToolCalling)
            { RequestTimeout = pin.RequestTimeout });
        var credential = new ProviderCredentialResolver(configuration, environmentSecrets, vault).Availability(id);
        return new EffectiveProviderConfiguration(pin, credential.Available, credential.Source)
        {
            FallbackSource = fallbackSource,
            FallbackCredentialAvailable = FallbackReadiness(pin),
        };
    }

    private List<bool> FallbackReadiness(PinnedProviderConfiguration pin)
    {
        var resolver = new ProviderCredentialResolver(configuration, environmentSecrets, vault);
        return pin.Fallbacks.Select(candidate => resolver.Availability(candidate.ProviderId).Available).ToList();
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
            FallbackCredentialAvailable = FallbackReadiness(current.Pin),
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

    /// <summary>Host configuration owns the whole list when it names any entry (ADR-0045 §7); otherwise Settings does.</summary>
    private (List<FallbackConfiguration> Entries, string Source) ResolveFallbackEntries(IReadOnlyList<FallbackSetting>? proposed)
    {
        var configured = configuration.GetSection("ModelProvider:Fallbacks").Get<List<FallbackConfiguration>>() ?? [];
        if (configured.Count > 0) return (configured, "configuration");
        var stored = proposed ?? settings.Fallbacks;
        return (ToEntries(stored), stored.Count > 0 ? "settings" : "default");
    }

    private static List<FallbackConfiguration> ToEntries(IReadOnlyList<FallbackSetting> stored) =>
        stored.Select(entry => new FallbackConfiguration { Provider = entry.Provider, Model = entry.Model }).ToList();

    private List<PinnedProviderCandidate> BuildFallbacks(
        List<FallbackConfiguration> configured, string primaryProvider, string primaryModel, TimeSpan? requestTimeout,
        ProviderProfile? profileOverride = null)
    {
        if (configured.Count > 3)
            throw new ArgumentException("At most three model fallback candidates may be configured.");
        var tuples = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { $"{primaryProvider}\u001f{primaryModel}" };
        var result = new List<PinnedProviderCandidate>(configured.Count);
        foreach (var entry in configured)
        {
            var id = Canonical(entry.Provider);
            if (string.IsNullOrWhiteSpace(entry.Model) || entry.Model.Length > 256)
                throw new ArgumentException("A fallback model must be nonblank and at most 256 characters.");
            if (!tuples.Add($"{id}\u001f{entry.Model}"))
                throw new ArgumentException("Duplicate provider/model candidates are not allowed in the fallback chain.");
            var profile = profileOverride is not null && Canonical(profileOverride.ProviderId).Equals(id, StringComparison.OrdinalIgnoreCase)
                ? profileOverride : settings.FindProviderProfile(id);
            var providerDefaults = configuration.GetSection("ModelProvider").Get<ChatModelOptions>()!;
            var baseUrl = profile?.BaseUrl ??
                (id.Equals(providerDefaults.Provider, StringComparison.OrdinalIgnoreCase) ? providerDefaults.BaseUrl : null);
            if (baseUrl is null)
                throw new ArgumentException($"'{id}' has no stored profile for fallback configuration.");
            var native = profile?.SupportsNativeToolCalling ?? providerDefaults.SupportsNativeToolCalling;
            ValidateShape(baseUrl, entry.Model);
            result.Add(new PinnedProviderCandidate(id, baseUrl, entry.Model, native, requestTimeout));
        }
        return result;
    }

    private static string Hash(PinnedProviderConfiguration pin)
    {
        var content = string.Join('\u001f', pin.SchemaVersion, pin.ProviderId, pin.BaseUrl, pin.Model,
            pin.SupportsNativeToolCalling, pin.RequestTimeout?.Ticks, pin.ProviderSource, pin.BaseUrlSource,
            pin.ModelSource, pin.SupportsNativeToolCallingSource, pin.RequestTimeoutSource);
        // An empty chain adds no immutable configuration, so it must hash exactly as a pin stored before fallbacks existed.
        if (pin.Fallbacks.Count > 0)
            content += '\u001f' + string.Join('\u001e', pin.Fallbacks.Select(candidate => string.Join('\u001d',
                candidate.ProviderId, candidate.BaseUrl, candidate.Model, candidate.SupportsNativeToolCalling,
                candidate.RequestTimeout?.Ticks)));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }

    private sealed record FallbackConfiguration
    {
        public string Provider { get; init; } = string.Empty;
        public string Model { get; init; } = string.Empty;
    }
}
