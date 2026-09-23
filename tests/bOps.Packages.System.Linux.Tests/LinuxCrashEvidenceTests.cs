// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using System.ComponentModel;
using bOps.Abstractions;
using bOps.Packages.Sys.Core;
using bOps.Packages.Sys.Linux;

namespace bOps.Packages.System.Linux.Tests;

public sealed class LinuxCrashEvidenceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-23T12:00:00Z");
    private static readonly string[] SmokeStatuses = ["complete", "partial", "unavailable"];
    private static readonly TimeProvider FixedClock = new FixedTimeProvider(Now);
    private const string Fixture = "[{\"timestamp\":\"2026-09-23T11:00:00Z\",\"pid\":\"42\",\"exe\":\"/usr/bin/demo\",\"COREDUMP_FILENAME\":\"/var/lib/systemd/coredump/core.demo\",\"COREDUMP_ID\":\"stable-1\",\"signal\":\"SIGSEGV\"}]";

    [Fact]
    public void StructuredRowsMapMetadataAndSerializeBoundedCrashContract()
    {
        Assert.True(LinuxCrashEvidenceTool.TryParse(Fixture, Now.AddHours(-2), out var rows));
        var row = Assert.Single(rows);
        Assert.Equal("demo", row.Process); Assert.Equal(42, row.Pid); Assert.Equal("stable-1", row.EventIdOrCrashId);
        Assert.Equal("/var/lib/systemd/coredump/core.demo", row.DumpPath); Assert.Equal("coredump", row.Kind); Assert.Equal("SIGSEGV", row.Summary); Assert.Equal("linux-coredumpctl", row.Source);
        var json = JsonNode.Parse(SystemMaintenanceFormatting.Crashes(new([row], [new("linux-coredumpctl", InventorySourceStatus.Available)]), 100))!.AsObject();
        Assert.True(json["complete"]!.GetValue<bool>());
        Assert.Equal("2026-09-23T11:00:00.0000000Z", json["items"]![0]!["timestampUtc"]!.GetValue<string>());
        Assert.Equal("demo", json["items"]![0]!["process"]!.GetValue<string>());
        Assert.Equal(42, json["items"]![0]!["pid"]!.GetValue<int>());
        Assert.Equal("coredump", json["items"]![0]!["kind"]!.GetValue<string>());
        Assert.Equal("/var/lib/systemd/coredump/core.demo", json["items"]![0]!["dumpPath"]!.GetValue<string>());
        Assert.Equal("stable-1", json["items"]![0]!["eventIdOrCrashId"]!.GetValue<string>());
        Assert.Equal("linux-coredumpctl", json["items"]![0]!["source"]!.GetValue<string>());
    }

    [Fact]
    public void ParserFiltersSinceAcceptsMissingOptionalFieldsAndRejectsMalformedOrOversizedRows()
    {
        Assert.True(LinuxCrashEvidenceTool.TryParse("[]", Now, out var empty)); Assert.Empty(empty);
        Assert.True(LinuxCrashEvidenceTool.TryParse("[{\"timestamp\":\"2026-09-22T00:00:00Z\"}]", Now.AddMinutes(-60), out empty)); Assert.Empty(empty);
        Assert.True(LinuxCrashEvidenceTool.TryParse("[{\"timestamp\":\"2026-09-23T11:00:00Z\"}]", Now.AddHours(-2), out var row));
        var optional = JsonNode.Parse(SystemMaintenanceFormatting.Crashes(new(row, [new("linux-coredumpctl", InventorySourceStatus.Available)]), 100))!["items"]![0]!;
        Assert.Null(optional["pid"]); Assert.Null(optional["dumpPath"]); Assert.Null(optional["summary"]);
        Assert.False(LinuxCrashEvidenceTool.TryParse("not json", Now, out _));
    }

    [Fact]
    public void ArgumentsApplyContractBounds()
    {
        Assert.True(SystemMaintenanceArguments.TryReadCrashes(ToolArguments.Empty, out var defaults, out _));
        Assert.Equal(1440, defaults.SinceMinutes); Assert.Equal(100, defaults.Limit);
        Assert.False(SystemMaintenanceArguments.TryReadCrashes(ToolArguments.FromJson(new JsonObject { ["limit"] = 1001 }), out _, out _));
    }

    [Fact]
    public void ProviderRegistersCrashEvidence()
    {
        Assert.Contains("system.crashes", new LinuxSystemToolProvider().GetTools().Select(x => x.Manifest.Name));
    }

    [Fact]
    public async Task UnavailablePermissionMalformedTimeoutAndOversizedEvidenceStayIncomplete()
    {
        var unavailable = JsonNode.Parse((await Tool((_, _, _) => throw new Win32Exception(), "|/usr/lib/my-handler").ExecuteAsync(ToolArguments.Empty)).Output!)!;
        Assert.False(unavailable["complete"]!.GetValue<bool>()); Assert.Empty(unavailable["items"]!.AsArray());
        Assert.Contains("piped core handler", unavailable["warnings"]!.ToJsonString());
        Assert.Equal("linux-core-pattern", unavailable["sources"]![0]!["name"]!.GetValue<string>());

        var cases = new[]
        {
            (new LinuxUpdatesProcessRunner.Result(true, false, true, 1, 1, "", "Permission denied"), "access denied"),
            (new LinuxUpdatesProcessRunner.Result(true, false, true, 1, 0, "broken", ""), "malformed"),
            (new LinuxUpdatesProcessRunner.Result(true, true, true, 1, null, null, null), "timed out"),
            (new LinuxUpdatesProcessRunner.Result(true, false, true, 1, 0, null, ""), "bounded limit"),
        };
        foreach (var (result, warning) in cases)
        {
            var output = JsonNode.Parse((await Tool((_, _, _) => Task.FromResult(result), "/var/lib/core").ExecuteAsync(ToolArguments.Empty)).Output!)!;
            Assert.False(output["complete"]!.GetValue<bool>()); Assert.Empty(output["items"]!.AsArray());
            Assert.Contains(warning, output["warnings"]!.ToJsonString(), StringComparison.OrdinalIgnoreCase);
            Assert.InRange(output["warnings"]![0]!.GetValue<string>().Length, 1, SystemMaintenanceLimits.WarningCharacters);
        }
    }

    [Fact]
    public async Task EmptySuccessfulQueryIsCompleteAndLimitTruncationIsIncomplete()
    {
        var empty = JsonNode.Parse((await Tool((_, _, _) => Task.FromResult(new LinuxUpdatesProcessRunner.Result(true, false, true, 1, 0, "[]", "")), null).ExecuteAsync(ToolArguments.Empty)).Output!)!;
        Assert.True(empty["complete"]!.GetValue<bool>()); Assert.Empty(empty["items"]!.AsArray());
        var two = "[{\"timestamp\":\"2026-09-23T11:00:00Z\",\"pid\":1,\"COREDUMP_ID\":\"a\"},{\"timestamp\":\"2026-09-23T10:00:00Z\",\"pid\":2,\"COREDUMP_ID\":\"b\"}]";
        var limited = await Tool((_, _, _) => Task.FromResult(new LinuxUpdatesProcessRunner.Result(true, false, true, 1, 0, two, "")), null)
            .ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["limit"] = 1 }));
        var json = JsonNode.Parse(limited.Output!)!;
        Assert.False(json["complete"]!.GetValue<bool>()); Assert.True(json["truncated"]!.GetValue<bool>());
        Assert.Single(json["items"]!.AsArray()); Assert.Contains("truncated", json["warnings"]!.ToJsonString());
    }

    [Fact]
    public async Task SinceOrderingAndStableIdDeduplicationAreDeterministic()
    {
        const string json = "[{\"timestamp\":\"2026-09-23T11:00:00Z\",\"pid\":7,\"comm\":\"zeta\",\"COREDUMP_ID\":\"same\"},{\"timestamp\":\"2026-09-23T10:50:00Z\",\"pid\":7,\"comm\":\"beta\",\"COREDUMP_ID\":\"different\"},{\"timestamp\":\"2026-09-23T11:00:00Z\",\"pid\":7,\"comm\":\"zeta\",\"COREDUMP_ID\":\"same\"},{\"timestamp\":\"2026-09-22T00:00:00Z\",\"pid\":8,\"comm\":\"old\",\"COREDUMP_ID\":\"old-id\"}]";
        async Task<string> Read() => (await Tool((_, _, _) => Task.FromResult(new LinuxUpdatesProcessRunner.Result(true, false, true, 1, 0, json, "")), null)
            .ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["sinceMinutes"] = 120 }))).Output!;
        var first = JsonNode.Parse(await Read())!; var second = JsonNode.Parse(await Read())!;
        var rows = first["items"]!.AsArray();
        Assert.Equal(2, rows.Count);
        Assert.Equal("same", rows[0]!["eventIdOrCrashId"]!.GetValue<string>());
        Assert.Equal("different", rows[1]!["eventIdOrCrashId"]!.GetValue<string>());
        Assert.Equal("zeta", rows[0]!["process"]!.GetValue<string>());
        Assert.Equal("beta", rows[1]!["process"]!.GetValue<string>());
        Assert.Equal(first.ToJsonString(), second.ToJsonString());
        Assert.True(first["complete"]!.GetValue<bool>());
    }

    [Fact]
    public async Task PlainAndPipedCorePatternFallbackNeverClaimsCrashHistoryComplete()
    {
        foreach (var pattern in new[] { "/var/lib/private-core/%e.%p", "|/usr/libexec/core-handler" })
        {
            var output = JsonNode.Parse((await Tool((_, _, _) => throw new Win32Exception(), pattern).ExecuteAsync(ToolArguments.Empty)).Output!)!;
            Assert.False(output["complete"]!.GetValue<bool>()); Assert.Empty(output["items"]!.AsArray());
            Assert.Contains("only core pattern metadata observed", output["warnings"]!.ToJsonString());
            if (pattern.StartsWith('|')) Assert.Contains("piped core handler", output["warnings"]!.ToJsonString());
        }
    }

    private static LinuxCrashEvidenceTool Tool(Func<IReadOnlyList<string>, TimeSpan, CancellationToken, Task<LinuxUpdatesProcessRunner.Result>> run, string? pattern) =>
        new("fixed-coredumpctl", () => pattern, FixedClock, TimeSpan.FromSeconds(2), run);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [LinuxOnlyFact]
    public async Task RealLinuxSmokeReportsReadOnlyMetadataAvailabilityHonestly()
    {
        var result = await new LinuxCrashEvidenceTool().ExecuteAsync(ToolArguments.Empty);
        Assert.True(result.Succeeded);
        var json = JsonNode.Parse(result.Output!)!.AsObject();
        Assert.NotNull(json["complete"]); Assert.NotNull(json["sources"]); Assert.InRange(json["items"]!.AsArray().Count, 0, 100);
        Assert.Contains(json["status"]!.GetValue<string>(), SmokeStatuses);
    }
}
