// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.System.Core.Tests;

/// <summary>
/// The parts of V1.3-C that are pure decisions rather than operating-system readings (ADR-0034):
/// the manifests both OS packages contribute, argument validation, the tree walk, the rate
/// arithmetic and the JSON shapes. They belong here, not in a platform suite, because they are
/// identical on Windows and Linux by construction — which is the whole point of rule A8.
/// </summary>
public sealed class ProcessDiagnosticsContractTests
{
    public static TheoryData<string> Platforms => ["windows", "linux"];

    [Theory]
    [MemberData(nameof(Platforms))]
    public void TheNewManifests_AreReadOnlyAndDeclareNoVerification(string platform)
    {
        // Every system.*/process.* observation tool in this codebase is RiskLevel.Read and declares
        // no VerificationSpec: verification exists to confirm an effect, and a read has none to
        // confirm. Only process.stop and process.kill, which do change the machine, declare one.
        foreach (var manifest in new[]
                 {
                     SystemToolManifests.ProcessMetrics(platform),
                     SystemToolManifests.ProcessTree(platform),
                     SystemToolManifests.ProcessModules(platform),
                     SystemToolManifests.ProcessInspect(platform),
                 })
        {
            Assert.Equal(RiskLevel.Read, manifest.Risk);
            Assert.Null(manifest.Verification);
            Assert.False(manifest.RequiresExplicitApproval);
            Assert.Equal([platform], manifest.Platforms);
            Assert.False(string.IsNullOrWhiteSpace(manifest.Description));
        }
    }

    [Fact]
    public void TheNewManifests_AreIdenticalAcrossPlatformsApartFromTheirPlatform()
    {
        AssertOnlyThePlatformDiffers(SystemToolManifests.ProcessMetrics);
        AssertOnlyThePlatformDiffers(SystemToolManifests.ProcessTree);
        AssertOnlyThePlatformDiffers(SystemToolManifests.ProcessModules);
        AssertOnlyThePlatformDiffers(SystemToolManifests.ProcessInspect);

        static void AssertOnlyThePlatformDiffers(Func<string, ToolManifest> manifest)
        {
            var windows = manifest("windows");
            var linux = manifest("linux");

            Assert.Equal(windows.Name, linux.Name);
            Assert.Equal(windows.Description, linux.Description);
            Assert.Equal(windows.Risk, linux.Risk);
            Assert.Equal(windows.Verification, linux.Verification);
            Assert.Equal(windows.Parameters, linux.Parameters);
            Assert.Equal(["windows"], windows.Platforms);
            Assert.Equal(["linux"], linux.Platforms);
        }
    }

    [Fact]
    public void TheNewManifests_HaveTheDocumentedNamesAndParameters()
    {
        Assert.Equal("process.metrics", SystemToolManifests.ProcessMetrics("linux").Name);
        Assert.Equal("process.tree", SystemToolManifests.ProcessTree("linux").Name);
        Assert.Equal("process.modules", SystemToolManifests.ProcessModules("linux").Name);

        Assert.Equal(
            ["pid", "sampleMilliseconds"],
            SystemToolManifests.ProcessMetrics("linux").Parameters.Select(parameter => parameter.Name));
        Assert.Equal(
            ["rootPid", "maxDepth", "limit"],
            SystemToolManifests.ProcessTree("linux").Parameters.Select(parameter => parameter.Name));
        Assert.Equal(
            ["pid", "limit", "maxOutputBytes"],
            SystemToolManifests.ProcessModules("linux").Parameters.Select(parameter => parameter.Name));
    }

