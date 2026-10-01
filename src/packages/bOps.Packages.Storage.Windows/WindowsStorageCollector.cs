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

    internal const string StorageNamespace = @"\\.\root\Microsoft\Windows\Storage";

    // MSFT_PhysicalDisk has no Temperature property; asking for one makes the whole query fail as InvalidQuery.
    internal const string PhysicalDiskHealthQuery =
        "SELECT ObjectId,DeviceId,FriendlyName,HealthStatus,OperationalStatus,MediaType,Usage,Size FROM MSFT_PhysicalDisk";

    internal const string ReliabilityClass = "MSFT_StorageReliabilityCounter";
    internal const string ReliabilityAssociationClass = "MSFT_PhysicalDiskToStorageReliabilityCounter";

    internal static readonly IReadOnlyList<string> ReliabilityProperties =
        ["DeviceId", "Temperature", "TemperatureMax", "Wear", "ReadErrorsTotal", "WriteErrorsTotal", "PowerOnHours"];

    public static IReadOnlyList<StorageHealth> Health() => WindowsStorageHealthComposer.Compose(Disks(), QueryHealth());

    private static StorageHealthSnapshot QueryHealth()
    {
        try
        {
            var scope = new ManagementScope(StorageNamespace);
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(PhysicalDiskHealthQuery));
            using var results = searcher.Get();
            var rows = new List<PhysicalDiskEvidence>();
            foreach (var item in results.Cast<ManagementObject>())
            {
                using (item)
                {
                    var disk = new PhysicalDiskRow(
                        Text(item["DeviceId"]), Text(item["FriendlyName"]), Number(item["HealthStatus"]), JoinNumbers(item["OperationalStatus"]));
                    var (state, reliability) = ReadReliability(item);
                    rows.Add(new PhysicalDiskEvidence(disk, state, reliability));
                }
            }

            return new StorageHealthSnapshot(StorageHealthProviderState.Available, rows, null);
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.InvalidNamespace)
        {
            return new StorageHealthSnapshot(StorageHealthProviderState.NamespaceUnsupported, [], null);
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
        {
            return new StorageHealthSnapshot(StorageHealthProviderState.Failed, [], "Storage health provider denied access.");
        }
        catch (ManagementException ex)
        {
            return new StorageHealthSnapshot(StorageHealthProviderState.Failed, [], $"Storage health provider unavailable: {ex.ErrorCode}.");
        }
        catch (UnauthorizedAccessException)
        {
            return new StorageHealthSnapshot(StorageHealthProviderState.Failed, [], "Storage health provider denied access.");
        }
    }

    // Reliability counters are supplemental: a failure here must never disturb the base health evidence.
    private static (ReliabilityState State, ReliabilityRow? Row) ReadReliability(ManagementObject physicalDisk)
    {
        try
        {
            var query = new RelatedObjectQuery(
                physicalDisk.Path.RelativePath, ReliabilityClass, ReliabilityAssociationClass, null, null, null, null, false);
            using var searcher = new ManagementObjectSearcher(physicalDisk.Scope, query);
            using var results = searcher.Get();
            var related = results.Cast<ManagementObject>().ToList();
            try
            {
                if (related.Count == 0) return (ReliabilityCountersDenied(physicalDisk.Scope) ? ReliabilityState.AccessDenied : ReliabilityState.Absent, null);
                if (related.Count != 1) return (ReliabilityState.Absent, null);
                var counter = related[0];
                return (ReliabilityState.Available, new ReliabilityRow(
                    Double(counter["Temperature"]), Double(counter["TemperatureMax"]), Double(counter["Wear"]),
                    UnsignedLong(counter["ReadErrorsTotal"]), UnsignedLong(counter["WriteErrorsTotal"]), UnsignedLong(counter["PowerOnHours"])));
            }
            finally
            {
                foreach (var item in related) item.Dispose();
            }
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
        {
            return (ReliabilityState.AccessDenied, null);
        }
        catch (UnauthorizedAccessException)
        {
            return (ReliabilityState.AccessDenied, null);
        }
        catch (ManagementException)
        {
            return (ReliabilityState.Failed, null);
        }
    }

    // The association query can return no rows instead of failing when the caller lacks rights, so ask the
    // class directly: only an explicit access-denied answer is reported as an elevation requirement.
    private static bool ReliabilityCountersDenied(ManagementScope scope)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT DeviceId FROM {ReliabilityClass}"));
            using var results = searcher.Get();
            foreach (var item in results.Cast<ManagementObject>())
            {
                item.Dispose();
                break;
            }

            return false;
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch (ManagementException)
        {
            return false;
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

    internal static string? DiskIndex(string id)
    {
        var match = PhysicalDriveRegex().Match(id);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static long HundredNanosecondsToMilliseconds(object? value) => (Long(value) ?? 0) / 10_000;
    private static double? Used(long? total, long? free) => total > 0 && free is not null ? Math.Clamp((total.Value - free.Value) * 100d / total.Value, 0, 100) : null;
    private static bool IsRemovable(string? media) => media?.Contains("removable", StringComparison.OrdinalIgnoreCase) == true;
    private static double? Double(object? value) => value is null ? null : Convert.ToDouble(value, CultureInfo.InvariantCulture);
    private static long? UnsignedLong(object? value)
    {
        if (value is null) return null;
        try { return Convert.ToInt64(value, CultureInfo.InvariantCulture); }
        catch (OverflowException) { return null; }
    }
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
