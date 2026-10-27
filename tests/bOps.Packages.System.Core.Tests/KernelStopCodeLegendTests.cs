// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.System.Core.Tests;

/// <summary>
/// MAF A/B campaign (F3): models named <c>0x193</c> PAGE_FAULT_IN_NONPAGED_AREA or VIDEO_TDR_ERROR, <c>0x101</c> VIDEO_TDR_FAILURE, and
/// counted live dumps as restarts. The crash result now carries a tiny legend of the stop codes it contains, taken from the Microsoft
/// Learn bug check and kernel live dump references: the official name, and whether the code identifies a live dump (the system keeps
/// running) or a real bugcheck. Unknown codes get no entry — the legend never guesses.
/// </summary>
public sealed class KernelStopCodeLegendTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);

    private sealed class CrashesTool(CrashEvidenceSnapshot snapshot) : SystemCrashesToolBase("test", new FixedClock())
    {
        protected override Task<CrashEvidenceSnapshot> CollectAsync(CrashesQuery query, CancellationToken ct) => Task.FromResult(snapshot);
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static CrashEvidence Crash(int minutesAgo, string kind, string code) =>
        new(Now.AddMinutes(-minutesAgo), EvidenceTimestampKind.Occurred, "wer", kind)
        {
            BugcheckCode = kind == CrashKinds.KernelBugcheck ? code : null,
            LiveDumpCode = kind == CrashKinds.KernelLiveDump ? code : null,
            EvidenceSources = ["wer"],
        };

    private static async Task<JsonObject> RunAsync(string mode, params CrashEvidence[] items)
    {
        var snapshot = new CrashEvidenceSnapshot(items, [new MaintenanceSource("wer", InventorySourceStatus.Available)])
        {
            Stores = [new CoverageStore("store.wer", CoverageBasis.EventLog, Now.AddDays(-400), 20_971_520, ["wer"])],
        };
        var arguments = ToolArguments.FromJson(new JsonObject { ["mode"] = mode });
        var result = await new CrashesTool(snapshot).ExecuteAsync(arguments);
        return (JsonObject)JsonNode.Parse(result.Output!)!;
    }

    [Theory]
    [InlineData("aggregate")]
    [InlineData("raw")]
    public async Task ALiveDumpCode_IsNamedAsALiveDump_NeverARestart_AndBugchecksKeepTheirOwnNames(string mode)
    {
        var output = await RunAsync(mode,
            Crash(1, CrashKinds.KernelLiveDump, "0x193"),
            Crash(2, CrashKinds.KernelBugcheck, "0x101"),
            Crash(3, CrashKinds.KernelBugcheck, "0x116"),
            Crash(4, CrashKinds.KernelBugcheck, "0x50"),
            Crash(5, CrashKinds.KernelBugcheck, "0x124"));

        var legend = Assert.IsType<JsonObject>(output["stopCodeLegend"]);
        Assert.Equal(["0x101", "0x116", "0x124", "0x193", "0x50"], legend.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.Contains("CLOCK_WATCHDOG_TIMEOUT", legend["0x101"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("VIDEO_TDR_FAILURE", legend["0x116"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("PAGE_FAULT_IN_NONPAGED_AREA", legend["0x50"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("WHEA_UNCORRECTABLE_ERROR", legend["0x124"]!.GetValue<string>(), StringComparison.Ordinal);

        var liveDump = legend["0x193"]!.GetValue<string>();
        Assert.Contains("VIDEO_DXGKRNL_LIVEDUMP", liveDump, StringComparison.Ordinal);
        Assert.Contains("live dump", liveDump, StringComparison.Ordinal);
        Assert.Contains("not a bugcheck", liveDump, StringComparison.Ordinal);
        Assert.Contains("does not restart", liveDump, StringComparison.Ordinal);
        Assert.DoesNotContain("not a bugcheck", legend["0x101"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownCode_GetsNoEntry_AndNoKnownCodeMeansNoLegend()
    {
        var unknown = await RunAsync("aggregate", Crash(1, CrashKinds.KernelBugcheck, "0xdeadbeef"));
        Assert.False(unknown.ContainsKey("stopCodeLegend"));

        var mixed = await RunAsync("aggregate",
            Crash(1, CrashKinds.KernelBugcheck, "0xdeadbeef"), Crash(2, CrashKinds.KernelBugcheck, "0x1e"));
        var legend = Assert.IsType<JsonObject>(mixed["stopCodeLegend"]);
        Assert.Equal(["0x1e"], legend.Select(pair => pair.Key));
    }

    [Fact]
    public void TheManifests_StateWhatAKindOrAnEventDoesAndDoesNotProve()
    {
        var crashes = SystemToolManifests.Crashes("windows").Description;
        Assert.Contains("kernel-live-dump is a dump taken while the system kept running", crashes, StringComparison.Ordinal);
        Assert.Contains("never a real bugcheck", crashes, StringComparison.Ordinal);
        Assert.Contains("a cause is a hypothesis", crashes, StringComparison.Ordinal);

        var events = SystemToolManifests.Events("windows").Description;
        Assert.Contains("Kernel-Power 41 records a restart without a clean shutdown", events, StringComparison.Ordinal);
        Assert.Contains("not evidence of a failing power supply by itself", events, StringComparison.Ordinal);
        Assert.Contains("corrected or uncorrected", events, StringComparison.Ordinal);
        Assert.Contains("written by bOps itself or by its test harness", events, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCatalog_IsMinimal_CanonicalAndLookupIsCaseAndPrefixTolerant()
    {
        Assert.InRange(KernelStopCodes.Count, 1, 12);
        Assert.Equal("VIDEO_DXGKRNL_LIVEDUMP", KernelStopCodes.Lookup("0x193")!.Name);
        Assert.True(KernelStopCodes.Lookup("0x00000193")!.IsLiveDumpOnly);
        Assert.False(KernelStopCodes.Lookup("0x101")!.IsLiveDumpOnly);
        Assert.Equal("IRQL_NOT_LESS_OR_EQUAL", KernelStopCodes.Lookup("0xA")!.Name);
        Assert.Null(KernelStopCodes.Lookup(null));
        Assert.Null(KernelStopCodes.Lookup("not-a-code"));
    }
}
