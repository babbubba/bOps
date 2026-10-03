// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Sys.Conformance;
using bOps.Packages.Sys.Core;
using bOps.Packages.Sys.Windows;

namespace bOps.Packages.System.Windows.Tests;

/// <summary>
/// ADR-0032 HARDEN-9 amendment §2: on Windows <c>excludeSources</c> is excluded natively, in the Event Log XPath, before the scan
/// ceiling, with the shared post-filter as defence in depth. The XPath is built only from validated or registered names; the largest
/// shape (eight exclusion terms, five levels, a provider and an event id) is run on the real Event Log, straight through the reader,
/// so the native exclusion is proven without the post-filter (accepted finding N5).
/// </summary>
[Trait("Platform", "Windows")]
public sealed class WindowsExcludeSourcesTests
{
    private static readonly DateTimeOffset From = new(2026, 9, 21, 11, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 9, 21, 12, 0, 30, 250, TimeSpan.Zero);

    private static SystemEventQuery Query(Func<SystemEventQuery, SystemEventQuery>? change = null)
    {
        var query = new SystemEventQuery(From, To, null, null, null, null, null, 100);
        return change is null ? query : change(query);
    }

    private static async Task<JsonObject> RunAsync(WindowsSystemEventsTool tool, Action<JsonObject> configure)
    {
        var json = new JsonObject();
        configure(json);
        var result = await tool.ExecuteAsync(ToolArguments.FromJson(json));
        Assert.True(result.Succeeded, result.ErrorMessage);
        return JsonNode.Parse(result.Output!)!.AsObject();
    }

    // ---- the XPath (pure) ----

    [Fact]
    public void XPath_Exclusion_IsOneProviderPredicate_WithANotEqualTermPerName()
    {
        var xpath = WindowsEventXPath.Build(Query(), providerName: null, ["Windows Error Reporting", ".NET Runtime"]);

        Assert.Equal(
            "*[System[TimeCreated[@SystemTime>='2026-09-21T11:00:00.000Z' and @SystemTime<='2026-09-21T12:00:30.250Z']"
            + " and Provider[@Name!='Windows Error Reporting' and @Name!='.NET Runtime']]]",
            xpath);
        Assert.DoesNotContain("not(", xpath, StringComparison.Ordinal);
    }

    [Fact]
    public void XPath_Exclusion_ComesAfterTheProviderEquality_AndBeforeTheEventId()
    {
        var xpath = WindowsEventXPath.Build(
            Query(q => q with { EventId = "7036", MinSeverity = SystemEventSeverity.Error }), "Service Control Manager", ["noise"]);

        Assert.EndsWith(
            " and (Level=1 or Level=2) and Provider[@Name='Service Control Manager'] and Provider[@Name!='noise'] and EventID=7036]]",
            xpath,
            StringComparison.Ordinal);
    }

