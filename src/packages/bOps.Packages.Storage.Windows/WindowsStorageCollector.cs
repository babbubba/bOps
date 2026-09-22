// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Management;
using System.Text.RegularExpressions;
using bOps.Packages.Storage.Core;

namespace bOps.Packages.Storage.Windows;

internal static partial class WindowsStorageCollector
{
    public static IReadOnlyList<StorageDisk> Disks()
    {
        using var searcher = new ManagementObjectSearcher(
            "SELECT Index,DeviceID,Caption,Model,SerialNumber,InterfaceType,MediaType,Size,BytesPerSector FROM Win32_DiskDrive");
        using var results = searcher.Get();
        return results.Cast<ManagementObject>().Select(item => new StorageDisk(
            Text(item["DeviceID"]) ?? $"disk-{Number(item["Index"]) ?? 0}",
            Text(item["Caption"]) ?? Text(item["DeviceID"]) ?? "unknown",
            Text(item["Model"]), Text(item["SerialNumber"]), Text(item["InterfaceType"]), Text(item["MediaType"]),
            Long(item["Size"]) ?? 0, Integer(item["BytesPerSector"]), null,
            null, IsRemovable(Text(item["MediaType"])), false)).ToArray();
    }

    public static IReadOnlyList<StoragePartition> Partitions()
    {
        using var searcher = new ManagementObjectSearcher(
            "SELECT DiskIndex,Index,DeviceID,Name,StartingOffset,Size,Type,BootPartition FROM Win32_DiskPartition");
        using var results = searcher.Get();
        return results.Cast<ManagementObject>().Select(item =>
        {
            var disk = Number(item["DiskIndex"]) ?? 0;
            return new StoragePartition(
                $@"\\.\PHYSICALDRIVE{disk}",
                Text(item["DeviceID"]) ?? $"{disk}:{Number(item["Index"]) ?? 0}",
                Text(item["Name"]) ?? Text(item["DeviceID"]) ?? "unknown",
                Long(item["StartingOffset"]) ?? 0, Long(item["Size"]) ?? 0, Text(item["Type"]),
                Boolean(item["BootPartition"]), false);
        }).ToArray();
    }

    public static IReadOnlyList<StorageMount> Mounts()
    {
        using var searcher = new ManagementObjectSearcher(
            "SELECT DeviceID,FileSystem,Size,FreeSpace,Access FROM Win32_LogicalDisk WHERE DriveType=3");
        using var results = searcher.Get();
        return results.Cast<ManagementObject>().Select(item =>
        {
            var total = Long(item["Size"]);
            var free = Long(item["FreeSpace"]);
            return new StorageMount(
                Text(item["DeviceID"]) ?? "unknown", Text(item["DeviceID"]) ?? "unknown", Text(item["FileSystem"]),
                total, free, Used(total, free), Number(item["Access"]) == 1, null, null, null, null);
        }).ToArray();
    }

