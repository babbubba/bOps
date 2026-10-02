// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Sys.Conformance;
using bOps.Packages.Sys.Core;
using bOps.Packages.Sys.Windows;

namespace bOps.Packages.System.Windows.Tests;

/// <summary>HARDEN-7 <c>system.stability</c> on Windows (ADR-0041 §3.1): typed classification of the fixed tuples, the minidump inventory and honest counts.</summary>
public sealed class WindowsStabilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    // ---- classification of the fixed tuples ----

    [Fact]
    public void KernelPower41_IsAnUnexpectedShutdown_ReportedAtTheNextBoot_WithItsBugcheckCodeAsCanonicalHex()
    {
        var plain = Classify(WindowsStabilityTool.UnexpectedShutdownSource, WerGoldenFixtures.KernelPower41, "System");
        var bugcheck = Classify(WindowsStabilityTool.UnexpectedShutdownSource, WerGoldenFixtures.KernelPower41Bugcheck, "System");

        Assert.Equal((StabilityCategory.UnexpectedShutdown, "Microsoft-Windows-Kernel-Power", "41", "0x0", (string?)null, EvidenceTimestampKind.Reported),
            (plain.Category, plain.Provider, plain.EventId, plain.Code, plain.Component, plain.TimestampKind));
        Assert.Equal("0x1000007f", bugcheck.Code);
        Assert.Equal("powerButton", bugcheck.Component);
    }

    [Fact]
    public void EventLog6008_IsAnUnexpectedShutdown_WhoseLocalizedTimeTextIsNeverParsed()
    {
        var evidence = Classify(WindowsStabilityTool.UnexpectedShutdownSource, WerGoldenFixtures.EventLog6008, "System");

        Assert.Equal((StabilityCategory.UnexpectedShutdown, "EventLog", "6008", (string?)null, EvidenceTimestampKind.Reported),
            (evidence.Category, evidence.Provider, evidence.EventId, evidence.Code, evidence.TimestampKind));
        Assert.Equal(DateTimeOffset.Parse("2026-09-30T18:42:14Z"), evidence.TimestampUtc);
    }

    [Fact]
    public void TheSystemLogBugcheckRecord_IsAKernelCrash_WithTheLeadingCodeOfParam1()
    {
        var evidence = Classify(WindowsStabilityTool.KernelCrashSource, WerGoldenFixtures.SystemBugcheck1001, "System");

        Assert.Equal((StabilityCategory.KernelCrash, "0x50", EvidenceTimestampKind.Reported), (evidence.Category, evidence.Code, evidence.TimestampKind));
    }

    [Fact]
    public void Whea_IsClassifiedConservatively_CorrectedOnlyAtWarningLevel_UncorrectedOnlyAtErrorOrCritical_ElseUnknown()
    {
        var corrected = Classify(WindowsStabilityTool.HardwareErrorSource, WerGoldenFixtures.Whea19Corrected, "System");
        var fatal = Classify(WindowsStabilityTool.HardwareErrorSource, WerGoldenFixtures.Whea18Fatal, "System");
        var informational = Classify(WindowsStabilityTool.HardwareErrorSource, WerGoldenFixtures.WheaInformational, "System");

        Assert.Equal((StabilitySeverityClasses.Corrected, EvidenceTimestampKind.Occurred, "19"), (corrected.SeverityClass, corrected.TimestampKind, corrected.EventId));
        Assert.Equal((StabilitySeverityClasses.Uncorrected, EvidenceTimestampKind.Reported, "18"), (fatal.SeverityClass, fatal.TimestampKind, fatal.EventId));
        Assert.Equal((StabilitySeverityClasses.Unknown, EvidenceTimestampKind.Reported), (informational.SeverityClass, informational.TimestampKind));
    }

    [Fact]
    public void Display4101_IsADisplayFault_NamingTheDriver()
    {
        var evidence = Classify(WindowsStabilityTool.DisplayFaultSystemSource, WerGoldenFixtures.Display4101, "System");

        Assert.Equal((StabilityCategory.DisplayFault, "Display", "4101", "nvlddmkm", EvidenceTimestampKind.Occurred),
            (evidence.Category, evidence.Provider, evidence.EventId, evidence.Component, evidence.TimestampKind));
    }

    [Theory]
    [InlineData("117", "0x117")]
    [InlineData("141", "0x141")]
    [InlineData("193", "0x193")]
    public void LiveKernelEvents117_141_193_AreDisplayFaults_ReportedTimes(string p1, string code)
    {
        var item = WindowsEventLogEvidence.ParseXml(WerGoldenFixtures.Wer1001LiveKernel(p1, Guid.NewGuid().ToString(), Now.AddDays(-1)).Trim(), "Application");

        var classified = WindowsStabilityTool.Classify(WindowsStabilityTool.DisplayFaultWerSource, item);

        Assert.NotNull(classified);
        Assert.Equal((StabilityCategory.DisplayFault, "Windows Error Reporting", "1001", code, EvidenceTimestampKind.Reported),
            (classified!.Evidence.Category, classified.Evidence.Provider, classified.Evidence.EventId, classified.Evidence.Code, classified.Evidence.TimestampKind));
    }

    [Fact]
    public void LiveKernelEvent1a1_IsNotDisplayEvidence_NorIsABugcheckOrAnApplicationCrash()
    {
        Assert.Null(WindowsStabilityTool.Classify(WindowsStabilityTool.DisplayFaultWerSource, WindowsCrashEvidenceTests.Item(WerGoldenFixtures.Wer1001LiveKernel1a1)));
        Assert.Null(WindowsStabilityTool.Classify(WindowsStabilityTool.DisplayFaultWerSource, WindowsCrashEvidenceTests.Item(WerGoldenFixtures.Wer1001BlueScreen50)));
        Assert.Null(WindowsStabilityTool.Classify(WindowsStabilityTool.DisplayFaultWerSource, WindowsCrashEvidenceTests.Item(WerGoldenFixtures.Wer1001AppCrash)));
        Assert.DoesNotContain("0x1a1", WindowsStabilityTool.DisplayLiveDumpCodes);
    }

    [Fact]
    public void StorageTuples_AreStorageErrors_NamingTheDevice()
    {
        var disk = Classify(WindowsStabilityTool.StorageErrorSource, WerGoldenFixtures.Disk153, "System");
        var nvme = Classify(WindowsStabilityTool.StorageErrorSource, WerGoldenFixtures.Stornvme129, "System");

        Assert.Equal(("disk", "153", @"\Device\Harddisk1\DR1"), (disk.Provider, disk.EventId, disk.Component));
        Assert.Equal(("stornvme", "129", @"\Device\RaidPort0"), (nvme.Provider, nvme.EventId, nvme.Component));
        Assert.Null(WindowsStabilityTool.Classify(WindowsStabilityTool.StorageErrorSource, WindowsEventLogEvidence.ParseXml(
            WerGoldenFixtures.Disk153.Replace("<EventID Qualifiers='32772'>153</EventID>", "<EventID Qualifiers='32772'>154</EventID>", StringComparison.Ordinal).Trim(), "System")));
    }

    [Fact]
    public void EverySourceQuery_IsBuiltOnlyFromPackageConstantsAndTheWindow()
    {
        foreach (var source in WindowsStabilityTool.EventSources)
        {
            var xpath = WindowsEventLogEvidence.WindowXPath(source.Tuples.Select(tuple => WindowsEventLogEvidence.ProviderClause(tuple.Provider, tuple.EventIds)), Now.AddDays(-30), Now);
            Assert.StartsWith("*[System[(", xpath, StringComparison.Ordinal);
            Assert.Contains("TimeCreated[@SystemTime>='2026-09-02T12:00:00.000Z' and @SystemTime<='2026-10-02T12:00:00.000Z']", xpath, StringComparison.Ordinal);
        }

        Assert.Equal(8, WindowsStabilityTool.EventSources.Count + 1); // seven Event Log sources and the minidump directory
    }

    // ---- the whole tool, with scripted sources ----

    [Fact]
    public async Task TheIncidentDataset_CountsDisplayFaultsFor0x193Only_AndNever0x1a1()
    {
        var wer = new List<EventLogItem>();
        foreach (var (code, count) in new[] { ("193", 76), ("1a1", 3) })
        {
            for (var index = 0; index < count; index++)
            {
                wer.Add(WindowsEventLogEvidence.ParseXml(WerGoldenFixtures.Wer1001LiveKernel(code, Guid.NewGuid().ToString(), Now.AddDays(-7).AddSeconds(index), wer.Count + 1).Trim(), "Application"));
            }
        }

        foreach (var code in new[] { "1e", "7f", "101", "3b", "50" })
        {
            wer.Add(WindowsEventLogEvidence.ParseXml(WerGoldenFixtures.Wer1001BlueScreen50.Replace("<Data Name='P1'>50</Data>", $"<Data Name='P1'>{code}</Data>", StringComparison.Ordinal)
                .Replace("07e3c759-e445-4da7-8efa-2694d7dea04c", Guid.NewGuid().ToString(), StringComparison.Ordinal).Trim(), "Application"));
        }

        var json = await RunAsync(Scripted(new Dictionary<string, IReadOnlyList<EventLogItem>> { [WindowsStabilityTool.DisplayFaultWerSource] = wer }));

        var display = Category(json, "displayFault");
        Assert.Equal(76, display["count"]!.GetValue<int>());
        var groups = json["groups"]!.AsArray().Where(group => group!["category"]!.GetValue<string>() == "displayFault").ToArray();
        var group = Assert.Single(groups);
        Assert.Equal(("0x193", 76, "reported"), (group!["code"]!.GetValue<string>(), group["count"]!.GetValue<int>(), group["timestampKind"]!.GetValue<string>()));
        Assert.DoesNotContain(json["groups"]!.AsArray(), item => item!["code"]?.GetValue<string>() == "0x1a1");
        Assert.Equal(76, json["groups"]!.AsArray().Where(item => item!["category"]!.GetValue<string>() != "minidump").Sum(item => item!["count"]!.GetValue<int>()));
        Assert.Equal("notCollected", Category(json, "kernelFault")["applicability"]!.GetValue<string>());
        Assert.Null(Category(json, "kernelFault")["count"]);
    }

    [Fact]
    public async Task RepeatedWerDeliveriesOfOneDisplayReport_CountOnce()
    {
        var reportId = Guid.NewGuid().ToString();
        var burst = Enumerable.Range(0, 10)
            .Select(index => WindowsEventLogEvidence.ParseXml(WerGoldenFixtures.Wer1001LiveKernel("193", reportId, Now.AddDays(-index), index + 1).Trim(), "Application"))
            .ToArray();

        var json = await RunAsync(Scripted(new Dictionary<string, IReadOnlyList<EventLogItem>> { [WindowsStabilityTool.DisplayFaultWerSource] = burst }));

        Assert.Equal(1, Category(json, "displayFault")["count"]!.GetValue<int>());
    }

    [Fact]
    public async Task AnUnreadableMinidumpDirectory_IsUnavailable_WithNullCounters_AndTheResultIsPartial()
    {
        var tool = Scripted(new Dictionary<string, IReadOnlyList<EventLogItem>>(), minidumps: (_, _, _) => new WindowsStabilityTool.MinidumpRead([], new InventorySourceResult(WindowsStabilityTool.MinidumpSource, InventorySourceStatus.Unavailable, "The minidump directory is not readable by this identity; it usually requires elevation."), false));
        var result = await tool.ExecuteAsync(ToolArguments.Empty);
        var json = JsonNode.Parse(result.Output!)!.AsObject();

        var minidump = Category(json, "minidump");
        Assert.Equal(("applicable", "unavailable"), (minidump["applicability"]!.GetValue<string>(), minidump["status"]!.GetValue<string>()));
        Assert.Null(minidump["count"]);
        var inventory = json["minidumps"]!;
        Assert.Equal("unavailable", inventory["status"]!.GetValue<string>());
        Assert.Null(inventory["observed"]);
        Assert.Null(inventory["returned"]);
        Assert.Null(inventory["totalBytes"]);
        Assert.Empty(inventory["files"]!.AsArray());
        Assert.Equal("partial", json["status"]!.GetValue<string>());
        Assert.Equal(ToolResultCompleteness.Partial, result.Completeness);
        var source = json["sources"]!.AsArray().Single(item => item!["name"]!.GetValue<string>() == "windows.minidump")!;
        Assert.Contains("requires elevation", source["detail"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingMinidumpDirectory_IsNotApplicable_WithZeroCounters_NotUnknown()
    {
        var missing = Path.Combine(Path.GetTempPath(), "bops-h7-no-minidump-" + Guid.NewGuid().ToString("N"));
        var json = await RunAsync(Scripted(new Dictionary<string, IReadOnlyList<EventLogItem>>(), minidumpDirectory: missing));

        var minidump = Category(json, "minidump");
        Assert.Equal(("applicable", "notApplicable", 0), (minidump["applicability"]!.GetValue<string>(), minidump["status"]!.GetValue<string>(), minidump["count"]!.GetValue<int>()));
        Assert.Equal(0, json["minidumps"]!["observed"]!.GetValue<int>());
        Assert.Equal(0, json["minidumps"]!["totalBytes"]!.GetValue<long>());
        Assert.DoesNotContain(json["coverage"]!["stores"]!.AsArray(), store => store!["name"]!.GetValue<string>() == "windows.minidump");
    }

    [Fact]
    public async Task TheMinidumpInventory_ReadsNamesSizesAndTimesOnly_WithADescriptiveFileNameDate()
    {
        var directory = Directory.CreateTempSubdirectory("bops-h7-minidump-").FullName;
        try
        {
            var inWindow = Path.Combine(directory, "090426-53171-01.dmp");
            await File.WriteAllBytesAsync(inWindow, new byte[1234]);
            File.SetLastWriteTimeUtc(inWindow, Now.AddDays(-2).UtcDateTime);
            var old = Path.Combine(directory, "010126-1-01.dmp");
            await File.WriteAllBytesAsync(old, new byte[10]);
            File.SetLastWriteTimeUtc(old, Now.AddDays(-300).UtcDateTime);
            var invalidDate = Path.Combine(directory, "133126-1-01.dmp");
            await File.WriteAllBytesAsync(invalidDate, new byte[20]);
            File.SetLastWriteTimeUtc(invalidDate, Now.AddDays(-1).UtcDateTime);

            var json = await RunAsync(Scripted(new Dictionary<string, IReadOnlyList<EventLogItem>>(), minidumpDirectory: directory));

            var inventory = json["minidumps"]!;
            Assert.Equal("available", inventory["status"]!.GetValue<string>());
            Assert.Equal(2, inventory["observed"]!.GetValue<int>());
            Assert.Equal(1254, inventory["totalBytes"]!.GetValue<long>());
            var files = inventory["files"]!.AsArray();
            Assert.Equal(["133126-1-01.dmp", "090426-53171-01.dmp"], files.Select(file => file!["name"]!.GetValue<string>()).ToArray());
            Assert.Null(files[0]!["fileNameLocalDate"]);
            Assert.Equal("2026-09-04", files[1]!["fileNameLocalDate"]!.GetValue<string>());
            Assert.All(files, file => Assert.Equal("reported", file!["timestampKind"]!.GetValue<string>()));
            Assert.Equal(["name", "sizeBytes", "fileTimeUtc", "timestampKind", "fileNameLocalDate"], files[0]!.AsObject().Select(pair => pair.Key).ToArray());
            Assert.Equal(2, Category(json, "minidump")["count"]!.GetValue<int>());
            var store = json["coverage"]!["stores"]!.AsArray().Single(item => item!["name"]!.GetValue<string>() == "windows.minidump")!;
            Assert.Equal(("directory", "unknown"), (store["basis"]!.GetValue<string>(), store["state"]!.GetValue<string>()));
            // The file-name date is descriptive only: the minidump timeline row uses the file time, in its own bucket.
            Assert.Contains(json["timeline"]!.AsArray(), row => row!["category"]!.GetValue<string>() == "minidump" && row["timestampKind"]!.GetValue<string>() == "reported");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task EntriesThatAreNotPlainDmpFiles_AreSkipped_AndMakeTheInventoryPartial_LowerBoundCounts()
    {
        var directory = Directory.CreateTempSubdirectory("bops-h7-minidump-skip-").FullName;
        try
        {
            var dump = Path.Combine(directory, "100126-1-01.dmp");
            await File.WriteAllBytesAsync(dump, new byte[100]);
            File.SetLastWriteTimeUtc(dump, Now.AddDays(-1).UtcDateTime);
            await File.WriteAllTextAsync(Path.Combine(directory, "notes.txt"), "x");
            Directory.CreateDirectory(Path.Combine(directory, "sub.dmp"));

            var json = await RunAsync(Scripted(new Dictionary<string, IReadOnlyList<EventLogItem>>(), minidumpDirectory: directory));

            Assert.Equal("partial", json["minidumps"]!["status"]!.GetValue<string>());
            Assert.Equal(1, json["minidumps"]!["observed"]!.GetValue<int>());
            Assert.Equal("partial", Category(json, "minidump")["status"]!.GetValue<string>());
            Assert.Equal(1, Category(json, "minidump")["count"]!.GetValue<int>());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("090426-53171-01.dmp", "2026-09-04")]
    [InlineData("123125-1-1.DMP", "2025-12-31")]
    [InlineData("022926-1-01.dmp", null)]
    [InlineData("MEMORY.DMP", null)]
    [InlineData("WATCHDOG-20260730-1010.dmp", null)]
    public void TheFileNameDate_IsReadOnlyFromTheMmddyyPattern_AndOnlyWhenItIsAValidDate(string name, string? expected) =>
        Assert.Equal(expected, WindowsStabilityTool.FileNameLocalDate(name));

    [Fact]
    public async Task AnUnreadableSource_MakesItsCategoryNull_NeverZero_AndItsPartialSiblingALowerBound()
    {
        var tool = Scripted(
            new Dictionary<string, IReadOnlyList<EventLogItem>>
            {
                [WindowsStabilityTool.DisplayFaultSystemSource] = [WindowsEventLogEvidence.ParseXml(WerGoldenFixtures.Display4101.Trim(), "System")],
            },
            unavailable: [WindowsStabilityTool.UnexpectedShutdownSource, WindowsStabilityTool.DisplayFaultWerSource]);

        var json = await RunAsync(tool);

        var shutdown = Category(json, "unexpectedShutdown");
        Assert.Equal("unavailable", shutdown["status"]!.GetValue<string>());
        Assert.Null(shutdown["count"]);
        var display = Category(json, "displayFault");
        Assert.Equal("partial", display["status"]!.GetValue<string>());
        Assert.Equal(1, display["count"]!.GetValue<int>());
        Assert.DoesNotContain(json["timeline"]!.AsArray(), row => row!["category"]!.GetValue<string>() == "unexpectedShutdown");
    }

    [Fact]
    public async Task ASlowSourceIsStoppedByItsSlice_AndEverySourceAfterItStillRuns()
    {
        var clock = new FakeTimeProvider(Now);
        var read = new List<(string Channel, TimeSpan Slice)>();
        var tool = new WindowsStabilityTool(
            clock,
            () => Path.Combine(Path.GetTempPath(), "bops-h7-none-" + Guid.NewGuid().ToString("N")),
            (request, slice) =>
            {
                read.Add((request.Channel, slice.Length));
                if (read.Count == 1)
                {
                    clock.Advance(slice.Length);
                    return new EventLogScan([], InventorySourceStatus.Partial, "stopped", true, null);
                }

                return new EventLogScan([], InventorySourceStatus.Available, null, false, request.WindowFromUtc);
            },
            (channel, backed, _) => new CoverageStore("windows.channel." + channel, CoverageBasis.EventLog, Now.AddDays(-400), 20_971_520, backed));

        var result = await tool.ExecuteAsync(ToolArguments.Empty);
        var json = JsonNode.Parse(result.Output!)!.AsObject();

        Assert.Equal(WindowsStabilityTool.EventSources.Count, read.Count);
        Assert.All(read, entry => Assert.True(entry.Slice > TimeSpan.Zero));
        Assert.True(clock.GetUtcNow() - Now <= StabilityLimits.CallTimeout);
        Assert.True(json["truncated"]!.GetValue<bool>());
        Assert.Equal("partial", Category(json, "unexpectedShutdown")["status"]!.GetValue<string>());
        Assert.Equal(0, Category(json, "unexpectedShutdown")["count"]!.GetValue<int>()); // observed lower bound
        Assert.Equal(ToolResultCompleteness.Partial, result.Completeness);
    }

    [Fact]
    public async Task BootContext_CountsBootsAndCleanShutdowns_ButIsNeverInstability()
    {
        var boot = WerGoldenFixtures.EventLog6008.Replace("<EventID Qualifiers='32768'>6008</EventID>", "<EventID Qualifiers='32768'>6005</EventID>", StringComparison.Ordinal).Replace("<Level>2</Level>", "<Level>4</Level>", StringComparison.Ordinal);
        var clean = boot.Replace("6005</EventID>", "6006</EventID>", StringComparison.Ordinal).Replace("<EventRecordID>500003</EventRecordID>", "<EventRecordID>500099</EventRecordID>", StringComparison.Ordinal);
        var json = await RunAsync(Scripted(new Dictionary<string, IReadOnlyList<EventLogItem>>
        {
            [WindowsStabilityTool.BootContextSource] = [WindowsEventLogEvidence.ParseXml(boot.Trim(), "System"), WindowsEventLogEvidence.ParseXml(clean.Trim(), "System")],
        }));

        Assert.Equal((1, 1), (json["context"]!["boots"]!.GetValue<int>(), json["context"]!["cleanShutdowns"]!.GetValue<int>()));
        Assert.Equal(0, Category(json, "unexpectedShutdown")["count"]!.GetValue<int>());
        Assert.Empty(json["timeline"]!.AsArray());
    }

    [Fact]
    public async Task ARequestOlderThanTheRetainedLogs_IsPartialCoverage_AndListsEachLogsReach()
    {
        var tool = new WindowsStabilityTool(
            new FakeTimeProvider(Now),
            () => Path.Combine(Path.GetTempPath(), "bops-h7-none-" + Guid.NewGuid().ToString("N")),
            (request, _) => new EventLogScan([], InventorySourceStatus.Available, null, false, request.WindowFromUtc),
            (channel, backed, _) => new CoverageStore("windows.channel." + channel, CoverageBasis.EventLog, channel == "System" ? Now.AddDays(-62) : Now.AddDays(-30), 20_971_520, backed));

        var result = await tool.ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["windowDays"] = 180 }));
        var json = JsonNode.Parse(result.Output!)!.AsObject();

        Assert.Equal("complete", json["status"]!.GetValue<string>());
        Assert.Equal("partial", json["coverage"]!["state"]!.GetValue<string>());
        Assert.Equal(["windows.channel.Application", "windows.channel.System"], json["coverage"]!["stores"]!.AsArray().Select(store => store!["name"]!.GetValue<string>()).ToArray());
        Assert.All(json["coverage"]!["stores"]!.AsArray(), store => Assert.Equal("partial", store!["state"]!.GetValue<string>()));
        Assert.False(json["complete"]!.GetValue<bool>());
        Assert.Equal(ToolResultCompleteness.Partial, result.Completeness);
        Assert.Equal("week", json["bucket"]!["width"]!.GetValue<string>());
    }

    [Fact]
    public void TheManifest_IsTheSharedStabilityContract()
    {
        SystemToolConformance.AssertStabilityManifest(new WindowsStabilityTool().Manifest, "windows");
        Assert.Contains("system.stability", new WindowsSystemToolProvider().GetTools().Select(tool => tool.Manifest.Name));
    }

    [WindowsOnlyFact]
    public async Task RealWindows_StabilityConformsToTheSharedEnvelope()
    {
        var tool = new WindowsStabilityTool();
        var result = await tool.ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["windowDays"] = 180 }));

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.True(Encoding.UTF8.GetByteCount(result.Output!) <= StabilityLimits.OutputBytes);
        var json = SystemToolConformance.AssertStabilityEnvelope(result.Output!, result.Completeness);
        Assert.Equal("applicable", Category(json, "unexpectedShutdown")["applicability"]!.GetValue<string>());
        Assert.Equal("notCollected", Category(json, "memoryExhaustion")["applicability"]!.GetValue<string>());
        Assert.Contains(json["coverage"]!["stores"]!.AsArray(), store => store!["name"]!.GetValue<string>() == "windows.channel.System");
    }

    // ---- helpers ----

    private static StabilityEvidence Classify(string source, string xml, string channel) =>
        WindowsStabilityTool.Classify(source, WindowsEventLogEvidence.ParseXml(xml.Trim(), channel))?.Evidence
        ?? throw new InvalidOperationException("The record was not classified.");

    private static JsonObject Category(JsonObject json, string name) =>
        json["categories"]!.AsArray().Single(category => category!["category"]!.GetValue<string>() == name)!.AsObject();

    private static async Task<JsonObject> RunAsync(WindowsStabilityTool tool, JsonObject? arguments = null)
    {
        var result = await tool.ExecuteAsync(ToolArguments.FromJson(arguments ?? []));
        Assert.True(result.Succeeded, result.ErrorMessage);
        return SystemToolConformance.AssertStabilityEnvelope(result.Output!, result.Completeness);
    }

    private static WindowsStabilityTool Scripted(
        IReadOnlyDictionary<string, IReadOnlyList<EventLogItem>> records,
        string? minidumpDirectory = null,
        IReadOnlyList<string>? unavailable = null,
        Func<string, DateTimeOffset, EvidenceBudgetSlice, WindowsStabilityTool.MinidumpRead>? minidumps = null)
    {
        var bySource = WindowsStabilityTool.EventSources.ToDictionary(
            source => WindowsEventLogEvidence.WindowXPath(source.Tuples.Select(tuple => WindowsEventLogEvidence.ProviderClause(tuple.Provider, tuple.EventIds)), Now.AddDays(-30), Now),
            source => source.Name,
            StringComparer.Ordinal);
        return new WindowsStabilityTool(
            new FakeTimeProvider(Now),
            () => minidumpDirectory ?? Path.Combine(Path.GetTempPath(), "bops-h7-none-" + Guid.NewGuid().ToString("N")),
            (request, _) =>
            {
                var name = bySource.GetValueOrDefault(request.XPath) ?? WindowsStabilityTool.EventSources.First(source => request.XPath.Contains(source.Tuples[0].Provider, StringComparison.Ordinal)).Name;
                if (unavailable?.Contains(name) == true)
                {
                    return new EventLogScan([], InventorySourceStatus.Unavailable, "The current identity is not allowed to read this channel.", false, null);
                }

                return new EventLogScan(records.GetValueOrDefault(name) ?? [], InventorySourceStatus.Available, null, false, request.WindowFromUtc);
            },
            (channel, backed, _) => new CoverageStore("windows.channel." + channel, CoverageBasis.EventLog, Now.AddDays(-400), 20_971_520, backed),
            minidumps);
    }
}
