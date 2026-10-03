// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Sys.Conformance;
using bOps.Packages.Sys.Core;
using bOps.Packages.Sys.Linux;

namespace bOps.Packages.System.Linux.Tests;

/// <summary>
/// ADR-0032 HARDEN-9 amendment §2: on Linux <c>excludeSources</c> is the shared post-filter only, matched against the canonical
/// <c>source</c> of the parser (<c>SYSLOG_IDENTIFIER</c>, else <c>_SYSTEMD_UNIT</c>, else <c>_COMM</c>, else <c>unknown</c>) and never
/// against the unit separately; no journal match expression is ever built from an entry, so every excluded record is scanned and counts
/// towards the scan ceiling.
/// </summary>
public sealed class LinuxExcludeSourcesTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static string Micro(int minutesAgo) =>
        (Now.AddMinutes(-minutesAgo).ToUnixTimeMilliseconds() * 1000).ToString(CultureInfo.InvariantCulture);

    private static string Line(
        int minutesAgo, string? identifier = "svc", string? unit = null, string? comm = "svcd", string message = "boom", string priority = "3")
    {
        var json = new JsonObject
        {
            ["__REALTIME_TIMESTAMP"] = Micro(minutesAgo),
            ["PRIORITY"] = priority,
            ["MESSAGE"] = message,
            ["_TRANSPORT"] = "journal",
        };
        if (identifier is not null) json["SYSLOG_IDENTIFIER"] = identifier;
        if (unit is not null) json["_SYSTEMD_UNIT"] = unit;
        if (comm is not null) json["_COMM"] = comm;
        return json.ToJsonString();
    }

    private static SystemEventRecord Parse(string line)
    {
        Assert.Equal(JournalRecordParser.LineKind.Record, JournalRecordParser.TryParse(line, out var record));
        return record!;
    }

    private static SystemEventQuery Query(params string[] excluded) =>
        new(Now.AddMinutes(-60), Now, null, null, null, null, null, 10_000, excluded);

    private static ToolArguments Args(Action<JsonObject> configure)
    {
        var json = new JsonObject();
        configure(json);
        return ToolArguments.FromJson(json);
    }

    // ---- canonical source (runs on every host: parser and shared filter only) ----

    [Fact]
    public void ExcludingTheSyslogIdentifier_RemovesTheRecord()
    {
        Assert.False(SystemEventFilter.Matches(Parse(Line(5, identifier: "nginx")), Query("nginx")));
        Assert.False(SystemEventFilter.Matches(Parse(Line(5, identifier: "nginx")), Query("NGINX")));
        Assert.True(SystemEventFilter.Matches(Parse(Line(5, identifier: "nginx")), Query("nginx.service")));
    }

    [Fact]
    public void AnIdentifierAndAUnit_ExcludeByTheIdentifierOnly_NeverByTheUnit()
    {
        var record = Parse(Line(5, identifier: "x", unit: "x.service"));

        Assert.Equal("x", record.Source);
        Assert.False(SystemEventFilter.Matches(record, Query("x")));
        Assert.True(SystemEventFilter.Matches(record, Query("x.service")));
    }

    [Fact]
    public void WithoutAnIdentifier_TheUnitIsTheCanonicalSource_AndIsExcludedByIt()
    {
        var record = Parse(Line(5, identifier: null, unit: "cron.service"));

        Assert.Equal("cron.service", record.Source);
        Assert.False(SystemEventFilter.Matches(record, Query("cron.service")));
        Assert.True(SystemEventFilter.Matches(record, Query("cron")));
    }

    [Fact]
    public void WithoutAnIdentifierOrAUnit_TheCommandNameIsTheCanonicalSource()
    {
        var record = Parse(Line(5, identifier: null, unit: null, comm: "kworker"));

        Assert.Equal("kworker", record.Source);
        Assert.False(SystemEventFilter.Matches(record, Query("kworker")));
    }

    [Fact]
    public void WithNothingToNameIt_TheSourceIsUnknown_AndCanBeExcludedAsUnknown()
    {
        var record = Parse(Line(5, identifier: null, unit: null, comm: null));

        Assert.Equal("unknown", record.Source);
        Assert.False(SystemEventFilter.Matches(record, Query("unknown")));
    }

    [Fact]
    public void TheCommandNameOfARecordWithAnIdentifier_IsNeverComparedEither()
    {
        var record = Parse(Line(5, identifier: "svc", comm: "other"));

        Assert.True(SystemEventFilter.Matches(record, Query("other")));
    }

    [Fact]
    public void NoJournalMatchExpression_IsBuiltFromAnExclusion()
    {
        var plain = JournalctlArguments.Build(new SystemEventQuery(Now.AddMinutes(-60), Now, null, null, null, null, null, 10_000));
        var excluded = JournalctlArguments.Build(Query("nginx", "cron.service", "_SYSTEMD_UNIT=evil"));

        Assert.Equal(plain, excluded);
        Assert.DoesNotContain(excluded, argument => argument.Contains("nginx", StringComparison.Ordinal) || argument.Contains("evil", StringComparison.Ordinal));
    }

    [Fact]
    public void TheManifest_IsTheSameContractAsWindows_IncludingTheNewParameter()
    {
        var linux = SystemToolManifests.Events("linux");
        var windows = SystemToolManifests.Events("windows");

        Assert.Equal(windows.Parameters, linux.Parameters);
        Assert.Contains(linux.Parameters, p => p is { Name: "excludeSources", MinLength: 1, MaxLength: 1_024 });
    }

    // ---- the whole tool over a stand-in journalctl (Linux hosts) ----

    private sealed class FakeJournalctl : IDisposable
    {
        private readonly string directory = Directory.CreateTempSubdirectory("bops-journalctl-").FullName;

        public FakeJournalctl(IEnumerable<string> lines)
        {
            DataPath = Path.Combine(directory, "data.jsonl");
            ArgsPath = Path.Combine(directory, "args.txt");
            File.WriteAllLines(DataPath, lines);
            ScriptPath = Path.Combine(directory, "journalctl");
            File.WriteAllText(ScriptPath, $"#!/bin/sh\nprintf '%s\\n' \"$@\" > '{ArgsPath}'\ncat '{DataPath}'\n");
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(ScriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        public string ScriptPath { get; }

        public string DataPath { get; }

        public string ArgsPath { get; }

        public string[] RecordedArguments => File.ReadAllLines(ArgsPath);

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static LinuxSystemEventsTool ToolFor(FakeJournalctl fake) =>
        new(fake.ScriptPath, new FixedClock(Now), TimeSpan.FromSeconds(60), new FakeJournal(Now.AddDays(-400)).Run);

    private static async Task<JsonObject> RunAsync(LinuxSystemEventsTool tool, Action<JsonObject>? configure = null)
    {
        var result = await tool.ExecuteAsync(Args(configure ?? (_ => { })));
        Assert.True(result.Succeeded, result.ErrorMessage);
        return JsonNode.Parse(result.Output!)!.AsObject();
    }

    [LinuxOnlyFact]
    public async Task TheTool_ExcludesBySource_InRawMode_AndEchoesTheExclusion()
    {
        using var fake = new FakeJournalctl([Line(1, "noise"), Line(2, "signal"), Line(3, "NOISE"), Line(4, "other", unit: "other.service")]);

        var json = await RunAsync(ToolFor(fake), j => j["excludeSources"] = "noise");

        Assert.Equal(["signal", "other"], json["events"]!.AsArray().Select(e => e!["source"]!.GetValue<string>()));
        Assert.Equal(2, json["observedEvents"]!.GetValue<int>());
        Assert.Equal(["noise"], json["excludeSources"]!.AsArray().Select(e => e!.GetValue<string>()));
        Assert.DoesNotContain(fake.RecordedArguments, argument => argument.Contains("noise", StringComparison.OrdinalIgnoreCase));
    }

    [LinuxOnlyFact]
    public async Task TheTool_ExcludesBySource_InAggregateMode()
    {
        using var fake = new FakeJournalctl([Line(1, "noise"), Line(2, "noise"), Line(3, "signal")]);

        var json = await RunAsync(ToolFor(fake), j => { j["mode"] = "aggregate"; j["windowDays"] = 1; j["excludeSources"] = "Noise, absent"; });

        var group = Assert.Single(json["groups"]!.AsArray());
        Assert.Equal("signal", group!["source"]!.GetValue<string>());
        Assert.Equal(["absent", "Noise"], json["excludeSources"]!.AsArray().Select(e => e!.GetValue<string>()));
    }

    [LinuxOnlyFact]
    public async Task AnExcludedUnit_IsNotRemoved_WhenTheIdentifierIsTheSource()
    {
        using var fake = new FakeJournalctl([Line(1, "x", unit: "x.service")]);

        var byUnit = await RunAsync(ToolFor(fake), j => j["excludeSources"] = "x.service");
        var byIdentifier = await RunAsync(ToolFor(fake), j => j["excludeSources"] = "x");

        Assert.Single(byUnit["events"]!.AsArray());
        Assert.Empty(byIdentifier["events"]!.AsArray());
    }

    [LinuxOnlyFact]
    public async Task ExcludedRecords_StillCountTowardsTheScanCeiling_SoTheResultSaysItWasCut()
    {
        // Newest first: more excluded records than the ceiling, then one that would have matched. The shared post-filter runs after
        // the scan, so the ceiling is spent on excluded records and the answer is truncated and incomplete (ADR-0032 HARDEN-9 §3).
        var lines = Enumerable.Range(0, SystemEventsLimits.ScanCeiling).Select(_ => Line(1, "noise")).Append(Line(30, "signal")).ToArray();
        using var fake = new FakeJournalctl(lines);

        var json = await RunAsync(ToolFor(fake), j => j["excludeSources"] = "noise");

        Assert.True(json["truncated"]!.GetValue<bool>());
        Assert.False(json["complete"]!.GetValue<bool>());
        Assert.Empty(json["events"]!.AsArray());
        Assert.Equal(0, json["observedEvents"]!.GetValue<int>());
    }

    [LinuxOnlyFact]
    public async Task ALegacyCallWithoutTheArgument_IsUnchanged_AndEchoesAnEmptyArray()
    {
        using var fake = new FakeJournalctl([Line(1, "noise"), Line(2, "signal")]);

        var json = await RunAsync(ToolFor(fake));

        Assert.Equal(2, json["observedEvents"]!.GetValue<int>());
        Assert.Empty(json["excludeSources"]!.AsArray());
        Assert.Equal(2, json["schemaVersion"]!.GetValue<int>());
    }

    [LinuxOnlyFact]
    public async Task AnInvalidExclusion_IsRefused_BeforeJournalctlRuns()
    {
        using var fake = new FakeJournalctl([]);

        var result = await ToolFor(fake).ExecuteAsync(Args(j => j["excludeSources"] = "a,,b"));

        Assert.Equal(ToolOutcome.Failure, result.Outcome);
        Assert.Equal(ToolFailureKind.Validation, result.FailureKind);
        Assert.False(File.Exists(fake.ArgsPath));
    }

    // ---- the real journal (Linux hosts) ----

    [LinuxOnlyFact]
    public Task ExcludeSources_ConformsInBothModes_AgainstTheRealJournal() =>
        SystemToolConformance.AssertSystemEventsExcludeSourcesConformAsync(new LinuxSystemEventsTool());
}
