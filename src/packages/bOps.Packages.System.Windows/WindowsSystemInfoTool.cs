using System.Runtime.InteropServices;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Windows;

/// <summary>Collects <c>system.info</c> data on Windows.</summary>
public sealed class WindowsSystemInfoTool() : SystemInfoToolBase("windows")
{
    protected override Task<SystemInfoResult> CollectAsync(CancellationToken ct)
    {
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var result = new SystemInfoResult(RuntimeInformation.OSDescription, Environment.MachineName, uptime);
        return Task.FromResult(result);
    }
}
