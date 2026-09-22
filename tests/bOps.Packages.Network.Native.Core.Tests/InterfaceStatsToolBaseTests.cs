// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Network.Native.Core;

namespace bOps.Packages.Network.Native.Core.Tests;

public sealed class InterfaceStatsToolBaseTests
{
    [Fact]
    public void Compute_ComputesPositiveRates()
    {
        var first = new[] { new InterfaceCounterSample("eth0", 1_000, 500, 10, 5, 0, 0, 0, 0, 1_000, "up") };
        var second = new[] { new InterfaceCounterSample("eth0", 2_000, 1_500, 20, 15, 1, 0, 0, 0, 1_000, "up") };

        var results = InterfaceStatsToolBase.Compute(first, second, seconds: 1.0);

        var result = Assert.Single(results);
        Assert.Equal(1_000, result.BytesReceivedPerSec);
        Assert.Equal(1_000, result.BytesSentPerSec);
        Assert.Equal(10, result.PacketsReceivedPerSec);
        Assert.Equal(10, result.PacketsSentPerSec);
        Assert.Equal(1, result.ReceiveErrorsPerSec);
    }

    [Fact]
    public void Compute_NullCounter_ProducesNullRate()
    {
        var first = new[] { new InterfaceCounterSample("eth0", null, 500, null, null, null, null, null, null, null, "up") };
        var second = new[] { new InterfaceCounterSample("eth0", null, 1_500, null, null, null, null, null, null, null, "up") };

        var results = InterfaceStatsToolBase.Compute(first, second, seconds: 1.0);

        Assert.Null(results[0].BytesReceivedPerSec);
        Assert.Equal(1_000, results[0].BytesSentPerSec);
    }

    [Fact]
    public void Compute_CounterDecreased_RateNeverNegative()
    {
        var first = new[] { new InterfaceCounterSample("eth0", 5_000, 0, 0, 0, 0, 0, 0, 0, null, "up") };
        var second = new[] { new InterfaceCounterSample("eth0", 100, 0, 0, 0, 0, 0, 0, 0, null, "up") };

        var results = InterfaceStatsToolBase.Compute(first, second, seconds: 1.0);

        Assert.Equal(0, results[0].BytesReceivedPerSec);
    }

    [Fact]
    public void Compute_InterfaceOnlyInSecondSample_HasNullRates()
    {
        var first = Array.Empty<InterfaceCounterSample>();
        var second = new[] { new InterfaceCounterSample("newIf", 100, 100, 1, 1, 0, 0, 0, 0, null, "up") };

        var results = InterfaceStatsToolBase.Compute(first, second, seconds: 1.0);

        Assert.Null(results[0].BytesReceivedPerSec);
        Assert.Equal("newIf", results[0].InterfaceName);
    }
}
