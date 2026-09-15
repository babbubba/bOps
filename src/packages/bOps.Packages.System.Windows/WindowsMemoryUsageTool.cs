// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Windows;

/// <summary>Collects <c>system.memory</c> data on Windows via <c>GlobalMemoryStatusEx</c>.</summary>
public sealed class WindowsMemoryUsageTool() : MemoryUsageToolBase("windows")
{
    protected override Task<MemoryUsageResult> CollectAsync(CancellationToken ct)
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
        var result = new MemoryUsageResult(
            (long)(status.TotalPhysicalBytes / BytesPerMb),
            (long)(status.AvailablePhysicalBytes / BytesPerMb));
        return Task.FromResult(result);
    }
}
