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
    private const string Fixture = "[{\"timestamp\":\"2026-09-23T11:00:00Z\",\"pid\":\"42\",\"exe\":\"/usr/bin/demo\",\"COREDUMP_FILENAME\":\"/var/lib/systemd/coredump/core.demo\",\"COREDUMP_ID\":\"stable-1\",\"signal\":\"SIGSEGV\"}]";

    [Fact]
    public void StructuredRowsMapMetadataAndSerializeBoundedCrashContract()
    {
        Assert.True(LinuxCrashEvidenceTool.TryParse(Fixture, Now.AddHours(-2), out var rows));
        var row = Assert.Single(rows);
        Assert.Equal("demo", row.Process); Assert.Equal(42, row.Pid); Assert.Equal("stable-1", row.EventIdOrCrashId);
        Assert.Equal("/var/lib/systemd/coredump/core.demo", row.DumpPath); Assert.Equal("linux-coredumpctl", row.Source);
        var json = JsonNode.Parse(SystemMaintenanceFormatting.Crashes(new([row], [new("linux-coredumpctl", InventorySourceStatus.Available)]), 100))!.AsObject();
        Assert.True(json["complete"]!.GetValue<bool>());
        Assert.Equal("2026-09-23T11:00:00.0000000Z", json["items"]![0]!["timestampUtc"]!.GetValue<string>());
        Assert.Equal("stable-1", json["items"]![0]!["eventIdOrCrashId"]!.GetValue<string>());
    }

    [Fact]
    public void ParserFiltersSinceAcceptsMissingOptionalFieldsAndRejectsMalformedOrOversizedRows()
    {
        Assert.True(LinuxCrashEvidenceTool.TryParse("[]", Now, out var empty)); Assert.Empty(empty);
        Assert.True(LinuxCrashEvidenceTool.TryParse("[{\"timestamp\":\"2026-09-22T00:00:00Z\"}]", Now.AddMinutes(-60), out empty)); Assert.Empty(empty);
        Assert.True(LinuxCrashEvidenceTool.TryParse("[{\"timestamp\":\"2026-09-23T11:00:00Z\"}]", Now.AddHours(-2), out var row));
        Assert.Null(Assert.Single(row).Pid);
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
        var unavailable = await Tool((_, _, _) => throw new Win32Exception(), "|/usr/lib/my-handler").ExecuteAsync(ToolArguments.Empty);
        Assert.False(JsonNode.Parse(unavailable.Output!)!["complete"]!.GetValue<bool>());
        Assert.Contains("piped core handler", JsonNode.Parse(unavailable.Output!)!["warnings"]!.ToJsonString());

        foreach (var result in new[]
        {
            new LinuxUpdatesProcessRunner.Result(true, false, true, 1, 1, "", "Permission denied"),
            new LinuxUpdatesProcessRunner.Result(true, false, true, 1, 0, "broken", ""),
            new LinuxUpdatesProcessRunner.Result(true, true, true, 1, null, null, null),
            new LinuxUpdatesProcessRunner.Result(true, false, true, 1, 0, null, ""),
        })
        {
            var output = await Tool((_, _, _) => Task.FromResult(result), "/var/lib/core").ExecuteAsync(ToolArguments.Empty);
            Assert.False(JsonNode.Parse(output.Output!)!["complete"]!.GetValue<bool>());
        }
    }

    [Fact]
    public async Task EmptySuccessfulQueryIsCompleteAndLimitTruncationIsIncomplete()
    {
        var empty = await Tool((_, _, _) => Task.FromResult(new LinuxUpdatesProcessRunner.Result(true, false, true, 1, 0, "[]", "")), null).ExecuteAsync(ToolArguments.Empty);
        Assert.True(JsonNode.Parse(empty.Output!)!["complete"]!.GetValue<bool>());
        var two = "[{\"timestamp\":\"2026-09-23T11:00:00Z\",\"pid\":1,\"COREDUMP_ID\":\"a\"},{\"timestamp\":\"2026-09-23T10:00:00Z\",\"pid\":2,\"COREDUMP_ID\":\"b\"}]";
        var limited = await Tool((_, _, _) => Task.FromResult(new LinuxUpdatesProcessRunner.Result(true, false, true, 1, 0, two, "")), null)
            .ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["limit"] = 1 }));
        var json = JsonNode.Parse(limited.Output!)!;
        Assert.False(json["complete"]!.GetValue<bool>()); Assert.True(json["truncated"]!.GetValue<bool>());
    }

    private static LinuxCrashEvidenceTool Tool(Func<IReadOnlyList<string>, TimeSpan, CancellationToken, Task<LinuxUpdatesProcessRunner.Result>> run, string? pattern) =>
        new("fixed-coredumpctl", () => pattern, TimeProvider.System, TimeSpan.FromSeconds(2), run);

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
