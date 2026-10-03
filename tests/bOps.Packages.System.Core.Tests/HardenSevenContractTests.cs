// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Sys.Conformance;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.System.Core.Tests;

/// <summary>
/// The platform-independent HARDEN-7 contracts of <c>bOps.Packages.System.Core</c>: the mode and horizon rules (ADR-0032 HARDEN-7
/// amendment §1–§2), event and crash aggregation (§3–§4), temporal coverage and the strict <c>complete</c> (§5–§6), and the
/// <c>system.stability</c> result (ADR-0041 §5, §8) with its honest counts (review findings R2, N2, N3) and budgets (R7).
/// </summary>
public sealed class HardenSevenContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero); // a Friday

    // ---- mode and horizon: the two cross-field rules, in their fixed order ----

    [Fact]
    public void Events_WithoutMode_AreRaw_AndAcceptOnlyTheMinuteWindow()
    {
        Assert.True(SystemEventsArguments.TryRead(Args(), Now, out var query, out var mode, out _, out _, out _));
        Assert.Equal(EvidenceMode.Raw, mode);
        Assert.Equal(Now.AddMinutes(-60), query!.FromUtc);

        Assert.True(SystemEventsArguments.TryRead(Args(j => { j["mode"] = "aggregate"; j["windowDays"] = 180; }), Now, out query, out mode, out _, out _, out _));
        Assert.Equal(EvidenceMode.Aggregate, mode);
        Assert.Equal(Now.AddDays(-180), query!.FromUtc);

        Assert.True(SystemEventsArguments.TryRead(Args(j => { j["mode"] = "aggregate"; j["windowMinutes"] = 10_080; }), Now, out query, out mode, out _, out _, out _));
        Assert.Equal((EvidenceMode.Aggregate, Now.AddDays(-7)), (mode, query!.FromUtc));
    }

    [Theory]
    [InlineData("""{"windowMinutes":60,"windowDays":2}""", "Use either windowMinutes or windowDays, not both.")]
    [InlineData("""{"mode":"raw","windowMinutes":60,"windowDays":2}""", "Use either windowMinutes or windowDays, not both.")]
    [InlineData("""{"mode":"aggregate","windowMinutes":60,"windowDays":2}""", "Use either windowMinutes or windowDays, not both.")]
    [InlineData("""{"windowMinutes":99999,"windowDays":2}""", "Use either windowMinutes or windowDays, not both.")]
    [InlineData("""{"windowDays":2}""", "windowDays applies only to mode aggregate; raw mode is limited to windowMinutes up to 10080 (7 days).")]
    [InlineData("""{"mode":"raw","windowDays":2}""", "windowDays applies only to mode aggregate; raw mode is limited to windowMinutes up to 10080 (7 days).")]
    [InlineData("""{"windowDays":999,"limit":0}""", "windowDays applies only to mode aggregate; raw mode is limited to windowMinutes up to 10080 (7 days).")]
    [InlineData("""{"mode":"aggregate","windowDays":181}""", "windowDays must be between 1 and 180.")]
    [InlineData("""{"mode":"aggregate","windowDays":0}""", "windowDays must be between 1 and 180.")]
    [InlineData("""{"mode":"raw","windowMinutes":10081}""", "windowMinutes must be between 1 and 10080.")]
    [InlineData("""{"mode":"summary"}""", "mode must be one of: raw, aggregate.")]
    public void Events_CrossFieldRules_AreReportedInTheirFixedOrder(string arguments, string error)
    {
        Assert.False(SystemEventsArguments.TryRead(ToolArguments.FromJson(JsonNode.Parse(arguments)!.AsObject()), Now, out _, out _, out _, out _, out var message));
        Assert.Equal(error, message);
    }

    [Fact]
    public void Crashes_DefaultToAggregate_SoSinceDaysNeedsNoMode_ButIsRefusedInRawMode()
    {
        Assert.True(SystemCrashesArguments.TryRead(Args(j => j["sinceDays"] = 180), Now, out var query, out _));
        Assert.Equal((EvidenceMode.Aggregate, Now.AddDays(-180)), (query!.Mode, query.FromUtc));

        Assert.False(SystemCrashesArguments.TryRead(Args(j => { j["mode"] = "raw"; j["sinceDays"] = 1; }), Now, out _, out var raw));
        Assert.Equal("sinceDays applies only to mode aggregate; raw mode is limited to sinceMinutes up to 10080 (7 days).", raw);
        Assert.False(SystemCrashesArguments.TryRead(Args(j => { j["mode"] = "raw"; j["sinceMinutes"] = 5; j["sinceDays"] = 1; }), Now, out _, out var both));
        Assert.Equal("Use either sinceMinutes or sinceDays, not both.", both);
        Assert.True(SystemCrashesArguments.TryRead(Args(j => j["mode"] = "raw"), Now, out query, out _));
        Assert.Equal((EvidenceMode.Raw, Now.AddMinutes(-1440)), (query!.Mode, query.FromUtc));
    }

    [Fact]
    public void TheManifests_CarryEveryNumericBound_AsATypedConstraint()
    {
        var events = SystemToolManifests.Events("x").Parameters;
        var crashes = SystemToolManifests.Crashes("x").Parameters;
        var stability = SystemToolManifests.Stability("x").Parameters;

        Assert.Equal((1d, 180d), Bound(events, "windowDays"));
        Assert.Equal((1d, 10080d), Bound(events, "windowMinutes"));
        Assert.Equal(["raw", "aggregate"], events.Single(p => p.Name == "mode").AllowedValues);
        Assert.Equal(["mode", "sinceMinutes", "sinceDays", "limit"], crashes.Select(p => p.Name).ToArray());
        Assert.Equal(["aggregate", "raw"], crashes.Single(p => p.Name == "mode").AllowedValues);
        Assert.Equal((1d, 180d), Bound(crashes, "sinceDays"));
        Assert.Equal((1d, 10080d), Bound(crashes, "sinceMinutes"));
        Assert.Equal(["windowDays", "limit"], stability.Select(p => p.Name).ToArray());
        Assert.Equal((1d, 180d), Bound(stability, "windowDays"));
        Assert.Equal((1d, 200d), Bound(stability, "limit"));
        Assert.Contains("never together with windowMinutes", events.Single(p => p.Name == "windowDays").Description, StringComparison.Ordinal);
        Assert.Contains("only with mode aggregate", events.Single(p => p.Name == "windowDays").Description, StringComparison.Ordinal);
        Assert.Contains("only in aggregate mode", crashes.Single(p => p.Name == "sinceDays").Description, StringComparison.Ordinal);
    }

    // ---- system.events aggregate ----

    [Fact]
    public void EventAggregation_GroupsByChannelSourceUnitEventIdAndSeverity_WithTheNewestSample()
    {
        var events = new[]
        {
            Event(1, "Microsoft-Windows-Kernel-Power", "41", SystemEventSeverity.Critical, "newest power", "System"),
            Event(5, "Microsoft-Windows-Kernel-Power", "41", SystemEventSeverity.Critical, "older power", "System"),
            Event(2, "Microsoft-Windows-Kernel-Power", "41", SystemEventSeverity.Error, "other severity", "System"),
            Event(3, "app", null, SystemEventSeverity.Error, new string('m', 600), null),
            Event(4, "app", null, SystemEventSeverity.Error, "x", null),
        };
        var query = new SystemEventQuery(Now.AddDays(-1), Now, null, null, null, null, null, 10_000);

        var json = JsonNode.Parse(SystemEventFormatting.Format(Snapshot(events), query, EvidenceMode.Aggregate, 50, 32_768))!.AsObject();

        Assert.Equal(
            ["schemaVersion", "mode", "status", "complete", "truncated", "window", "excludeSources", "coverage", "observedEvents", "sources", "observedGroups", "returnedGroups", "groups"],
            json.Select(pair => pair.Key).ToArray());
        Assert.Equal(5, json["observedEvents"]!.GetValue<int>());
        Assert.Equal(3, json["observedGroups"]!.GetValue<int>());
        var groups = json["groups"]!.AsArray();
        Assert.Equal(("System", "Microsoft-Windows-Kernel-Power", 2, "newest power"), (groups[0]!["channel"]!.GetValue<string>(), groups[0]!["source"]!.GetValue<string>(), groups[0]!["count"]!.GetValue<int>(), groups[0]!["sampleMessage"]!.GetValue<string>()));
        Assert.Equal(EvidenceTime.Format(Now.AddMinutes(-5)), groups[0]!["firstSeenUtc"]!.GetValue<string>());
        Assert.Equal(EvidenceTime.Format(Now.AddMinutes(-1)), groups[0]!["lastSeenUtc"]!.GetValue<string>());
        Assert.Null(groups[1]!["channel"]); // a null key is a value of its own
        Assert.Equal(512, groups[1]!["sampleMessage"]!.GetValue<string>().Length);
        Assert.True(groups[1]!["sampleMessageTruncated"]!.GetValue<bool>());
        Assert.Equal(("error", 1), (groups[2]!["severity"]!.GetValue<string>(), groups[2]!["count"]!.GetValue<int>()));
        Assert.Null(groups[0]!["processId"]);
    }

    [Fact]
    public void EventAggregation_FiltersBeforeGrouping_AndTheByteBudgetCutsGroupsFromTheEnd()
    {
        var events = Enumerable.Range(0, 200).Select(index => Event(index + 1, "source" + index.ToString("D3", global::System.Globalization.CultureInfo.InvariantCulture), "1", SystemEventSeverity.Error, new string('x', 300), "System")).ToArray();
        var query = new SystemEventQuery(Now.AddDays(-1), Now, SystemEventSeverity.Error, null, null, null, null, 10_000);

        var output = SystemEventFormatting.Format(Snapshot(events), query, EvidenceMode.Aggregate, 500, 8_192);
        var json = JsonNode.Parse(output)!.AsObject();

        Assert.True(Encoding.UTF8.GetByteCount(output) <= 8_192);
        Assert.Equal(200, json["observedGroups"]!.GetValue<int>());
        Assert.InRange(json["returnedGroups"]!.GetValue<int>(), 1, 199);
        Assert.True(json["truncated"]!.GetValue<bool>());
        Assert.False(json["complete"]!.GetValue<bool>());
    }

    // ---- temporal coverage ----

    [Theory]
    [InlineData(-200, "complete")]
    [InlineData(-180, "complete")]
    [InlineData(-60, "partial")]
    [InlineData(null, "unknown")]
    public void AnEventLogStore_IsCompleteOnlyWhenItReachesTheStartOfTheRequest(int? oldestDays, string state)
    {
        var store = new CoverageStore("windows.channel.System", CoverageBasis.EventLog, oldestDays is { } days ? Now.AddDays(days) : null, 20_971_520, ["s"]);

        Assert.Equal(state, TemporalCoverage.StoreState(store, Now.AddDays(-180)));
        Assert.Equal(state, TemporalCoverage.GlobalState([store], Now.AddDays(-180)));
    }

    [Fact]
    public void ADirectoryStore_IsAlwaysUnknown_AndNeverChangesTheGlobalState()
    {
        var directory = new CoverageStore("windows.minidump", CoverageBasis.Directory, Now.AddDays(-999), null, ["windows.minidump"]);
        var log = new CoverageStore("windows.channel.System", CoverageBasis.EventLog, Now.AddDays(-999), null, ["s"]);

        Assert.Equal("unknown", TemporalCoverage.StoreState(directory, Now.AddDays(-1)));
        Assert.Equal("complete", TemporalCoverage.GlobalState([directory, log], Now.AddDays(-1)));
        Assert.Equal("unknown", TemporalCoverage.GlobalState([directory], Now.AddDays(-1)));
        Assert.Equal("unknown", TemporalCoverage.GlobalState([], Now.AddDays(-1)));
    }

    [Fact]
    public void APartialStore_OutweighsAnUnknownOne_InTheGlobalState()
    {
        var shortLog = new CoverageStore("a", CoverageBasis.EventLog, Now.AddDays(-60), null, ["a"]);
        var unknown = new CoverageStore("b", CoverageBasis.Journal, null, null, ["b"]);

        Assert.Equal("partial", TemporalCoverage.GlobalState([unknown, shortLog], Now.AddDays(-180)));
    }

    [Fact]
    public void AStoreIsListedOnlyWhenASourceItBacksIsNotNotApplicable()
    {
        var stores = new[]
        {
            new CoverageStore("z.store", CoverageBasis.EventLog, Now, null, ["z"]),
            new CoverageStore("a.store", CoverageBasis.EventLog, Now, null, ["a"]),
            new CoverageStore("gone", CoverageBasis.Directory, null, null, ["gone"]),
            new CoverageStore("orphan", CoverageBasis.EventLog, Now, null, ["missing"]),
        };
        var statuses = new Dictionary<string, InventorySourceStatus>
        {
            ["z"] = InventorySourceStatus.Unavailable,
            ["a"] = InventorySourceStatus.Available,
            ["gone"] = InventorySourceStatus.NotApplicable,
        };

        Assert.Equal(["a.store", "z.store"], TemporalCoverage.Listed(stores, statuses).Select(store => store.Name).ToArray());
    }

    [Fact]
    public void ARequestFor180DaysAgainstTwoMonthsOfLog_IsPartial_WhateverTheScanFound()
    {
        var snapshot = new SystemEventSnapshot([], [new InventorySourceResult("windows.channel.System", InventorySourceStatus.Available, null) { ExaminedFromUtc = Now.AddDays(-180) }])
        {
            Stores = [new CoverageStore("windows.channel.System", CoverageBasis.EventLog, Now.AddDays(-62), 20_971_520, ["windows.channel.System"])],
        };
        var query = new SystemEventQuery(Now.AddDays(-180), Now, null, null, null, null, null, 10_000);

        var json = JsonNode.Parse(SystemEventFormatting.Format(snapshot, query, EvidenceMode.Aggregate, 50, 32_768))!.AsObject();
        var coverageState = SystemToolConformance.AssertCoverage(json);

        Assert.Equal("partial", coverageState);
        Assert.Equal("complete", json["status"]!.GetValue<string>());
        Assert.False(json["truncated"]!.GetValue<bool>());
        Assert.False(json["complete"]!.GetValue<bool>());
        Assert.Equal(EvidenceTime.Format(Now.AddDays(-180)), json["sources"]![0]!["examinedFromUtc"]!.GetValue<string>());
        Assert.Equal(ToolResultCompleteness.Partial, EvidenceCompleteness.FromOutput(json.ToJsonString()));
    }

    // ---- the call time budget (review note R7) ----

    [Fact]
    public void TheTimeBudget_GivesEachSourceAShareOfWhatIsLeft_AndUnusedTimeRollsOver()
    {
        var clock = new ManualClock(Now);
        var budget = new EvidenceTimeBudget(TimeSpan.FromSeconds(20), clock);

        Assert.Equal(TimeSpan.FromSeconds(5), budget.Slice(4));
        Assert.Equal(TimeSpan.FromSeconds(2), budget.Slice(4, cap: TimeSpan.FromSeconds(2)));
        clock.Advance(TimeSpan.FromSeconds(1)); // the first source finished early
        Assert.Equal(TimeSpan.FromSeconds(19) / 3, budget.Slice(3));
        clock.Advance(TimeSpan.FromSeconds(30)); // something overran: the rest still gets a slice, of zero, and stops at once
        Assert.Equal(TimeSpan.Zero, budget.Remaining);
        using var slice = budget.Start(1, CancellationToken.None);
        Assert.True(slice.Token.IsCancellationRequested);
        Assert.True(slice.TimedOut);
    }

    [Fact]
    public void ASliceTimesOut_OnlyWhenItsTimeRunsOut_NotWhenTheCallerCancels()
    {
        var clock = new ManualClock(Now);
        using var cancel = new CancellationTokenSource();
        var budget = new EvidenceTimeBudget(TimeSpan.FromSeconds(20), clock);
        using var slice = budget.Start(2, cancel.Token);

        cancel.Cancel();

        Assert.True(slice.Token.IsCancellationRequested);
        Assert.False(slice.TimedOut);
    }

    // ---- system.crashes aggregation ----

    [Fact]
    public void CrashAggregation_KeysOnKindCodeApplicationModuleAndTimestampKind_AndTheEventNameOnlyForWer()
    {
        CrashEvidence Crash(string kind, string? eventName, string? process, EvidenceTimestampKind time, string? reportId = "r", string? dump = null) =>
            new(Now.AddHours(-1), time, "s", kind) { EventName = eventName, Process = process, ExceptionCode = "0xc0000005", ReportId = reportId, DumpPath = dump, EvidenceSources = ["s"] };

        var groups = SystemCrashFormatting.Aggregate(
        [
            Crash(CrashKinds.ApplicationCrash, "APPCRASH", "App.exe", EvidenceTimestampKind.Occurred, dump: @"C:\d.dmp"),
            Crash(CrashKinds.ApplicationCrash, "BEX", "app.exe", EvidenceTimestampKind.Occurred, reportId: null),
            Crash(CrashKinds.ApplicationCrash, "APPCRASH", "app.exe", EvidenceTimestampKind.Reported),
            Crash(CrashKinds.Wer, "MoAppHang", null, EvidenceTimestampKind.Reported),
            Crash(CrashKinds.Wer, "crashpad_log", null, EvidenceTimestampKind.Reported),
        ]);

        Assert.Equal(4, groups.Count);
        var occurred = groups[0];
        Assert.Equal(("application-crash", "App.exe", "0xc0000005", "occurred", 2), (occurred["kind"]!.GetValue<string>(), occurred["application"]!.GetValue<string>(), occurred["code"]!.GetValue<string>(), occurred["timestampKind"]!.GetValue<string>(), occurred["count"]!.GetValue<int>()));
        Assert.Null(occurred["eventName"]);
        Assert.Equal(1, occurred["uncorrelatedCount"]!.GetValue<int>());
        Assert.Equal(1, occurred["dumpReferenceCount"]!.GetValue<int>());
        Assert.Contains(groups, group => group["eventName"]?.GetValue<string>() == "MoAppHang");
        Assert.Contains(groups, group => group["eventName"]?.GetValue<string>() == "crashpad_log");
    }

    // ---- system.stability: buckets ----

    [Theory]
    [InlineData(1, "hour", "2026-10-01T10:00:00.0000000Z", 25)]
    [InlineData(2, "hour", "2026-09-30T10:00:00.0000000Z", 49)]
    [InlineData(3, "day", "2026-09-29T00:00:00.0000000Z", 4)]
    [InlineData(31, "day", "2026-09-01T00:00:00.0000000Z", 32)]
    [InlineData(32, "week", "2026-08-31T00:00:00.0000000Z", 5)]
    [InlineData(180, "week", "2026-03-30T00:00:00.0000000Z", 27)]
    public void TheBucketWidth_IsDerivedFromWindowDays_AlignedAndDense(int days, string width, string firstStart, int count)
    {
        var buckets = StabilityFormatting.Buckets(new StabilityQuery(days, Now.AddDays(-days), Now, 50));

        Assert.Equal((width, firstStart, count), (buckets.Width, EvidenceTime.Format(buckets.FirstStartUtc), buckets.Count));
    }

    [Fact]
    public void EveryWindowFrom1To180Days_StaysWithinTheBucketBound_AtAnyTimeOfDay()
    {
        foreach (var offset in new[] { 0, 7, 13, 23 })
        {
            var now = new DateTimeOffset(2026, 10, 2, offset, 37, 11, TimeSpan.Zero);
            for (var days = 1; days <= 180; days++)
            {
                var buckets = StabilityFormatting.Buckets(new StabilityQuery(days, now.AddDays(-days), now, 50));
                Assert.InRange(buckets.Count, 1, buckets.Width switch { "hour" => 49, "day" => 32, _ => 27 });
                Assert.True(buckets.FirstStartUtc <= now.AddDays(-days));
                Assert.True(buckets.FirstStartUtc + buckets.Count * buckets.Length > now);
                if (buckets.Width == "week")
                {
                    Assert.Equal(DayOfWeek.Monday, buckets.FirstStartUtc.DayOfWeek);
                }
            }
        }
    }

    // ---- system.stability: honest counts (R2, N2, N3) ----

    [Fact]
    public void CategoryCounts_AreExactWhenAvailable_LowerBoundsWhenPartial_NullWhenUnavailable_AndZeroOnlyForAbsentSources()
    {
        var snapshot = Stability(
            evidence:
            [
                Evidence(StabilityCategory.UnexpectedShutdown, 1),
                Evidence(StabilityCategory.UnexpectedShutdown, 2),
                Evidence(StabilityCategory.DisplayFault, 3),
            ],
            sources:
            [
                new("shutdown", InventorySourceStatus.Available, null),
                new("crash", InventorySourceStatus.Unavailable, "denied"),
                new("display.a", InventorySourceStatus.Available, null),
                new("display.b", InventorySourceStatus.Unavailable, "denied"),
                new("dumps", InventorySourceStatus.NotApplicable, "The minidump directory does not exist."),
            ]);

        var json = Format(snapshot, 30);

        Assert.Equal(("available", 2), CategoryState(json, "unexpectedShutdown"));
        Assert.Equal(("unavailable", null), CategoryState(json, "kernelCrash"));
        Assert.Equal(("partial", 1), CategoryState(json, "displayFault"));
        Assert.Equal(("notApplicable", 0), CategoryState(json, "minidump")); // status notApplicable: no source exists, so 0
        var memory = json["categories"]!.AsArray().Single(c => c!["category"]!.GetValue<string>() == "memoryExhaustion")!;
        Assert.Equal(("notCollected", null, null), (memory["applicability"]!.GetValue<string>(), memory["status"]?.GetValue<string>(), memory["count"]?.GetValue<int>()));
        var kernelFault = json["categories"]!.AsArray().Single(c => c!["category"]!.GetValue<string>() == "kernelFault")!;
        Assert.Equal(("notApplicable", null, null), (kernelFault["applicability"]!.GetValue<string>(), kernelFault["status"]?.GetValue<string>(), kernelFault["count"]?.GetValue<int>()));
        // N3: an unavailable category has no timeline row and no group, and its count (null) says that this is not zero.
        Assert.DoesNotContain(json["timeline"]!.AsArray(), row => row!["category"]!.GetValue<string>() == "kernelCrash");
        Assert.DoesNotContain(json["groups"]!.AsArray(), group => group!["category"]!.GetValue<string>() == "kernelCrash");
        Assert.Equal("partial", json["status"]!.GetValue<string>());
        Assert.False(json["complete"]!.GetValue<bool>());
        SystemToolConformance.AssertStabilityEnvelope(json.ToJsonString(), ToolResultCompleteness.Partial);
    }

    [Fact]
    public void TheMinidumpCounters_FollowTheCategoryRule()
    {
        MinidumpFile File(int daysAgo, long size, string name) => new(name, size, Now.AddDays(-daysAgo), null);

        var available = Format(Stability(sources: [new("dumps", InventorySourceStatus.Available, null)], files: [File(1, 10, "a.dmp"), File(2, 20, "b.dmp"), File(400, 99, "old.dmp")]), 30);
        var partial = Format(Stability(sources: [new("dumps", InventorySourceStatus.Partial, "skipped")], files: [File(1, 10, "a.dmp")]), 30);
        var unavailable = Format(Stability(sources: [new("dumps", InventorySourceStatus.Unavailable, "denied")], files: []), 30);

        Assert.Equal((2, 2, 30L), (available["minidumps"]!["observed"]!.GetValue<int>(), available["minidumps"]!["returned"]!.GetValue<int>(), available["minidumps"]!["totalBytes"]!.GetValue<long>()));
        Assert.Equal(("partial", 1), (partial["minidumps"]!["status"]!.GetValue<string>(), partial["minidumps"]!["observed"]!.GetValue<int>()));
        Assert.Null(unavailable["minidumps"]!["observed"]);
        Assert.Null(unavailable["minidumps"]!["totalBytes"]);
        Assert.Equal(("unavailable", null), CategoryState(unavailable, "minidump"));
    }

    [Fact]
    public void AtMost16MinidumpFilesAreListed_WithoutSettingTruncated_AndTheCountsStayTotal()
    {
        var files = Enumerable.Range(0, 40).Select(index => new MinidumpFile($"0{index:D2}.dmp", 100, Now.AddHours(-index - 1), null)).ToArray();
        var json = Format(Stability(sources: [new("dumps", InventorySourceStatus.Available, null)], files: files), 30);

        Assert.Equal(16, json["minidumps"]!["files"]!.AsArray().Count);
        Assert.Equal(40, json["minidumps"]!["observed"]!.GetValue<int>());
        Assert.Equal(4_000, json["minidumps"]!["totalBytes"]!.GetValue<long>());
        Assert.Equal("000.dmp", json["minidumps"]!["files"]![0]!["name"]!.GetValue<string>());
        Assert.False(json["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public void TheTimeline_IsDensePerCategoryAndKind_OccurredBeforeReported_AndCountsOnlyRecordsInTheWindow()
    {
        var json = Format(Stability(evidence:
        [
            Evidence(StabilityCategory.HardwareError, 1, EvidenceTimestampKind.Occurred, severity: "corrected"),
            Evidence(StabilityCategory.HardwareError, 1, EvidenceTimestampKind.Reported, severity: "uncorrected"),
            Evidence(StabilityCategory.HardwareError, 5, EvidenceTimestampKind.Occurred, severity: "corrected"),
            Evidence(StabilityCategory.HardwareError, 50, EvidenceTimestampKind.Occurred, severity: "corrected"),
            Evidence(StabilityCategory.UnexpectedShutdown, 2),
        ]), 7);

        var timeline = json["timeline"]!.AsArray();
        Assert.Equal(
            [("unexpectedShutdown", "reported"), ("hardwareError", "occurred"), ("hardwareError", "reported")],
            timeline.Select(row => (row!["category"]!.GetValue<string>(), row["timestampKind"]!.GetValue<string>())).ToArray());
        var occurred = timeline[1]!["counts"]!.AsArray().Select(value => value!.GetValue<int>()).ToArray();
        Assert.Equal(json["bucket"]!["count"]!.GetValue<int>(), occurred.Length);
        Assert.Equal(2, occurred.Sum()); // the record 50 days ago is outside the 7-day window
        Assert.Equal(3, CategoryState(json, "hardwareError").Count);
    }

    [Fact]
    public void TheGroupKeyIncludesSeverityClassAndTimestampKind_AndGroupsFollowTheCategoryOrder()
    {
        var json = Format(Stability(evidence:
        [
            Evidence(StabilityCategory.StorageError, 1, EvidenceTimestampKind.Occurred, provider: "disk", eventId: "153", component: @"\Device\Harddisk1\DR1"),
            Evidence(StabilityCategory.HardwareError, 1, EvidenceTimestampKind.Occurred, severity: "corrected"),
            Evidence(StabilityCategory.HardwareError, 2, EvidenceTimestampKind.Occurred, severity: "corrected"),
            Evidence(StabilityCategory.HardwareError, 3, EvidenceTimestampKind.Reported, severity: "unknown"),
            Evidence(StabilityCategory.UnexpectedShutdown, 1, code: "0x0"),
        ]), 30);

        var groups = json["groups"]!.AsArray();
        Assert.Equal(
            [("unexpectedShutdown", 1), ("hardwareError", 2), ("hardwareError", 1), ("storageError", 1)],
            groups.Select(group => (group!["category"]!.GetValue<string>(), group["count"]!.GetValue<int>())).ToArray());
        Assert.Equal(("corrected", "occurred"), (groups[1]!["severityClass"]!.GetValue<string>(), groups[1]!["timestampKind"]!.GetValue<string>()));
        Assert.Equal(("unknown", "reported"), (groups[2]!["severityClass"]!.GetValue<string>(), groups[2]!["timestampKind"]!.GetValue<string>()));
        Assert.DoesNotContain("drillDown", json.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheLimitCutsGroups_ButNeverTheCategoryTotals()
    {
        var evidence = Enumerable.Range(0, 30).Select(index => Evidence(StabilityCategory.StorageError, 1, EvidenceTimestampKind.Occurred, provider: "disk", eventId: "153", component: "dev" + index)).ToArray();

        var json = Format(Stability(evidence: evidence), 30, limit: 5);

        Assert.Equal((30, 5), (json["observedGroups"]!.GetValue<int>(), json["returnedGroups"]!.GetValue<int>()));
        Assert.Equal(30, CategoryState(json, "storageError").Count);
        Assert.True(json["truncated"]!.GetValue<bool>());
        Assert.False(json["complete"]!.GetValue<bool>());
    }

    [Fact]
    public void The32KiBBudget_RemovesGroupsThenFiles_KeepsTheEnvelopeAndTheTotals()
    {
        var evidence = Enumerable.Range(0, 200).Select(index => Evidence(StabilityCategory.StorageError, 1, EvidenceTimestampKind.Occurred, provider: "disk", eventId: "153", component: new string('d', 100) + index.ToString("D3", global::System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        var files = Enumerable.Range(0, 16).Select(index => new MinidumpFile(new string('f', 100) + index.ToString("D3", global::System.Globalization.CultureInfo.InvariantCulture) + ".dmp", 10, Now.AddHours(-index - 1), null)).ToArray();

        var output = StabilityFormatting.Format(Stability(evidence: evidence, sources: [new("dumps", InventorySourceStatus.Available, null)], files: files), Query(180, 200));
        var json = JsonNode.Parse(output)!.AsObject();

        Assert.True(Encoding.UTF8.GetByteCount(output) <= StabilityLimits.OutputBytes);
        Assert.True(json["truncated"]!.GetValue<bool>());
        Assert.Equal(201, json["observedGroups"]!.GetValue<int>()); // 200 storage signatures and the one minidump group
        Assert.Equal(json["groups"]!.AsArray().Count, json["returnedGroups"]!.GetValue<int>());
        Assert.Equal(200, CategoryState(json, "storageError").Count);
        Assert.Equal(16, json["minidumps"]!["observed"]!.GetValue<int>());
    }

    [Fact]
    public void TheEnvelopeAlone_AlwaysFitsTheBudget()
    {
        var sources = Enumerable.Range(0, 8).Select(index => new InventorySourceResult("windows.stability.source" + index, InventorySourceStatus.Partial, new string('x', 256))).ToArray();
        var evidence = StabilityFormatting.CategoryOrder.SelectMany(category => new[] { EvidenceTimestampKind.Occurred, EvidenceTimestampKind.Reported }
            .Select(kind => Evidence(category, 1, kind))).ToArray();

        var output = StabilityFormatting.Format(Stability(evidence: evidence, sources: sources), Query(2, 200));

        Assert.True(Encoding.UTF8.GetByteCount(output) <= StabilityLimits.OutputBytes);
    }

    [Fact]
    public async Task TheStabilityAuditSummary_CarriesCountsAndStatus_NeverAComponentAFileNameOrADirectory()
    {
        var tool = new FakeStabilityTool(Stability(
            evidence: [Evidence(StabilityCategory.StorageError, 1, EvidenceTimestampKind.Occurred, provider: "disk", eventId: "153", component: "SecretDevice")],
            sources: [new("dumps", InventorySourceStatus.Available, null)],
            files: [new MinidumpFile("secret-name.dmp", 10, Now.AddHours(-1), null)]));
        var arguments = ToolArguments.FromJson(new JsonObject { ["windowDays"] = 7 });

        var result = await tool.ExecuteAsync(arguments);
        var summary = tool.CreateAuditSummary(arguments, result)!;
        var text = summary.ToJsonString();

        Assert.Equal(7, summary["windowDays"]!.GetValue<int>());
        Assert.Equal(8, summary["categories"]!.AsArray().Count);
        Assert.DoesNotContain("SecretDevice", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-name", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Minidump", text, StringComparison.Ordinal);
    }

    // ---- helpers ----

    private static ToolArguments Args(Action<JsonObject>? set = null)
    {
        var json = new JsonObject();
        set?.Invoke(json);
        return ToolArguments.FromJson(json);
    }

    private static (double?, double?) Bound(IReadOnlyList<ToolParameter> parameters, string name)
    {
        var parameter = parameters.Single(candidate => candidate.Name == name);
        return (parameter.Minimum, parameter.Maximum);
    }

    private static SystemEventRecord Event(int minutesAgo, string source, string? eventId, SystemEventSeverity severity, string message, string? channel) =>
        new(Now.AddMinutes(-minutesAgo), severity, source, eventId, channel, message, null, null);

    private static SystemEventSnapshot Snapshot(IReadOnlyList<SystemEventRecord> events) =>
        new(events, [new InventorySourceResult("windows.channel.System", InventorySourceStatus.Available, null)])
        {
            Stores = [new CoverageStore("windows.channel.System", CoverageBasis.EventLog, Now.AddDays(-400), null, ["windows.channel.System"])],
        };

    private static StabilityEvidence Evidence(
        StabilityCategory category,
        int daysAgo,
        EvidenceTimestampKind kind = EvidenceTimestampKind.Reported,
        string? provider = "p",
        string? eventId = "1",
        string? code = null,
        string? component = null,
        string? severity = null) =>
        new(category, provider, eventId, code, component, severity, kind, Now.AddDays(-daysAgo).AddMinutes(-1));

    /// <summary>A snapshot whose categories map to the named test sources (and their store).</summary>
    private static StabilitySnapshot Stability(
        IReadOnlyList<StabilityEvidence>? evidence = null,
        IReadOnlyList<InventorySourceResult>? sources = null,
        IReadOnlyList<MinidumpFile>? files = null)
    {
        sources ??= [];
        var names = new[] { "shutdown", "crash", "hardware", "display.a", "display.b", "storage", "dumps" };
        var all = names.Select(name => sources.FirstOrDefault(source => source.Name == name) ?? new InventorySourceResult(name, InventorySourceStatus.Available, null))
            .Concat(sources.Where(source => !names.Contains(source.Name)))
            .ToArray();
        return new StabilitySnapshot(
            evidence ?? [],
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
            new MinidumpInventory(CategoryApplicability.Applicable, "dumps", @"C:\Windows\Minidump", files ?? []),
            CollectionTruncated: false)
        {
            Stores = [new CoverageStore("store", CoverageBasis.EventLog, Now.AddDays(-400), 20_971_520, names)],
        };
    }

    private static StabilityQuery Query(int days, int limit = 50) => new(days, Now.AddDays(-days), Now, limit);

    private static JsonObject Format(StabilitySnapshot snapshot, int days, int limit = 50) =>
        JsonNode.Parse(StabilityFormatting.Format(snapshot, Query(days, limit)))!.AsObject();

    private static (string? Status, int? Count) CategoryState(JsonObject json, string name)
    {
        var category = json["categories"]!.AsArray().Single(item => item!["category"]!.GetValue<string>() == name)!;
        return (category["status"]?.GetValue<string>(), category["count"]?.GetValue<int>());
    }

    private sealed class FakeStabilityTool(StabilitySnapshot snapshot) : SystemStabilityToolBase("test", new ManualClock(Now))
    {
        protected override Task<StabilitySnapshot> CollectAsync(StabilityQuery query, CancellationToken ct) => Task.FromResult(snapshot);
    }

    /// <summary>A clock a test moves by hand; its timers fire when it is advanced past their due time.</summary>
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private readonly List<(DateTimeOffset Due, TimerCallback Callback, object? State)> timers = [];
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public override long GetTimestamp() => now.UtcTicks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                timers.Add((now + dueTime, callback, state));
            }

            return new NoTimer();
        }

        public void Advance(TimeSpan by)
        {
            now += by;
            foreach (var timer in timers.Where(timer => timer.Due <= now).ToArray())
            {
                timers.Remove(timer);
                timer.Callback(timer.State);
            }
        }

        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
