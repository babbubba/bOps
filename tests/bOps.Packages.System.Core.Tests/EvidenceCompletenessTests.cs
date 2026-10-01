// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.System.Core.Tests;

/// <summary>
/// HARDEN-6 / ADR-0022: the system tools state the completeness of their evidence as a typed <see cref="ToolResultCompleteness"/>
/// derived from the very fields their formatter emitted, so <c>Success</c> no longer has to be read as "everything was collected"
/// and the typed value cannot disagree with the legacy <c>complete</c>/<c>status</c>/<c>truncated</c>/<c>partial</c> fields.
/// </summary>
public sealed class EvidenceCompletenessTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("""{"complete":true,"status":"complete","truncated":false}""", ToolResultCompleteness.Complete)]
    [InlineData("""{"complete":false,"status":"partial","truncated":false}""", ToolResultCompleteness.Partial)]
    [InlineData("""{"complete":false,"status":"complete","truncated":true}""", ToolResultCompleteness.Partial)]
    [InlineData("""{"complete":false,"status":"unavailable","truncated":false}""", ToolResultCompleteness.Unavailable)]
    [InlineData("""{"status":"complete","truncated":false}""", ToolResultCompleteness.Complete)]
    [InlineData("""{"status":"complete","truncated":true}""", ToolResultCompleteness.Partial)]
    [InlineData("""{"status":"partial","truncated":false}""", ToolResultCompleteness.Partial)]
    [InlineData("""{"status":"unavailable","truncated":false}""", ToolResultCompleteness.Unavailable)]
    [InlineData("""{"status":"unsupported"}""", ToolResultCompleteness.Unavailable)]
    [InlineData("""{"exists":true,"partial":false}""", ToolResultCompleteness.Complete)]
    [InlineData("""{"exists":true,"partial":true}""", ToolResultCompleteness.Partial)]
    [InlineData("""{"exists":false,"partial":true}""", ToolResultCompleteness.Unavailable)]
    [InlineData("""{"rootFound":false,"partial":true}""", ToolResultCompleteness.Unavailable)]
    [InlineData("""{"exists":false,"status":"available","truncated":false}""", ToolResultCompleteness.Unavailable)]
    [InlineData("""{"exists":false,"complete":false,"truncated":false}""", ToolResultCompleteness.Unavailable)]
    [InlineData("""{"rootFound":false,"complete":false,"truncated":false}""", ToolResultCompleteness.Unavailable)]
    [InlineData("""{"rootFound":true,"complete":false,"truncated":true}""", ToolResultCompleteness.Partial)]
    [InlineData("""{"hostname":"a"}""", ToolResultCompleteness.Unspecified)]
    [InlineData("""[1,2]""", ToolResultCompleteness.Unspecified)]
    [InlineData("not json", ToolResultCompleteness.Unspecified)]
    [InlineData("", ToolResultCompleteness.Unspecified)]
    public void FromOutput_MapsTheCompletenessFieldsDeterministically(string output, ToolResultCompleteness expected) =>
        Assert.Equal(expected, EvidenceCompleteness.FromOutput(output));

    [Fact]
    public void FromOutput_OfNull_IsUnspecified() =>
        Assert.Equal(ToolResultCompleteness.Unspecified, EvidenceCompleteness.FromOutput(null));

    // ---- system.crashes (and its maintenance siblings) ----

    [Fact]
    public async Task Crashes_OnePartialSource_YieldsPartial_AndTheLegacyFieldsAgree()
    {
        var snapshot = new MaintenanceSnapshot<CrashRecord>(
            [Crash("app")],
            [new MaintenanceSource("wer", InventorySourceStatus.Available), new MaintenanceSource("minidumps", InventorySourceStatus.Partial, "directory not readable")]);

        var result = await new CrashesTool(snapshot).ExecuteAsync(ToolArguments.Empty);

        Assert.True(result.Succeeded);
        Assert.Equal(ToolResultCompleteness.Partial, result.Completeness);
        AssertLegacyFieldsAgree(result);
    }

    [Fact]
    public async Task Crashes_EveryApplicableSourceAvailable_YieldsComplete()
    {
        var snapshot = new MaintenanceSnapshot<CrashRecord>([Crash("app")], [new MaintenanceSource("wer", InventorySourceStatus.Available)]);

        var result = await new CrashesTool(snapshot).ExecuteAsync(ToolArguments.Empty);

        Assert.Equal(ToolResultCompleteness.Complete, result.Completeness);
        AssertLegacyFieldsAgree(result);
    }

    [Fact]
    public async Task Crashes_NoSourceCouldBeRead_YieldsUnavailable_NotAnEmptyLookingSuccess()
    {
        var snapshot = new MaintenanceSnapshot<CrashRecord>([], [new MaintenanceSource("wer", InventorySourceStatus.Unavailable, "access denied")]);

        var result = await new CrashesTool(snapshot).ExecuteAsync(ToolArguments.Empty);

        Assert.True(result.Succeeded);
        Assert.Equal(ToolResultCompleteness.Unavailable, result.Completeness);
        AssertLegacyFieldsAgree(result);
    }

    [Fact]
    public async Task Crashes_CutByTheLimit_YieldsPartialEvenThoughEverySourceWasAvailable()
    {
        var snapshot = new MaintenanceSnapshot<CrashRecord>(
            [Crash("a"), Crash("b")], [new MaintenanceSource("wer", InventorySourceStatus.Available)]);

        var result = await new CrashesTool(snapshot).ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["limit"] = 1 }));

        Assert.Equal(ToolResultCompleteness.Partial, result.Completeness);
        AssertLegacyFieldsAgree(result);
    }

    [Fact]
    public async Task Crashes_ArgumentRejectedByThePackage_IsAValidationFailureWithNoCompleteness()
    {
        var result = await new CrashesTool(new MaintenanceSnapshot<CrashRecord>([], [])).ExecuteAsync(
            ToolArguments.FromJson(new JsonObject { ["sinceMinutes"] = 0 }));

        Assert.False(result.Succeeded);
        Assert.Equal(ToolFailureKind.Validation, result.FailureKind);
        Assert.Equal(ToolResultCompleteness.Unspecified, result.Completeness);
        Assert.Equal("sinceMinutes must be between 1 and 10080.", result.ErrorMessage);
    }

    // ---- system.events ----

    [Theory]
    [InlineData(InventorySourceStatus.Available, ToolResultCompleteness.Complete)]
    [InlineData(InventorySourceStatus.Partial, ToolResultCompleteness.Partial)]
    [InlineData(InventorySourceStatus.Unavailable, ToolResultCompleteness.Unavailable)]
    [InlineData(InventorySourceStatus.Unsupported, ToolResultCompleteness.Unavailable)]
    public async Task Events_SourceStatusDrivesCompleteness_AndTheLegacyFieldsAgree(InventorySourceStatus source, ToolResultCompleteness expected)
    {
        var snapshot = new SystemEventSnapshot([Event("first")], [new InventorySourceResult("System", source, null)]);

        var result = await new EventsTool(snapshot).ExecuteAsync(ToolArguments.Empty);

        Assert.True(result.Succeeded);
        Assert.Equal(expected, result.Completeness);
        AssertLegacyFieldsAgree(result);
    }

    [Fact]
    public async Task Events_TrimmedByTheByteBound_BecomesPartial_EvenWhenEverySourceWasAvailable()
    {
        var events = Enumerable.Range(0, 60).Select(index => Event($"message {index} {new string('x', 400)}", index)).ToArray();
        var snapshot = new SystemEventSnapshot(events, [new InventorySourceResult("System", InventorySourceStatus.Available, null)]);

        var result = await new EventsTool(snapshot).ExecuteAsync(
            ToolArguments.FromJson(new JsonObject { ["limit"] = 60, ["maxOutputBytes"] = 4096 }));

        Assert.True(result.Succeeded);
        Assert.Equal(ToolResultCompleteness.Partial, result.Completeness);
        var json = JsonNode.Parse(result.Output!)!.AsObject();
        Assert.True(json["truncated"]!.GetValue<bool>());
        Assert.False(json["complete"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Events_OutOfRangeWindow_IsAValidationFailure()
    {
        var result = await new EventsTool(new SystemEventSnapshot([], [])).ExecuteAsync(
            ToolArguments.FromJson(new JsonObject { ["windowMinutes"] = 99999 }));

        Assert.Equal(ToolFailureKind.Validation, result.FailureKind);
        Assert.Equal("windowMinutes must be between 1 and 10080.", result.ErrorMessage);
    }

    // ---- system.apps ----

    [Fact]
    public void Inventory_PartialSource_IsPartial_AndCompleteSourceIsComplete()
    {
        var item = new ApplicationInventoryItem("id", "name", "1.0", "publisher", "registry");
        var partial = SystemInventoryFormatting.FormatApplications(
            new InventorySnapshot<ApplicationInventoryItem>([item], [new InventorySourceResult("registry", InventorySourceStatus.Partial, "one hive unreadable")]), 10, 32768);
        var complete = SystemInventoryFormatting.FormatApplications(
            new InventorySnapshot<ApplicationInventoryItem>([item], [new InventorySourceResult("registry", InventorySourceStatus.Available, null)]), 10, 32768);

        Assert.Equal(ToolResultCompleteness.Partial, EvidenceCompleteness.FromOutput(partial));
        Assert.Equal(ToolResultCompleteness.Complete, EvidenceCompleteness.FromOutput(complete));
    }

    // ---- a tool with nothing to say stays exactly as it was ----

    [Fact]
    public async Task ToolsWithoutACompletenessConcept_StayUnspecified()
    {
        var result = await new InfoTool().ExecuteAsync(ToolArguments.Empty);

        Assert.True(result.Succeeded);
        Assert.Equal(ToolResultCompleteness.Unspecified, result.Completeness);
        Assert.Equal(ToolFailureKind.Unspecified, result.FailureKind);
    }

    // ---- helpers ----

    /// <summary>The typed value must be a function of the legacy fields the same output carries.</summary>
    private static void AssertLegacyFieldsAgree(ToolCallResult result)
    {
        var json = JsonNode.Parse(result.Output!)!.AsObject();
        var complete = json["complete"]!.GetValue<bool>();
        var status = json["status"]!.GetValue<string>();
        var expected = complete
            ? ToolResultCompleteness.Complete
            : status == "unavailable" ? ToolResultCompleteness.Unavailable : ToolResultCompleteness.Partial;

        Assert.Equal(expected, result.Completeness);
    }

    private static CrashRecord Crash(string process) => new(Now, process, 5, "segfault", null, "e1", "crashed", "wer");

    private static SystemEventRecord Event(string message, int minute = 0) =>
        new(Now.AddMinutes(-minute), SystemEventSeverity.Error, "Service Control Manager", "7034", "System", message, 4, "services");

    private sealed class CrashesTool(MaintenanceSnapshot<CrashRecord> snapshot) : SystemCrashesToolBase("test")
    {
        protected override Task<MaintenanceSnapshot<CrashRecord>> CollectAsync(MaintenanceArguments arguments, CancellationToken ct) =>
            Task.FromResult(snapshot);
    }

    private sealed class EventsTool(SystemEventSnapshot snapshot) : SystemEventsToolBase("test", new FixedClock(Now))
    {
        protected override string? ValidateEventId(string eventId) => null;

        protected override string? ValidateChannel(string channel) => null;

        protected override Task<SystemEventSnapshot> CollectAsync(SystemEventQuery query, CancellationToken ct) =>
            Task.FromResult(snapshot);
    }

    private sealed class InfoTool : SystemInfoToolBase
    {
        public InfoTool()
            : base("test")
        {
        }

        protected override Task<SystemInfoResult> CollectAsync(CancellationToken ct) =>
            Task.FromResult(new SystemInfoResult("Test OS", "host", TimeSpan.FromHours(1)));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
