// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Runtime;

/// <summary>
/// A provider's non-secret configuration (ADR-0029), persisted separately from its API key (which
/// lives in the encrypted vault, keyed by the same <see cref="ProviderId"/>). Fully operator-set
/// from the Settings UI: endpoint, model, tool-calling support, and any provider-specific extras
/// that do not yet have a first-class field.
/// </summary>
#pragma warning disable CA1054, CA1056 // Configuration-bound value, same as ChatModelOptions.BaseUrl.
public sealed record ProviderProfile(
    string ProviderId,
    string BaseUrl,
    string Model,
    bool SupportsNativeToolCalling,
    Dictionary<string, string> ExtraParameters,
    DateTimeOffset UpdatedUtc);
#pragma warning restore CA1054, CA1056
