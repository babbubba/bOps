// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Sys.Conformance;
using bOps.Packages.Sys.Linux;
using bOps.Packages.Identity.Linux;
using System.Text.Json.Nodes;

namespace bOps.Packages.System.Linux.Tests;

/// <summary>
/// Integration tests against the real Linux OS (never mocked — agentic/04-testing-rules.md,
/// "Never mock the operating system"), run through the shared conformance suite so a difference
/// in output shape from the Windows package would be caught here. Marked
/// <c>[Trait("Platform","Linux")]</c> so CI can select or skip this project explicitly; there is
/// no Linux host in this dev environment, so this project is scaffolded but not exercised here
/// (HANDOFF.md) — it runs wherever a real Linux target exists (a container, or the Aspire
/// AppHost from V0.5).
/// </summary>
[Trait("Platform", "Linux")]
public sealed class LinuxSystemToolsTests
{
    [Theory] [InlineData("yes", true)] [InlineData("YES", true)] [InlineData("true", true)] [InlineData("no", false)] [InlineData("FALSE", false)] [InlineData("unknown", null)]
    public void TimedatectlBooleanParsesSystemdValues(string value, bool? expected) => Assert.Equal(expected, LinuxSystemTimeTool.ParseBool(new Dictionary<string, string> { ["NTP"] = value }, "NTP"));
    [Theory] [InlineData("Timezone=Europe/Rome\nNTP=yes\nNTPSynchronized=no\n", true)] [InlineData("Timezone=Europe/Rome\nNTP=bad\n", false)] [InlineData("Timezone=Europe/Rome\n", false)] [InlineData(null, false)]
    public void TimedatectlCompletenessRequiresValidNtpEvidence(string? output, bool expected) => Assert.Equal(expected, LinuxSystemTimeTool.HasRequiredEvidence(output));
    [Fact] public void TimedatectlDuplicatePropertyIsMalformedAndIncomplete() { Assert.Null(LinuxSystemTimeTool.ParseProperties("NTP=yes\nNTP=no\n")); Assert.False(LinuxSystemTimeTool.HasRequiredEvidence("Timezone=UTC\nNTP=yes\nNTP=no\nNTPSynchronized=yes\n")); }
    [Fact] public async Task CommandOutputIsBounded() { using var shortOutput = new StringReader("abc"); using var longOutput = new StringReader("abcdef"); using var shortLoginOutput = new StringReader("abc"); using var longLoginOutput = new StringReader("abcdef"); Assert.Equal("abc", await LinuxTimedateCtl.ReadBoundedOutputAsync(shortOutput, 3, CancellationToken.None)); Assert.Null(await LinuxTimedateCtl.ReadBoundedOutputAsync(longOutput, 3, CancellationToken.None)); Assert.Equal("abc", await LinuxLoginCtl.ReadBoundedOutputAsync(shortLoginOutput, 3, CancellationToken.None)); Assert.Null(await LinuxLoginCtl.ReadBoundedOutputAsync(longLoginOutput, 3, CancellationToken.None)); }
    [Theory] [InlineData("yes", true)] [InlineData("YES", true)] [InlineData("true", true)] [InlineData("no", false)] [InlineData("FALSE", false)] [InlineData("unknown", null)]
    public void LoginCtlSystemdBooleanAcceptsYesNo(string value, bool? expected) => Assert.Equal(expected, LinuxSessionCollector.ParseBool(value));
    [Fact] public void LoginCtlSessionParserResistsLegendHeaders() { Assert.False(LinuxLoginCtl.IsSessionId("SESSION UID USER SEAT TTY")); Assert.True(LinuxLoginCtl.IsSessionId("123")); Assert.True(LinuxLoginCtl.IsSessionId("c1")); }
    [Theory] [InlineData("unavailable")] [InlineData("denied")] [InlineData("timeout")]
    public async Task LoginCtlFailuresUseBoundedLocalFallbackAndRemainIncomplete(string failure)
    { var result = await LinuxSessionCollector.ObserveAsync(_ => Task.FromResult<string?>(null), (_, _) => throw new InvalidOperationException("unexpected show"), _ => Task.FromResult<string?>("alice pts/0 2026-09-23 10:00"), CancellationToken.None); Assert.False(result.Complete, failure); Assert.Equal("linux.who.fallback", result.Source); Assert.Single(result.Rows); Assert.Equal("alice", result.Rows[0].User); }
    [Fact] public async Task LoginCtlAndFallbackUnavailableIsEmptyAndIncomplete()
    { var result = await LinuxSessionCollector.ObserveAsync(_ => Task.FromResult<string?>(null), (_, _) => throw new InvalidOperationException("unexpected show"), _ => Task.FromResult<string?>(null), CancellationToken.None); Assert.False(result.Complete); Assert.Empty(result.Rows); Assert.Equal("linux.loginctl+who.unavailable", result.Source); }
    [Fact] public async Task LoginCtlDetailFailureMakesSuccessfulListingIncomplete()
    { var result = await LinuxSessionCollector.ObserveAsync(_ => Task.FromResult<string?>("12 1000 alice seat0 tty1"), (_, _) => Task.FromResult<string?>(null), _ => throw new InvalidOperationException("unexpected fallback"), CancellationToken.None); Assert.False(result.Complete); Assert.Empty(result.Rows); }
    [Fact] public async Task LoginCtlYesNoDetailsProduceRealSessionEvidence()
    { var result = await LinuxSessionCollector.ObserveAsync(_ => Task.FromResult<string?>("c1 1000 alice seat0 tty1"), (_, _) => Task.FromResult<string?>("Name=alice\nState=active\nRemote=yes\nTimestamp=1780000000000000\nTTY=tty1\n"), _ => throw new InvalidOperationException("unexpected fallback"), CancellationToken.None); Assert.True(result.Complete); Assert.True(result.Rows.Single().Remote); Assert.Equal("alice", result.Rows.Single().User); Assert.NotNull(result.Rows.Single().LoginTimeUtc); }
    [Fact] public async Task LoginCtlLegendHeaderIsIgnoredAndCannotBecomeUser()
    { var result = await LinuxSessionCollector.ObserveAsync(_ => Task.FromResult<string?>("SESSION UID USER SEAT TTY"), (_, _) => throw new InvalidOperationException("unexpected show"), _ => throw new InvalidOperationException("unexpected fallback"), CancellationToken.None); Assert.False(result.Complete); Assert.Empty(result.Rows); }
    [Fact] public void LinuxEffectiveIdentityUsesEuidAndIncludesPrimaryGroup() { Assert.True(LinuxCurrentIdentityTool.IsElevated(0)); Assert.False(LinuxCurrentIdentityTool.IsElevated(1000)); Assert.Equal([10u, 20u], LinuxCurrentIdentityTool.DistinctGroupIds([20], 10).Order().ToArray()); Assert.Equal(["primary", "supplementary"], LinuxCurrentIdentityTool.BuildGroups([20], 10, gid => gid == 10 ? "primary" : "supplementary")); }
    [Fact] public void LinuxEffectiveIdentityUsesEffectiveUidAndGidWhenTheyDifferFromRealIds() { var result = LinuxCurrentIdentityTool.BuildEffectiveResult(new LinuxEffectiveIdentity(1000, 0, 100, 200), [300], uid => uid == 0 ? "root" : "real-user", gid => gid switch { 200 => "effective-primary", 300 => "supplementary", _ => "real-primary" }); Assert.Equal("0", result.UidOrSid); Assert.Equal("root", result.User); Assert.True(result.Elevated); Assert.Equal(["effective-primary", "supplementary"], result.Groups); Assert.DoesNotContain("real-primary", result.Groups); }
    [Fact] public void LinuxPasswdAndGroupSourcesDistinguishReadablePartialAndFailures() { var passwd = LinuxUsersTool.Observe(new(["alice:x:1000:1000::/home/alice:/bin/sh"], LinuxFileReadStatus.Readable)); Assert.Single(passwd.Rows); Assert.False(passwd.Complete); Assert.Equal("linux.passwd.local-directory-incomplete", passwd.Source); Assert.Equal("linux.passwd.truncated", LinuxUsersTool.Observe(new([], LinuxFileReadStatus.Truncated)).Source); Assert.Equal("linux.passwd.unavailable", LinuxUsersTool.Observe(new([], LinuxFileReadStatus.Unavailable)).Source); Assert.Equal("linux.passwd.denied", LinuxUsersTool.Observe(new([], LinuxFileReadStatus.Denied)).Source); var group = LinuxGroupsTool.Observe(new(["staff:x:1000:alice"], LinuxFileReadStatus.Readable)); Assert.Single(group.Rows); Assert.False(group.Complete); Assert.Equal("linux.group.error", LinuxGroupsTool.Observe(new([], LinuxFileReadStatus.Error)).Source); }
    [Fact] public async Task LoginCtlMalformedTimestampMakesObservationIncomplete() { var result = await LinuxSessionCollector.ObserveAsync(_ => Task.FromResult<string?>("c1 1000 alice seat0 tty1"), (_, _) => Task.FromResult<string?>("Name=alice\nState=active\nRemote=no\nTimestamp=not-a-timestamp\n"), _ => throw new InvalidOperationException("unexpected fallback"), CancellationToken.None); Assert.False(result.Complete); Assert.Null(result.Rows.Single().LoginTimeUtc); }
    [Fact] public async Task LoginCtlMissingTimestampIsDistinctFromMalformedTimestamp() { var result = await LinuxSessionCollector.ObserveAsync(_ => Task.FromResult<string?>("c1 1000 alice seat0 tty1"), (_, _) => Task.FromResult<string?>("Name=alice\nState=active\nRemote=no\n"), _ => throw new InvalidOperationException("unexpected fallback"), CancellationToken.None); Assert.True(result.Complete); Assert.Null(result.Rows.Single().LoginTimeUtc); }

