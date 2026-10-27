// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.System.Core.Tests;

/// <summary>
/// ADR-0052 AF-04: the bounded, package-owned high-density <see cref="EvidenceFact"/>s of <c>system.stability</c>,
/// <c>system.crashes</c> and <c>system.dump_analyze</c>. Facts are claims of observation, never of absence: nothing is emitted for
/// a zero count, an unavailable or non-applicable category, or an analysis that produced no evidence.
/// </summary>
public sealed class HighDensityFactsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);

    // ---- system.stability ----

    [Fact]
    public async Task Stability_ObservedCategories_EmitCountsInFixedOrder_AndTheWindow()
    {
        var snapshot = Stability(
        [
            Evidence(StabilityCategory.HardwareError, 1), Evidence(StabilityCategory.HardwareError, 2), Evidence(StabilityCategory.HardwareError, 3),
            Evidence(StabilityCategory.KernelCrash, 1),
        ]);

        var result = await new StabilityTool(snapshot).ExecuteAsync(ToolArguments.Empty);

        Assert.Equal(ToolResultCompleteness.Complete, result.Completeness);
        Assert.Equal(
            [
                (StabilityFacts.CategoryCountType, "kernelCrash", 1), (StabilityFacts.CategoryCountType, "hardwareError", 3),
                (StabilityFacts.WindowDaysType, StabilityFacts.WindowDaysKey, StabilityLimits.DefaultWindowDays),
            ],
            result.Facts.Select(fact => (fact.Type, fact.Key, fact.Value.GetValue<int>())));
        Assert.All(result.Facts, fact => Assert.Equal(ToolParameterType.Integer, fact.ValueType));
        AssertBoundedAndRoundTrips(result.Facts);
    }

    [Fact]
    public async Task Stability_NeverEmitsAbsence_ZeroUnavailableNotApplicableOrNotCollected()
    {
        // hardware source is unavailable: its category has no count, so nothing may say "no hardware errors".
        var snapshot = Stability(
            [Evidence(StabilityCategory.StorageError, 1)],
            [new InventorySourceResult("hardware", InventorySourceStatus.Unavailable, "access denied")]);

        var result = await new StabilityTool(snapshot).ExecuteAsync(ToolArguments.Empty);

        Assert.Equal(ToolResultCompleteness.Partial, result.Completeness);
        var keys = result.Facts.Where(fact => fact.Type == StabilityFacts.CategoryCountType).Select(fact => fact.Key).ToArray();
        Assert.Equal(["storageError"], keys);
        Assert.DoesNotContain(result.Facts, fact => fact.Key is "hardwareError" or "kernelFault" or "memoryExhaustion" or "unexpectedShutdown");
    }

    [Fact]
    public async Task Stability_PartialCategoryWithEvidence_KeepsItsCount_UnderAPartialResult()
    {
        var snapshot = Stability(
            [Evidence(StabilityCategory.DisplayFault, 1), Evidence(StabilityCategory.DisplayFault, 2)],
            [new InventorySourceResult("display.b", InventorySourceStatus.Unavailable, "no access")]);

        var result = await new StabilityTool(snapshot).ExecuteAsync(ToolArguments.Empty);

        Assert.Equal(ToolResultCompleteness.Partial, result.Completeness);
        var display = Assert.Single(result.Facts, fact => fact.Key == "displayFault");
        Assert.Equal(2, display.Value.GetValue<int>());
    }

    [Fact]
    public async Task Stability_NothingObserved_OrEverythingUnavailable_EmitsNoFacts()
    {
        Assert.Empty((await new StabilityTool(Stability([])).ExecuteAsync(ToolArguments.Empty)).Facts);

        var names = new[] { "shutdown", "crash", "hardware", "display.a", "display.b", "storage", "dumps" };
        var down = Stability([Evidence(StabilityCategory.KernelCrash, 1)],
            [.. names.Select(name => new InventorySourceResult(name, InventorySourceStatus.Unavailable, "denied"))]);
        Assert.Empty((await new StabilityTool(down).ExecuteAsync(ToolArguments.Empty)).Facts);
    }

    [Fact]
    public async Task Stability_FactsAreOrdersOfMagnitudeSmallerThanALargeRawResult()
    {
        var many = Enumerable.Range(0, 400)
            .Select(index => Evidence(StabilityCategory.HardwareError, 1 + (index % 25), provider: "p" + index, eventId: index.ToString(global::System.Globalization.CultureInfo.InvariantCulture)))
            .ToArray();

        var result = await new StabilityTool(Stability(many)).ExecuteAsync(ToolArguments.Empty);

        Assert.Equal(400, Assert.Single(result.Facts, fact => fact.Key == "hardwareError").Value.GetValue<int>());
        Assert.True(Encoding.UTF8.GetByteCount(result.Output!) > 10 * JsonSerializer.SerializeToUtf8Bytes(result.Facts).Length);
    }

    // ---- system.crashes ----

    [Fact]
    public async Task Crashes_Observed_EmitKindCodeDumpAndGroupFacts()
    {
        var snapshot = Crashes(
            Crash(1, CrashKinds.KernelBugcheck, bugcheck: "0x124", dump: @"C:\Windows\Minidump\newest.dmp"),
            Crash(2, CrashKinds.KernelBugcheck, bugcheck: "0x124", dump: @"C:\Windows\Minidump\older.dmp"),
            Crash(3, CrashKinds.KernelBugcheck, bugcheck: "0x50"),
            Crash(4, CrashKinds.ApplicationCrash, process: "a.exe", dump: @"C:\crash\a.dmp"),
            Crash(5, CrashKinds.ApplicationCrash, process: "b.exe"));

        var result = await new CrashesTool(snapshot).ExecuteAsync(ToolArguments.Empty);

        var facts = result.Facts.ToDictionary(fact => (fact.Type, fact.Key), fact => fact.Value);
        Assert.Equal(5, facts[(SystemCrashFacts.ObservedCountType, "all")]!.GetValue<int>());
        Assert.Equal(4, facts[(SystemCrashFacts.GroupCountType, "groups")]!.GetValue<int>());
        Assert.Equal(3, facts[(SystemCrashFacts.KindCountType, CrashKinds.KernelBugcheck)]!.GetValue<int>());
        Assert.Equal(2, facts[(SystemCrashFacts.KindCountType, CrashKinds.ApplicationCrash)]!.GetValue<int>());
        Assert.Equal(2, facts[(SystemCrashFacts.KernelCodeCountType, "kind=kernel-bugcheck;code=0x124")]!.GetValue<int>());
        Assert.Equal(1, facts[(SystemCrashFacts.KernelCodeCountType, "kind=kernel-bugcheck;code=0x50")]!.GetValue<int>());
        Assert.Equal(3, facts[(SystemCrashFacts.DumpReferenceCountType, "all")]!.GetValue<int>());
        var path = Assert.Single(result.Facts, fact => fact.Type == SystemCrashFacts.KernelDumpPathType);
        Assert.Equal((ToolParameterType.Path, @"C:\Windows\Minidump\newest.dmp"), (path.ValueType, path.Value.GetValue<string>()));
        AssertBoundedAndRoundTrips(result.Facts);
    }

    [Fact]
    public async Task Crashes_RawMode_EmitsTheSameFacts_AsTheAggregate()
    {
        var snapshot = Crashes(Crash(1, CrashKinds.KernelBugcheck, bugcheck: "0x124"), Crash(2, CrashKinds.ApplicationHang, process: "a.exe"));

        var aggregate = await new CrashesTool(snapshot).ExecuteAsync(ToolArguments.Empty);
        var raw = await new CrashesTool(snapshot).ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["mode"] = "raw" }));

        Assert.Equal(JsonSerializer.Serialize(aggregate.Facts), JsonSerializer.Serialize(raw.Facts));
    }

    [Fact]
    public async Task Crashes_NoneInWindow_OrUnavailableSources_EmitNoCountFacts_OnlyTheCallMetadata()
    {
        // BOPS-0128 slice B: the call metadata (the window applied and the coverage of the stores) is stated even when nothing was
        // observed; what stays forbidden is any fact of observation, so every fact left must be of the selection or coverage types.
        var old = Crashes(Crash(60 * 24 * 365, CrashKinds.KernelBugcheck, bugcheck: "0x124"));
        var none = await new CrashesTool(old).ExecuteAsync(ToolArguments.Empty);
        Assert.All(none.Facts, fact => Assert.True(IsCallMetadata(fact), $"{fact.Type} is a fact of observation."));
        Assert.Contains(none.Facts, fact => fact.Type == "system.crashes.selection" && fact.Key == "window-minutes");
        Assert.Contains(none.Facts, fact => fact.Type == "system.crashes.coverage" && fact.Key == "state");

        var unavailable = new CrashEvidenceSnapshot([], [new MaintenanceSource("wer", InventorySourceStatus.Unavailable, "denied")]);
        var result = await new CrashesTool(unavailable).ExecuteAsync(ToolArguments.Empty);
        Assert.Equal(ToolResultCompleteness.Unavailable, result.Completeness);
        Assert.All(result.Facts, fact => Assert.True(IsCallMetadata(fact), $"{fact.Type} is a fact of observation."));
        // An unavailable result never reports its coverage as complete.
        Assert.DoesNotContain(result.Facts, fact => fact.Type == "system.crashes.coverage" && fact.Key == "state" && fact.Value.GetValue<string>() == "complete");
    }

    private static bool IsCallMetadata(EvidenceFact fact) => fact.Type is "system.crashes.selection" or "system.crashes.coverage";

    [Fact]
    public async Task Crashes_PartialSource_StillEmitsObservedCounts_UnderAPartialResult()
    {
        var snapshot = new CrashEvidenceSnapshot(
            [Crash(1, CrashKinds.KernelBugcheck, bugcheck: "0x124")],
            [new MaintenanceSource("wer", InventorySourceStatus.Available), new MaintenanceSource("minidumps", InventorySourceStatus.Partial, "unreadable")]);

        var result = await new CrashesTool(snapshot).ExecuteAsync(ToolArguments.Empty);

        Assert.Equal(ToolResultCompleteness.Partial, result.Completeness);
        Assert.Equal(1, Assert.Single(result.Facts, fact => fact.Type == SystemCrashFacts.ObservedCountType).Value.GetValue<int>());
    }

    [Fact]
    public async Task Crashes_NeverCutsAPath_AndOnlyReferencesKernelDumps()
    {
        var longPath = @"C:\Windows\Minidump\" + new string('a', DumpAnalysisLimits.PathCharacters) + ".dmp";
        var snapshot = Crashes(
            Crash(1, CrashKinds.KernelBugcheck, bugcheck: "0x124", dump: longPath),
            Crash(2, CrashKinds.ApplicationCrash, process: "a.exe", dump: @"C:\crash\app.dmp"));

        var result = await new CrashesTool(snapshot).ExecuteAsync(ToolArguments.Empty);

        Assert.DoesNotContain(result.Facts, fact => fact.Type == SystemCrashFacts.KernelDumpPathType);
        Assert.Equal(2, Assert.Single(result.Facts, fact => fact.Type == SystemCrashFacts.DumpReferenceCountType).Value.GetValue<int>());
    }

    [Fact]
    public async Task Crashes_ManyCodesAndKinds_StayWithinTheFactBounds()
    {
        var items = Enumerable.Range(0, 300)
            .Select(index => Crash(1 + (index % 50), CrashKinds.KernelBugcheck, bugcheck: "0x" + index.ToString("x", global::System.Globalization.CultureInfo.InvariantCulture),
                dump: @"C:\Windows\Minidump\" + index + ".dmp"))
            .ToArray();

        var result = await new CrashesTool(Crashes(items)).ExecuteAsync(ToolArguments.Empty);

        Assert.Equal(SystemCrashFacts.MaximumKernelCodes, result.Facts.Count(fact => fact.Type == SystemCrashFacts.KernelCodeCountType));
        AssertBoundedAndRoundTrips(result.Facts);
    }

    // ---- system.dump_analyze ----

    [Fact]
    public void DumpAnalysis_Complete_EmitsBugcheckAndDebuggerQualifiedAttribution()
    {
        var report = new DumpAnalysisReport
        {
            Status = DumpAnalysisStatus.Complete,
            Dump = new DumpFileDescription(@"C:\Windows\Minidump\a.dmp", "a.dmp", 1_000, Now, "kernel-small"),
            SymbolsStatus = "loaded",
            Bugcheck = new DumpBugcheck("0x124", "WHEA_UNCORRECTABLE_ERROR", ["0x0", "0x1", "0x2", "0x3"]),
            Attribution = new DumpDebuggerAttribution(null, "storport.sys", null, null, null, "0x124_AuthenticAMD_PROCESSOR", null, null),
        };

        var result = SystemDumpAnalyzeToolBase.ToResult(report);

        var facts = result.Facts.ToDictionary(fact => fact.Type, fact => fact);
        Assert.Equal("complete", facts[DumpAnalysisFacts.AnalysisStatusType].Value.GetValue<string>());
        Assert.Equal("0x124", facts[DumpAnalysisFacts.BugcheckCodeType].Value.GetValue<string>());
        Assert.Equal("storport.sys", facts[DumpAnalysisFacts.AttributedModuleType].Value.GetValue<string>());
        Assert.Equal("0x124_AuthenticAMD_PROCESSOR", facts[DumpAnalysisFacts.AttributedFailureBucketType].Value.GetValue<string>());
        Assert.Equal("loaded", facts[DumpAnalysisFacts.SymbolsStatusType].Value.GetValue<string>());
        Assert.Equal((ToolParameterType.Path, @"C:\Windows\Minidump\a.dmp"),
            (facts[DumpAnalysisFacts.DumpPathType].ValueType, facts[DumpAnalysisFacts.DumpPathType].Value.GetValue<string>()));
        // What the debugger attributed is qualified in the type itself; no fact claims a root cause.
        Assert.All(result.Facts.Where(fact => fact.Key is DumpAnalysisFacts.ModuleKey or DumpAnalysisFacts.FailureBucketKey),
            fact => Assert.Contains("debugger-attributed", fact.Type, StringComparison.Ordinal));
        Assert.DoesNotContain(result.Facts, fact => fact.Type.Contains("cause", StringComparison.OrdinalIgnoreCase));
        AssertBoundedAndRoundTrips(result.Facts);
    }

    [Fact]
    public void DumpAnalysis_Partial_OmitsWhatTheDebuggerDidNotPrint_AndSkipsOverlongValues()
    {
        var result = SystemDumpAnalyzeToolBase.ToResult(new DumpAnalysisReport
        {
            Status = DumpAnalysisStatus.Partial,
            Bugcheck = new DumpBugcheck(null, null, []),
            Attribution = new DumpDebuggerAttribution(null, new string('m', 500), null, null, null, null, null, null),
        });

        Assert.Equal(ToolResultCompleteness.Partial, result.Completeness);
        Assert.Equal([DumpAnalysisFacts.AnalysisStatusType], result.Facts.Select(fact => fact.Type));
        Assert.Equal("partial", result.Facts[0].Value.GetValue<string>());
    }

    [Theory]
    [InlineData(DumpAnalysisStatus.Unavailable, DumpAnalysisFailure.AccessDenied)]
    [InlineData(DumpAnalysisStatus.Unavailable, DumpAnalysisFailure.DebuggerUnavailable)]
    [InlineData(DumpAnalysisStatus.Unavailable, DumpAnalysisFailure.Timeout)]
    [InlineData(DumpAnalysisStatus.Unavailable, DumpAnalysisFailure.NotFound)]
    public void DumpAnalysis_WhenNoEvidenceWasProduced_EmitsNoFacts(DumpAnalysisStatus status, DumpAnalysisFailure failure)
    {
        var result = SystemDumpAnalyzeToolBase.ToResult(new DumpAnalysisReport
        {
            Status = status,
            Failure = failure,
            Dump = new DumpFileDescription(@"C:\Windows\Minidump\a.dmp", "a.dmp", null, null, "kernel-small"),
        });

        Assert.Empty(result.Facts);
    }

    [Fact]
    public void DumpAnalysis_InvalidDump_EmitsOnlyThePositiveStatusAndThePath()
    {
        var result = SystemDumpAnalyzeToolBase.ToResult(new DumpAnalysisReport
        {
            Status = DumpAnalysisStatus.Invalid,
            Failure = DumpAnalysisFailure.InvalidDump,
            Dump = new DumpFileDescription(@"C:\Windows\Minidump\a.dmp", "a.dmp", 10, Now, "kernel-small"),
        });

        Assert.Equal([DumpAnalysisFacts.AnalysisStatusType, DumpAnalysisFacts.DumpPathType], result.Facts.Select(fact => fact.Type));
        Assert.Equal("invalid", result.Facts[0].Value.GetValue<string>());
    }

    // ---- fixtures ----

    private static void AssertBoundedAndRoundTrips(IReadOnlyList<EvidenceFact> facts)
    {
        Assert.InRange(facts.Count, 1, 16);
        Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(facts).Length, 1, 4096);
        Assert.All(facts, fact =>
        {
            Assert.InRange(fact.Type.Length, 1, 128);
            Assert.InRange(fact.Key.Length, 1, 128);
        });
        Assert.Equal(facts.Count, facts.Select(fact => (fact.Type, fact.Key)).Distinct().Count());
        var again = JsonSerializer.Deserialize<EvidenceFact[]>(JsonSerializer.Serialize(facts))!;
        Assert.Equal(JsonSerializer.Serialize(facts), JsonSerializer.Serialize(again));
    }

    private static StabilityEvidence Evidence(
        StabilityCategory category, int daysAgo, string? provider = "p", string? eventId = "1") =>
        new(category, provider, eventId, null, null, null, EvidenceTimestampKind.Reported, Now.AddDays(-daysAgo).AddMinutes(-1));

    private static StabilitySnapshot Stability(
        IReadOnlyList<StabilityEvidence> evidence, IReadOnlyList<InventorySourceResult>? sources = null)
    {
        sources ??= [];
        var names = new[] { "shutdown", "crash", "hardware", "display.a", "display.b", "storage", "dumps" };
        var all = names.Select(name => sources.FirstOrDefault(source => source.Name == name)
            ?? new InventorySourceResult(name, InventorySourceStatus.Available, null)).ToArray();
        return new StabilitySnapshot(
            evidence,
            all,
            [
                new(StabilityCategory.UnexpectedShutdown, CategoryApplicability.Applicable, null, ["shutdown"]),
                new(StabilityCategory.KernelCrash, CategoryApplicability.Applicable, null, ["crash"]),
                new(StabilityCategory.KernelFault, CategoryApplicability.NotApplicable, "Not a category of this test platform.", []),
                new(StabilityCategory.HardwareError, CategoryApplicability.Applicable, null, ["hardware"]),
                new(StabilityCategory.DisplayFault, CategoryApplicability.Applicable, null, ["display.a", "display.b"]),
                new(StabilityCategory.StorageError, CategoryApplicability.Applicable, null, ["storage"]),
                new(StabilityCategory.MemoryExhaustion, CategoryApplicability.NotCollected, "Not collected on this test platform.", []),
                new(StabilityCategory.Minidump, CategoryApplicability.Applicable, null, ["dumps"]),
            ],
            new StabilityContext(CategoryApplicability.Applicable, "shutdown", 3, 2),
            new MinidumpInventory(CategoryApplicability.Applicable, "dumps", @"C:\Windows\Minidump", []),
            CollectionTruncated: false)
        {
            Stores = [new CoverageStore("store", CoverageBasis.EventLog, Now.AddDays(-400), 20_971_520, names)],
        };
    }

    private static CrashEvidence Crash(
        int minutesAgo, string kind, string? bugcheck = null, string? process = null, string? dump = null) =>
        new(Now.AddMinutes(-minutesAgo), EvidenceTimestampKind.Occurred, "wer", kind)
        {
            Process = process,
            BugcheckCode = kind == CrashKinds.KernelBugcheck ? bugcheck : null,
            DumpPath = dump,
            EvidenceSources = ["wer"],
        };

    private static CrashEvidenceSnapshot Crashes(params CrashEvidence[] items) =>
        new(items, [new MaintenanceSource("wer", InventorySourceStatus.Available)])
        {
            Stores = [new CoverageStore("store.wer", CoverageBasis.EventLog, Now.AddDays(-400), 20_971_520, ["wer"])],
        };

    private sealed class StabilityTool(StabilitySnapshot snapshot) : SystemStabilityToolBase("test", new FixedClock())
    {
        protected override Task<StabilitySnapshot> CollectAsync(StabilityQuery query, CancellationToken ct) => Task.FromResult(snapshot);
    }

    private sealed class CrashesTool(CrashEvidenceSnapshot snapshot) : SystemCrashesToolBase("test", new FixedClock())
    {
        protected override Task<CrashEvidenceSnapshot> CollectAsync(CrashesQuery query, CancellationToken ct) => Task.FromResult(snapshot);
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
