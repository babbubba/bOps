// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Text.Json.Nodes;
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

    /// <summary>The shape <c>coredumpctl --json=short list</c> writes: <c>time</c> in microseconds and the numeric <c>sig</c>.</summary>
    private const string CoredumpctlFixture = "[{\"time\":1790161200000000,\"pid\":4242,\"uid\":1000,\"gid\":1000,\"sig\":11,\"corefile\":\"present\",\"exe\":\"/usr/bin/demo\",\"size\":123456}]";

    [Fact]
    public void StructuredRowsMapMetadata_AsOccurredCoreDumps()
    {
        Assert.True(LinuxCrashEvidenceTool.TryParse(Fixture, Now.AddHours(-2), out var rows));
        var row = Assert.Single(rows);
        Assert.Equal("demo", row.Process); Assert.Equal(42, row.Pid); Assert.Equal("stable-1", row.EventIdOrCrashId);
        Assert.Equal("/var/lib/systemd/coredump/core.demo", row.DumpPath); Assert.Equal("coredump", row.Kind); Assert.Equal("linux-coredumpctl", row.Source);
        Assert.Equal(EvidenceTimestampKind.Occurred, row.TimestampKind);
        Assert.Null(row.Code); // "SIGSEGV" is not the numeric signal coredumpctl writes; unknown stays null
    }

    [Fact]
    public void TheRealCoredumpctlShape_GivesTheTimeAndTheSignalAsCode()
    {
        Assert.True(LinuxCrashEvidenceTool.TryParse(CoredumpctlFixture, Now.AddDays(-30), out var rows));
        var row = Assert.Single(rows);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_790_161_200), row.TimestampUtc);
        Assert.Equal("11", row.Code);
        Assert.Equal("11", row.Summary);
        Assert.Equal(4242, row.Pid);
    }

    [Fact]
    public void ParserFiltersSinceAcceptsMissingOptionalFieldsAndRejectsMalformedRows()
    {
        Assert.True(LinuxCrashEvidenceTool.TryParse("[]", Now, out var empty)); Assert.Empty(empty);
        Assert.True(LinuxCrashEvidenceTool.TryParse("[{\"timestamp\":\"2026-09-22T00:00:00Z\"}]", Now.AddMinutes(-60), out empty)); Assert.Empty(empty);
        Assert.True(LinuxCrashEvidenceTool.TryParse("[{\"timestamp\":\"2026-09-23T11:00:00Z\"}]", Now.AddHours(-2), out var rows));
        var row = Assert.Single(rows);
        Assert.Null(row.Pid); Assert.Null(row.DumpPath); Assert.Null(row.Summary);
        Assert.False(LinuxCrashEvidenceTool.TryParse("not json", Now, out _));
    }

    [Fact]
    public void ArgumentsApplyContractBounds()
    {
        Assert.True(SystemCrashesArguments.TryRead(ToolArguments.Empty, Now, out var defaults, out _));
        Assert.Equal(Now.AddMinutes(-1440), defaults!.FromUtc); Assert.Equal(100, defaults.Limit); Assert.Equal(EvidenceMode.Aggregate, defaults.Mode);
        Assert.False(SystemCrashesArguments.TryRead(ToolArguments.FromJson(new JsonObject { ["limit"] = 1001 }), Now, out _, out _));
    }

    [Fact]
    public void ProviderRegistersCrashEvidenceAndStability()
    {
        var names = new LinuxSystemToolProvider().GetTools().Select(x => x.Manifest.Name).ToArray();
        Assert.Contains("system.crashes", names);
        Assert.Contains("system.stability", names);
    }

    [Fact]
    public async Task UnavailablePermissionMalformedTimeoutAndOversizedEvidenceStayIncomplete()
    {
        var unavailable = JsonNode.Parse((await Tool((_, _, _) => throw new Win32Exception(), "|/usr/lib/my-handler").ExecuteAsync(Raw())).Output!)!;
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
            var output = JsonNode.Parse((await Tool((_, _, _) => Task.FromResult(result), "/var/lib/core").ExecuteAsync(Raw())).Output!)!;
            Assert.False(output["complete"]!.GetValue<bool>()); Assert.Empty(output["items"]!.AsArray());
            Assert.Contains(warning, output["warnings"]!.ToJsonString(), StringComparison.OrdinalIgnoreCase);
            Assert.InRange(output["warnings"]![0]!.GetValue<string>().Length, 1, SystemMaintenanceLimits.WarningCharacters);
        }
    }

    [Fact]
    public async Task EmptySuccessfulQueryIsComplete_WhenTheJournalReachesTheRequest_AndLimitTruncationIsIncomplete()
    {
        var empty = JsonNode.Parse((await Tool((_, _, _) => Task.FromResult(new LinuxUpdatesProcessRunner.Result(true, false, true, 1, 0, "[]", "")), null).ExecuteAsync(Raw())).Output!)!;
        Assert.True(empty["complete"]!.GetValue<bool>()); Assert.Empty(empty["items"]!.AsArray());
        Assert.Equal("complete", empty["coverage"]!["state"]!.GetValue<string>());

        var two = "[{\"timestamp\":\"2026-09-23T11:00:00Z\",\"pid\":1,\"COREDUMP_ID\":\"a\"},{\"timestamp\":\"2026-09-23T10:00:00Z\",\"pid\":2,\"COREDUMP_ID\":\"b\"}]";
        var limited = await Tool((_, _, _) => Task.FromResult(new LinuxUpdatesProcessRunner.Result(true, false, true, 1, 0, two, "")), null)
            .ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["limit"] = 1, ["mode"] = "raw" }));
        var json = JsonNode.Parse(limited.Output!)!;
        Assert.False(json["complete"]!.GetValue<bool>()); Assert.True(json["truncated"]!.GetValue<bool>());
        Assert.Single(json["items"]!.AsArray());
    }

    [Fact]
    public async Task AJournalShorterThanTheRequest_IsPartialCoverage_NeverComplete()
    {
        var shortJournal = new FakeJournal(Now.AddDays(-3));
        var result = await new LinuxCrashEvidenceTool("fixed-coredumpctl", () => null, FixedClock, TimeSpan.FromSeconds(2),
                (_, _, _) => Task.FromResult(new LinuxUpdatesProcessRunner.Result(true, false, true, 1, 0, "[]", "")), shortJournal.Run)
            .ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["sinceDays"] = 30 }));

        var json = JsonNode.Parse(result.Output!)!;
        Assert.Equal("complete", json["status"]!.GetValue<string>());
        Assert.Equal("partial", json["coverage"]!["state"]!.GetValue<string>());
        Assert.Equal("2026-09-20T12:00:00.0000000Z", json["coverage"]!["stores"]![0]!["oldestAvailableUtc"]!.GetValue<string>());
        Assert.False(json["complete"]!.GetValue<bool>());
        Assert.Equal(ToolResultCompleteness.Partial, result.Completeness);
    }

    [Fact]
    public async Task TheCoverageProbe_IsScopedToTheSystemJournal_WhereCoreDumpRecordsLive()
    {
        var journal = new FakeJournal(Now.AddDays(-400));
        await new LinuxCrashEvidenceTool("fixed-coredumpctl", () => null, FixedClock, TimeSpan.FromSeconds(2),
                (_, _, _) => Task.FromResult(new LinuxUpdatesProcessRunner.Result(true, false, true, 1, 0, "[]", "")), journal.Run)
            .ExecuteAsync(ToolArguments.Empty);

        var arguments = Assert.Single(journal.Calls);
        Assert.Equal(["--no-pager", "--output=json", "--utc", "--output-fields=_TRANSPORT", "--system"], arguments);
    }

    [Fact]
    public async Task SinceOrderingAndStableIdDeduplicationAreDeterministic()
    {
        const string json = "[{\"timestamp\":\"2026-09-23T11:00:00Z\",\"pid\":7,\"comm\":\"zeta\",\"COREDUMP_ID\":\"same\"},{\"timestamp\":\"2026-09-23T10:50:00Z\",\"pid\":7,\"comm\":\"beta\",\"COREDUMP_ID\":\"different\"},{\"timestamp\":\"2026-09-23T11:00:00Z\",\"pid\":7,\"comm\":\"zeta\",\"COREDUMP_ID\":\"same\"},{\"timestamp\":\"2026-09-22T00:00:00Z\",\"pid\":8,\"comm\":\"old\",\"COREDUMP_ID\":\"old-id\"}]";
        async Task<string> Read() => (await Tool((_, _, _) => Task.FromResult(new LinuxUpdatesProcessRunner.Result(true, false, true, 1, 0, json, "")), null)
            .ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["sinceMinutes"] = 120, ["mode"] = "raw" }))).Output!;
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
    public async Task TheDefaultModeGroupsCoreDumpsByProcessAndSignal()
    {
        const string json = "[{\"time\":1790161200000000,\"pid\":1,\"sig\":11,\"exe\":\"/usr/bin/demo\",\"COREDUMP_ID\":\"a\"},{\"time\":1790157600000000,\"pid\":2,\"sig\":11,\"exe\":\"/usr/bin/demo\",\"COREDUMP_ID\":\"b\"},{\"time\":1790157600000000,\"pid\":3,\"sig\":6,\"exe\":\"/usr/bin/other\",\"COREDUMP_ID\":\"c\"}]";
        var output = JsonNode.Parse((await Tool((_, _, _) => Task.FromResult(new LinuxUpdatesProcessRunner.Result(true, false, true, 1, 0, json, "")), null)
            .ExecuteAsync(ToolArguments.Empty)).Output!)!;

        Assert.Equal("aggregate", output["mode"]!.GetValue<string>());
        var groups = output["groups"]!.AsArray();
        Assert.Equal(2, groups.Count);
        Assert.Equal("coredump", groups[0]!["kind"]!.GetValue<string>());
        Assert.Equal("11", groups[0]!["code"]!.GetValue<string>());
        Assert.Equal("demo", groups[0]!["application"]!.GetValue<string>());
        Assert.Equal("occurred", groups[0]!["timestampKind"]!.GetValue<string>());
        Assert.Equal(2, groups[0]!["count"]!.GetValue<int>());
    }

    [Fact]
    public async Task PlainAndPipedCorePatternFallbackNeverClaimsCrashHistoryComplete()
    {
        foreach (var pattern in new[] { "/var/lib/private-core/%e.%p", "|/usr/libexec/core-handler" })
        {
            var output = JsonNode.Parse((await Tool((_, _, _) => throw new Win32Exception(), pattern).ExecuteAsync(Raw())).Output!)!;
            Assert.False(output["complete"]!.GetValue<bool>()); Assert.Empty(output["items"]!.AsArray());
            Assert.Contains("only core pattern metadata observed", output["warnings"]!.ToJsonString());
            if (pattern.StartsWith('|')) Assert.Contains("piped core handler", output["warnings"]!.ToJsonString());
        }
    }

    private static ToolArguments Raw() => ToolArguments.FromJson(new JsonObject { ["mode"] = "raw" });

    private static LinuxCrashEvidenceTool Tool(Func<IReadOnlyList<string>, TimeSpan, CancellationToken, Task<LinuxUpdatesProcessRunner.Result>> run, string? pattern) =>
        new("fixed-coredumpctl", () => pattern, FixedClock, TimeSpan.FromSeconds(2), run, new FakeJournal(Now.AddDays(-400)).Run);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [LinuxOnlyFact]
    public async Task RealLinuxSmokeReportsReadOnlyMetadataAvailabilityHonestly()
    {
        var result = await new LinuxCrashEvidenceTool().ExecuteAsync(Raw());
        Assert.True(result.Succeeded);
        var json = JsonNode.Parse(result.Output!)!.AsObject();
        Assert.Equal(2, json["schemaVersion"]!.GetValue<int>());
        Assert.NotNull(json["complete"]); Assert.NotNull(json["sources"]); Assert.InRange(json["items"]!.AsArray().Count, 0, 100);
        Assert.Contains(json["status"]!.GetValue<string>(), SmokeStatuses);
    }
}
