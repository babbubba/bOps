// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using System.Diagnostics;
using bOps.Abstractions;
using bOps.Packages.Sys.Linux;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.System.Linux.Tests;

public sealed class LinuxUpdatesTests
{
    private static readonly string TestChild = Path.Combine(AppContext.BaseDirectory, "bOps.Packages.System.Linux.TestChild.dll");

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

    [Fact]
    public void UnknownCatalogAgeIsSerializedWithExplicitUncertainty()
    {
        var json = JsonNode.Parse(SystemMaintenanceFormatting.Updates(
            LinuxUpdatesTool.CreateSnapshot([], "linux.apt"), 200))!.AsObject();

        Assert.Null(json["catalogAge"]);
        Assert.Equal("complete", json["status"]!.GetValue<string>());
        Assert.True(json["complete"]!.GetValue<bool>());
        Assert.Contains(json["warnings"]!.AsArray().Select(x => x!.GetValue<string>()),
            warning => warning.Contains("metadata age is unknown", StringComparison.Ordinal));
    }

    [Fact]
    public void NoCurrentLinuxBackendHasAnAuthoritativeCatalogAgeSource()
    {
        // APT's simulation, DNF's cache-only query and Zypper's XML listing expose no
        // reliable catalog timestamp consumed by this collector. Stale age is therefore N/A.
        var unknown = JsonNode.Parse(SystemMaintenanceFormatting.Updates(
            LinuxUpdatesTool.CreateSnapshot([], "linux.dnf"), 200))!.AsObject();
        Assert.Null(unknown["catalogAge"]);
        Assert.Contains(unknown["warnings"]!.AsArray().Select(x => x!.GetValue<string>()),
            warning => warning.Contains("metadata age is unknown", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProcessBoundaryTimesOutKillsAndConfirmsChildExit()
    {
        var result = await LinuxUpdatesProcessRunner.RunAsync(DotNetHostPath, [TestChild, "sleep", "30000"], TimeSpan.FromMilliseconds(700), CancellationToken.None);

        Assert.True(result.Started);
        Assert.True(result.TimedOut);
        Assert.True(result.Exited);
        Assert.False(IsRunning(result.ProcessId));
    }

    [Fact]
    public async Task ProcessBoundaryCancellationKillsAndConfirmsChildExit()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = LinuxUpdatesProcessRunner.RunAsync(DotNetHostPath, [TestChild, "sleep", "30000"], TimeSpan.FromSeconds(20), cancellation.Token, started: started.SetResult);
        var processId = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.False(IsRunning(processId));
    }

    [Fact]
    public void TimedOutProcessCannotProduceACompleteUpdatesSnapshot()
    {
        var json = JsonNode.Parse(SystemMaintenanceFormatting.Updates(LinuxUpdatesTool.CreateProcessFailureSnapshot("linux.apt"), 200))!.AsObject();

        Assert.False(json["complete"]!.GetValue<bool>());
        Assert.Equal("partial", json["status"]!.GetValue<string>());
        Assert.Contains("timeout", json["sources"]![0]!["detail"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    private static string DotNetHostPath => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";

    private static bool IsRunning(int processId)
    {
        try { using var process = Process.GetProcessById(processId); return !process.HasExited; }
        catch (ArgumentException) { return false; }
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
