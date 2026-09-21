// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Sys.Conformance;
using bOps.Packages.Sys.Core;
using bOps.Packages.Sys.Linux;

namespace bOps.Packages.System.Linux.Tests;

/// <summary>
/// The Linux <c>system.events</c> tool. The process handling (arguments, environment, bounds, timeout, cancellation, exit codes and the
/// permission hint) is exercised against real child processes: small scripts that behave like <c>journalctl</c>, so no part of it is
/// mocked. The journal itself is exercised against the machine's real journald.
/// </summary>
public sealed class LinuxSystemEventsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static ToolArguments Args(Action<JsonObject>? configure = null)
    {
        var json = new JsonObject();
        configure?.Invoke(json);
        return ToolArguments.FromJson(json);
    }

    private static string Micro(int minutesAgo) =>
        (Now.AddMinutes(-minutesAgo).ToUnixTimeMilliseconds() * 1000).ToString(CultureInfo.InvariantCulture);

    private static string Record(
        int minutesAgo, string priority = "3", string? identifier = "svc", string? unit = null, string message = "boom", string? comm = "svcd", string? pid = "77")
    {
        var json = new JsonObject
        {
            ["__CURSOR"] = "s=1",
            ["__REALTIME_TIMESTAMP"] = Micro(minutesAgo),
            ["PRIORITY"] = priority,
            ["MESSAGE"] = message,
            ["_TRANSPORT"] = "journal",
        };
        if (identifier is not null) json["SYSLOG_IDENTIFIER"] = identifier;
        if (unit is not null) json["_SYSTEMD_UNIT"] = unit;
        if (comm is not null) json["_COMM"] = comm;
        if (pid is not null) json["_PID"] = pid;
        return json.ToJsonString();
    }

    /// <summary>A directory holding a fake <c>journalctl</c> script, the lines it prints and the arguments and pid it recorded.</summary>
    private sealed class FakeJournalctl : IDisposable
    {
        private readonly string directory = Directory.CreateTempSubdirectory("bops-journalctl-").FullName;

        public FakeJournalctl(string body, IEnumerable<string>? lines = null)
        {
            DataPath = Path.Combine(directory, "data.jsonl");
            ArgsPath = Path.Combine(directory, "args.txt");
            PidPath = Path.Combine(directory, "pid.txt");
            File.WriteAllLines(DataPath, lines ?? []);
            ScriptPath = Path.Combine(directory, "journalctl");
            File.WriteAllText(
                ScriptPath,
                $"#!/bin/sh\nprintf '%s\\n' \"$@\" > '{ArgsPath}'\necho $$ > '{PidPath}'\n{body.Replace("{data}", $"'{DataPath}'", StringComparison.Ordinal)}\n");
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(ScriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        public string ScriptPath { get; }

        public string DataPath { get; }

        public string ArgsPath { get; }

        public string PidPath { get; }

        public string[] RecordedArguments => File.ReadAllLines(ArgsPath);

        public int RecordedPid => int.Parse(File.ReadAllText(PidPath).Trim(), CultureInfo.InvariantCulture);

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }

    private static LinuxSystemEventsTool ToolFor(FakeJournalctl fake, TimeSpan? timeout = null) =>
        new(fake.ScriptPath, new FixedClock(Now), timeout ?? TimeSpan.FromSeconds(20));

    private static async Task<JsonObject> RunAsync(LinuxSystemEventsTool tool, Action<JsonObject>? configure = null)
    {
        var result = await tool.ExecuteAsync(Args(configure));
        Assert.True(result.Succeeded, result.ErrorMessage);
        return JsonNode.Parse(result.Output!)!.AsObject();
    }

    private static async Task AssertGoneAsync(int pid)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (!Directory.Exists($"/proc/{pid}"))
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail($"Process {pid} was still running after the tool returned.");
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // ---- validation that needs no process ----

    [Theory]
    [InlineData("abc")]
    [InlineData("7036")]
    [InlineData("39f53479d3a045ac8e11786248231fb")]
    [InlineData("39f53479d3a045ac8e11786248231fbfa")]
    [InlineData("zzf53479d3a045ac8e11786248231fbf")]
    public async Task AnEventId_ThatIsNotA32DigitHexMessageId_IsRejectedBeforeAnyProcessStarts(string eventId)
    {
        var tool = new LinuxSystemEventsTool("/nonexistent/journalctl", new FixedClock(Now), TimeSpan.FromSeconds(1));

        var result = await tool.ExecuteAsync(Args(j => j["eventId"] = eventId));

        Assert.Equal(ToolOutcome.Failure, result.Outcome);
        Assert.Contains("MESSAGE_ID", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("System")]
    [InlineData("kernell")]
    [InlineData("Application")]
    public async Task AChannel_ThatIsNotAJournalTransport_IsRejectedBeforeAnyProcessStarts(string channel)
    {
        var tool = new LinuxSystemEventsTool("/nonexistent/journalctl", new FixedClock(Now), TimeSpan.FromSeconds(1));

        var result = await tool.ExecuteAsync(Args(j => j["channel"] = channel));

        Assert.Equal(ToolOutcome.Failure, result.Outcome);
        Assert.Contains("kernel", result.ErrorMessage, StringComparison.Ordinal);
    }

    // ---- process handling, against real child processes ----

    [LinuxOnlyFact]
    public async Task TheRecordsJournalctlPrints_AreReturnedNormalizedAndNewestFirst()
    {
        using var fake = new FakeJournalctl(
            "cat {data}",
            [Record(30, message: "older"), Record(5, message: "newest", unit: "svc.service"), Record(10, priority: "4", message: "middle")]);

        var json = await RunAsync(ToolFor(fake));

        var messages = json["events"]!.AsArray().Select(e => e!["message"]!.GetValue<string>()).ToArray();
        Assert.Equal(["newest", "middle", "older"], messages);
        Assert.Equal("complete", json["status"]!.GetValue<string>());
        Assert.True(json["complete"]!.GetValue<bool>());
        Assert.Equal("linux.journald", json["sources"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("svc.service", json["events"]![0]!["unit"]!.GetValue<string>());
        Assert.Equal(77, json["events"]![0]!["processId"]!.GetValue<int>());
    }

    [LinuxOnlyFact]
    public async Task JournalctlIsRunWithFixedSwitches_AndOnlyTheValidatedValuesTheQueryNeeds()
    {
        using var fake = new FakeJournalctl("cat {data}");

        // A source with shell syntax is refused before journalctl runs at all.
        var refused = await ToolFor(fake).ExecuteAsync(Args(j => j["source"] = "$(touch /tmp/bops-pwned); nginx"));
        Assert.Equal(ToolOutcome.Failure, refused.Outcome);
        var refusedText = await ToolFor(fake).ExecuteAsync(Args(j => j["text"] = "`id` && rm -rf /\n"));
        Assert.Equal(ToolOutcome.Failure, refusedText.Outcome);
        Assert.False(File.Exists(fake.ArgsPath));
        Assert.False(File.Exists("/tmp/bops-pwned"));

        await RunAsync(ToolFor(fake), j =>
        {
            j["windowMinutes"] = 30;
            j["minSeverity"] = "error";
            j["channel"] = "kernel";
            j["eventId"] = "39f53479d3a045ac8e11786248231fbf";
            j["source"] = "nginx.service";
            j["text"] = "out of memory";
        });

        Assert.Equal(
            [
                "--no-pager",
                "--output=json",
                "--reverse",
                "--utc",
                "--since=2026-09-21 11:30:00 UTC",
                "--until=2026-09-21 12:00:00 UTC",
                "--lines=10000",
                "--output-fields=MESSAGE,PRIORITY,SYSLOG_IDENTIFIER,_SYSTEMD_UNIT,_PID,_COMM,MESSAGE_ID,_TRANSPORT",
                "--priority=3",
                "_TRANSPORT=kernel",
                "MESSAGE_ID=39f53479d3a045ac8e11786248231fbf",
            ],
            fake.RecordedArguments);
    }

    [LinuxOnlyFact]
    public async Task TheEnvironmentIsFixed_SoJournalctlNeverPagesOrChangesLanguage()
    {
        using var fake = new FakeJournalctl("env > \"$(dirname \"$0\")/env.txt\"\ncat {data}");

        await RunAsync(ToolFor(fake));

        var environment = await File.ReadAllLinesAsync(Path.Combine(Path.GetDirectoryName(fake.ScriptPath)!, "env.txt"));
        Assert.Contains("LC_ALL=C", environment);
        Assert.Contains("TZ=UTC", environment);
        Assert.Contains("SYSTEMD_COLORS=0", environment);
        Assert.Contains("SYSTEMD_PAGER=", environment);
    }

    [LinuxOnlyFact]
    public async Task TheSharedFilter_SelectsBySourceOrUnit_ByTextAndBySeverity()
    {
        using var fake = new FakeJournalctl(
            "cat {data}",
            [
                Record(1, identifier: "nginx", unit: "nginx.service", message: "worker exited"),
                Record(2, identifier: "sshd", unit: "ssh.service", message: "worker exited"),
                Record(3, identifier: "nginx", unit: "nginx.service", message: "reloaded", priority: "6"),
                Record(4, identifier: null, unit: "cron.service", message: "started", comm: "cron"),
            ]);
        var tool = ToolFor(fake);

        var byIdentifier = await RunAsync(tool, j => j["source"] = "NGINX");
        var byUnit = await RunAsync(tool, j => j["source"] = "ssh.service");
        var byText = await RunAsync(tool, j => j["text"] = "WORKER");
        var bySeverity = await RunAsync(tool, j => j["minSeverity"] = "error");
        var byFallbackUnit = await RunAsync(tool, j => j["source"] = "cron.service");

        Assert.Equal(2, byIdentifier["returnedEvents"]!.GetValue<int>());
        Assert.Equal(["worker exited"], byUnit["events"]!.AsArray().Select(e => e!["message"]!.GetValue<string>()).ToArray());
        Assert.Equal(2, byText["returnedEvents"]!.GetValue<int>());
        Assert.Equal(3, bySeverity["returnedEvents"]!.GetValue<int>());
        Assert.Equal("cron.service", byFallbackUnit["events"]![0]!["source"]!.GetValue<string>());
    }

    [LinuxOnlyFact]
    public async Task AnEmptyJournalWindow_IsCompleteAndEmpty_NotAnError()
    {
        using var fake = new FakeJournalctl("echo '-- No entries --'");

        var json = await RunAsync(ToolFor(fake));

        Assert.Empty(json["events"]!.AsArray());
        Assert.Equal("complete", json["status"]!.GetValue<string>());
        Assert.True(json["complete"]!.GetValue<bool>());
    }

    [LinuxOnlyFact]
    public async Task ARecordThatCannotBeRead_IsSkipped_AndMakesTheSourcePartial()
    {
        using var fake = new FakeJournalctl("cat {data}", [Record(2, message: "good"), "this is not json", "{\"MESSAGE\":\"no time\"}", Record(3, message: "also good")]);

        var json = await RunAsync(ToolFor(fake));

        Assert.Equal(2, json["returnedEvents"]!.GetValue<int>());
        Assert.Equal("partial", json["status"]!.GetValue<string>());
        Assert.False(json["complete"]!.GetValue<bool>());
        Assert.Contains("2 journal record(s)", json["sources"]![0]!["detail"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [LinuxOnlyFact]
    public async Task WhenJournaldSaysTheIdentityDoesNotSeeTheWholeJournal_TheResultIsPartial_WithTheReason()
    {
        using var fake = new FakeJournalctl(
            "echo 'Hint: You are currently not seeing messages from other users and the system.' >&2\necho \"  Users in groups 'adm', 'systemd-journal' can see all messages.\" >&2\ncat {data}",
            [Record(2)]);

        var json = await RunAsync(ToolFor(fake));

        Assert.Equal("partial", json["status"]!.GetValue<string>());
        Assert.False(json["complete"]!.GetValue<bool>());
        Assert.Equal(1, json["returnedEvents"]!.GetValue<int>());
        Assert.Contains("systemd-journal", json["sources"]![0]!["detail"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [LinuxOnlyFact]
    public async Task AJournalctlThatFails_IsUnavailable_WithItsExitCodeAndFirstErrorLine_NeverAnEmptyHealthyResult()
    {
        using var fake = new FakeJournalctl("echo 'Failed to open journal: Permission denied' >&2\necho 'second line' >&2\nexit 1");

        var json = await RunAsync(ToolFor(fake));

        Assert.Equal("unavailable", json["status"]!.GetValue<string>());
        Assert.False(json["complete"]!.GetValue<bool>());
        var detail = json["sources"]![0]!["detail"]!.GetValue<string>();
        Assert.Contains("code 1", detail, StringComparison.Ordinal);
        Assert.Contains("Permission denied", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("second line", detail, StringComparison.Ordinal);
    }

    [LinuxOnlyFact]
    public async Task AMissingJournalctl_IsUnavailable()
    {
        var tool = new LinuxSystemEventsTool("/nonexistent/journalctl", new FixedClock(Now), TimeSpan.FromSeconds(5));

        var json = await RunAsync(tool);

        Assert.Equal("unavailable", json["status"]!.GetValue<string>());
        Assert.Contains("not installed", json["sources"]![0]!["detail"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [LinuxOnlyFact]
    public async Task TheScanCeiling_StopsTheReading_AndMarksTheResultTruncated()
    {
        var lines = Enumerable.Range(0, SystemEventsLimits.ScanCeiling + 50).Select(i => Record(minutesAgo: 1 + (i % 50), message: $"m{i}")).ToArray();
        using var fake = new FakeJournalctl("cat {data}", lines);

        var json = await RunAsync(ToolFor(fake), j => j["limit"] = 10);

        Assert.True(json["truncated"]!.GetValue<bool>());
        Assert.False(json["complete"]!.GetValue<bool>());
        Assert.Equal(SystemEventsLimits.ScanCeiling, json["observedEvents"]!.GetValue<int>());
        Assert.Equal(10, json["returnedEvents"]!.GetValue<int>());
    }

    [LinuxOnlyFact]
    public async Task JustBelowTheCeiling_TheScanIsComplete_AndAtTheCeilingItIsNot()
    {
        var below = Enumerable.Range(0, SystemEventsLimits.ScanCeiling - 1).Select(i => Record(minutesAgo: 1 + (i % 50), message: $"m{i}")).ToArray();
        using var belowFake = new FakeJournalctl("cat {data}", below);
        var atCeiling = Enumerable.Range(0, SystemEventsLimits.ScanCeiling + 5).Select(i => Record(minutesAgo: 1 + (i % 50), message: $"m{i}")).ToArray();
        using var ceilingFake = new FakeJournalctl("cat {data}", atCeiling);

        var complete = await RunAsync(ToolFor(belowFake), j => j["text"] = "m9998");
        var cut = await RunAsync(ToolFor(ceilingFake), j => j["text"] = "m9998");

        Assert.Equal(1, complete["observedEvents"]!.GetValue<int>());
        Assert.False(complete["truncated"]!.GetValue<bool>());
        Assert.True(complete["complete"]!.GetValue<bool>());
        Assert.Equal(1, cut["observedEvents"]!.GetValue<int>());
        Assert.True(cut["truncated"]!.GetValue<bool>());
        Assert.False(cut["complete"]!.GetValue<bool>());
    }

    [LinuxOnlyFact]
    public async Task ATimeout_StopsJournalctl_AndSaysTheAnswerIsIncomplete()
    {
        using var fake = new FakeJournalctl("cat {data}\nexec sleep 30", [Record(2, message: "before the stall")]);

        var stopwatch = Stopwatch.StartNew();
        var json = await RunAsync(ToolFor(fake, TimeSpan.FromMilliseconds(700)));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15));
        Assert.Equal("partial", json["status"]!.GetValue<string>());
        Assert.True(json["truncated"]!.GetValue<bool>());
        Assert.False(json["complete"]!.GetValue<bool>());
        Assert.Equal(["before the stall"], json["events"]!.AsArray().Select(e => e!["message"]!.GetValue<string>()).ToArray());
        await AssertGoneAsync(fake.RecordedPid);
    }

    [LinuxOnlyFact]
    public async Task Cancellation_StopsJournalctlPromptly_AndPropagates()
    {
        using var fake = new FakeJournalctl("exec sleep 30");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ToolFor(fake).ExecuteAsync(Args(), cts.Token));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15));
        await AssertGoneAsync(fake.RecordedPid);
    }

    [LinuxOnlyFact]
    public async Task ALargeErrorStream_DoesNotBlockJournalctl_AndIsNotKept()
    {
        using var fake = new FakeJournalctl("i=0\nwhile [ $i -lt 2000 ]; do echo 'noise noise noise noise noise noise noise noise noise noise noise noise' >&2; i=$((i+1)); done\ncat {data}", [Record(2)]);

        var json = await RunAsync(ToolFor(fake));

        Assert.Equal(1, json["returnedEvents"]!.GetValue<int>());
        Assert.Equal("complete", json["status"]!.GetValue<string>());
    }

    [LinuxOnlyFact]
    public async Task TheAuditSummary_OfARealRun_CarriesNoMessage()
    {
        using var fake = new FakeJournalctl("cat {data}", [Record(2, message: "password=hunter2 for alice")]);
        var tool = ToolFor(fake);
        var arguments = Args();

        var result = await tool.ExecuteAsync(arguments);
        var summary = tool.CreateAuditSummary(arguments, result)!.ToJsonString();

        Assert.DoesNotContain("hunter2", summary, StringComparison.Ordinal);
        Assert.Contains("\"returnedEvents\":1", summary, StringComparison.Ordinal);
    }

    // ---- the machine's real journald ----

    [LinuxOnlyFact]
    public Task SystemEvents_Conform() =>
        SystemToolConformance.AssertSystemEventsConformAsync(new LinuxSystemEventsTool(), "linux");

    [LinuxOnlyFact]
    public Task SystemEvents_RejectBadArguments() =>
        SystemToolConformance.AssertSystemEventsRejectBadArgumentsAsync(new LinuxSystemEventsTool());

    [LinuxOnlyFact]
    public async Task ARealMessageWrittenToJournald_IsReadBack_WithItsSourceSeverityAndProcess()
    {
        var identifier = "bops-events-" + Guid.NewGuid().ToString("N")[..12];
        var marker = "marker-" + Guid.NewGuid().ToString("N");
        WriteToJournal(identifier, "user.err", $"bOps system.events test {marker}");
        var tool = new LinuxSystemEventsTool();

        JsonObject? found = null;
        for (var attempt = 0; attempt < 60 && found is null; attempt++)
        {
            var json = await RunAsync(tool, j => { j["windowMinutes"] = 10; j["source"] = identifier; j["text"] = marker; });
            found = json["events"]!.AsArray().Select(e => e!.AsObject()).FirstOrDefault();
            if (found is null)
            {
                await Task.Delay(500);
            }
        }

        Assert.NotNull(found);
        Assert.Equal(identifier, found["source"]!.GetValue<string>());
        Assert.Equal("error", found["severity"]!.GetValue<string>());
        Assert.Contains(marker, found["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Null(found["eventId"]);
    }

    [LinuxOnlyFact]
    public async Task ARealMessage_IsSelectedOrExcludedBySeverityAndTransport()
    {
        var identifier = "bops-events-" + Guid.NewGuid().ToString("N")[..12];
        WriteToJournal(identifier, "user.warning", "a warning for the severity test");
        var tool = new LinuxSystemEventsTool();

        var atWarning = await WaitForAsync(tool, j => { j["windowMinutes"] = 10; j["source"] = identifier; j["minSeverity"] = "warning"; });
        var atError = await RunAsync(tool, j => { j["windowMinutes"] = 10; j["source"] = identifier; j["minSeverity"] = "error"; });
        var byKernelTransport = await RunAsync(tool, j => { j["windowMinutes"] = 10; j["source"] = identifier; j["channel"] = "kernel"; });

        Assert.Equal(1, atWarning["returnedEvents"]!.GetValue<int>());
        Assert.Equal("warning", atWarning["events"]![0]!["severity"]!.GetValue<string>());
        Assert.Equal(0, atError["returnedEvents"]!.GetValue<int>());
        Assert.Equal(0, byKernelTransport["returnedEvents"]!.GetValue<int>());
    }

    private static async Task<JsonObject> WaitForAsync(LinuxSystemEventsTool tool, Action<JsonObject> configure)
    {
        JsonObject json = [];
        for (var attempt = 0; attempt < 60; attempt++)
        {
            json = await RunAsync(tool, configure);
            if (json["returnedEvents"]!.GetValue<int>() > 0)
            {
                return json;
            }

            await Task.Delay(500);
        }

        return json;
    }

    private static void WriteToJournal(string identifier, string priority, string message)
    {
        var startInfo = new ProcessStartInfo("logger") { UseShellExecute = false };
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add(identifier);
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(priority);
        startInfo.ArgumentList.Add(message);
        using var process = Process.Start(startInfo)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
