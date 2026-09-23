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
        Assert.True(File.Exists(TestChild), $"Test child was not deployed to {AppContext.BaseDirectory}.");
        Assert.True(File.Exists(Path.ChangeExtension(TestChild, ".runtimeconfig.json")), "Test child runtime configuration was not deployed.");
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

    [Fact]
    public void DebianHistoryRegression_PreservesUnknownAndRejectsIncompleteTransactions()
    {
        var apt = LinuxUpdateHistoryParser.Parse("debian", "Start-Date: 2026-09-23 10:00:00\nUpgrade: demo (1.0, 2.0)\nEnd-Date: 2026-09-23 10:01:00\n", "linux.apt-history");
        Assert.True(apt.Valid); Assert.Single(apt.Items); Assert.Equal("unknown", apt.Items[0].Result); Assert.Equal("2.0", apt.Items[0].Version);
        Assert.False(LinuxUpdateHistoryParser.Parse("debian", "Start-Date: 2026-09-23 10:00:00\nUpgrade: demo (1.0, 2.0)\n", "linux.apt-history").Valid);
    }

    [Fact]
    public void DnfHistory_MapsTransactionsResultsAndNullVersion()
    {
        var parsed = LinuxUpdateHistoryParser.Parse("dnf", Dnf(" 3 | install kernel core | 2026-09-22 10:30 | Install | 2 ", " 4 | update demo | 2026-09-23 10:30 | Upgrade | 1 * ", " 5 | update another | 2026-09-23 11:30 | Upgrade | 1 # ", " 6 | update warned | 2026-09-23 12:30 | Upgrade | 1 E ", " 7 | update rpmdb | 2026-09-23 13:30 | Upgrade | 1 >< "), "dnf-history");

        Assert.True(parsed.Valid);
        Assert.Equal(["success", "failure", "failure", "success", "success"], parsed.Items.Select(item => item.Result));
        Assert.All(parsed.Items, item => { Assert.StartsWith("dnf:", item.Id, StringComparison.Ordinal); Assert.Null(item.Version); Assert.Equal("dnf-history", item.Source); });
        Assert.Equal("install kernel core", parsed.Items[0].Name);
    }

    [Fact]
    public void DnfHistory_RejectsMalformedHeaderRowsAndAmbiguousMarkers()
    {
        Assert.False(LinuxUpdateHistoryParser.Parse("dnf", "ID | Command line | Date and time\n", "dnf-history").Valid);
        Assert.False(LinuxUpdateHistoryParser.Parse("dnf", Dnf("bad | update demo | 2026-09-23 10:30 | Upgrade | 1"), "dnf-history").Valid);
        var ambiguous = LinuxUpdateHistoryParser.Parse("dnf", Dnf("1 | update demo | 2026-09-23 10:30 | Upgrade | 1 ?"), "dnf-history");
        Assert.True(ambiguous.Valid); Assert.Equal("unknown", ambiguous.Items.Single().Result);
    }

    [Fact]
    public void DnfHistory_EmptySucceededTableIsCompleteAndSerializedDeterministically()
    {
        var parsed = LinuxUpdateHistoryParser.Parse("dnf", Dnf(), "dnf-history");
        Assert.True(parsed.Valid); Assert.Empty(parsed.Items);
        var empty = JsonNode.Parse(SystemMaintenanceFormatting.History(new(parsed.Items, [new("dnf-history", InventorySourceStatus.Available)]), 1))!.AsObject();
        Assert.True(empty["complete"]!.GetValue<bool>()); Assert.Empty(empty["items"]!.AsArray());

        var rows = LinuxUpdateHistoryParser.Parse("dnf", Dnf("2 | update zulu | 2026-09-22 10:30 | Upgrade | 1", "1 | update alpha | 2026-09-23 10:30 | Upgrade | 1"), "dnf-history").Items;
        var first = SystemMaintenanceFormatting.History(new(rows, [new("dnf-history", InventorySourceStatus.Available)]), 1);
        var second = SystemMaintenanceFormatting.History(new(rows.Reverse().ToArray(), [new("dnf-history", InventorySourceStatus.Available)]), 1);
        Assert.Equal(first, second);
        var json = JsonNode.Parse(first)!.AsObject(); Assert.True(json["truncated"]!.GetValue<bool>()); Assert.False(json["complete"]!.GetValue<bool>());
    }

    [Fact]
    public void DnfHistory_SinceDaysCanBeAppliedAfterUtcParsing()
    {
        var rows = LinuxUpdateHistoryParser.Parse("dnf", Dnf("1 | update old | 2020-01-01 00:00 | Upgrade | 1", "2 | update current | 2026-09-23 10:30 | Upgrade | 1"), "dnf-history").Items;
        var cutoff = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var current = Assert.Single(rows, row => row.TimestampUtc >= cutoff);
        Assert.Equal("update current", current.Name);
    }

    [Fact]
    public void DnfHistory_CommandUnavailableAndNonZeroExitAreSerializedAsIncomplete()
    {
        var arguments = new MaintenanceArguments(null, 30, null, 200);
        var unavailable = LinuxUpdateHistoryTool.CreateDnfHistorySnapshot(new(false, false, false, 0, null, null, null), arguments);
        var failedExit = LinuxUpdateHistoryTool.CreateDnfHistorySnapshot(new(true, false, true, 1, 1, Dnf(), "failure"), arguments);

        foreach (var snapshot in new[] { unavailable, failedExit })
        {
            var json = JsonNode.Parse(SystemMaintenanceFormatting.History(snapshot, arguments.Limit))!.AsObject();
            Assert.False(json["complete"]!.GetValue<bool>()); Assert.Equal("unavailable", json["status"]!.GetValue<string>()); Assert.Empty(json["items"]!.AsArray());
        }
    }

    [Fact]
    public void ZyppHistory_MapsCommittedPackageRecordsAndIgnoresComments()
    {
        const string fixture = "# zypp history\n2026-09-22 10:30:00|install|demo|1.2.3|x86_64|repo\n2026-09-23 10:30:00|remove|old-demo|0.9|x86_64|repo\n";
        var parsed = LinuxUpdateHistoryParser.Parse("zypper", fixture, "zypp-history");

        Assert.True(parsed.Valid); Assert.Empty(parsed.Warnings); Assert.Equal(2, parsed.Items.Count);
        Assert.Equal("demo", parsed.Items[0].Name); Assert.Equal("1.2.3", parsed.Items[0].Version); Assert.Equal("success", parsed.Items[0].Result);
        Assert.StartsWith("zypp:1:", parsed.Items[0].Id, StringComparison.Ordinal); Assert.Equal("zypp-history", parsed.Items[0].Source);
        Assert.True(parsed.Items[0].TimestampUtc <= parsed.Items[1].TimestampUtc);
    }

    [Fact]
    public void ZyppHistory_MalformedAndUnknownRecordsRemainIncompleteEvidence()
    {
        Assert.False(LinuxUpdateHistoryParser.Parse("zypper", "2026-09-23 10:30:00|install|demo\n", "zypp-history").Valid);
        Assert.False(LinuxUpdateHistoryParser.Parse("zypper", "malformed", "zypp-history").Valid);
        var unknown = LinuxUpdateHistoryParser.Parse("zypper", "2026-09-23 10:30:00|mystery|demo|1.0\n", "zypp-history");
        Assert.True(unknown.Valid); Assert.NotEmpty(unknown.Warnings);
        var serialized = JsonNode.Parse(SystemMaintenanceFormatting.History(new(unknown.Items, [new("zypp-history", InventorySourceStatus.Partial)], unknown.Warnings), 200))!.AsObject();
        Assert.False(serialized["complete"]!.GetValue<bool>()); Assert.Contains(serialized["warnings"]!.AsArray(), warning => warning!.GetValue<string>().Contains("unrecognized", StringComparison.Ordinal));
    }

    [Fact]
    public void ZyppHistory_RotatedAndMissingSourcesAreExplicitlyIncomplete()
    {
        var rotated = JsonNode.Parse(SystemMaintenanceFormatting.History(new(
            LinuxUpdateHistoryParser.Parse("zypper", "2026-09-23 10:30:00|install|demo|1.0\n", "zypp-history").Items,
            [new("zypp-history", InventorySourceStatus.Partial)],
            ["Older rotated ZYpp history was not read; returned evidence may be incomplete."]), 200))!.AsObject();
        Assert.False(rotated["complete"]!.GetValue<bool>()); Assert.Contains(rotated["warnings"]!.AsArray(), warning => warning!.GetValue<string>().Contains("rotated", StringComparison.Ordinal));
        var missing = JsonNode.Parse(SystemMaintenanceFormatting.History(new(Array.Empty<UpdateHistoryRecord>(), [new("zypp-history", InventorySourceStatus.Unavailable)]), 200))!.AsObject();
        Assert.False(missing["complete"]!.GetValue<bool>());
    }

    private static string Dnf(params string[] rows) => string.Join('\n', [
        "ID | Command line | Date and time | Action(s) | Altered",
        "-----------------------------------------------------",
        .. rows]);

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
