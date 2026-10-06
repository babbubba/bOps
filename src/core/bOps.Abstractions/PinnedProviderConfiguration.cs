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
    private IReadOnlyList<PinnedProviderCandidate>? fallbacks;

    /// <summary>
    /// The explicit ordered fallback candidates, excluding the primary. A pin stored before fallback existed (or one whose
    /// serializer supplied no value) reads as an empty chain, never as <c>null</c>.
    /// </summary>
    public IReadOnlyList<PinnedProviderCandidate> Fallbacks
    {
        get => fallbacks ?? [];
        init => fallbacks = value;
    }

    /// <summary>The sticky candidate ordinal for this execution. Excluded from <see cref="SnapshotHash"/>.</summary>
    public int FallbackOrdinal { get; set; }

    /// <summary>Value equality, comparing the fallback chain element by element rather than by list reference.</summary>
    public bool Equals(PinnedProviderConfiguration? other) =>
        other is not null &&
        SchemaVersion == other.SchemaVersion && Generation == other.Generation &&
        ProviderId == other.ProviderId && BaseUrl == other.BaseUrl && Model == other.Model &&
        SupportsNativeToolCalling == other.SupportsNativeToolCalling && RequestTimeout == other.RequestTimeout &&
        ProviderSource == other.ProviderSource && BaseUrlSource == other.BaseUrlSource && ModelSource == other.ModelSource &&
        SupportsNativeToolCallingSource == other.SupportsNativeToolCallingSource &&
        RequestTimeoutSource == other.RequestTimeoutSource && SnapshotHash == other.SnapshotHash &&
        FallbackOrdinal == other.FallbackOrdinal && Fallbacks.SequenceEqual(other.Fallbacks);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SchemaVersion);
        hash.Add(Generation);
        hash.Add(ProviderId);
        hash.Add(Model);
        hash.Add(SnapshotHash);
        hash.Add(FallbackOrdinal);
        foreach (var candidate in Fallbacks) hash.Add(candidate);
        return hash.ToHashCode();
    }
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
