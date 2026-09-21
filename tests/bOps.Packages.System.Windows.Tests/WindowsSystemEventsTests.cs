// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Sys.Conformance;
using bOps.Packages.Sys.Core;
using bOps.Packages.Sys.Windows;

namespace bOps.Packages.System.Windows.Tests;

[Trait("Platform", "Windows")]
public sealed class WindowsSystemEventsTests
{
    private static readonly string[] KnownChannels = ["System", "Application"];
    private static readonly string[] SourceStatuses = ["available", "partial", "unavailable"];

    private static ToolArguments Args(Action<JsonObject>? configure = null)
    {
        var json = new JsonObject();
        configure?.Invoke(json);
        return ToolArguments.FromJson(json);
    }

    private static async Task<JsonObject> RunAsync(WindowsSystemEventsTool tool, Action<JsonObject>? configure = null)
    {
        var result = await tool.ExecuteAsync(Args(configure));
        Assert.True(result.Succeeded, result.ErrorMessage);
        return JsonNode.Parse(result.Output!)!.AsObject();
    }

    // ---- the XPath (pure) ----

    private static readonly DateTimeOffset From = new(2026, 9, 21, 11, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 9, 21, 12, 0, 30, 250, TimeSpan.Zero);

    private static SystemEventQuery Query(Func<SystemEventQuery, SystemEventQuery>? change = null)
    {
        var query = new SystemEventQuery(From, To, null, null, null, null, null, 100);
        return change is null ? query : change(query);
    }

    [Fact]
    public void XPath_ForOnlyAWindow_IsTheTimeRange()
    {
        Assert.Equal(
            "*[System[TimeCreated[@SystemTime>='2026-09-21T11:00:00.000Z' and @SystemTime<='2026-09-21T12:00:30.250Z']]]",
            WindowsEventXPath.Build(Query(), providerName: null));
    }

    [Theory]
    [InlineData(SystemEventSeverity.Critical, "(Level=1)")]
    [InlineData(SystemEventSeverity.Error, "(Level=1 or Level=2)")]
    [InlineData(SystemEventSeverity.Warning, "(Level=1 or Level=2 or Level=3)")]
    [InlineData(SystemEventSeverity.Information, "(Level=1 or Level=2 or Level=3 or Level=4)")]
    [InlineData(SystemEventSeverity.Verbose, "(Level=1 or Level=2 or Level=3 or Level=4 or Level=5)")]
    public void XPath_MinimumSeverity_IsEveryLevelAtLeastThatSevere(SystemEventSeverity minimum, string expectedLevels)
    {
        var xpath = WindowsEventXPath.Build(Query(q => q with { MinSeverity = minimum }), providerName: null);

        Assert.Contains(" and " + expectedLevels, xpath, StringComparison.Ordinal);
    }

    [Fact]
    public void XPath_ProviderAndEventId_AreAddedWhenGiven()
    {
        var xpath = WindowsEventXPath.Build(Query(q => q with { EventId = "7036" }), "Service Control Manager");

        Assert.EndsWith(" and Provider[@Name='Service Control Manager'] and EventID=7036]]", xpath, StringComparison.Ordinal);
    }

