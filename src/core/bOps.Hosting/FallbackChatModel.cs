// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Hosting;

/// <summary>An execution-scoped, ordered and sticky model candidate selector.</summary>
public sealed class FallbackChatModel : IFallbackChatModelControl
{
    private readonly IReadOnlyList<IChatModel> candidates;
    private readonly PinnedProviderConfiguration pin;

    /// <summary>Creates a selector over a primary followed by zero to three explicit fallbacks.</summary>
    public FallbackChatModel(IReadOnlyList<IChatModel> candidates, PinnedProviderConfiguration pin)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(pin);
        if (candidates.Count is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(candidates), "A candidate chain must contain one to four entries.");
        if (pin.FallbackOrdinal < 0 || pin.FallbackOrdinal >= candidates.Count)
            throw new ArgumentOutOfRangeException(nameof(pin), "The persisted fallback ordinal is outside the candidate chain.");
        this.candidates = candidates;
        this.pin = pin;
    }

    /// <inheritdoc />
    public ChatModelDescriptor Descriptor => candidates[pin.FallbackOrdinal].Descriptor;

    /// <inheritdoc />
    public int FallbackOrdinal => pin.FallbackOrdinal;

    /// <inheritdoc />
    public bool HasNextCandidate => pin.FallbackOrdinal + 1 < candidates.Count;

    /// <inheritdoc />
    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default) =>
        candidates[pin.FallbackOrdinal].CompleteAsync(request, ct);

    /// <inheritdoc />
    public bool TryAdvance()
    {
        if (pin.FallbackOrdinal + 1 >= candidates.Count) return false;
        pin.FallbackOrdinal++;
        return true;
    }
}
