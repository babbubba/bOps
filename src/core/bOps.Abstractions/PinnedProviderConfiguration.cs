// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Abstractions;

/// <summary>The durable, non-secret model configuration selected when an execution is admitted (ADR-0045).</summary>
#pragma warning disable CA1054, CA1056 // Configuration-bound URL and its provenance are serialized as strings.
public sealed record PinnedProviderConfiguration(
    int SchemaVersion,
    long Generation,
    string ProviderId,
    string BaseUrl,
    string Model,
    bool SupportsNativeToolCalling,
    TimeSpan? RequestTimeout,
    string ProviderSource,
    string BaseUrlSource,
    string ModelSource,
    string SupportsNativeToolCallingSource,
    string RequestTimeoutSource,
    string SnapshotHash)
{
    /// <summary>The explicit ordered fallback candidates, excluding the primary.</summary>
    public IReadOnlyList<PinnedProviderCandidate> Fallbacks { get; init; } = [];

    /// <summary>The sticky candidate ordinal for this execution. Excluded from <see cref="SnapshotHash"/>.</summary>
    public int FallbackOrdinal { get; set; }
}

/// <summary>One non-secret fallback candidate captured in an execution pin.</summary>
#pragma warning disable CA1054, CA1056 // Configuration-bound URL is serialized as a string.
public sealed record PinnedProviderCandidate(
    string ProviderId,
    string BaseUrl,
    string Model,
    bool SupportsNativeToolCalling,
    TimeSpan? RequestTimeout);
#pragma warning restore CA1054, CA1056
#pragma warning restore CA1054, CA1056
