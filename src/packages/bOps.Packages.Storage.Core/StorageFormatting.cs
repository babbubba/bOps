// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json.Nodes;

namespace bOps.Packages.Storage.Core;

public static class StorageFormatting
{
    public static string Disks(IReadOnlyList<StorageDisk> values, int limit, int bytes)
    {
        ArgumentNullException.ThrowIfNull(values);
        return FormatRows("disks", values.Take(limit).Select(x => (JsonNode)new JsonObject
        {
            ["id"] = Bound(x.Id), ["name"] = Bound(x.Name), ["model"] = Bound(x.Model), ["serial"] = Bound(x.Serial),
            ["busType"] = x.BusType, ["mediaType"] = x.MediaType, ["sizeBytes"] = x.SizeBytes,
            ["logicalSectorBytes"] = x.LogicalSectorBytes, ["physicalSectorBytes"] = x.PhysicalSectorBytes,
            ["rotational"] = x.Rotational, ["removable"] = x.Removable, ["readOnly"] = x.ReadOnly,
        }), values.Count, limit, bytes);
    }

    public static string Partitions(IReadOnlyList<StoragePartition> values, string? diskId, int limit, int bytes)
    {
        ArgumentNullException.ThrowIfNull(values);
        var filtered = values.Where(x => diskId is null || string.Equals(x.DiskId, diskId, StringComparison.OrdinalIgnoreCase)).ToArray();
        return FormatRows("partitions", filtered.Take(limit).Select(x => (JsonNode)new JsonObject
        {
            ["diskId"] = Bound(x.DiskId), ["partitionId"] = Bound(x.PartitionId), ["name"] = Bound(x.Name),
            ["startBytes"] = x.StartBytes, ["sizeBytes"] = x.SizeBytes, ["type"] = Bound(x.Type), ["boot"] = x.Boot, ["readOnly"] = x.ReadOnly,
        }), filtered.Length, limit, bytes);
    }

    public static string Mounts(IReadOnlyList<StorageMount> values, int limit, int bytes)
    {
        ArgumentNullException.ThrowIfNull(values);
        return FormatRows("mounts", values.Take(limit).Select(x => (JsonNode)new JsonObject
        {
            ["source"] = Bound(x.Source), ["mountPoint"] = Bound(x.MountPoint), ["fileSystemType"] = x.FileSystemType,
            ["totalBytes"] = x.TotalBytes, ["freeBytes"] = x.FreeBytes, ["usedPercent"] = Round(x.UsedPercent),
            ["readOnly"] = x.ReadOnly, ["mountOptions"] = Bound(x.MountOptions), ["inodeTotal"] = x.InodeTotal,
            ["inodeFree"] = x.InodeFree, ["inodeUsedPercent"] = Round(x.InodeUsedPercent),
        }), values.Count, limit, bytes);
    }

    public static string Io(IReadOnlyList<StorageIoResult> values, int sampleMilliseconds, int bytes)
    {
        ArgumentNullException.ThrowIfNull(values);
        return FormatRows("devices", values.Select(x => (JsonNode)new JsonObject
        {
            ["device"] = Bound(x.Device), ["readsPerSec"] = Round(x.ReadsPerSec), ["writesPerSec"] = Round(x.WritesPerSec),
            ["readBytesPerSec"] = Round(x.ReadBytesPerSec), ["writeBytesPerSec"] = Round(x.WriteBytesPerSec),
            ["readLatencyMs"] = Round(x.ReadLatencyMs), ["writeLatencyMs"] = Round(x.WriteLatencyMs),
            ["queueDepth"] = Round(x.QueueDepth), ["utilizationPercent"] = Round(x.UtilizationPercent),
        }), values.Count, values.Count, bytes, sampleMilliseconds);
    }

    public static string Health(IReadOnlyList<StorageHealth> values, string? device, int limit, int bytes)
    {
        ArgumentNullException.ThrowIfNull(values);
        var filtered = values.Where(x => device is null || string.Equals(x.Device, device, StringComparison.OrdinalIgnoreCase)).ToArray();
        return FormatRows("devices", filtered.Take(limit).Select(x => (JsonNode)new JsonObject
        {
            ["device"] = Bound(x.Device), ["health"] = x.Health, ["operationalStatus"] = Bound(x.OperationalStatus),
            ["temperatureC"] = Round(x.TemperatureC), ["powerOnHours"] = x.PowerOnHours, ["mediaErrors"] = x.MediaErrors,
            ["reallocatedSectors"] = x.ReallocatedSectors, ["wearPercent"] = Round(x.WearPercent),
            ["smartAvailable"] = x.SmartAvailable, ["source"] = x.Source, ["detail"] = Bound(x.Detail, 512),
        }), filtered.Length, limit, bytes);
    }

    private static string FormatRows(string property, IEnumerable<JsonNode> source, int matched, int limit, int maxBytes, int? sampleMilliseconds = null)
    {
        var rows = new JsonArray(source.ToArray());
        var root = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["matched"] = matched,
            ["returned"] = rows.Count,
            ["truncated"] = matched > rows.Count,
            [property] = rows,
        };
        if (sampleMilliseconds is not null)
        {
            root["sampleMilliseconds"] = sampleMilliseconds;
        }

        var output = root.ToJsonString();
        while (Encoding.UTF8.GetByteCount(output) > maxBytes && rows.Count > 0)
        {
            rows.RemoveAt(rows.Count - 1);
            root["returned"] = rows.Count;
            root["truncated"] = true;
            output = root.ToJsonString();
        }

        return output;
    }

    private static double? Round(double? value) => value is null ? null : Math.Round(value.Value, 2, MidpointRounding.AwayFromZero);
    private static string? Bound(string? value, int maximum = StorageLimits.TextCharacters) =>
        value is null || value.Length <= maximum ? value : string.Concat(value.AsSpan(0, maximum), "…");
}