    [Fact]
    public void XPath_HasNoExclusionTerm_WhenNothingIsExcluded()
    {
        Assert.DoesNotContain("!=", WindowsEventXPath.Build(Query(), providerName: null), StringComparison.Ordinal);
        Assert.DoesNotContain("!=", WindowsEventXPath.Build(Query(), providerName: null, null), StringComparison.Ordinal);
        Assert.DoesNotContain("!=", WindowsEventXPath.Build(Query(), providerName: null, []), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("x' or '1'='1")]
    [InlineData("a]")]
    [InlineData("a\"b")]
    [InlineData("a*b")]
    [InlineData("a=b")]
    [InlineData("a|b")]
    [InlineData("a\nb")]
    [InlineData("")]
    public void XPath_RefusesAnExcludedNameThatCouldChangeTheQuery(string provider) =>
        Assert.Throws<ArgumentException>(() => WindowsEventXPath.Build(Query(), providerName: null, ["fine", provider]));

    [Fact]
    public void XPath_TheLargestShape_HasEightExclusions_FiveLevels_AProviderAndAnEventId()
    {
        var excluded = Enumerable.Range(0, 8).Select(i => new string((char)('a' + i), 120)).ToArray();

        var xpath = WindowsEventXPath.Build(
            Query(q => q with { MinSeverity = SystemEventSeverity.Verbose, EventId = "65535" }), new string('p', 128), excluded);

        Assert.Equal(8, xpath.Split("@Name!=").Length - 1);
        Assert.Contains("(Level=1 or Level=2 or Level=3 or Level=4 or Level=5)", xpath, StringComparison.Ordinal);
        Assert.Contains("Provider[@Name='" + new string('p', 128) + "']", xpath, StringComparison.Ordinal);
        Assert.EndsWith(" and EventID=65535]]", xpath, StringComparison.Ordinal);
        Assert.True(xpath.Length < 2_048, $"The largest XPath is {xpath.Length} characters.");
    }

    // ---- resolution to the registered spelling (pure) ----

    [Fact]
    public void TheExclusion_IsResolvedToTheRegisteredSpelling_WhenOneExists_ElseTheEntryItself()
    {
        var resolved = WindowsSystemEventsTool.ResolveAgainst(
            ["windows error reporting", "No Such Provider", "dotnet runtime"],
            [".NET Runtime", "Windows Error Reporting", "WINDOWS ERROR REPORTING"]);

        Assert.Equal(["Windows Error Reporting", "No Such Provider", "dotnet runtime"], resolved);
    }

    [Fact]
    public void TheExclusion_KeepsTheValidatedEntry_WhenTheRegisteredSpellingIsOutsideTheSourceCharacterSet()
    {
        var resolved = WindowsSystemEventsTool.ResolveAgainst(["odd provider"], ["ODD PROVIDER'"]);
        Assert.Equal(["odd provider"], resolved);

        var unsafeFirst = WindowsSystemEventsTool.ResolveAgainst(["odd provider"], ["Odd Provider"]);
        Assert.Equal(["Odd Provider"], unsafeFirst);
    }

    [Fact]
    public void TheResolution_IsEmpty_WhenNothingIsExcluded()
    {
        Assert.Empty(WindowsSystemEventsTool.ResolveExcludedProviders(null));
        Assert.Empty(WindowsSystemEventsTool.ResolveExcludedProviders([]));
    }

    // ---- the shared post-filter (defence in depth) ----

    [Fact]
    public void ARecordWhoseSpellingDiffers_IsRemovedByTheSharedPostFilter()
    {
        var record = new SystemEventRecord(From.AddMinutes(10), SystemEventSeverity.Error, "Windows Error Reporting", "1001", "Application", "m", 4, null);

        Assert.False(SystemEventFilter.Matches(record, Query(q => q with { ExcludeSources = ["windows ERROR reporting"] })));
        Assert.True(SystemEventFilter.Matches(record, Query(q => q with { ExcludeSources = ["Windows Error"] })));
    }

    [Fact]
    public void TheTool_OffersNoParameterThatCarriesQuerySyntax()
    {
        var parameters = new WindowsSystemEventsTool().Manifest.Parameters;

        Assert.Contains(parameters, p => p is { Name: "excludeSources", Type: ToolParameterType.String });
        Assert.DoesNotContain(parameters, p => p.Name.Contains("xpath", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("query", StringComparison.OrdinalIgnoreCase));
    }

    // ---- the real Event Log ----

    [WindowsOnlyFact]
    public Task ExcludeSources_ConformsInBothModes_AgainstTheRealLog() =>
        SystemToolConformance.AssertSystemEventsExcludeSourcesConformAsync(new WindowsSystemEventsTool());

    [WindowsOnlyFact]
    public async Task AnExclusionInAnotherCase_StillRemovesTheProvider_FromARealRead()
    {
        var tool = new WindowsSystemEventsTool();
        var all = await RunAsync(tool, j => { j["mode"] = "aggregate"; j["windowDays"] = 7; j["limit"] = 200; j["maxOutputBytes"] = 65_536; });
        var noisiest = all["groups"]!.AsArray()
            .GroupBy(g => g!["source"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Sum(i => i!["count"]!.GetValue<int>()))
            .FirstOrDefault();
        if (noisiest is null)
        {
            return; // an idle host: nothing to exclude
        }

        var swapped = new string([.. noisiest.Key.Select(c => char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c))]);
        var json = await RunAsync(tool, j => { j["windowMinutes"] = 10_080; j["limit"] = 500; j["maxOutputBytes"] = 65_536; j["excludeSources"] = swapped; });

        Assert.Equal([swapped], json["excludeSources"]!.AsArray().Select(i => i!.GetValue<string>()).ToArray());
        Assert.DoesNotContain(
            json["events"]!.AsArray(), e => string.Equals(e!["source"]!.GetValue<string>(), noisiest.Key, StringComparison.OrdinalIgnoreCase));
    }

    [WindowsOnlyFact]
    public async Task ALegacyCallWithoutExcludeSources_IsUnchanged_AndEchoesAnEmptyArray()
    {
        var json = await RunAsync(new WindowsSystemEventsTool(), j => { j["windowMinutes"] = 60; j["limit"] = 5; });

        Assert.Empty(json["excludeSources"]!.AsArray());
        Assert.Equal(2, json["schemaVersion"]!.GetValue<int>());
        var keys = json.Select(pair => pair.Key).ToList();
        Assert.Equal(keys.IndexOf("window") + 1, keys.IndexOf("excludeSources"));
    }

    /// <summary>
    /// The native <c>Provider[@Name!=…]</c> exclusion is honoured by the real Event Log (ADR-0032 HARDEN-9 amendment §2, finding N5).
    /// The largest XPath the tool can build — eight exclusion terms, all five levels, a provider and an event id, and the window — is
    /// run straight through the Event Log reader (no shared post-filter involved) on both default channels: the query is accepted, the
    /// read completes within a bound, no returned record comes from an excluded provider, and a record that is not excluded is read.
    /// </summary>
    [WindowsOnlyFact]
    public void TheLargestNativeXPath_IsAcceptedByTheRealEventLog_AndExcludesNatively()
    {
        foreach (var channel in new[] { "System", "Application" })
        {
            var to = DateTimeOffset.UtcNow;
            var window = new SystemEventQuery(to.AddDays(-30), to, SystemEventSeverity.Verbose, null, null, null, null, 2_000);

            // Sample the channel for real providers and a real record to anchor the maximal query on.
            var sample = ReadNative(channel, WindowsEventXPath.Build(window, providerName: null), maximum: 2_000)
                .Where(r => r.Level is >= 1 and <= 5 && WindowsEventXPath.IsSafeProviderName(r.Provider))
                .ToList();
            if (sample.Count == 0)
            {
                // Nothing readable here: only the acceptance of the largest shape can be asserted.
                var emptyShape = WindowsEventXPath.Build(window with { EventId = "65535" }, "Bops Nonexistent Provider", [.. Padding(8)]);
                Assert.Empty(ReadNative(channel, emptyShape, maximum: 100));
                continue;
            }

            var anchor = sample[0];
            var others = sample
                .Select(r => r.Provider)
                .Where(p => !string.Equals(p, anchor.Provider, StringComparison.OrdinalIgnoreCase))
                .GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .Take(8)
                .ToList();
            var excluded = others.Concat(Padding(8 - others.Count)).ToList();
            Assert.Equal(8, excluded.Count);

            // The largest shape: 8 != terms, 5 Level terms, Provider = anchor, EventID = anchor and the window.
            var xpath = WindowsEventXPath.Build(
                window with { EventId = anchor.EventId.ToString(CultureInfo.InvariantCulture) }, anchor.Provider, excluded);
            Assert.Equal(8, xpath.Split("@Name!=").Length - 1);

            var started = DateTimeOffset.UtcNow;
            var read = ReadNative(channel, xpath, maximum: 500);
            var elapsed = DateTimeOffset.UtcNow - started;

            Assert.True(elapsed < TimeSpan.FromSeconds(20), $"{channel}: the read took {elapsed.TotalSeconds:0.0} s.");
            Assert.DoesNotContain(read, r => excluded.Contains(r.Provider, StringComparer.OrdinalIgnoreCase));
            Assert.Contains(read, r => string.Equals(r.Provider, anchor.Provider, StringComparison.OrdinalIgnoreCase) && r.EventId == anchor.EventId);

            // Excluding the anchor's own provider with the same construct removes it, with no post-filter in between.
            var removed = ReadNative(channel, WindowsEventXPath.Build(window, providerName: null, [anchor.Provider]), maximum: 1_000);
            Assert.DoesNotContain(removed, r => string.Equals(r.Provider, anchor.Provider, StringComparison.OrdinalIgnoreCase));
        }
    }

    [WindowsOnlyFact]
    public async Task ARealCallInTheLargestShape_CompletesWithinItsBound_AndLeaksNoExcludedProvider()
    {
        var tool = new WindowsSystemEventsTool();
        var excluded = string.Join(',', Padding(7).Append("Windows Error Reporting"));

        foreach (var channel in new[] { "System", "Application" })
        {
            var json = await RunAsync(tool, j =>
            {
                j["channel"] = channel;
                j["windowMinutes"] = 10_080;
                j["minSeverity"] = "verbose";
                j["source"] = "Service Control Manager";
                j["eventId"] = "7036";
                j["excludeSources"] = excluded;
                j["limit"] = 50;
                j["maxOutputBytes"] = 4_096;
            });

            Assert.Equal(8, json["excludeSources"]!.AsArray().Count);
            Assert.True(json["status"]!.GetValue<string>() is "complete" or "partial" or "unavailable");
            Assert.DoesNotContain(
                json["events"]!.AsArray(), e => string.Equals(e!["source"]!.GetValue<string>(), "Windows Error Reporting", StringComparison.OrdinalIgnoreCase));
        }
    }

    private static IEnumerable<string> Padding(int count) =>
        Enumerable.Range(0, Math.Max(0, count)).Select(i => $"Bops Nonexistent Provider {i}");

    private static List<(string Provider, int EventId, int? Level)> ReadNative(string channel, string xpath, int maximum)
    {
        var records = new List<(string, int, int?)>();
        try
        {
            using var reader = new EventLogReader(new EventLogQuery(channel, PathType.LogName, xpath) { ReverseDirection = true, TolerateQueryErrors = false });
            while (records.Count < maximum && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    records.Add((record.ProviderName ?? string.Empty, record.Id, record.Level));
                }
            }
        }
        catch (EventLogNotFoundException)
        {
            // The channel does not exist on this host: no records.
        }

        return records;
    }
}
