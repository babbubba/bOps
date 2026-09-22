// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Network.Native.Core;

namespace bOps.Packages.Network.Native.Core.Tests;

public sealed class NetworkNativeArgumentsTests
{
    [Fact]
    public void Sockets_Defaults_AreApplied()
    {
        Assert.True(NetworkNativeArguments.TryReadSockets(ToolArguments.Empty, out var protocol, out var state, out var pid, out var localPort, out var limit, out _));
        Assert.Equal("all", protocol);
        Assert.Null(state);
        Assert.Null(pid);
        Assert.Null(localPort);
        Assert.Equal(NetworkNativeLimits.DefaultRows, limit);
    }

    [Theory]
    [InlineData("ftp")]
    [InlineData("TCP")]
    public void Sockets_RejectsInvalidProtocol(string protocol)
    {
        Assert.False(NetworkNativeArguments.TryReadSockets(
            Arguments(new JsonObject { ["protocol"] = protocol }), out _, out _, out _, out _, out _, out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65_536)]
    public void Sockets_RejectsOutOfRangeLocalPort(int port)
    {
        Assert.False(NetworkNativeArguments.TryReadSockets(
            Arguments(new JsonObject { ["localPort"] = port }), out _, out _, out _, out _, out _, out _));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(65_535)]
    public void Sockets_AcceptsLocalPortAtEdges(int port)
    {
        Assert.True(NetworkNativeArguments.TryReadSockets(
            Arguments(new JsonObject { ["localPort"] = port }), out _, out _, out _, out var localPort, out _, out _));
        Assert.Equal(port, localPort);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(NetworkNativeLimits.MaximumRows + 1)]
    public void Sockets_RejectsLimitOutOfRange(int limit)
    {
        Assert.False(NetworkNativeArguments.TryReadSockets(
            Arguments(new JsonObject { ["limit"] = limit }), out _, out _, out _, out _, out _, out _));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(NetworkNativeLimits.MaximumRows)]
    public void Sockets_AcceptsLimitAtEdges(int limit)
    {
        Assert.True(NetworkNativeArguments.TryReadSockets(
            Arguments(new JsonObject { ["limit"] = limit }), out _, out _, out _, out _, out var read, out _));
        Assert.Equal(limit, read);
    }

    [Fact]
    public void Routes_RejectsInvalidAddressFamily()
    {
        Assert.False(NetworkNativeArguments.TryReadRoutes(Arguments(new JsonObject { ["addressFamily"] = "ipv5" }), out _, out _, out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("all")]
    [InlineData("ipv4")]
    [InlineData("ipv6")]
    public void Routes_AcceptsEveryAddressFamily(string family)
    {
        Assert.True(NetworkNativeArguments.TryReadRoutes(Arguments(new JsonObject { ["addressFamily"] = family }), out var read, out _, out _));
        Assert.Equal(family, read);
    }

    [Fact]
    public void Neighbors_RejectsBlankInterfaceName()
    {
        Assert.False(NetworkNativeArguments.TryReadNeighbors(Arguments(new JsonObject { ["interfaceName"] = "  " }), out _, out _, out _, out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(NetworkNativeLimits.MinimumSampleMilliseconds - 1)]
    [InlineData(NetworkNativeLimits.MaximumSampleMilliseconds + 1)]
    public void InterfaceStats_RejectsSampleMillisecondsOutOfRange(int sample)
    {
        Assert.False(NetworkNativeArguments.TryReadInterfaceStats(
            Arguments(new JsonObject { ["sampleMilliseconds"] = sample }), out _, out _, out _));
    }

    [Theory]
    [InlineData(NetworkNativeLimits.MinimumSampleMilliseconds)]
    [InlineData(NetworkNativeLimits.MaximumSampleMilliseconds)]
    public void InterfaceStats_AcceptsSampleMillisecondsAtEdges(int sample)
    {
        Assert.True(NetworkNativeArguments.TryReadInterfaceStats(
            Arguments(new JsonObject { ["sampleMilliseconds"] = sample }), out _, out var read, out _));
        Assert.Equal(sample, read);
    }

    [Fact]
    public void DnsQuery_RequiresNameAndRecordType()
    {
        Assert.False(NetworkNativeArguments.TryReadDnsQuery(ToolArguments.Empty, out _, out _, out _, out _, out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("MX")]
    [InlineData("txt")]
    public void DnsQuery_RejectsUnsupportedRecordType(string recordType)
    {
        Assert.False(NetworkNativeArguments.TryReadDnsQuery(
            Arguments(new JsonObject { ["name"] = "example.com", ["recordType"] = recordType }), out _, out _, out _, out _, out _));
    }

    [Theory]
    [InlineData(NetworkNativeLimits.MinimumDnsTimeoutMilliseconds - 1)]
    [InlineData(NetworkNativeLimits.MaximumDnsTimeoutMilliseconds + 1)]
    public void DnsQuery_RejectsTimeoutOutOfRange(int timeout)
    {
        Assert.False(NetworkNativeArguments.TryReadDnsQuery(
            Arguments(new JsonObject { ["name"] = "example.com", ["recordType"] = "A", ["timeout"] = timeout }), out _, out _, out _, out _, out _));
    }

    [Fact]
    public void Traceroute_RequiresHost()
    {
        Assert.False(NetworkNativeArguments.TryReadTraceroute(ToolArguments.Empty, out _, out _, out _, out _, out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(NetworkNativeLimits.MinimumMaxHops - 1)]
    [InlineData(NetworkNativeLimits.MaximumMaxHops + 1)]
    public void Traceroute_RejectsMaxHopsOutOfRange(int maxHops)
    {
        Assert.False(NetworkNativeArguments.TryReadTraceroute(
            Arguments(new JsonObject { ["host"] = "localhost", ["maxHops"] = maxHops }), out _, out _, out _, out _, out _));
    }

    [Theory]
    [InlineData(NetworkNativeLimits.MinimumMaxHops)]
    [InlineData(NetworkNativeLimits.MaximumMaxHops)]
    public void Traceroute_AcceptsMaxHopsAtEdges(int maxHops)
    {
        Assert.True(NetworkNativeArguments.TryReadTraceroute(
            Arguments(new JsonObject { ["host"] = "localhost", ["maxHops"] = maxHops }), out _, out var read, out _, out _, out _));
        Assert.Equal(maxHops, read);
    }

    [Fact]
    public void Traceroute_RejectsInvalidAddressFamily()
    {
        Assert.False(NetworkNativeArguments.TryReadTraceroute(
            Arguments(new JsonObject { ["host"] = "localhost", ["addressFamily"] = "ipv7" }), out _, out _, out _, out _, out _));
    }

    [Fact]
    public void NtpProbe_RequiresHost()
    {
        Assert.False(NetworkNativeArguments.TryReadNtpProbe(ToolArguments.Empty, out _, out _, out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(NetworkNativeLimits.MinimumNtpTimeoutMilliseconds - 1)]
    [InlineData(NetworkNativeLimits.MaximumNtpTimeoutMilliseconds + 1)]
    public void NtpProbe_RejectsTimeoutOutOfRange(int timeout)
    {
        Assert.False(NetworkNativeArguments.TryReadNtpProbe(
            Arguments(new JsonObject { ["host"] = "pool.ntp.org", ["timeout"] = timeout }), out _, out _, out _));
    }

    [Theory]
    [InlineData(NetworkNativeLimits.MinimumNtpTimeoutMilliseconds)]
    [InlineData(NetworkNativeLimits.MaximumNtpTimeoutMilliseconds)]
    public void NtpProbe_AcceptsTimeoutAtEdges(int timeout)
    {
        Assert.True(NetworkNativeArguments.TryReadNtpProbe(
            Arguments(new JsonObject { ["host"] = "pool.ntp.org", ["timeout"] = timeout }), out _, out var read, out _));
        Assert.Equal(timeout, read);
    }

    private static ToolArguments Arguments(JsonObject json) => ToolArguments.FromJson(json);
}
