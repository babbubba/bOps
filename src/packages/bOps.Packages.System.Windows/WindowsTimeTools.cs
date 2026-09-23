using System.ServiceProcess;
using Microsoft.Win32;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Windows;

public sealed class WindowsSystemTimeTool() : SystemTimeToolBase("windows")
{
    protected override Task<SystemTimeResult> CollectAsync(CancellationToken ct)
    {
        var utc = DateTimeOffset.UtcNow; var local = utc.ToLocalTime(); var zone = TimeZoneInfo.Local; string? status = null; string? rawType = null;
        try { using var service = new ServiceController("W32Time"); status = service.Status.ToString(); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        var configReadable = false;
        try { using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\W32Time\Parameters"); if (key is not null && key.GetValue("Type") is string type) { rawType = type; configReadable = true; } } catch (System.Security.SecurityException) { } catch (UnauthorizedAccessException) { }
        var config = WindowsTimeConfiguration.Evaluate(rawType, status is not null, configReadable);
        return Task.FromResult(new SystemTimeResult(utc, local, zone.Id, (int)local.Offset.TotalMinutes, zone.IsDaylightSavingTime(local.DateTime), "bcl", config.Configured, null, status, "windows.bcl+w32time", config.Complete));
    }
}

public sealed class WindowsRebootPendingTool() : RebootPendingToolBase("windows")
{
    internal const string CbsKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending";
    internal const string WindowsUpdateKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired";
    internal const string PendingFileRenameKey = @"SYSTEM\CurrentControlSet\Control\Session Manager";
    internal const string ComputerNameKey = @"SYSTEM\CurrentControlSet\Control\ComputerName";
    internal const string NetlogonParametersKey = @"SYSTEM\CurrentControlSet\Services\Netlogon\Parameters";
    protected override Task<RebootPendingResult> CollectAsync(CancellationToken ct)
    {
        var evidence = new Dictionary<string, bool?>
        {
            ["windows.cbs"] = ReadSubKey(CbsKey),
            ["windows.update"] = ReadSubKey(WindowsUpdateKey),
            ["windows.pending-file-rename"] = ReadMultiString(PendingFileRenameKey, "PendingFileRenameOperations"),
            ["windows.pending-computer-rename"] = ReadComputerRename(),
            ["windows.pending-domain-change"] = ReadJoinDomain()
        };
        return Task.FromResult(WindowsRebootEvidence.Combine(evidence));
    }
    private static bool? ReadSubKey(string path) { try { using var key = Registry.LocalMachine.OpenSubKey(path); return key is not null; } catch (System.Security.SecurityException) { return null; } catch (UnauthorizedAccessException) { return null; } }
    private static bool? ReadMultiString(string path, string name) { try { using var key = Registry.LocalMachine.OpenSubKey(path); if (key is null) return null; return key.GetValue(name) switch { null => false, string[] values => values.Length > 0, _ => null }; } catch (System.Security.SecurityException) { return null; } catch (UnauthorizedAccessException) { return null; } }
    private static bool? ReadComputerRename() { try { using var key = Registry.LocalMachine.OpenSubKey(ComputerNameKey); using var active = key?.OpenSubKey("ActiveComputerName"); using var pending = key?.OpenSubKey("ComputerName"); if (active?.GetValue("ComputerName") is not string activeName || pending?.GetValue("ComputerName") is not string pendingName) return null; return !string.Equals(activeName, pendingName, StringComparison.OrdinalIgnoreCase); } catch (System.Security.SecurityException) { return null; } catch (UnauthorizedAccessException) { return null; } }
    private static bool? ReadJoinDomain() { try { using var key = Registry.LocalMachine.OpenSubKey(NetlogonParametersKey); if (key is null) return null; return key.GetValue("JoinDomain") is string value && !string.IsNullOrWhiteSpace(value); } catch (System.Security.SecurityException) { return null; } catch (UnauthorizedAccessException) { return null; } }
}

internal static class WindowsRebootEvidence
{
    internal static RebootPendingResult Combine(IReadOnlyDictionary<string, bool?> evidence) { var reasons = evidence.Where(x => x.Value == true).Select(x => x.Key).OrderBy(x => x, StringComparer.Ordinal).ToArray(); return new(reasons.Length > 0, reasons, "windows.registry", evidence.Values.All(x => x.HasValue)); }
}

internal static class WindowsTimeConfiguration
{
    internal static bool? ParseType(string? value) => value?.Trim().ToUpperInvariant() switch { "NOSYNC" => false, "NTP" or "NT5DS" or "ALLSYNC" => true, _ => null };
    internal static (bool? Configured, bool Complete) Evaluate(string? type, bool serviceAvailable, bool configReadable) { var configured = configReadable ? ParseType(type) : null; return (configured, configured.HasValue && serviceAvailable); }
}
