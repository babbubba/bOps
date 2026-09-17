// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using bOps.Packages.Web;

namespace bOps.Packages.Web.Tests;

public sealed class IpAddressPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.255")]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")] // cloud-metadata address, link-local
    [InlineData("169.254.0.1")]
    [InlineData("100.64.0.1")] // RFC 6598 CGNAT
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")] // multicast
    [InlineData("255.255.255.255")] // broadcast
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456:789a::1")]
    [InlineData("ff02::1")]
    [InlineData("::")]
    public void IsAllowed_DeniesKnownUnsafeAddresses_ByDefault(string address)
    {
        var policy = IpAddressPolicy.Default;

        Assert.False(policy.IsAllowed(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]
    [InlineData("2606:4700:4700::1111")]
    public void IsAllowed_AllowsOrdinaryPublicAddresses_ByDefault(string address)
    {
        var policy = IpAddressPolicy.Default;

        Assert.True(policy.IsAllowed(IPAddress.Parse(address)));
    }

    [Fact]
    public void IsAllowed_DeniesIPv4MappedIPv6WrappingADeniedAddress()
    {
        var policy = IpAddressPolicy.Default;

        Assert.False(policy.IsAllowed(IPAddress.Parse("::ffff:127.0.0.1")));
        Assert.False(policy.IsAllowed(IPAddress.Parse("::ffff:169.254.169.254")));
    }

    [Fact]
    public void IsAllowed_AllowsAnExplicitlyAllowlistedAddress_EvenThoughDeniedByDefault()
    {
        var policy = new IpAddressPolicy(allowedAddresses: ["127.0.0.1"]);

        Assert.True(policy.IsAllowed(IPAddress.Parse("127.0.0.1")));
        Assert.False(policy.IsAllowed(IPAddress.Parse("127.0.0.2")));
    }

    [Fact]
    public void IsAllowed_AllowsAnAddressInsideAnExplicitlyAllowlistedNetwork()
    {
        var policy = new IpAddressPolicy(allowedNetworks: ["10.20.0.0/16"]);

        Assert.True(policy.IsAllowed(IPAddress.Parse("10.20.5.6")));
        Assert.False(policy.IsAllowed(IPAddress.Parse("10.21.5.6")));
        Assert.False(policy.IsAllowed(IPAddress.Parse("10.0.0.1"))); // still denied — not the allowlisted /16
    }

    [Fact]
    public void Constructor_RejectsAMalformedCidrNetwork()
    {
        Assert.Throws<FormatException>(() => new IpAddressPolicy(allowedNetworks: ["not-a-cidr"]));
    }

    [Fact]
    public void Constructor_RejectsAPrefixLengthOutOfRangeForTheAddressFamily()
    {
        Assert.Throws<FormatException>(() => new IpAddressPolicy(allowedNetworks: ["10.0.0.0/33"]));
    }
}
