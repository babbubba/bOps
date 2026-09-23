using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Scheduler.Conformance;
using bOps.Packages.Scheduler.Linux;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace bOps.Packages.Scheduler.Linux.Tests;

public sealed class LinuxSchedulerTests
{
    [Fact] public void Provider_ExposesExactlyFiveTools() { var names = new LinuxSchedulerToolProvider().GetTools().Select(x => x.Manifest.Name).ToArray(); Assert.Equal(["scheduler.list", "scheduler.inspect", "scheduler.history", "scheduler.enable", "scheduler.disable"], names); SchedulerConformance.AssertManifests(); }
    [Fact] public void CronParser_AndStableId_AreUsed() { var root = Temp(); File.WriteAllText(Path.Combine(root, "crontab"), "MAILTO=x\n*/5 * * * * alice /bin/job\n"); var paths = new LinuxCronPaths(Path.Combine(root,"crontab"), Path.Combine(root,"drop"), Path.Combine(root,"h"), Path.Combine(root,"d"), Path.Combine(root,"w"), Path.Combine(root,"m"), [Path.Combine(root,"spool")]); var reader = new LinuxCronInventoryReader(paths); var first = reader.Read(); var second = reader.Read(); Assert.Single(first.Items); Assert.Equal(first.Items[0].Id, second.Items[0].Id); Assert.Equal("alice", first.Items[0].User); Directory.Delete(root, true); }
    [Fact] public async Task Systemd_List_IgnoresHumanTimeAndUsesProperties() { var runner = new FakeRunner { List = "NEXT LEFT LAST PASSED UNIT ACTIVATES\n tomorrow text x y backup.timer backup.service\n", Show = "Id=backup.timer\nDescription=Backup\nLoadState=loaded\nActiveState=active\nUnitFileState=enabled\nUnit=backup.service\nResult=success\nNextElapseUSecRealtime=1000000\nNextElapseUSecMonotonic=999999\nLastTriggerUSec=900000\nTimersCalendar=*-*-* 02:00:00\nTimersMonotonic=\n" }; var tool = new LinuxSchedulerListTool(runner, EmptyInventory()); var result = await tool.ExecuteAsync(ToolArguments.Empty); Assert.True(result.Succeeded); Assert.Contains("backup.timer", result.Output!); Assert.Contains("1970-01-01T00:00:01", result.Output!); Assert.DoesNotContain("tomorrow", result.Output!); }
    [Fact] public async Task MonotonicOnly_DoesNotBecomeUtc() { var runner = new FakeRunner { List = "x y monotonic.timer service\n", Show = "Id=monotonic.timer\nLoadState=loaded\nActiveState=active\nUnitFileState=disabled\nUnit=service\nResult=\nNextElapseUSecRealtime=0\nNextElapseUSecMonotonic=1000000\nLastTriggerUSec=0\nTimersCalendar=\nTimersMonotonic=5min\nDescription=\n" }; var output = await new LinuxSchedulerListTool(runner, EmptyInventory()).ExecuteAsync(ToolArguments.Empty); Assert.DoesNotContain("1970-01-01", output.Output!); Assert.Contains("5min", output.Output!); }
    [Fact] public async Task Mutations_AreExactAndCronReadOnly() { var runner = new FakeRunner { Show = "Id=a.timer\nLoadState=loaded\nActiveState=active\nUnitFileState=disabled\nUnit=a\nResult=\nNextElapseUSecRealtime=0\nNextElapseUSecMonotonic=0\nLastTriggerUSec=0\nTimersCalendar=\nTimersMonotonic=\nDescription=\n" }; Assert.True((await new LinuxSchedulerEnableTool(runner).ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["id"] = "a.timer" }))).Succeeded); Assert.Equal(["enable", "--", "a.timer"], runner.Calls[1]); var cron = await new LinuxSchedulerEnableTool(runner).ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["id"] = "cron:x" })); Assert.False(cron.Succeeded); Assert.DoesNotContain("--now", string.Join(' ', runner.Calls.SelectMany(x=>x))); }
    [RealSystemdFact]
    [Trait("Platform", "Linux")]
    public void RealSystemdTimerLifecycle()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var timer = $"bops-quality-{suffix}.timer";
        var service = $"bops-quality-{suffix}.service";
        var timerPath = $"/etc/systemd/system/{timer}";
        var servicePath = $"/etc/systemd/system/{service}";
        File.WriteAllText(servicePath, "[Unit]\nDescription=bOps quality test-owned timer service\n[Service]\nType=oneshot\nExecStart=/bin/true\n");
        File.WriteAllText(timerPath, $"[Unit]\nDescription=bOps quality test-owned timer\n[Timer]\nOnCalendar=*-*-* 00:00:00\nUnit={service}\n[Install]\nWantedBy=timers.target\n");
        try
        {
            Run("systemctl", "daemon-reload");
            Assert.Contains(timer, Run("systemctl", "list-timers", "--all", "--no-pager", "--no-legend", "--full"));
            Assert.Contains($"Id={timer}", Run("systemctl", "show", "--no-pager", $"--property=Id,UnitFileState,Unit,TimersCalendar", "--", timer));
            Run("systemctl", "enable", timer);
            Assert.Contains("UnitFileState=enabled", Run("systemctl", "show", "--no-pager", "--property=UnitFileState", "--", timer));
            Run("systemctl", "disable", timer);
            Assert.Contains("UnitFileState=disabled", Run("systemctl", "show", "--no-pager", "--property=UnitFileState", "--", timer));
            Run("systemctl", "enable", timer);
            Assert.Contains("UnitFileState=enabled", Run("systemctl", "show", "--no-pager", "--property=UnitFileState", "--", timer));
        }
        finally
        {
            RunCore("systemctl", ["disable", timer], true);
            File.Delete(timerPath);
            File.Delete(servicePath);
            RunCore("systemctl", ["daemon-reload"], true);
        }
    }

    private sealed class RealSystemdFactAttribute : FactAttribute
    {
        public RealSystemdFactAttribute()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) Skip = "Requires Linux.";
            else if (Environment.GetEnvironmentVariable("BOPS_RUN_REAL_SYSTEMD_TESTS") != "1") Skip = "Requires explicit real-systemd opt-in.";
            else if (!string.Equals(Environment.UserName, "root", StringComparison.Ordinal)) Skip = "Requires root.";
            else if (!File.Exists("/run/systemd/system")) Skip = "Requires systemd.";
        }
    }

    private static string Run(string executable, params string[] arguments) => RunCore(executable, arguments, false);

    private static string RunCore(string executable, string[] arguments, bool allowFailure)
    {
        var startInfo = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {executable}.");
        process.WaitForExit();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (!allowFailure && process.ExitCode != 0) throw new InvalidOperationException($"{executable} failed ({process.ExitCode}): {error}");
        return output;
    }
    private static LinuxCronInventoryReader EmptyInventory() => new(new LinuxCronPaths("missing", "missing", "missing", "missing", "missing", "missing", []));
    private static string Temp() { var p = Path.Combine(Path.GetTempPath(), "bops-scheduler-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(p); return p; }
    private sealed class FakeRunner : ILinuxProcessRunner { public string List = ""; public string Show = ""; public List<string[]> Calls { get; } = []; public Task<LinuxProcessResult> RunAsync(string exe, IReadOnlyList<string> args, CancellationToken ct) { Calls.Add(args.ToArray()); return Task.FromResult(new LinuxProcessResult(0, args.Contains("list-timers") ? List : Show, "")); } }
}