    [Fact]
    public void NoManifest_MentionsAnEnvironmentVariable()
    {
        foreach (var manifest in new[]
                 {
                     SystemToolManifests.ProcessInspect("linux"),
                     SystemToolManifests.ProcessMetrics("linux"),
                     SystemToolManifests.ProcessTree("linux"),
                     SystemToolManifests.ProcessModules("linux"),
                 })
        {
            Assert.DoesNotContain(manifest.Parameters, parameter => parameter.Name.Contains("env", StringComparison.OrdinalIgnoreCase));
        }

        foreach (var manifest in new[]
                 {
                     SystemToolManifests.ProcessMetrics("linux"),
                     SystemToolManifests.ProcessTree("linux"),
                     SystemToolManifests.ProcessModules("linux"),
                 })
        {
            Assert.DoesNotContain("environment", manifest.Description, StringComparison.OrdinalIgnoreCase);
        }

        // process.inspect is the one that names them, and only to say it never reports them.
        Assert.Contains("Environment variables are never reported", SystemToolManifests.ProcessInspect("linux").Description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(199)]
    [InlineData(5_001)]
    [InlineData(0)]
    public void Metrics_RejectAnOutOfRangeSampleInterval(int sample)
    {
        var accepted = ProcessDiagnosticsArguments.TryReadMetrics(
            Arguments(new JsonObject { ["pid"] = 1, ["sampleMilliseconds"] = sample }), out _, out _, out var error);

        Assert.False(accepted);
        Assert.Contains("sampleMilliseconds", error!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(500)]
    [InlineData(5_000)]
    public void Metrics_AcceptTheDocumentedRange(int sample)
    {
        Assert.True(ProcessDiagnosticsArguments.TryReadMetrics(
            Arguments(new JsonObject { ["pid"] = 1, ["sampleMilliseconds"] = sample }), out _, out var read, out _));
        Assert.Equal(sample, read);
    }

    [Fact]
    public void Metrics_DefaultToFiveHundredMilliseconds()
    {
        Assert.True(ProcessDiagnosticsArguments.TryReadMetrics(Arguments(new JsonObject { ["pid"] = 7 }), out var pid, out var sample, out _));

        Assert.Equal(7, pid);
        Assert.Equal(500, sample);
    }

    [Fact]
    public void EveryPidArgument_IsRejectedBelowOne()
    {
        Assert.False(ProcessDiagnosticsArguments.TryReadPid(Arguments(new JsonObject { ["pid"] = 0 }), out _, out _));
        Assert.False(ProcessDiagnosticsArguments.TryReadPid(Arguments(new JsonObject { ["pid"] = -3 }), out _, out _));
        Assert.False(ProcessDiagnosticsArguments.TryReadTree(Arguments(new JsonObject { ["rootPid"] = 0 }), out _, out _, out _, out _));
        Assert.True(ProcessDiagnosticsArguments.TryReadPid(Arguments(new JsonObject { ["pid"] = 1 }), out _, out _));
    }

    [Fact]
    public void Tree_DefaultsToDepthFourAndTwoHundredRows()
    {
        Assert.True(ProcessDiagnosticsArguments.TryReadTree(ToolArguments.Empty, out var rootPid, out var maxDepth, out var limit, out _));

        Assert.Null(rootPid);
        Assert.Equal(4, maxDepth);
        Assert.Equal(200, limit);
    }

    [Theory]
    [InlineData("maxDepth", 17)]
    [InlineData("maxDepth", -1)]
    [InlineData("limit", 0)]
    [InlineData("limit", 2_001)]
    public void Tree_RejectsOutOfRangeBounds(string name, int value)
    {
        var accepted = ProcessDiagnosticsArguments.TryReadTree(Arguments(new JsonObject { [name] = value }), out _, out _, out _, out var error);

        Assert.False(accepted);
        Assert.Contains(name, error!, StringComparison.Ordinal);
    }

    [Fact]
    public void Modules_DefaultToTwoHundredRowsAndThirtyTwoKilobytes()
    {
        Assert.True(ProcessDiagnosticsArguments.TryReadModules(Arguments(new JsonObject { ["pid"] = 3 }), out var pid, out var limit, out var bytes, out _));

        Assert.Equal(3, pid);
        Assert.Equal(200, limit);
        Assert.Equal(32_768, bytes);
    }

    [Theory]
    [InlineData("limit", 0)]
    [InlineData("limit", 1_001)]
    [InlineData("maxOutputBytes", 4_095)]
    [InlineData("maxOutputBytes", 131_073)]
    public void Modules_RejectOutOfRangeBounds(string name, int value)
    {
        var accepted = ProcessDiagnosticsArguments.TryReadModules(
            Arguments(new JsonObject { ["pid"] = 3, [name] = value }), out _, out _, out _, out var error);

        Assert.False(accepted);
        Assert.Contains(name, error!, StringComparison.Ordinal);
    }

    [Fact]
    public void Modules_AcceptTheDocumentedMaxima()
    {
        Assert.True(ProcessDiagnosticsArguments.TryReadModules(
            Arguments(new JsonObject { ["pid"] = 3, ["limit"] = 1_000, ["maxOutputBytes"] = 131_072 }), out _, out var limit, out var bytes, out _));

        Assert.Equal(1_000, limit);
        Assert.Equal(131_072, bytes);
    }

    [Fact]
    public void TheTreeWalk_IsDepthFirstWithChildrenInPidOrder()
    {
        var snapshot = new ProcessTreeSnapshot(
        [
            new ProcessTreeEntry(1, null, "init"),
            new ProcessTreeEntry(30, 1, "b"),
            new ProcessTreeEntry(20, 1, "a"),
            new ProcessTreeEntry(40, 20, "a-child"),
        ]);

        var selection = ProcessTreeBuilder.Build(snapshot, rootPid: 1, maxDepth: 4, limit: 200);

        Assert.True(selection.RootFound);
        Assert.Equal([1, 20, 40, 30], selection.Rows.Select(row => row.Pid));
        Assert.Equal([0, 1, 2, 1], selection.Rows.Select(row => row.Depth));
        Assert.False(selection.Truncated);
    }

    [Fact]
    public void TheTreeWalk_StopsAtMaxDepthAndSaysSo()
    {
        var snapshot = new ProcessTreeSnapshot(
        [
            new ProcessTreeEntry(1, null, "init"),
            new ProcessTreeEntry(2, 1, "child"),
            new ProcessTreeEntry(3, 2, "grandchild"),
        ]);

        var shallow = ProcessTreeBuilder.Build(snapshot, rootPid: 1, maxDepth: 1, limit: 200);

        Assert.Equal([1, 2], shallow.Rows.Select(row => row.Pid));
        Assert.True(shallow.Truncated);
        Assert.False(ProcessTreeBuilder.Build(snapshot, rootPid: 1, maxDepth: 2, limit: 200).Truncated);
    }

    [Fact]
    public void TheTreeWalk_CutsAtTheRowLimitAndKeepsTheObservedCount()
    {
        var snapshot = new ProcessTreeSnapshot(
        [
            new ProcessTreeEntry(1, null, "init"),
            new ProcessTreeEntry(2, 1, "a"),
            new ProcessTreeEntry(3, 1, "b"),
        ]);

        var selection = ProcessTreeBuilder.Build(snapshot, rootPid: 1, maxDepth: 4, limit: 2);

        Assert.Equal(2, selection.Rows.Count);
        Assert.Equal(3, selection.ObservedProcesses);
        Assert.True(selection.Truncated);
    }

    [Fact]
    public void TheTreeWalk_ReportsAMissingRootRatherThanAnEmptyMachine()
    {
        var snapshot = new ProcessTreeSnapshot([new ProcessTreeEntry(1, null, "init")]);

        var selection = ProcessTreeBuilder.Build(snapshot, rootPid: 99, maxDepth: 4, limit: 200);

        Assert.False(selection.RootFound);
        Assert.Empty(selection.Rows);
    }

    [Fact]
    public void TheTreeWalk_TreatsAnOrphanAsARootAndSurvivesACycle()
    {
        // 5 -> 6 -> 5 is only reachable after PID reuse, and it must bound the walk rather than
        // recurse for ever. 9's parent was never observed, so 9 is a root of the visible forest.
        var snapshot = new ProcessTreeSnapshot(
        [
            new ProcessTreeEntry(5, 6, "a"),
            new ProcessTreeEntry(6, 5, "b"),
            new ProcessTreeEntry(9, 4_242, "orphan"),
        ]);

        var selection = ProcessTreeBuilder.Build(snapshot, rootPid: null, maxDepth: 16, limit: 200);

        Assert.Contains(9, selection.Rows.Select(row => row.Pid));
        Assert.Equal(selection.Rows.Count, selection.Rows.Select(row => row.Pid).Distinct().Count());
    }

    [Fact]
    public void TheTreeOutput_IsCompleteOnlyWhenNothingWasCutOrRefused()
    {
        var snapshot = new ProcessTreeSnapshot([new ProcessTreeEntry(1, null, "init")], Skipped: 2);
        var selection = ProcessTreeBuilder.Build(snapshot, rootPid: 1, maxDepth: 4, limit: 200);

        var json = JsonNode.Parse(ProcessDiagnosticsFormatting.FormatTree(selection, snapshot, 1, 4, new Dictionary<int, string?> { [1] = "root" }))!;

        Assert.Equal(2, json["skipped"]!.GetValue<int>());
        Assert.False(json["complete"]!.GetValue<bool>());
        Assert.Equal("root", json["processes"]![0]!["user"]!.GetValue<string>());
    }

    [Fact]
    public void Metrics_ReportAProcessThatDisappearedBetweenSamples()
    {
        var result = ProcessMetricsToolBase.Compute(
            42, 500, new ProcessSample { Exists = true }, new ProcessSample { Exists = false }, TimeSpan.FromMilliseconds(500));

        Assert.False(result.Exists);
        Assert.True(result.Partial);
        Assert.Null(result.CpuPercent);
        Assert.Null(result.ReadBytesPerSec);
    }

    [Fact]
    public void Metrics_NormalizeCpuAcrossEveryHostProcessor()
    {
        // One second of processor time over one second of wall clock is one core fully busy.
        var first = new ProcessSample { Exists = true, CpuTotal = TimeSpan.Zero };
        var second = new ProcessSample { Exists = true, CpuTotal = TimeSpan.FromSeconds(1) };

        var result = ProcessMetricsToolBase.Compute(42, 1_000, first, second, TimeSpan.FromSeconds(1));

        Assert.Equal(100.0 / Environment.ProcessorCount, result.CpuPercent!.Value, 3);
        Assert.InRange(result.CpuPercent.Value, 0, 100);
    }

    [Fact]
    public void Metrics_NeverReportANegativeRate()
    {
        var first = new ProcessSample { Exists = true, ReadBytes = 500, WriteBytes = 500, PageFaults = 500 };
        var second = new ProcessSample { Exists = true, ReadBytes = 100, WriteBytes = 100, PageFaults = 100 };

        var result = ProcessMetricsToolBase.Compute(42, 1_000, first, second, TimeSpan.FromSeconds(1));

        Assert.Equal(0, result.ReadBytesPerSec);
        Assert.Equal(0, result.WriteBytesPerSec);
        Assert.Equal(0, result.PageFaultsPerSec);
    }

    [Fact]
    public void Metrics_AreOnlyCompleteWhenEveryCounterWasReadable()
    {
        var full = Full();
        Assert.False(ProcessMetricsToolBase.Compute(42, 1_000, full, full, TimeSpan.FromSeconds(1)).Partial);

        var withoutIo = full with { ReadBytes = null };
        Assert.True(ProcessMetricsToolBase.Compute(42, 1_000, withoutIo, withoutIo, TimeSpan.FromSeconds(1)).Partial);
    }

    [Fact]
    public void TheMetricsOutput_CarriesEveryDocumentedFieldEvenWhenNull()
    {
        var json = JsonNode.Parse(ProcessDiagnosticsFormatting.Format(
            ProcessMetricsToolBase.Compute(42, 500, new ProcessSample { Exists = false }, new ProcessSample { Exists = false }, TimeSpan.FromSeconds(1))))!.AsObject();

        foreach (var field in new[]
                 {
                     "pid", "exists", "sampleMilliseconds", "cpuPercent", "workingSetMb", "privateMemoryMb",
                     "virtualMemoryMb", "threadCount", "handleOrFdCount", "readBytesPerSec", "writeBytesPerSec",
                     "pageFaultsPerSec", "partial",
                 })
        {
            Assert.True(json.ContainsKey(field), field);
        }
    }

    [Fact]
    public void TheInspectOutput_CarriesEveryAddedFieldEvenWhenNull()
    {
        var json = JsonNode.Parse(SystemToolFormatting.Format(
            new ProcessInspectResult(9, Exists: true, "svc", 12, 4, DateTimeOffset.UnixEpoch)))!.AsObject();

        foreach (var field in new[]
                 {
                     "parentPid", "executablePath", "commandLine", "user", "privateMemoryMb",
                     "virtualMemoryMb", "handleOrFdCount", "cpuTotalMs", "ioReadBytes", "ioWriteBytes",
                 })
        {
            Assert.True(json.ContainsKey(field), field);
            Assert.Null(json[field]);
        }

        // The pre-V1.3-C shape is unchanged, so an existing reader still works.
        Assert.Equal(9, json["pid"]!.GetValue<int>());
        Assert.True(json["exists"]!.GetValue<bool>());
        Assert.Equal("svc", json["name"]!.GetValue<string>());
    }

    [Fact]
    public void TheInspectOutput_BoundsALongCommandLine()
    {
        var json = JsonNode.Parse(SystemToolFormatting.Format(
            new ProcessInspectResult(9, Exists: true, "svc", 12, 4, null) { CommandLine = new string('x', 10_000) }))!;

        Assert.True(json["commandLine"]!.GetValue<string>().Length <= ProcessDiagnosticsLimits.CommandLineCharacters + 1);
    }

    [Fact]
    public void Modules_AreUniqueByPathAndOrderedByName()
    {
        var snapshot = new ProcessModuleSnapshot(Exists: true,
        [
            new ProcessModuleEntry("z.so", "/lib/z.so", "0x20", 8, null),
            new ProcessModuleEntry("a.so", "/lib/a.so", "0x10", 4, "1.0"),
            new ProcessModuleEntry("a.so", "/LIB/A.SO", "0x30", 9, null),
        ]);

        var json = JsonNode.Parse(ProcessDiagnosticsFormatting.FormatModules(snapshot, 5, 200, 32_768))!;

        Assert.Equal(2, json["observedModules"]!.GetValue<int>());
        Assert.Equal(["a.so", "z.so"], json["modules"]!.AsArray().Select(module => module!["name"]!.GetValue<string>()));
        Assert.True(json["complete"]!.GetValue<bool>());
    }

    [Fact]
    public void Modules_AreNotCompleteWhenTheReadWasRefused()
    {
        var snapshot = new ProcessModuleSnapshot(Exists: true, [], InventorySourceStatus.Unavailable, "refused");

        var json = JsonNode.Parse(ProcessDiagnosticsFormatting.FormatModules(snapshot, 5, 200, 32_768))!;

        Assert.Equal("unavailable", json["status"]!.GetValue<string>());
        Assert.Equal("refused", json["detail"]!.GetValue<string>());
        Assert.False(json["complete"]!.GetValue<bool>());
        Assert.Empty(json["modules"]!.AsArray());
    }

    [Fact]
    public void Modules_DropRowsUntilTheOutputFitsTheByteBudget()
    {
        var many = Enumerable.Range(0, 400)
            .Select(index => new ProcessModuleEntry($"m{index:D4}.so", $"/lib/very/long/path/segment/m{index:D4}.so", "0x10", 4, "1.2.3.4"))
            .ToArray();

        var output = ProcessDiagnosticsFormatting.FormatModules(new ProcessModuleSnapshot(Exists: true, many), 5, 400, 4_096);
        var json = JsonNode.Parse(output)!;

        Assert.True(Encoding.UTF8.GetByteCount(output) <= 4_096);
        Assert.True(json["returnedModules"]!.GetValue<int>() < 400);
        Assert.True(json["truncated"]!.GetValue<bool>());
        Assert.False(json["complete"]!.GetValue<bool>());
    }

    private static ProcessSample Full() => new()
    {
        Exists = true,
        CpuTotal = TimeSpan.FromSeconds(1),
        WorkingSetBytes = 1024 * 1024,
        PrivateMemoryBytes = 1024 * 1024,
        VirtualMemoryBytes = 1024 * 1024,
        ThreadCount = 3,
        HandleOrFdCount = 7,
        ReadBytes = 1,
        WriteBytes = 1,
        PageFaults = 1,
    };

    private static ToolArguments Arguments(JsonObject json) => ToolArguments.FromJson(json);
}
