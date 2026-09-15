// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Windows;

/// <summary>
/// Collects <c>system.swap</c> data on Windows via <c>GlobalMemoryStatusEx</c>'s page-file
/// fields — Windows' commit-limit accounting, the same figures Task Manager's "Committed" chart
/// is built from, not a literal read of <c>pagefile.sys</c>'s current byte usage. Reading the
/// real per-file page-file usage would need WMI (<c>Win32_PageFileUsage</c>), a new dependency
/// this tool does not need for the conventional, already-available figure.
/// </summary>
public sealed class WindowsSwapUsageTool() : SwapUsageToolBase("windows")
{
    protected override Task<SwapUsageResult> CollectAsync(CancellationToken ct)
    {
        var status = new NativeMethods.MemoryStatusEx
        {
            Length = (uint)Marshal.SizeOf<NativeMethods.MemoryStatusEx>(),
        };

        if (!NativeMethods.GlobalMemoryStatusEx(ref status))
        {
            throw new InvalidOperationException("GlobalMemoryStatusEx failed.");
        }

        const int BytesPerMb = 1024 * 1024;
        var totalMb = (long)(status.TotalPageFileBytes / BytesPerMb);
        var availableMb = (long)(status.AvailablePageFileBytes / BytesPerMb);
        return Task.FromResult(new SwapUsageResult(totalMb, totalMb - availableMb));
    }
}
