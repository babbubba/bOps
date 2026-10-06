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
    string SnapshotHash);
#pragma warning restore CA1054, CA1056
