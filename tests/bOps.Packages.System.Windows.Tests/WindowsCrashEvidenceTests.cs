// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Sys.Core;
using bOps.Packages.Sys.Windows;

namespace bOps.Packages.System.Windows.Tests;

public sealed class WindowsCrashEvidenceTests
{
    [Fact]
    public void WerMetadata_MapsOnlyExplicitCrashFields_AndKeepsDumpPathAsMetadata()
    {
        var eventTime = new DateTimeOffset(2026, 9, 23, 10, 0, 0, TimeSpan.Zero).ToFileTime();
        var report = WindowsWerReport.Parse($"Version=1\r\nEventType=APPCRASH\r\nEventTime={eventTime}\r\nAppName=sample.exe\r\nAppPid=0x2A\r\nReportIdentifier=report-1\r\nDumpPath=C:\\dumps\\sample.dmp\r\nFaultModuleName=kernelbase.dll\r\n");

        var row = report.ToCrashRecord(DateTime.UnixEpoch);

        Assert.Equal("sample.exe", row.Process); Assert.Equal(42, row.Pid); Assert.Equal("application-crash", row.Kind);
        Assert.Equal("report-1", row.EventIdOrCrashId); Assert.Equal("C:\\dumps\\sample.dmp", row.DumpPath);
        Assert.Equal("APPCRASH; kernelbase.dll", row.Summary); Assert.Equal(new DateTimeOffset(2026, 9, 23, 10, 0, 0, TimeSpan.Zero), row.TimestampUtc);
    }

    [Fact]
    public void WerMetadata_MissingPidIsNull_AndMalformedMetadataIsRejected()
    {
        var row = WindowsWerReport.Parse("EventType=APPHANG\nAppName=waiting.exe\nReportIdentifier=report-2\n").ToCrashRecord(DateTime.UnixEpoch);
        Assert.Null(row.Pid); Assert.Equal("application-hang", row.Kind);
        Assert.Throws<InvalidDataException>(() => WindowsWerReport.Parse("not metadata"));
    }

    [Fact]
    public void EventNormalization_UsesProviderIdAndStructuredFields_NotLocalizedMessage()
    {
        var evidence = new WindowsCrashEventEvidence("Application Error", 1000, new(2026, 9, 23, 11, 0, 0, TimeSpan.Zero), "17",
            new Dictionary<string, string> { ["AppName"] = "sample.exe", ["ProcessId"] = "0x2A", ["ReportId"] = "crash-17", ["FaultingModuleName"] = "sample.dll" });

        Assert.True(WindowsCrashEventNormalizer.TryNormalize(evidence, out var row));
        Assert.Equal("windows-event-application-error", row!.Source); Assert.Equal("application-crash", row.Kind);
        Assert.Equal("sample.exe", row.Process); Assert.Equal(42, row.Pid); Assert.Equal("crash-17", row.EventIdOrCrashId); Assert.Equal("sample.dll", row.Summary);
    }

    [Fact]
    public void WerEventNormalization_MapsMissingOptionalFieldsWithoutMessageParsing()
    {
        var evidence = new WindowsCrashEventEvidence("Windows Error Reporting", 1001, new(2026, 9, 23, 11, 0, 0, TimeSpan.Zero), "18",
            new Dictionary<string, string> { ["EventType"] = "APPHANG", ["AppName"] = "waiting.exe" });

        Assert.True(WindowsCrashEventNormalizer.TryNormalize(evidence, out var row));
        Assert.Equal("windows-event-wer", row!.Source); Assert.Equal("application-hang", row.Kind); Assert.Null(row.Pid); Assert.Equal("18", row.EventIdOrCrashId);
    }

