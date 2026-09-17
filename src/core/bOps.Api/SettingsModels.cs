// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Api;

/// <summary>
/// The administrator-facing view of one provider (ADR-0029). <see cref="HasStoredKey"/>/
/// <see cref="KeyMaskPrefix"/>/<see cref="KeyMaskSuffix"/> describe the vault entry, never its
/// plaintext. <see cref="BaseUrl"/>/<see cref="Model"/>/<see cref="SupportsNativeToolCalling"/>/
/// <see cref="ExtraParameters"/> describe the non-secret profile; either half can be absent
/// independently of the other.
/// </summary>
internal sealed record SettingsProviderView(
    string ProviderId,
    bool IsActive,
    bool HasStoredKey,
    string? KeyMaskPrefix,
    string? KeyMaskSuffix,
    int? KeyPlaintextLength,
    DateTimeOffset? KeyUpdatedUtc,
    string? BaseUrl,
    string? Model,
    bool? SupportsNativeToolCalling,
    IReadOnlyDictionary<string, string>? ExtraParameters,
    DateTimeOffset? ProfileUpdatedUtc);

/// <summary>
/// The full Settings view (ADR-0029). <see cref="VaultVersion"/> is the optimistic-concurrency
/// token every provider-key write must echo back as <c>expectedVersion</c>.
/// </summary>
internal sealed record SettingsView(
    int VaultVersion,
    string? ActiveProviderId,
    string ActiveProviderSource,
    IReadOnlyList<SettingsProviderView> Providers);

/// <summary>Sets or replaces a provider's API key. Write-only: never echoes the key back.</summary>
internal sealed record SetProviderKeyRequest(string ApiKey, int ExpectedVersion);

/// <summary>Sets or replaces a provider's non-secret profile.</summary>
#pragma warning disable CA1056 // BaseUrl is configuration-bound, same as ChatModelOptions.BaseUrl.
internal sealed record SetProviderProfileRequest(
    string BaseUrl, string Model, bool SupportsNativeToolCalling, Dictionary<string, string>? ExtraParameters);
#pragma warning restore CA1056

/// <summary>Selects the active provider.</summary>
internal sealed record SetActiveProviderRequest(string ProviderId);
