// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Sys.Linux;

namespace bOps.Packages.System.Linux.Tests;

public sealed class LinuxUpdatesTests
{
    [Theory]
    [InlineData("ID=debian", "debian")]
    [InlineData("ID=ubuntu\nID_LIKE=debian", "debian")]
    [InlineData("ID=fedora", "dnf")]
    [InlineData("ID=rocky\nID_LIKE=rhel", "dnf")]
    [InlineData("ID=opensuse-leap", "zypper")]
    [InlineData("ID=sles", "zypper")]
    [InlineData("ID=gentoo", null)]
    public void DetectsSupportedDistroFamily(string input, string? expected) => Assert.Equal(expected, LinuxUpdateParser.Detect(input));

    [Fact]
    public void AptParsesNoUpdatesUpdateSecurityKeptBackAndMalformed()
    {
        Assert.Empty(LinuxUpdateParser.Parse("debian", "Reading package lists... Done\n", 0).Items);
        var update = LinuxUpdateParser.Parse("debian", "Inst demo [1.0] (2.0 repo [security])\n", 0);
        Assert.True(update.Valid); Assert.Equal("1.0", update.Items[0].CurrentVersion); Assert.Equal("2.0", update.Items[0].AvailableVersion); Assert.Equal("security", update.Items[0].Kind);
        Assert.True(LinuxUpdateParser.Parse("debian", "The following packages have been kept back:\n demo\n", 0).KeptBack);
        Assert.False(LinuxUpdateParser.Parse("debian", "Inst broken\n", 0).Valid);
    }

    [Fact]
    public void DnfExitCodesAndRowsAreHonest()
    {
        Assert.True(LinuxUpdateParser.Parse("dnf", "", 0).Valid);
        var available = LinuxUpdateParser.Parse("dnf", "demo.x86_64 2.0 repo\n", 100);
        Assert.True(available.Valid); Assert.Null(available.Items[0].CurrentVersion); Assert.Equal("2.0", available.Items[0].AvailableVersion);
        Assert.False(LinuxUpdateParser.Parse("dnf", "", 1).Valid);
        Assert.False(LinuxUpdateParser.Parse("dnf", "malformed row\n", 100).Valid);
    }

    [Fact]
    public void ZypperParsesRowsEmptyAndRejectsMalformedOrMissingFields()
    {
        const string row = "<stream><update name=\"demo\" edition=\"2.0\" arch=\"x86_64\"/></stream>";
        var parsed = LinuxUpdateParser.Parse("zypper", row, 0); Assert.True(parsed.Valid); Assert.Equal("2.0", parsed.Items[0].AvailableVersion);
        Assert.True(LinuxUpdateParser.Parse("zypper", "<stream/>", 0).Valid);
        Assert.False(LinuxUpdateParser.Parse("zypper", "<stream>", 0).Valid);
        Assert.False(LinuxUpdateParser.Parse("zypper", "<stream><update name=\"demo\"/></stream>", 0).Valid);
        Assert.False(LinuxUpdateParser.Parse("zypper", "<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///etc/passwd'>]><stream>&e;</stream>", 0).Valid);
    }

    [Fact]
    public void ProviderRegistersUpdatesAndClassificationFilteringIsNormalized()
    {
        Assert.Contains(new LinuxUpdatesTool().Manifest.Name, new LinuxSystemToolProvider().GetTools().Select(x => x.Manifest.Name));
        var other = LinuxUpdateParser.Parse("debian", "Inst demo [1] (2 repo)\n", 0).Items.Single();
        Assert.Equal("other", other.Kind);
        Assert.DoesNotContain(other, new[] { other }.Where(x => x.Kind == "security"));
    }

    [LinuxOnlyFact]
    public async Task RealLinuxSmokeUsesReadOnlyDistroQueryAndExposesCompleteness()
    {
        var result = await new LinuxUpdatesTool().ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["limit"] = 200 }));
        Assert.True(result.Succeeded);
        var json = JsonNode.Parse(result.Output!)!.AsObject();
        Assert.InRange(json["returnedItems"]!.GetValue<int>(), 0, 200);
        Assert.NotNull(json["sources"]); Assert.NotNull(json["complete"]); Assert.NotNull(json["warnings"]);
    }
}