    [Fact]
    public void CrashSerialization_IsDeterministic_Truncates_AndReportsPartialEvidence()
    {
        var now = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        var snapshot = new MaintenanceSnapshot<CrashRecord>([
            new(now, "z.exe", null, "wer", null, "z", null, "windows-wer-report"),
            new(now.AddMinutes(-1), "a.exe", 2, "application-crash", "C:\\dumps\\a.dmp", "a", "summary", "windows-event-application-error")],
            [new("windows.wer.reportarchive", InventorySourceStatus.Available), new("windows-event-application-error", InventorySourceStatus.Partial, "fixture source failure")], ["fixture source failure"]);

        var json = JsonNode.Parse(SystemMaintenanceFormatting.Crashes(snapshot, 1))!.AsObject();
        Assert.False(json["complete"]!.GetValue<bool>()); Assert.True(json["truncated"]!.GetValue<bool>()); Assert.Equal("z.exe", json["items"]![0]!["process"]!.GetValue<string>());
        Assert.Equal("fixture source failure", json["warnings"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task FixtureWerDirectories_ApplySinceDeduplicateAndMakeOversizeEvidenceIncomplete()
    {
        var root = Path.Combine(Path.GetTempPath(), "bops-k5-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var now = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        try
        {
            await WriteReportAsync(root, "new", now, "shared-id");
            await WriteReportAsync(root, "duplicate", now.AddMinutes(-1), "shared-id");
            await WriteReportAsync(root, "old", now.AddHours(-2), "old-id");
            var tool = new WindowsCrashEvidenceTool(new FixedTimeProvider(now), () => [root]);
            var normal = await tool.ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["sinceMinutes"] = 60, ["limit"] = 20 }));
            var rows = JsonNode.Parse(normal.Output!)!["items"]!.AsArray().Where(item => item!["source"]!.GetValue<string>() == "windows-wer-report").ToArray();
            Assert.Single(rows); Assert.Equal("shared-id", rows[0]!["eventIdOrCrashId"]!.GetValue<string>());

            await File.WriteAllTextAsync(Path.Combine(Directory.CreateDirectory(Path.Combine(root, "oversized")).FullName, "Report.wer"), new string('x', 32 * 1024 + 1));
            var oversized = await tool.ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["sinceMinutes"] = 60, ["limit"] = 20 }));
            var json = JsonNode.Parse(oversized.Output!)!.AsObject();
            Assert.False(json["complete"]!.GetValue<bool>()); Assert.Contains(json["warnings"]!.AsArray(), warning => warning!.GetValue<string>().Contains("exceeds the byte limit", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task MissingKnownWerDirectory_IsNotTreatedAsACrashAndIsExplicitlyNotApplicable()
    {
        var missing = Path.Combine(Path.GetTempPath(), "bops-k5-missing-" + Guid.NewGuid().ToString("N"));
        var result = await new WindowsCrashEvidenceTool(new FixedTimeProvider(new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero)), () => [missing])
            .ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["sinceMinutes"] = 60, ["limit"] = 20 }));
        var source = JsonNode.Parse(result.Output!)!["sources"]!.AsArray().Single(item => item!["name"]!.GetValue<string>() == "windows.wer." + Path.GetFileName(missing).ToLowerInvariant());
        Assert.Equal("notApplicable", source!["status"]!.GetValue<string>());
    }

    [WindowsOnlyFact]
    public async Task RealWindowsSmoke_ReadsOnlyBoundedCrashEvidence_AndReturnsTheContractShape()
    {
        var result = await new WindowsCrashEvidenceTool().ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["sinceMinutes"] = 60, ["limit"] = 20 }));
        Assert.True(result.Succeeded, result.ErrorMessage);
        var json = JsonNode.Parse(result.Output!)!.AsObject();
        Assert.NotNull(json["complete"]); Assert.NotNull(json["warnings"]); Assert.True(json["items"]!.AsArray().Count <= 20);
        Assert.Contains(json["sources"]!.AsArray(), source => source!["name"]!.GetValue<string>().StartsWith("windows-event-", StringComparison.Ordinal));
    }

    private static Task WriteReportAsync(string root, string directory, DateTimeOffset time, string id)
    {
        var path = Directory.CreateDirectory(Path.Combine(root, directory)).FullName;
        return File.WriteAllTextAsync(Path.Combine(path, "Report.wer"), $"EventType=APPCRASH\nEventTime={time.ToFileTime()}\nAppName=fixture.exe\nReportIdentifier={id}\n");
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
