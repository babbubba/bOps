using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Linux;

/// <summary>Collects <c>system.disk</c> data on Linux via <see cref="DriveInfo"/>.</summary>
public sealed class LinuxDiskUsageTool() : DiskUsageToolBase("linux")
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
