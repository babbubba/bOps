// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Hosting;

namespace bOps.Api.Tests;

/// <summary>HARDEN-13 B2a: the host-layer selector owns only monotonic, execution-scoped candidate selection.</summary>
public sealed class FallbackChatModelTests
{
    private sealed class Candidate(string id) : IChatModel
    {
        public int Calls { get; private set; }

        public ChatModelDescriptor Descriptor { get; } = new(id, $"{id}-model");

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new ModelResponse(id, [], true, null));
        }
    }

    private static PinnedProviderConfiguration Pin(int ordinal = 0) =>
        new(1, 1, "p0", "https://p0.test", "m0", true, null, "settings", "settings", "settings", "settings", "default", "hash")
        { FallbackOrdinal = ordinal };

    [Fact]
    public async Task Advances_OnlyForward_OncePerCall_AndNeverPastTheLastCandidate()
    {
        Candidate[] candidates = [new("a"), new("b"), new("c"), new("d")];
        var pin = Pin();
        var model = new FallbackChatModel(candidates, pin);
        var request = new ModelRequest("t", [], []);

        Assert.Equal("a-model", model.Descriptor.ModelId);
        Assert.True(model.HasNextCandidate);
        Assert.True(model.TryAdvance());
        Assert.True(model.TryAdvance());
        Assert.Equal(("c-model", 2, 2), (model.Descriptor.ModelId, model.FallbackOrdinal, pin.FallbackOrdinal));
        Assert.True(model.TryAdvance());
        Assert.False(model.HasNextCandidate);
        Assert.False(model.TryAdvance());
        Assert.Equal(3, model.FallbackOrdinal);

        Assert.Equal("d", (await model.CompleteAsync(request)).TextResponse);
        Assert.Equal([0, 0, 0, 1], candidates.Select(c => c.Calls));
    }

    [Fact]
    public async Task ReconstructedFromAPersistedOrdinal_StartsThereAndDoesNotShareStateWithAnotherExecution()
    {
        Candidate[] candidates = [new("a"), new("b")];
        var first = new FallbackChatModel(candidates, Pin());
        var second = new FallbackChatModel(candidates, Pin(ordinal: 1));
        first.TryAdvance();
        var third = new FallbackChatModel(candidates, Pin());

        Assert.Equal(1, second.FallbackOrdinal);
        Assert.Equal(0, third.FallbackOrdinal);
        Assert.Equal("b", (await second.CompleteAsync(new ModelRequest("t", [], []))).TextResponse);
        Assert.Equal([0, 1], candidates.Select(c => c.Calls));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 0)]
    [InlineData(2, 2)]
    [InlineData(2, -1)]
    public void RejectsAnEmptyOrOversizedChain_AndAnOrdinalOutsideIt(int count, int ordinal)
    {
        var candidates = Enumerable.Range(0, count).Select(i => (IChatModel)new Candidate($"c{i}")).ToList();

        Assert.Throws<ArgumentOutOfRangeException>(() => new FallbackChatModel(candidates, Pin(ordinal)));
    }
}
