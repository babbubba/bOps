// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Runtime.InteropServices;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Linux;

/// <summary>Collects <c>system.info</c> data on Linux via <c>/proc/uptime</c>. See <a href="https://man7.org/linux/man-pages/man5/proc.5.html">proc(5)</a>.</summary>
public sealed class LinuxSystemInfoTool() : SystemInfoToolBase("linux")
{
    protected override async Task<SystemInfoResult> CollectAsync(CancellationToken ct)
    {
        var uptimeSeconds = await ReadUptimeSecondsAsync(ct);
        var hardwareModel = await ReadHardwareModelAsync(ct);
        return new SystemInfoResult(
            RuntimeInformation.OSDescription,
            Environment.MachineName,
            TimeSpan.FromSeconds(uptimeSeconds),
            hardwareModel);
    }

    private static async Task<double> ReadUptimeSecondsAsync(CancellationToken ct)
    {
        var text = await File.ReadAllTextAsync("/proc/uptime", ct);
        var firstField = text.Split(' ', 2)[0];
        return double.Parse(firstField, CultureInfo.InvariantCulture);
    }

    private static async Task<string?> ReadHardwareModelAsync(CancellationToken ct)
    {
        foreach (var path in new[] { "/sys/devices/virtual/dmi/id/product_name", "/proc/device-tree/model" })
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                var value = (await File.ReadAllTextAsync(path, ct)).Trim('\0', ' ', '\r', '\n', '\t');
                if (value.Length > 0)
                {
                    return value;
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                // Fall through to the next read-only source; absence is rendered explicitly as unknown.
            }
        }

        return null;
    }
}
