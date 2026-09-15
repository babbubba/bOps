// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Windows;

/// <summary>Collects <c>system.disk</c> data on Windows via <see cref="DriveInfo"/>.</summary>
public sealed class WindowsDiskUsageTool() : DiskUsageToolBase("windows")
{
    private const int BytesPerMb = 1024 * 1024;

    protected override Task<IReadOnlyList<DiskUsageResult>> CollectAsync(CancellationToken ct)
    {
        IReadOnlyList<DiskUsageResult> disks = DriveInfo.GetDrives()
            .Where(drive => drive.IsReady)
            .Select(drive => new DiskUsageResult(drive.Name, drive.TotalSize / BytesPerMb, drive.AvailableFreeSpace / BytesPerMb))
            .ToList();

        return Task.FromResult(disks);
    }
}
