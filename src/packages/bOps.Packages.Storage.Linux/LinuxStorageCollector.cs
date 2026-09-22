// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using bOps.Packages.Storage.Core;

namespace bOps.Packages.Storage.Linux;

internal static class LinuxStorageCollector
{
    private const string SysBlock = "/sys/block";

    public static IReadOnlyList<StorageDisk> Disks(string sysBlock = SysBlock) => DeviceNames(sysBlock).Select(name =>
    {
        var path = Path.Combine(sysBlock, name);
        return new StorageDisk(
            $"/dev/{name}", name, Read(path, "device/model"), Read(path, "device/serial"), Bus(path, name), Media(Read(path, "queue/rotational")),
            SectorBytes(ReadLong(path, "size")), ToInt(ReadLong(path, "queue/logical_block_size")), ToInt(ReadLong(path, "queue/physical_block_size")),
            ReadBool(path, "queue/rotational"), ReadBool(path, "removable") ?? false, ReadBool(path, "ro") ?? false);
    }).ToArray();

    public static IReadOnlyList<StoragePartition> Partitions(string sysBlock = SysBlock)
    {
        var rows = new List<StoragePartition>();
        foreach (var disk in DeviceNames(sysBlock))
        {
            var diskPath = Path.Combine(sysBlock, disk);
            foreach (var partitionPath in Directory.EnumerateDirectories(diskPath).Where(path => File.Exists(Path.Combine(path, "partition"))))
            {
                var name = Path.GetFileName(partitionPath);
                rows.Add(new StoragePartition(
                    $"/dev/{disk}", $"/dev/{name}", name, SectorBytes(ReadLong(partitionPath, "start")),
                    SectorBytes(ReadLong(partitionPath, "size")), Read(partitionPath, "partition"), false, ReadBool(partitionPath, "ro") ?? false));
            }
        }

        return rows;
    }

    public static IReadOnlyList<StorageMount> Mounts(string mountInfo = "/proc/self/mountinfo")
    {
        return LinuxMountInfoParser.Parse(File.ReadLines(mountInfo)).Select(mount =>
        {
            long? total = null, free = null, inodeTotal = null, inodeFree = null;
            if (LinuxStatVfs.Read(mount.MountPoint, out var stat) == 0)
            {
                total = Saturating(stat.Blocks, stat.FragmentSize);
                free = Saturating(stat.BlocksAvailable, stat.FragmentSize);
                inodeTotal = Clamp(stat.Files);
                inodeFree = Clamp(stat.FilesFree);
            }

            return new StorageMount(
                mount.Source, mount.MountPoint, mount.FileSystemType, total, free, Percent(total, free), mount.ReadOnly,
                mount.Options, inodeTotal, inodeFree, Percent(inodeTotal, inodeFree));
        }).ToArray();
    }

    public static IReadOnlyList<StorageIoSample> Io(string? device, string sysBlock = SysBlock, string diskStats = "/proc/diskstats") =>
        LinuxDiskStatsParser.Parse(File.ReadLines(diskStats), DeviceNames(sysBlock).ToHashSet(StringComparer.Ordinal), device);

    public static async Task<IReadOnlyList<StorageHealth>> HealthAsync(CancellationToken ct, string sysBlock = SysBlock)
    {
        var executable = LinuxSmartctl.FindExecutable();
        var rows = new List<StorageHealth>();
        foreach (var name in DeviceNames(sysBlock))
        {
            ct.ThrowIfCancellationRequested();
            var device = $"/dev/{name}";
            StorageHealth? smart = null;
            if (executable is not null)
            {
                try { smart = await LinuxSmartctl.ReadAsync(executable, device, ct); }
                catch (InvalidOperationException) { }
            }

            rows.Add(smart ?? new StorageHealth(
                device, "unknown", Read(Path.Combine(sysBlock, name), "device/state"), null, null, null, null, null,
                false, "sysfs", executable is null ? "smartctl is not installed." : "smartctl returned no usable JSON for this device."));
        }

        return rows;
    }

    private static IEnumerable<string> DeviceNames(string root) => Directory.Exists(root)
        ? Directory.EnumerateDirectories(root).Select(path => Path.GetFileName(path)!).Where(name => !string.IsNullOrEmpty(name))
        : [];
    private static string? Read(string root, string relative)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } value ? value : null;
    }
    private static long? ReadLong(string root, string relative) => long.TryParse(Read(root, relative), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    private static bool? ReadBool(string root, string relative) => ReadLong(root, relative) switch { 0 => false, 1 => true, _ => null };
    private static long SectorBytes(long? sectors) => sectors is null || sectors > long.MaxValue / 512 ? 0 : sectors.Value * 512;
    private static int? ToInt(long? value) => value is >= int.MinValue and <= int.MaxValue ? (int)value.Value : null;
    private static string? Bus(string path, string name)
    {
        if (name.StartsWith("nvme", StringComparison.Ordinal)) return "nvme";
        var subsystem = new DirectoryInfo(Path.Combine(path, "device/subsystem"));
        return subsystem.Exists ? subsystem.LinkTarget?.Replace('\\', '/').Split('/').LastOrDefault() : null;
    }
    private static string? Media(string? rotational) => rotational switch { "1" => "hdd", "0" => "ssd", _ => null };
    private static long Clamp(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;
    private static long Saturating(ulong count, ulong size) => count != 0 && size > (ulong)long.MaxValue / count ? long.MaxValue : (long)(count * size);
    private static double? Percent(long? total, long? free) => total > 0 && free is not null ? Math.Clamp((total.Value - free.Value) * 100d / total.Value, 0, 100) : null;
}