    [LinuxOnlyFact] public async Task LinuxTimeAndRebootSourcesReturnExplicitEvidence() { var time = await new LinuxSystemTimeTool().ExecuteAsync(bOps.Abstractions.ToolArguments.Empty); Assert.True(time.Succeeded); var timeJson = JsonNode.Parse(time.Output!)!; Assert.NotNull(timeJson["source"]); Assert.NotNull(timeJson["complete"]); var reboot = await new LinuxRebootPendingTool().ExecuteAsync(bOps.Abstractions.ToolArguments.Empty); Assert.True(reboot.Succeeded); var rebootJson = JsonNode.Parse(reboot.Output!)!; Assert.NotNull(rebootJson["source"]); Assert.NotNull(rebootJson["complete"]); }

    [Fact] public async Task RebootMarkerExistsAndAbsentAreKnownOnUbuntu()
    { var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); try { var os = Path.Combine(dir, "os-release"); await File.WriteAllTextAsync(os, "ID=ubuntu\n"); var marker = Path.Combine(dir, "marker"); var absent = await LinuxRebootDetector.Probe(os, [marker], CancellationToken.None); Assert.True(absent.Complete); Assert.False(absent.Pending); await File.WriteAllTextAsync(marker, ""); var present = await LinuxRebootDetector.Probe(os, [marker], CancellationToken.None); Assert.True(present.Complete); Assert.True(present.Pending); } finally { Directory.Delete(dir, true); } }
    [Fact] public async Task RebootUnsupportedAndUnavailableEvidenceRemainIncomplete()
    { var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); try { var os = Path.Combine(dir, "os-release"); await File.WriteAllTextAsync(os, "ID=arch\n"); var unsupported = await LinuxRebootDetector.Probe(os, [], CancellationToken.None); Assert.False(unsupported.Complete); Assert.False(unsupported.Pending); var missing = await LinuxRebootDetector.Probe(Path.Combine(dir, "missing"), [], CancellationToken.None); Assert.False(missing.Complete); } finally { Directory.Delete(dir, true); } }
    [Fact] public void RebootReadErrorIsNotMarkerAbsence() { var result = LinuxRebootDetector.EvaluateMarkers([null]); Assert.False(result.Complete); Assert.False(result.Pending); }
    [LinuxOnlyFact]
    public Task SystemInfo_Conforms() =>
        SystemToolConformance.AssertSystemInfoConformsAsync(new LinuxSystemInfoTool(), "linux");

    [LinuxOnlyFact]
    public Task Applications_Conform() =>
        SystemToolConformance.AssertApplicationsConformAsync(new LinuxApplicationInventoryTool(), "linux");

    [LinuxOnlyFact]
    public Task Devices_Conform() =>
        SystemToolConformance.AssertDevicesConformAsync(new LinuxDeviceInventoryTool(), "linux");

    [LinuxOnlyFact]
    public Task Cpu_Conforms() =>
        SystemToolConformance.AssertCpuUsageConformsAsync(new LinuxCpuUsageTool(), "linux");

    [LinuxOnlyFact]
    public Task Memory_Conforms() =>
        SystemToolConformance.AssertMemoryUsageConformsAsync(new LinuxMemoryUsageTool(), "linux");

    [LinuxOnlyFact]
    public Task Disk_Conforms() =>
        SystemToolConformance.AssertDiskUsageConformsAsync(new LinuxDiskUsageTool(), "linux");

    [LinuxOnlyFact]
    public Task ProcessList_Conforms() =>
        SystemToolConformance.AssertProcessListConformsAsync(new LinuxProcessListTool(), "linux");

    [LinuxOnlyFact]
    public Task ProcessList_RespectsLimit() =>
        SystemToolConformance.AssertProcessListRespectsLimitAsync(new LinuxProcessListTool());

    [LinuxOnlyFact]
    public Task Swap_Conforms() =>
        SystemToolConformance.AssertSwapUsageConformsAsync(new LinuxSwapUsageTool(), "linux");

    [LinuxOnlyFact]
    public Task Io_Conforms() =>
        SystemToolConformance.AssertIoUsageConformsAsync(new LinuxIoUsageTool(), "linux");

    [LinuxOnlyFact]
    public Task ProcessInspect_Conforms() =>
        SystemToolConformance.AssertProcessInspectConformsAsync(new LinuxProcessInspectTool(), "linux");

    [LinuxOnlyFact]
    public Task ProcessInspect_ReportsMissing_ForAnUnlikelyPid() =>
        SystemToolConformance.AssertProcessInspectReportsMissingAsync(new LinuxProcessInspectTool());

    [Fact]
    public void ToolProvider_ContributesExactlyTheTwentyTwoSystemAndProcessTools()
    {
        var names = new LinuxSystemToolProvider().GetTools().Select(t => t.Manifest.Name).ToList();

        Assert.Equal(
            [
                "system.info", "system.time", "system.reboot_pending", "system.apps", "system.devices", "system.events", "system.updates", "system.update_history", "system.crashes", "system.drivers", "system.cpu", "system.memory", "system.disk", "process.list",
                "system.swap", "system.io", "process.inspect", "process.metrics", "process.tree", "process.modules",
                "process.stop", "process.kill",
            ],
            names);
    }
}
