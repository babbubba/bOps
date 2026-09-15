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
        return new SystemInfoResult(RuntimeInformation.OSDescription, Environment.MachineName, TimeSpan.FromSeconds(uptimeSeconds));
    }

    private static async Task<double> ReadUptimeSecondsAsync(CancellationToken ct)
    {
        var text = await File.ReadAllTextAsync("/proc/uptime", ct);
        var firstField = text.Split(' ', 2)[0];
        return double.Parse(firstField, CultureInfo.InvariantCulture);
    }
}
