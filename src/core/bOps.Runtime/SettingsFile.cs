// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Runtime;

/// <summary>
/// Non-secret Settings persisted separately from the encrypted vault (ADR-0029): the
/// administrator's chosen active provider id, and every provider's non-secret profile
/// (endpoint, model, tool-calling support, extras) keyed by provider id.
/// </summary>
public sealed record SettingsFile(
    int SchemaVersion, string? ActiveProviderId, Dictionary<string, ProviderProfile> Providers, int Revision = 0,
    IReadOnlyList<FallbackSetting>? Fallbacks = null);

/// <summary>One administrator-chosen fallback candidate: a provider id and a model. Non-secret by construction (ADR-0045).</summary>
public sealed record FallbackSetting(string Provider, string Model);
