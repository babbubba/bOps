// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Packages.Storage.Core;

public static class StorageManifests
{
    public static ToolManifest Disks(string platform) => ListManifest(
        "storage.disks", platform,
        "Lists physical disks with stable identity, model, serial, bus/media type, size, sectors and access flags.", []);

    public static ToolManifest Partitions(string platform) => ListManifest(
        "storage.partitions", platform,
        "Lists disk partitions with their owning disk, offsets, sizes, type and boot/read-only flags.",
        [new ToolParameter("diskId", ToolParameterType.String, "Filter by exact disk id.", Required: false)]);

    public static ToolManifest Mounts(string platform) => ListManifest(
        "storage.mounts", platform,
        "Lists mounted filesystems with capacity, inode usage, mount options and read-only state.", []);

    public static ToolManifest Io(string platform) => new()
    {
        Name = "storage.io",
        Description = "Samples storage counters twice and reports IOPS, throughput, latency, queue depth and utilization. Unavailable values are null.",
        Risk = RiskLevel.Read,
        Platforms = [platform],
        Requires = [],
        Parameters =
        [
            new ToolParameter("device", ToolParameterType.String, "Filter by exact enumerated device id.", Required: false),
            new ToolParameter("sampleMilliseconds", ToolParameterType.Integer,
                $"Sampling interval ({StorageLimits.MinimumSampleMilliseconds}-{StorageLimits.MaximumSampleMilliseconds}, default {StorageLimits.DefaultSampleMilliseconds}).", Required: false),
            OutputParameter(),
        ],
    };

    public static ToolManifest Health(string platform) => ListManifest(
        "storage.health", platform,
        "Reports bounded hardware health evidence. Unknown is never treated as healthy and raw SMART/WMI payloads are never returned.",
        [new ToolParameter("device", ToolParameterType.String, "Filter by exact enumerated device id.", Required: false)]);

    private static ToolManifest ListManifest(string name, string platform, string description, IReadOnlyList<ToolParameter> filters) => new()
    {
        Name = name,
        Description = description,
        Risk = RiskLevel.Read,
        Platforms = [platform],
        Requires = [],
        Parameters = [.. filters, RowParameter(), OutputParameter()],
    };

    private static ToolParameter RowParameter() => new(
        "limit", ToolParameterType.Integer, $"Maximum rows (1-{StorageLimits.MaximumRows}, default {StorageLimits.DefaultRows}).", Required: false);

    private static ToolParameter OutputParameter() => new(
        "maxOutputBytes", ToolParameterType.Integer,
        $"Maximum UTF-8 output bytes ({StorageLimits.MinimumOutputBytes}-{StorageLimits.MaximumOutputBytes}, default {StorageLimits.DefaultOutputBytes}).", Required: false);
}