    public static IReadOnlyList<StorageIoSample> Io(string? requestedDevice)
    {
        var disks = DiskIndexes();
        using var searcher = new ManagementObjectSearcher(
            "SELECT Name,DiskReadsPerSec,DiskWritesPerSec,DiskReadBytesPerSec,DiskWriteBytesPerSec,PercentDiskReadTime,PercentDiskWriteTime,AvgDiskQueueLength,PercentDiskTime,CurrentDiskQueueLength FROM Win32_PerfRawData_PerfDisk_PhysicalDisk");
        using var results = searcher.Get();
        var samples = new List<StorageIoSample>();
        foreach (var item in results.Cast<ManagementObject>())
        {
            var name = Text(item["Name"]);
            if (!TryMapPerformanceInstance(name, disks, out var device) ||
                (requestedDevice is not null && !string.Equals(requestedDevice, device, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            samples.Add(new StorageIoSample(
                device!, Long(item["DiskReadsPerSec"]) ?? 0, Long(item["DiskWritesPerSec"]) ?? 0,
                Long(item["DiskReadBytesPerSec"]) ?? 0, Long(item["DiskWriteBytesPerSec"]) ?? 0,
                HundredNanosecondsToMilliseconds(item["PercentDiskReadTime"]),
                HundredNanosecondsToMilliseconds(item["PercentDiskWriteTime"]),
                HundredNanosecondsToMilliseconds(item["AvgDiskQueueLength"]),
                HundredNanosecondsToMilliseconds(item["PercentDiskTime"]),
                Long(item["CurrentDiskQueueLength"]) ?? 0));
        }

        return samples;
    }

    public static IReadOnlyList<StorageHealth> Health()
    {
        var disks = Disks();
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(
                "SELECT DeviceId,FriendlyName,HealthStatus,OperationalStatus,MediaType,Temperature,Usage,Size FROM MSFT_PhysicalDisk"));
            using var results = searcher.Get();
            var rows = new List<StorageHealth>();
            foreach (var item in results.Cast<ManagementObject>())
            {
                var deviceId = Text(item["DeviceId"]);
                var matched = disks.SingleOrDefault(disk => DiskIndex(disk.Id) == deviceId);
                var device = matched?.Id ?? (deviceId is null ? Text(item["FriendlyName"]) ?? "unknown" : $@"\\.\PHYSICALDRIVE{deviceId}");
                var healthCode = Number(item["HealthStatus"]);
                rows.Add(new StorageHealth(
                    device, healthCode switch { 0 => "healthy", 1 => "warning", 2 => "critical", _ => "unknown" },
                    JoinNumbers(item["OperationalStatus"]), Celsius(item["Temperature"]), null, null, null,
                    null, false, "MSFT_PhysicalDisk", healthCode is null ? "HealthStatus unavailable." : null));
            }

            return rows.Count == 0 ? UnknownHealth(disks, "MSFT_PhysicalDisk returned no rows.") : rows;
        }
        catch (ManagementException ex)
        {
            return UnknownHealth(disks, $"Storage health provider unavailable: {ex.ErrorCode}.");
        }
        catch (UnauthorizedAccessException)
        {
            return UnknownHealth(disks, "Storage health provider denied access.");
        }
    }

    internal static bool TryMapPerformanceInstance(string? instanceName, IReadOnlyDictionary<int, string> disks, out string? device)
    {
        device = null;
        if (string.IsNullOrWhiteSpace(instanceName) || string.Equals(instanceName, "_Total", StringComparison.OrdinalIgnoreCase)) return false;
        var match = DiskInstanceRegex().Match(instanceName);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var index)) return false;
        return disks.TryGetValue(index, out device);
    }

    private static Dictionary<int, string> DiskIndexes()
    {
        using var searcher = new ManagementObjectSearcher("SELECT Index,DeviceID FROM Win32_DiskDrive");
        using var results = searcher.Get();
        return results.Cast<ManagementObject>().Where(x => Number(x["Index"]) is not null && Text(x["DeviceID"]) is not null)
            .ToDictionary(x => Number(x["Index"])!.Value, x => Text(x["DeviceID"])!, EqualityComparer<int>.Default);
    }

    private static StorageHealth[] UnknownHealth(IReadOnlyList<StorageDisk> disks, string detail) =>
        disks.Select(disk => new StorageHealth(disk.Id, "unknown", null, null, null, null, null, null, false, "Win32_DiskDrive", detail)).ToArray();

    private static string? DiskIndex(string id)
    {
        var match = PhysicalDriveRegex().Match(id);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static long HundredNanosecondsToMilliseconds(object? value) => (Long(value) ?? 0) / 10_000;
    private static double? Used(long? total, long? free) => total > 0 && free is not null ? Math.Clamp((total.Value - free.Value) * 100d / total.Value, 0, 100) : null;
    private static bool IsRemovable(string? media) => media?.Contains("removable", StringComparison.OrdinalIgnoreCase) == true;
    private static double? Celsius(object? value) => Number(value) is { } n && n > 0 ? n : null;
    private static string? Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() is { Length: > 0 } text ? text : null;
    private static long? Long(object? value) => value is null ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    private static int? Integer(object? value) => value is null ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    private static int? Number(object? value) => Integer(value);
    private static bool Boolean(object? value) => value is not null && Convert.ToBoolean(value, CultureInfo.InvariantCulture);
    private static string? JoinNumbers(object? value) => value is Array values ? string.Join(',', values.Cast<object>().Select(Number)) : Text(value);

    [GeneratedRegex(@"^(\d+)(?:\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex DiskInstanceRegex();

    [GeneratedRegex(@"PHYSICALDRIVE(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PhysicalDriveRegex();
}
