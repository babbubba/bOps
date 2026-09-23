using System.Diagnostics;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Linux;

public sealed class LinuxSystemTimeTool() : SystemTimeToolBase("linux")
{
    protected override async Task<SystemTimeResult> CollectAsync(CancellationToken ct)
    {
        var utc = DateTimeOffset.UtcNow; var local = utc.ToLocalTime(); var zone = TimeZoneInfo.Local;
        var output = await LinuxTimedateCtl.RunAsync(ct); var values = output?.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=', 2)).Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1], StringComparer.Ordinal) ?? [];
        var complete = output is not null && values.ContainsKey("Timezone");
        return new SystemTimeResult(utc, local, values.GetValueOrDefault("Timezone", zone.Id), (int)local.Offset.TotalMinutes, zone.IsDaylightSavingTime(local.DateTime), "bcl", ParseBool(values, "NTP"), ParseBool(values, "NTPSynchronized"), values.GetValueOrDefault("NTPService"), output is null ? "linux.bcl" : "linux.timedatectl", complete);
    }
    private static bool? ParseBool(Dictionary<string, string> values, string key) => values.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) ? parsed : null;
}

public sealed class LinuxRebootPendingTool() : RebootPendingToolBase("linux")
{
    protected override async Task<RebootPendingResult> CollectAsync(CancellationToken ct)
    {
        var known = File.Exists("/var/run/reboot-required") || File.Exists("/run/reboot-required");
        var supported = File.Exists("/etc/os-release") && (await File.ReadAllTextAsync("/etc/os-release", ct)).Contains("ID=ubuntu", StringComparison.OrdinalIgnoreCase);
        return new RebootPendingResult(known, known ? ["linux.reboot-required"] : [], "linux.reboot-required", supported);
    }
}

internal static class LinuxTimedateCtl
{
    internal static async Task<string?> RunAsync(CancellationToken ct)
    {
        try { using var p = new Process { StartInfo = new ProcessStartInfo("timedatectl") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } }; p.StartInfo.ArgumentList.Add("show"); p.StartInfo.ArgumentList.Add("--property=Timezone,NTP,NTPSynchronized,NTPService"); if (!p.Start()) return null; using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(3)); var text = await p.StandardOutput.ReadToEndAsync(timeout.Token); await p.WaitForExitAsync(timeout.Token); return p.ExitCode == 0 ? text[..Math.Min(text.Length, 4096)] : null; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException) { return null; }
    }
}