    [Fact]
    public void XPath_HasNoProviderCondition_WhenTheProviderIsResolvedAfterwards()
    {
        var xpath = WindowsEventXPath.Build(Query(q => q with { Source = "anything" }), providerName: null);

        Assert.DoesNotContain("Provider", xpath, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("x' or '1'='1")]
    [InlineData("a]")]
    [InlineData("a\"b")]
    [InlineData("a*b")]
    [InlineData("a=b")]
    public void XPath_RefusesAProviderNameThatCouldChangeTheQuery(string provider) =>
        Assert.Throws<ArgumentException>(() => WindowsEventXPath.Build(Query(), provider));

    [Theory]
    [InlineData("7036 or 1=1")]
    [InlineData("-1")]
    [InlineData("65536")]
    [InlineData("abc")]
    [InlineData("")]
    public void XPath_RefusesAnEventIdThatIsNotANumberInRange(string eventId) =>
        Assert.Throws<ArgumentException>(() => WindowsEventXPath.Build(Query(q => q with { EventId = eventId }), providerName: null));

    [Theory]
    [InlineData("0")]
    [InlineData("65535")]
    public void XPath_AcceptsTheEventIdBounds(string eventId) =>
        Assert.Contains("EventID=" + eventId, WindowsEventXPath.Build(Query(q => q with { EventId = eventId }), providerName: null), StringComparison.Ordinal);

    // ---- the real Event Log ----

    [WindowsOnlyFact]
    public Task SystemEvents_Conform() =>
        SystemToolConformance.AssertSystemEventsConformAsync(new WindowsSystemEventsTool(), "windows");

    [WindowsOnlyFact]
    public Task SystemEvents_RejectBadArguments() =>
        SystemToolConformance.AssertSystemEventsRejectBadArgumentsAsync(new WindowsSystemEventsTool());

    [WindowsOnlyFact]
    public async Task ARealReadOfTheSystemAndApplicationLogs_ReturnsNormalizedEvents_AndNamesBothSources()
    {
        var json = await RunAsync(new WindowsSystemEventsTool(), j => { j["windowMinutes"] = 10_080; j["limit"] = 500; j["maxOutputBytes"] = 65_536; });

        var names = json["sources"]!.AsArray().Select(s => s!["name"]!.GetValue<string>()).ToArray();
        Assert.Equal(["windows.channel.Application", "windows.channel.System"], names);
        Assert.NotEmpty(json["events"]!.AsArray());
        Assert.All(json["events"]!.AsArray(), e =>
        {
            Assert.Contains(e!["channel"]!.GetValue<string>(), KnownChannels, StringComparer.OrdinalIgnoreCase);
            Assert.True(int.TryParse(e["eventId"]!.GetValue<string>(), out _));
            Assert.Null(e["unit"]);
        });
    }

    [WindowsOnlyFact]
    public async Task Filters_SelectExactlyWhatTheyAskFor_AgainstRealEvents()
    {
        var tool = new WindowsSystemEventsTool();
        var all = await RunAsync(tool, j => { j["windowMinutes"] = 10_080; j["limit"] = 500; j["maxOutputBytes"] = 65_536; });
        var sample = all["events"]!.AsArray().Select(e => e!.AsObject()).First(e => e["severity"]!.GetValue<string>() != "unknown");
        var source = sample["source"]!.GetValue<string>();
        var eventId = sample["eventId"]!.GetValue<string>();
        var channel = sample["channel"]!.GetValue<string>();
        var severity = sample["severity"]!.GetValue<string>();

        var filtered = await RunAsync(tool, j =>
        {
            j["windowMinutes"] = 10_080;
            j["limit"] = 500;
            j["maxOutputBytes"] = 65_536;
            j["source"] = source.ToUpperInvariant();
            j["eventId"] = eventId;
            j["channel"] = channel.ToLowerInvariant();
            j["minSeverity"] = severity;
        });

        var events = filtered["events"]!.AsArray().Select(e => e!.AsObject()).ToArray();
        Assert.Contains(events, e => e["timestampUtc"]!.GetValue<string>() == sample["timestampUtc"]!.GetValue<string>()
            && e["message"]!.GetValue<string>() == sample["message"]!.GetValue<string>());
        Assert.All(events, e =>
        {
            Assert.Equal(source, e["source"]!.GetValue<string>(), ignoreCase: true);
            Assert.Equal(eventId, e["eventId"]!.GetValue<string>());
            Assert.Equal(channel, e["channel"]!.GetValue<string>(), ignoreCase: true);
        });
    }

    [Fact]
    public void AMessage_IsCappedOneCharacterAboveTheResultLimit_SoTheFilterSeesWhatTheResultCanShow()
    {
        Assert.Equal(new string('a', 2_001), WindowsSystemEventsTool.CapMessage(new string('a', 2_001)));
        Assert.Equal(new string('a', 2_001), WindowsSystemEventsTool.CapMessage(new string('a', 50_000)));
        Assert.Equal("short", WindowsSystemEventsTool.CapMessage("short"));
    }

    [WindowsOnlyFact]
    public async Task TheTextFilter_IsACaseInsensitiveSubstringOfTheRealMessage()
    {
        var tool = new WindowsSystemEventsTool();
        var all = await RunAsync(tool, j => { j["windowMinutes"] = 10_080; j["limit"] = 500; j["maxOutputBytes"] = 65_536; });
        var message = all["events"]!.AsArray().Select(e => e!["message"]!.GetValue<string>()).First(m => m.Length >= 12);
        var fragment = message.Substring(3, 8).Trim();
        Assert.False(string.IsNullOrWhiteSpace(fragment));

        var filtered = await RunAsync(tool, j =>
        {
            j["windowMinutes"] = 10_080;
            j["limit"] = 500;
            j["maxOutputBytes"] = 65_536;
            j["text"] = fragment.ToUpperInvariant().Replace('\r', ' ').Replace('\n', ' ');
        });

        Assert.All(filtered["events"]!.AsArray(), e =>
            Assert.Contains(fragment.Replace('\r', ' ').Replace('\n', ' '), e!["message"]!.GetValue<string>().Replace('\r', ' ').Replace('\n', ' '), StringComparison.OrdinalIgnoreCase));
    }

    [WindowsOnlyFact]
    public async Task AChannelThatDoesNotExist_IsReportedAsUnavailable_NotAsAnEmptyHealthyLog()
    {
        var json = await RunAsync(new WindowsSystemEventsTool(), j => j["channel"] = "Bops-Nonexistent/Channel");

        Assert.Equal("unavailable", json["status"]!.GetValue<string>());
        Assert.False(json["complete"]!.GetValue<bool>());
        Assert.Empty(json["events"]!.AsArray());
        var source = json["sources"]![0]!.AsObject();
        Assert.Equal("windows.channel.Bops-Nonexistent/Channel", source["name"]!.GetValue<string>());
        Assert.Equal("unavailable", source["status"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(source["detail"]!.GetValue<string>()));
    }

    [WindowsOnlyFact]
    public async Task AProtectedChannel_IsEitherReadOrReportedAsUnavailable_NeverSilentlyEmpty()
    {
        var json = await RunAsync(new WindowsSystemEventsTool(), j => j["channel"] = "Security");

        var source = json["sources"]![0]!.AsObject();
        var status = source["status"]!.GetValue<string>();
        Assert.Contains(status, SourceStatuses);
        if (status == "unavailable")
        {
            Assert.Equal("unavailable", json["status"]!.GetValue<string>());
            Assert.False(string.IsNullOrWhiteSpace(source["detail"]!.GetValue<string>()));
            Assert.False(json["complete"]!.GetValue<bool>());
        }
    }

    [WindowsOnlyFact]
    public async Task ARealResult_NeverPutsAnEventMessageIntoTheAuditSummary()
    {
        var tool = new WindowsSystemEventsTool();
        var arguments = Args(j => { j["windowMinutes"] = 10_080; j["limit"] = 20; });
        var result = await tool.ExecuteAsync(arguments);
        Assert.True(result.Succeeded, result.ErrorMessage);

        var summary = tool.CreateAuditSummary(arguments, result)!.ToJsonString();
        var messages = JsonNode.Parse(result.Output!)!["events"]!.AsArray()
            .Select(e => e!["message"]!.GetValue<string>())
            .Where(m => m.Length >= 20);

        Assert.All(messages, message => Assert.DoesNotContain(message, summary, StringComparison.Ordinal));
    }

    [WindowsOnlyFact]
    public async Task ANonNumericEventId_IsRejected_BeforeAnyRead()
    {
        var result = await new WindowsSystemEventsTool().ExecuteAsync(Args(j => j["eventId"] = "abc"));

        Assert.Equal(ToolOutcome.Failure, result.Outcome);
        Assert.Contains("number", result.ErrorMessage, StringComparison.Ordinal);
    }

    [WindowsOnlyFact]
    public async Task ACancelledToken_StopsTheRead()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new WindowsSystemEventsTool().ExecuteAsync(Args(j => j["windowMinutes"] = 10_080), cts.Token));
    }

    [WindowsOnlyFact]
    public async Task ATimeout_StopsTheRead_AndSaysTheAnswerIsIncomplete()
    {
        var tool = new WindowsSystemEventsTool(TimeProvider.System, TimeSpan.Zero);

        var json = await RunAsync(tool, j => { j["windowMinutes"] = 10_080; j["limit"] = 5; });

        Assert.False(json["complete"]!.GetValue<bool>());
        Assert.True(json["truncated"]!.GetValue<bool>());
        Assert.All(json["sources"]!.AsArray(), s => Assert.Equal("partial", s!["status"]!.GetValue<string>()));
    }
}
