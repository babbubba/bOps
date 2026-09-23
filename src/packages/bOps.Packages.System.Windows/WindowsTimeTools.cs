using System.ServiceProcess;
using Microsoft.Win32;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Windows;

public sealed class WindowsSystemTimeTool() : SystemTimeToolBase("windows")
{
    protected override Task<SystemTimeResult> CollectAsync(CancellationToken ct)
    {
        var utc = DateTimeOffset.UtcNow; var local = utc.ToLocalTime(); var zone = TimeZoneInfo.Local; string? status = null; bool? configured = null;
        try { using var service = new ServiceController("W32Time"); status = service.Status.ToString(); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        try { using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\W32Time\Parameters"); configured = key?.GetValue("Type") is string type && type.Length > 0; } catch (System.Security.SecurityException) { }
        return Task.FromResult(new SystemTimeResult(utc, local, zone.Id, (int)local.Offset.TotalMinutes, zone.IsDaylightSavingTime(local.DateTime), "bcl", configured, null, status, "windows.bcl+w32time", status is not null));
    }
}

public sealed class WindowsRebootPendingTool() : RebootPendingToolBase("windows")
{
    protected override Task<RebootPendingResult> CollectAsync(CancellationToken ct)
    {
        var reasons = new List<string>(); var complete = true;
        Probe(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing", "RebootPending", "windows.cbs", reasons, ref complete);
        Probe(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update", "RebootRequired", "windows.update", reasons, ref complete);
        try { using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager"); if (key?.GetValue("PendingFileRenameOperations") is string[] values && values.Length > 0) reasons.Add("windows.pending-file-rename"); } catch (System.Security.SecurityException) { complete = false; }
        return Task.FromResult(new RebootPendingResult(reasons.Count > 0, reasons, "windows.registry", complete));
    }
    private static void Probe(string path, string value, string reason, List<string> reasons, ref bool complete)
    { try { using var key = Registry.LocalMachine.OpenSubKey(path); if (key is null) { complete = false; return; } if (key.GetValue(value) is not null) reasons.Add(reason); } catch (System.Security.SecurityException) { complete = false; } }
}
