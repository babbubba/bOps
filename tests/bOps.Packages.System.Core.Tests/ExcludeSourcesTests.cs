// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.System.Core.Tests;

/// <summary>
/// ADR-0032 HARDEN-9 amendment: the <c>excludeSources</c> argument of <c>system.events</c> — its grammar and rejections, the
/// matching on the canonical <c>source</c> only, the order of the filters, the always-present echo that the byte budget never cuts
/// (also in the largest envelope the smallest budget allows), and its manifest contract.
/// </summary>
public sealed class ExcludeSourcesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private static ToolArguments Args(Action<JsonObject>? configure = null)
    {
        var json = new JsonObject();
        configure?.Invoke(json);
        return ToolArguments.FromJson(json);
    }

    private static ToolArguments Exclude(string? value, string? source = null) =>
        Args(j =>
        {
            j["excludeSources"] = value;
            if (source is not null)
            {
                j["source"] = source;
            }
        });

    private static bool TryRead(ToolArguments arguments, out SystemEventQuery? query, out string? error) =>
        SystemEventsArguments.TryRead(arguments, Now, out query, out _, out _, out _, out error);

    private static SystemEventRecord Event(string source, int minutesAgo = 5, string? unit = null, string? channel = "System", string message = "text") =>
        new(Now.AddMinutes(-minutesAgo), SystemEventSeverity.Error, source, "7", channel, message, 42, "proc", unit);

    private static SystemEventQuery Query(IReadOnlyList<string>? excluded = null, string? source = null) =>
        new(Now.AddMinutes(-60), Now, null, source, null, null, null, SystemEventsLimits.ScanCeiling, excluded);

    private static SystemEventSnapshot Snapshot(params SystemEventRecord[] events) =>
        new(events, [new InventorySourceResult("test.source", InventorySourceStatus.Available, null)])
        {
            Stores = [new CoverageStore("test.store", CoverageBasis.EventLog, Now.AddDays(-400), null, ["test.source"])],
        };

    private static JsonObject Format(SystemEventSnapshot snapshot, SystemEventQuery query, EvidenceMode mode = EvidenceMode.Raw, int limit = 100, int maxBytes = 32_768) =>
        JsonNode.Parse(SystemEventFormatting.Format(snapshot, query, mode, limit, maxBytes))!.AsObject();

    private static string[] Echo(JsonObject root) => [.. root["excludeSources"]!.AsArray().Select(item => item!.GetValue<string>())];

    // ---- grammar: accepted ----

    [Fact]
    public void Omitted_MeansNoExclusion_AndTheDefaultsAreUnchanged()
    {
        Assert.True(TryRead(Args(), out var query, out var error), error);

        Assert.Empty(query!.ExcludeSources ?? []);
    }

    [Fact]
    public void JsonNull_IsTreatedAsAbsent()
    {
        Assert.True(TryRead(Exclude(null), out var query, out var error), error);

        Assert.Empty(query!.ExcludeSources ?? []);
    }

    [Fact]
    public void OneSource_IsAccepted()
    {
        Assert.True(TryRead(Exclude(".NET Runtime"), out var query, out var error), error);

        Assert.Equal([".NET Runtime"], query!.ExcludeSources);
    }

    [Fact]
    public void MultipleSources_AreAccepted_WithEachEntryTrimmed()
    {
        Assert.True(TryRead(Exclude("  Windows Error Reporting ,Microsoft-Windows-Security-SPP,\tnginx.service  "), out var query, out var error), error);

        Assert.Equal(["Microsoft-Windows-Security-SPP", "nginx.service", "Windows Error Reporting"], query!.ExcludeSources);
    }

    [Fact]
    public void TheAcceptedEntries_KeepTheCallersSpelling_AndAreSortedIgnoringCaseThenOrdinally()
    {
        Assert.True(TryRead(Exclude("zeta,Beta,alpha,Gamma"), out var query, out var error), error);

        Assert.Equal(["alpha", "Beta", "Gamma", "zeta"], query!.ExcludeSources);
    }

    [Fact]
    public void AnUnknownButSyntacticallyValidSource_IsAccepted()
    {
        Assert.True(TryRead(Exclude("No Such Provider (v9)"), out var query, out var error), error);

        Assert.Equal(["No Such Provider (v9)"], query!.ExcludeSources);
    }

    [Fact]
    public void EightEntries_AreAccepted()
    {
        Assert.True(TryRead(Exclude("a,b,c,d,e,f,g,h"), out var query, out var error), error);

        Assert.Equal(8, query!.ExcludeSources!.Count);
    }

    [Fact]
    public void AnEntryOf128Characters_IsAccepted()
    {
        Assert.True(TryRead(Exclude(new string('a', 128)), out _, out var error), error);
    }

    [Fact]
    public void TheWholeValue_IsAcceptedAt1024Characters_AndTheTotalBoundPrevailsOverEightFullEntries()
    {
        // Seven entries of 128 and one of 120, with seven separators, are exactly 1,023 characters.
        var fits = string.Join(',', Enumerable.Repeat(new string('a', 128), 0).Concat(Enumerable.Range(0, 7).Select(i => new string((char)('a' + i), 128))).Append(new string('z', 121)));
        Assert.Equal(1_024, fits.Length);
        Assert.True(TryRead(Exclude(fits), out var query, out var error), error);
        Assert.Equal(8, query!.ExcludeSources!.Count);

        // Eight entries of 128 are 1,031 characters: each entry is within its bound, the total is not.
        var tooLong = string.Join(',', Enumerable.Range(0, 8).Select(i => new string((char)('a' + i), 128)));
        Assert.Equal(1_031, tooLong.Length);
        Assert.False(TryRead(Exclude(tooLong), out var rejected, out var message));
        Assert.Null(rejected);
        Assert.Contains("excludeSources", message, StringComparison.Ordinal);
        Assert.Contains("1024", message, StringComparison.Ordinal);
    }

    // ---- grammar: rejected ----

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(",")]
    [InlineData("a,,b")]
    [InlineData("a, ,b")]
    [InlineData("a,")]
    [InlineData(",a")]
    [InlineData("a,b,")]
    public void AnEmptyEntry_IsRejected(string value)
    {
        Assert.False(TryRead(Exclude(value), out var query, out var error));

        Assert.Null(query);
        Assert.Contains("excludeSources", error, StringComparison.Ordinal);
    }

    [Fact]
    public void NineEntries_AreRejected()
    {
        Assert.False(TryRead(Exclude("a,b,c,d,e,f,g,h,i"), out _, out var error));

        Assert.Contains("at most 8", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEntryOf129Characters_IsRejected()
    {
        Assert.False(TryRead(Exclude(new string('a', 129)), out _, out var error));

        Assert.Contains("128", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AValueOf1025Characters_IsRejected()
    {
        // Entries within their bound but a whole value one character over the total.
        var value = string.Join(',', Enumerable.Range(0, 7).Select(i => new string((char)('a' + i), 128))) + "," + new string('z', 122);
        Assert.Equal(1_025, value.Length);

        Assert.False(TryRead(Exclude(value), out _, out var error));
        Assert.Contains("1024", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("x' or '1'='1")]
    [InlineData("a]")]
    [InlineData("a[b")]
    [InlineData("a\"b")]
    [InlineData("a*b")]
    [InlineData("a=b")]
    [InlineData("a;b")]
    [InlineData("a|b")]
    [InlineData("a\nb")]
    [InlineData("a\\b")]
    [InlineData("a,b\tc")]
    [InlineData("é")]
    [InlineData("a,b!")]
    public void AnEntryThatCouldCarryQuerySyntax_IsRejected(string value)
    {
        Assert.False(TryRead(Exclude(value), out var query, out var error));

        Assert.Null(query);
        Assert.Contains("excludeSources", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("nginx,NGINX")]
    [InlineData("Alpha,beta,ALPHA")]
    [InlineData("a, a")]
    public void ADuplicateIgnoringCase_IsRejected(string value)
    {
        Assert.False(TryRead(Exclude(value), out _, out var error));

        Assert.Contains("twice", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("nginx.service", "nginx.service")]
    [InlineData("NGINX.SERVICE", "nginx.service")]
    [InlineData("a,nginx.service,b", "Nginx.Service")]
    public void AnEntryEqualToTheSourceArgument_IsRejected(string value, string source)
    {
        Assert.False(TryRead(Exclude(value, source), out _, out var error));

        Assert.Contains("source argument", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ASourceAndADifferentExclusion_AreBothAccepted()
    {
        Assert.True(TryRead(Exclude("noise", "nginx.service"), out var query, out var error), error);

        Assert.Equal("nginx.service", query!.Source);
        Assert.Equal(["noise"], query.ExcludeSources);
    }

    [Fact]
    public void ANonStringValue_IsRejected()
    {
        Assert.False(TryRead(Args(j => j["excludeSources"] = 7), out _, out var error));

        Assert.Contains("string", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHardenSevenCrossFieldRules_StillComeFirst()
    {
        var accepted = SystemEventsArguments.TryRead(
            Args(j => { j["windowMinutes"] = 60; j["windowDays"] = 2; j["excludeSources"] = "a,,b"; }),
            Now, out _, out _, out _, out _, out var error);

        Assert.False(accepted);
        Assert.Equal("Use either windowMinutes or windowDays, not both.", error);
    }

    // ---- matching ----

    [Theory]
    [InlineData("svc", "SVC")]
    [InlineData("Service Control Manager", "service control manager")]
    public void AnExclusion_MatchesTheCanonicalSource_IgnoringCase(string source, string excluded)
    {
        Assert.False(SystemEventFilter.Matches(Event(source), Query([excluded])));
        Assert.True(SystemEventFilter.Matches(Event(source), Query(["other"])));
        Assert.True(SystemEventFilter.Matches(Event(source), Query([])));
        Assert.True(SystemEventFilter.Matches(Event(source), Query()));
    }

    [Fact]
    public void AnExclusion_MatchesWholeSourcesOnly_NeverASubstringOrAPrefix()
    {
        Assert.True(SystemEventFilter.Matches(Event("nginx.service"), Query(["nginx"])));
        Assert.True(SystemEventFilter.Matches(Event("nginx"), Query(["nginx.service"])));
        Assert.True(SystemEventFilter.Matches(Event("my nginx"), Query(["nginx"])));
    }

    [Fact]
    public void AnExclusion_NeverComparesTheUnit_UnlikeTheSourceFilter()
    {
        var record = Event("x", unit: "x.service");

        Assert.False(SystemEventFilter.Matches(record, Query(["x"])));
        Assert.True(SystemEventFilter.Matches(record, Query(["x.service"])));
        // The source filter, by contrast, also matches the unit.
        Assert.True(SystemEventFilter.Matches(record, Query(source: "x.service")));
    }

    [Fact]
    public void AnExclusion_AppliesWithTheOtherFilters()
    {
        var kept = Event("keep", message: "disk failure");
        var dropped = Event("drop", message: "disk failure");
        var query = Query(["drop"]) with { Text = "disk", EventId = "7", Channel = "System", MinSeverity = SystemEventSeverity.Error };

        Assert.True(SystemEventFilter.Matches(kept, query));
        Assert.False(SystemEventFilter.Matches(dropped, query));
    }

    // ---- output: raw and aggregate ----

    [Fact]
    public void ExcludedRecords_AreAbsentFromRows_AndFromObservedEvents_InRawMode()
    {
        var snapshot = Snapshot(Event("noise", 1), Event("noise", 2), Event("signal", 3), Event("Noise", 4));

        var json = Format(snapshot, Query(["noise"]));

        Assert.Equal(1, json["observedEvents"]!.GetValue<int>());
        Assert.Equal(1, json["returnedEvents"]!.GetValue<int>());
        Assert.Equal("signal", json["events"]![0]!["source"]!.GetValue<string>());
        Assert.Equal(["noise"], Echo(json));
    }

    [Fact]
    public void ExcludedRecords_AreAbsentFromGroups_AndFromObservedEvents_InAggregateMode()
    {
        var snapshot = Snapshot(Event("noise", 1), Event("noise", 2), Event("signal", 3), Event("signal", 4), Event("other", 5, channel: "Application"));

        var json = Format(snapshot, Query(["NOISE"]), EvidenceMode.Aggregate);

        Assert.Equal(3, json["observedEvents"]!.GetValue<int>());
        Assert.Equal(2, json["observedGroups"]!.GetValue<int>());
        Assert.DoesNotContain(json["groups"]!.AsArray(), group => string.Equals(group!["source"]!.GetValue<string>(), "noise", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["NOISE"], Echo(json));
    }

    [Theory]
    [InlineData(EvidenceMode.Raw, "events")]
    [InlineData(EvidenceMode.Aggregate, "groups")]
    public void AnExclusionThatRemovesEveryMatchingRecord_GivesAnEmptyResultWithTheEcho(EvidenceMode mode, string entries)
    {
        var json = Format(Snapshot(Event("noise", 1), Event("noise", 2)), Query(["noise"]), mode);

        Assert.Empty(json[entries]!.AsArray());
        Assert.Equal(0, json["observedEvents"]!.GetValue<int>());
        Assert.Equal(["noise"], Echo(json));
        Assert.Equal(2, json["schemaVersion"]!.GetValue<int>());
    }

    [Fact]
    public void AnUnknownExcludedSource_IsEchoed_AndRemovesNothing()
    {
        var json = Format(Snapshot(Event("signal")), Query(["No Such Source"]));

        Assert.Equal(1, json["observedEvents"]!.GetValue<int>());
        Assert.Equal(["No Such Source"], Echo(json));
    }

    [Fact]
    public void TheExclusion_IsAppliedBeforeTheLimit()
    {
        var snapshot = Snapshot(Event("noise", 1), Event("noise", 2), Event("noise", 3), Event("signal", 40));

        var json = Format(snapshot, Query(["noise"]), limit: 1);

        Assert.Equal("signal", json["events"]![0]!["source"]!.GetValue<string>());
        Assert.False(json["truncated"]!.GetValue<bool>());
    }

    // ---- the echo ----

    [Fact]
    public void WithoutTheArgument_TheEchoIsAnEmptyArray_RightAfterTheWindow_InBothModes()
    {
        foreach (var mode in new[] { EvidenceMode.Raw, EvidenceMode.Aggregate })
        {
            var json = Format(Snapshot(Event("a")), Query(), mode);

            Assert.Empty(json["excludeSources"]!.AsArray());
            var keys = json.Select(pair => pair.Key).ToList();
            Assert.Equal(keys.IndexOf("window") + 1, keys.IndexOf("excludeSources"));
            Assert.Equal(keys.IndexOf("excludeSources") + 1, keys.IndexOf("coverage"));
        }
    }

    [Fact]
    public void TheEcho_IsStable_ForPermutedInputAndRepeatedCalls()
    {
        Assert.True(TryRead(Exclude("Gamma, alpha,Beta"), out var one, out var error), error);
        Assert.True(TryRead(Exclude("Beta,Gamma,alpha"), out var two, out error), error);

        var first = SystemEventFormatting.Format(Snapshot(Event("x")), one!, EvidenceMode.Raw, 10, 32_768);
        var second = SystemEventFormatting.Format(Snapshot(Event("x")), two!, EvidenceMode.Raw, 10, 32_768);

        Assert.Equal(first, second);
        Assert.Equal(["alpha", "Beta", "Gamma"], Echo(JsonNode.Parse(first)!.AsObject()));
    }

    [Fact]
    public void TheEcho_KeepsTheCallersSpelling_NotTheSpellingOfTheRecords()
    {
        var json = Format(Snapshot(Event("Service Control Manager")), Query(["service control manager"]));

        Assert.Equal(["service control manager"], Echo(json));
        Assert.Equal(0, json["observedEvents"]!.GetValue<int>());
    }

    [Fact]
    public void TheByteBudget_CutsRowsAndNeverTheEcho()
    {
        var excluded = Enumerable.Range(0, 7).Select(i => new string((char)('a' + i), 128)).Append(new string('z', 121)).ToArray();
        var events = Enumerable.Range(0, 60).Select(i => Event("signal", i % 50, message: new string('m', 600))).ToArray();

        var json = Format(Snapshot(events), Query(excluded), limit: 60, maxBytes: 4_096);

        Assert.Equal(excluded.OrderBy(name => name, StringComparer.OrdinalIgnoreCase), Echo(json));
        Assert.True(json["returnedEvents"]!.GetValue<int>() < 60);
        Assert.True(json["truncated"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(EvidenceMode.Raw)]
    [InlineData(EvidenceMode.Aggregate)]
    public void TheLargestEnvelope_FitsTheSmallestBudget_WithTheFullEcho_AndOnlyRowsOrGroupsAreCut(EvidenceMode mode)
    {
        // Maximal echo (1,024 characters: the total bound), the two default Windows channels, every source with its longest detail,
        // and a coverage store per channel; then enough records to need cutting.
        var excluded = Enumerable.Range(0, 7).Select(i => new string((char)('a' + i), 128)).Append(new string('z', 121)).ToArray();
        var longDetail = new string('d', 300);
        var snapshot = new SystemEventSnapshot(
            Enumerable.Range(0, 200).Select(i => Event($"source-{i}", i % 55, channel: i % 2 == 0 ? "System" : "Application", message: new string('m', 2_000))).ToArray(),
            [
                new InventorySourceResult("windows.channel.System", InventorySourceStatus.Partial, longDetail) { ExaminedFromUtc = Now.AddMinutes(-50) },
                new InventorySourceResult("windows.channel.Application", InventorySourceStatus.Partial, longDetail) { ExaminedFromUtc = Now.AddMinutes(-50) },
            ],
            CollectionTruncated: true)
        {
            Stores =
            [
                new CoverageStore("windows.channel.System", CoverageBasis.EventLog, Now.AddMinutes(-30), long.MaxValue, ["windows.channel.System"]),
                new CoverageStore("windows.channel.Application", CoverageBasis.EventLog, Now.AddMinutes(-30), long.MaxValue, ["windows.channel.Application"]),
            ],
        };

        var output = SystemEventFormatting.Format(snapshot, Query(excluded) with { FromUtc = Now.AddDays(-180) }, mode, 500, 4_096);

        var bytes = Encoding.UTF8.GetByteCount(output);
        Assert.True(bytes <= 4_096, $"The envelope is {bytes} bytes.");
        var json = JsonNode.Parse(output)!.AsObject();
        Assert.Equal(excluded.OrderBy(name => name, StringComparer.OrdinalIgnoreCase), Echo(json));
        Assert.Equal(2, json["sources"]!.AsArray().Count);
        Assert.Equal(2, json["coverage"]!["stores"]!.AsArray().Count);
        Assert.False(json["complete"]!.GetValue<bool>());
    }

    [Fact]
    public void TheEnvelopeWithoutAnyRows_IsFarBelowTheSmallestBudget_SoOnlyRowsEverNeedCutting()
    {
        var excluded = Enumerable.Range(0, 7).Select(i => new string((char)('a' + i), 128)).Append(new string('z', 121)).ToArray();
        var longDetail = new string('d', 300);
        var snapshot = new SystemEventSnapshot(
            [],
            [
                new InventorySourceResult("windows.channel.System", InventorySourceStatus.Partial, longDetail) { ExaminedFromUtc = Now.AddMinutes(-50) },
                new InventorySourceResult("windows.channel.Application", InventorySourceStatus.Partial, longDetail) { ExaminedFromUtc = Now.AddMinutes(-50) },
            ])
        {
            Stores =
            [
                new CoverageStore("windows.channel.System", CoverageBasis.EventLog, Now.AddMinutes(-30), long.MaxValue, ["windows.channel.System"]),
                new CoverageStore("windows.channel.Application", CoverageBasis.EventLog, Now.AddMinutes(-30), long.MaxValue, ["windows.channel.Application"]),
            ],
        };

        var bytes = Encoding.UTF8.GetByteCount(SystemEventFormatting.Format(snapshot, Query(excluded), EvidenceMode.Raw, 500, 4_096));

        Assert.True(bytes < 4_096 - 512, $"The envelope alone is {bytes} bytes.");
    }

    [Theory]
    [InlineData(EvidenceMode.Raw)]
    [InlineData(EvidenceMode.Aggregate)]
    public void TheOutput_PromotesNoRecordTimeToAnOccurrenceTime_AndCarriesNoRelationCauseOrConfidenceField(EvidenceMode mode)
    {
        // ADR-0042 §8, §17 row 25: system.events exposes record times only (no timestampKind to promote), and HARDEN-9 adds no field
        // that relates records, names a cause or scores anything.
        var json = Format(Snapshot(Event("a", 1), Event("b", 2), Event("a", 3, unit: "a.service")), Query(["c"]), mode);

        var names = new List<string>();
        void Collect(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var (key, value) in obj)
                    {
                        names.Add(key);
                        Collect(value);
                    }

                    break;
                case JsonArray array:
                    foreach (var item in array)
                    {
                        Collect(item);
                    }

                    break;
            }
        }

        Collect(json);

        Assert.DoesNotContain("timestampKind", names);
        Assert.DoesNotContain("occurredUtc", names);
        foreach (var forbidden in new[] { "cause", "confidence", "relation", "correlat", "precededBy", "followedBy", "coOccurred", "possibleCause", "score", "likelihood" })
        {
            Assert.DoesNotContain(names, name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }

    // ---- the tool shell and the manifest ----

    [Fact]
    public async Task TheTool_PassesTheValidatedExclusionToTheCollector_AndEchoesIt()
    {
        var tool = new RecordingTool(Snapshot(Event("noise"), Event("signal")));

        var result = await tool.ExecuteAsync(Exclude(" noise ,zzz"));

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(["noise", "zzz"], tool.LastQuery!.ExcludeSources);
        var json = JsonNode.Parse(result.Output!)!.AsObject();
        Assert.Equal(["noise", "zzz"], Echo(json));
        Assert.Equal(1, json["observedEvents"]!.GetValue<int>());
    }

    [Fact]
    public async Task TheTool_RejectsAnInvalidExclusion_AsValidation_BeforeAnyCollection()
    {
        var tool = new RecordingTool(Snapshot());

        var result = await tool.ExecuteAsync(Exclude("a,,b"));

        Assert.Equal(ToolOutcome.Failure, result.Outcome);
        Assert.Equal(ToolFailureKind.Validation, result.FailureKind);
        Assert.Equal(0, tool.Collections);
    }

    [Fact]
    public void TheManifest_DeclaresTheStringParameterWithMachineReadableBounds()
    {
        var parameter = SystemToolManifests.Events("windows").Parameters.Single(p => p.Name == "excludeSources");

        Assert.Equal(ToolParameterType.String, parameter.Type);
        Assert.False(parameter.Required);
        Assert.False(parameter.Sensitive);
        Assert.Equal(1, parameter.MinLength);
        Assert.Equal(1_024, parameter.MaxLength);
        Assert.Null(parameter.AllowedValues);
    }

    [Fact]
    public void TheParameterDescription_StatesWhatTheConstraintsCannot()
    {
        var description = SystemToolManifests.Events("linux").Parameters.Single(p => p.Name == "excludeSources").Description;

        foreach (var phrase in new[] { "Comma-separated", "at most 8", "128", "1024", "case-insensitively", "source value shown", "No duplicates", "no empty entry", "none equal to source", "echoed", "not evidence of absence" })
        {
            Assert.Contains(phrase, description, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheToolDescription_SaysTheExclusionIsEchoedAndNeverEvidenceOfAbsence()
    {
        var description = SystemToolManifests.Events("windows").Description;

        Assert.Contains("excludeSources", description, StringComparison.Ordinal);
        Assert.Contains("never evidence that it logged nothing", description, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSchemaVersion_StaysTwo()
    {
        Assert.Equal(2, SystemEventFormatting.SchemaVersion);
        Assert.Contains("schemaVersion 2", SystemToolManifests.Events("windows").Description, StringComparison.Ordinal);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingTool(SystemEventSnapshot snapshot) : SystemEventsToolBase("fake", new FixedClock(Now))
    {
        public int Collections { get; private set; }

        public SystemEventQuery? LastQuery { get; private set; }

        protected override string? ValidateEventId(string eventId) => null;

        protected override string? ValidateChannel(string channel) => null;

        protected override Task<SystemEventSnapshot> CollectAsync(SystemEventQuery query, CancellationToken ct)
        {
            Collections++;
            LastQuery = query;
            return Task.FromResult(snapshot);
        }
    }
}
