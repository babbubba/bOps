// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Sys.Conformance;
using bOps.Packages.Sys.Core;
using bOps.Packages.Sys.Linux;

namespace bOps.Packages.System.Linux.Tests;

/// <summary>HARDEN-7 <c>system.stability</c> on Linux (ADR-0041 §3.2): fixed kernel-message rules, scoped coverage and honest applicability.</summary>
public sealed class LinuxStabilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Kernel panic - not syncing: Fatal exception in interrupt", StabilityCategory.KernelCrash, "panic", null, null)]
    [InlineData("Oops: 0002 [#1] PREEMPT SMP NOPTI", StabilityCategory.KernelFault, "oops", null, null)]
    [InlineData("BUG: unable to handle page fault for address: ffff8881", StabilityCategory.KernelFault, "bug", null, null)]
    [InlineData("kernel BUG at mm/slub.c:4024!", StabilityCategory.KernelFault, "bug", null, null)]
    [InlineData("general protection fault, probably for non-canonical address 0xdead000000000100", StabilityCategory.KernelFault, "general-protection-fault", null, null)]
    [InlineData("mce: [Hardware Error]: Machine check events logged", StabilityCategory.HardwareError, "mce", null, "corrected")]
    [InlineData("mce: [Hardware Error]: CPU 2: Machine Check: 0 Bank 5: be00000000800400", StabilityCategory.HardwareError, "mce", null, "unknown")]
    [InlineData("EDAC MC0: 1 CE memory read error on CPU_SrcID#0_Ha#0_Chan#1_DIMM#0", StabilityCategory.HardwareError, "edac", null, "corrected")]
    [InlineData("EDAC MC0: 1 UE memory read error on CPU_SrcID#0_Ha#0_Chan#1_DIMM#0", StabilityCategory.HardwareError, "edac", null, "uncorrected")]
    [InlineData("Out of memory: Killed process 4242 (java) total-vm:8123456kB, anon-rss:4000000kB", StabilityCategory.MemoryExhaustion, "oom-kill", "java", null)]
    [InlineData("Memory cgroup out of memory: Killed process 77 (node) total-vm:100kB", StabilityCategory.MemoryExhaustion, "cgroup-oom-kill", "node", null)]
    public void TheFixedRules_TypeKernelMessages(string message, StabilityCategory category, string code, string? component, string? severity)
    {
        var evidence = LinuxStabilityTool.Classify(message, Now);

        Assert.NotNull(evidence);
        Assert.Equal((category, "kernel", (string?)null, code, component, severity, EvidenceTimestampKind.Occurred),
            (evidence!.Category, evidence.Provider, evidence.EventId, evidence.Code, evidence.Component, evidence.SeverityClass, evidence.TimestampKind));
    }

    [Theory]
    [InlineData("usb 1-1: new high-speed USB device number 2")]
    [InlineData("oops: lower-case is not the kernel's Oops")]
    [InlineData("A Kernel panic - not syncing in the middle is not a prefix")]
    [InlineData("EDAC MC0: memory controller registered")]
    [InlineData("nvidia-modeset: ERROR: GPU:0: Idling display engine timed out")]
    public void MessagesMatchingNoRule_AreNotEvidence(string message) =>
        Assert.Null(LinuxStabilityTool.Classify(message, Now));

    [Fact]
    public void AnOomKillWithoutAProcessName_KeepsTheEvidence_WithANullComponent()
    {
        var evidence = LinuxStabilityTool.Classify("Out of memory: Killed process 4242 (" + new string('x', 65) + ")", Now);

        Assert.Equal("oom-kill", evidence!.Code);
        Assert.Null(evidence.Component);
    }

    [Fact]
    public void TheRead_IsOneFixedJournalctlInvocationOfTheKernelTransport()
    {
        var arguments = LinuxStabilityTool.Arguments(new StabilityQuery(30, Now.AddDays(-30), Now, 50));

        Assert.Equal(
            ["--no-pager", "--output=json", "--reverse", "--utc", "--since=2026-09-02 12:00:00 UTC", "--until=2026-10-02 12:00:00 UTC", "--lines=10000", "--output-fields=MESSAGE,_TRANSPORT", "_TRANSPORT=kernel"],
            arguments);
    }

    [Fact]
    public async Task TheCoverageProbe_UsesTheKernelScope_SoABroaderJournalCannotMakeCoverageLookComplete()
    {
        // The whole journal reaches back 400 days, the kernel transport only 10: coverage must follow the kernel scope (review note R6).
        var journal = new FakeJournal(Now.AddDays(-400), new Dictionary<string, DateTimeOffset?> { ["_TRANSPORT=kernel"] = Now.AddDays(-10) });

        var json = await RunAsync(new LinuxStabilityTool(new FixedClock(Now), journal.Run));

        var probe = Assert.Single(journal.Probes);
        Assert.Equal(["--no-pager", "--output=json", "--utc", "--output-fields=_TRANSPORT", "_TRANSPORT=kernel"], probe);
        Assert.Equal("partial", json["coverage"]!["state"]!.GetValue<string>());
        var store = Assert.Single(json["coverage"]!["stores"]!.AsArray())!;
        Assert.Equal(("linux.journald", "journal", EvidenceTime.Format(Now.AddDays(-10))), (store["name"]!.GetValue<string>(), store["basis"]!.GetValue<string>(), store["oldestAvailableUtc"]!.GetValue<string>()));
        Assert.Null(store["logMaximumBytes"]);
        Assert.False(json["complete"]!.GetValue<bool>());
    }

    [Fact]
    public async Task KernelEvidence_IsCountedPerCategory_WithEveryCategoryListedAndHonestApplicability()
    {
        var journal = new FakeJournal(Now.AddDays(-400))
        {
            Lines =
            [
                FakeJournal.Line(Now.AddDays(-1), "Out of memory: Killed process 4242 (java) total-vm:1kB"),
                FakeJournal.Line(Now.AddDays(-2), "Out of memory: Killed process 4243 (java) total-vm:1kB"),
                FakeJournal.Line(Now.AddDays(-3), "mce: [Hardware Error]: Machine check events logged"),
                FakeJournal.Line(Now.AddDays(-4), "usb 1-1: new device"),
                FakeJournal.Line(Now.AddDays(-5), "Oops: 0002 [#1] SMP"),
            ],
        };

        var json = await RunAsync(new LinuxStabilityTool(new FixedClock(Now), journal.Run));

        Assert.True(json["complete"]!.GetValue<bool>());
        Assert.Equal(("applicable", "available", 2), Category(json, "memoryExhaustion"));
        Assert.Equal(("applicable", "available", 1), Category(json, "hardwareError"));
        Assert.Equal(("applicable", "available", 1), Category(json, "kernelFault"));
        Assert.Equal(("applicable", "available", 0), Category(json, "kernelCrash"));
        Assert.Equal(("notApplicable", null, null), Category(json, "unexpectedShutdown"));
        Assert.Equal(("notCollected", null, null), Category(json, "displayFault"));
        Assert.Equal(("notCollected", null, null), Category(json, "storageError"));
        Assert.Equal(("notCollected", null, null), Category(json, "minidump"));
        Assert.Contains("persistent journal", json["categories"]!.AsArray().Single(c => c!["category"]!.GetValue<string>() == "kernelCrash")!["detail"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("notCollected", json["context"]!["applicability"]!.GetValue<string>());
        Assert.Null(json["context"]!["boots"]);
        Assert.Equal("notCollected", json["minidumps"]!["applicability"]!.GetValue<string>());
        Assert.Null(json["minidumps"]!["observed"]);
        var oom = json["groups"]!.AsArray().Single(group => group!["category"]!.GetValue<string>() == "memoryExhaustion")!;
        Assert.Equal(("kernel", "oom-kill", "java", 2), (oom["provider"]!.GetValue<string>(), oom["code"]!.GetValue<string>(), oom["component"]!.GetValue<string>(), oom["count"]!.GetValue<int>()));
        Assert.Null(oom["eventId"]);
        Assert.Equal("day", json["bucket"]!["width"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnIdentityThatCannotSeeTheSystemJournal_IsPartial_AndEveryCountIsALowerBound()
    {
        var journal = new FakeJournal(Now.AddDays(-400))
        {
            Lines = [FakeJournal.Line(Now.AddDays(-1), "Oops: 0002 [#1] SMP")],
            StandardError = "Hint: You are currently not seeing messages from other users and the system.",
        };

        var result = await new LinuxStabilityTool(new FixedClock(Now), journal.Run).ExecuteAsync(ToolArguments.Empty);
        var json = SystemToolConformance.AssertStabilityEnvelope(result.Output!, result.Completeness);

        Assert.Equal("partial", json["status"]!.GetValue<string>());
        Assert.Equal(("applicable", "partial", 1), Category(json, "kernelFault"));
        Assert.Equal(("applicable", "partial", 0), Category(json, "kernelCrash"));
        Assert.Equal(ToolResultCompleteness.Partial, result.Completeness);
    }

    [Fact]
    public async Task AJournalctlThatFails_IsUnavailable_AndEveryApplicableCountIsNull_NeverZero()
    {
        var journal = new FakeJournal(Now.AddDays(-400)) { ExitCode = 1, StandardError = "Failed to open journal: Permission denied" };

        var result = await new LinuxStabilityTool(new FixedClock(Now), journal.Run).ExecuteAsync(ToolArguments.Empty);
        var json = SystemToolConformance.AssertStabilityEnvelope(result.Output!, result.Completeness);

        Assert.Equal("unavailable", json["status"]!.GetValue<string>());
        foreach (var category in new[] { "kernelCrash", "kernelFault", "hardwareError", "memoryExhaustion" })
        {
            Assert.Equal(("applicable", "unavailable", null), Category(json, category));
        }

        Assert.Empty(json["timeline"]!.AsArray());
        Assert.Equal(ToolResultCompleteness.Unavailable, result.Completeness);
    }

    [Fact]
    public async Task AMissingJournalctl_IsUnavailable_AndItsCoverageUnknown()
    {
        var journal = new FakeJournal(null) { Started = false };

        var result = await new LinuxStabilityTool(new FixedClock(Now), journal.Run).ExecuteAsync(ToolArguments.Empty);
        var json = SystemToolConformance.AssertStabilityEnvelope(result.Output!, result.Completeness);

        Assert.Equal("unknown", json["coverage"]!["state"]!.GetValue<string>());
        Assert.Contains("not installed", json["sources"]![0]!["detail"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(ToolResultCompleteness.Unavailable, result.Completeness);
    }

    [Fact]
    public async Task TheRecordCeiling_StopsTheRead_AsPartialAndTruncated_WithTheReachedInstant()
    {
        var lines = Enumerable.Range(0, StabilityLimits.JournalRecordCeiling + 5)
            .Select(index => FakeJournal.Line(Now.AddMinutes(-1 - index), "Out of memory: Killed process 1 (a)"))
            .ToArray();
        var journal = new FakeJournal(Now.AddDays(-400)) { Lines = lines };

        var json = await RunAsync(new LinuxStabilityTool(new FixedClock(Now), journal.Run));

        Assert.True(json["truncated"]!.GetValue<bool>());
        Assert.Equal(("applicable", "partial", StabilityLimits.JournalRecordCeiling), Category(json, "memoryExhaustion"));
        Assert.Equal(EvidenceTime.Format(Now.AddMinutes(-StabilityLimits.JournalRecordCeiling)), json["sources"]![0]!["examinedFromUtc"]!.GetValue<string>());
    }

    [Fact]
    public void TheManifest_IsTheSharedStabilityContract()
    {
        SystemToolConformance.AssertStabilityManifest(new LinuxStabilityTool().Manifest, "linux");
        Assert.Equal(SystemToolManifests.Stability("windows").Parameters, new LinuxStabilityTool().Manifest.Parameters);
        Assert.Equal(SystemToolManifests.Stability("windows").Description, new LinuxStabilityTool().Manifest.Description);
    }

    [Fact]
    public async Task TheEventsProbe_UsesTheRequestedTransportScope_OrTheWholeJournalItReads()
    {
        var journal = new FakeJournal(Now.AddDays(-400));
        var tool = new LinuxSystemEventsTool("/nonexistent/journalctl", new FixedClock(Now), TimeSpan.FromSeconds(5), journal.Run);

        await tool.ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["channel"] = "kernel" }));
        await tool.ExecuteAsync(ToolArguments.Empty);

        Assert.Equal(
            [
                ["--no-pager", "--output=json", "--utc", "--output-fields=_TRANSPORT", "_TRANSPORT=kernel"],
                ["--no-pager", "--output=json", "--utc", "--output-fields=_TRANSPORT"],
            ],
            journal.Probes);
    }

    [LinuxOnlyFact]
    public async Task RealLinux_StabilityConformsToTheSharedEnvelope()
    {
        var result = await new LinuxStabilityTool().ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["windowDays"] = 180 }));

        Assert.True(result.Succeeded, result.ErrorMessage);
        var json = SystemToolConformance.AssertStabilityEnvelope(result.Output!, result.Completeness);
        Assert.Equal("linux.journald.kernel", json["sources"]![0]!["name"]!.GetValue<string>());
    }

    private static (string Applicability, string? Status, int? Count) Category(JsonObject json, string name)
    {
        var category = json["categories"]!.AsArray().Single(item => item!["category"]!.GetValue<string>() == name)!;
        return (category["applicability"]!.GetValue<string>(), category["status"]?.GetValue<string>(), category["count"]?.GetValue<int>());
    }

    private static async Task<JsonObject> RunAsync(LinuxStabilityTool tool)
    {
        var result = await tool.ExecuteAsync(ToolArguments.Empty);
        Assert.True(result.Succeeded, result.ErrorMessage);
        return SystemToolConformance.AssertStabilityEnvelope(result.Output!, result.Completeness);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
