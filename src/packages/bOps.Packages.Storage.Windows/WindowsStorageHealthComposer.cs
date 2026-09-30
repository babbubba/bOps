// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Storage.Core;

namespace bOps.Packages.Storage.Windows;

internal enum StorageHealthProviderState
{
    Available,
    NamespaceUnsupported,
    Failed,
}

internal enum ReliabilityState
{
    Available,
    Absent,
    AccessDenied,
    Failed,
}

internal sealed record PhysicalDiskRow(string? DeviceId, string? FriendlyName, int? HealthStatus, string? OperationalStatus);

internal sealed record ReliabilityRow(
    double? Temperature, double? TemperatureMax, double? Wear, long? ReadErrorsTotal, long? WriteErrorsTotal, long? PowerOnHours);

internal sealed record PhysicalDiskEvidence(PhysicalDiskRow Disk, ReliabilityState ReliabilityState, ReliabilityRow? Reliability);

internal sealed record StorageHealthSnapshot(StorageHealthProviderState State, IReadOnlyList<PhysicalDiskEvidence> Rows, string? Detail);

/// <summary>Pure mapping of Windows Storage provider evidence to <see cref="StorageHealth"/>; no WMI access.</summary>
internal static class WindowsStorageHealthComposer
{
    internal const string PhysicalDiskSource = "MSFT_PhysicalDisk";
    internal const string ReliabilitySource = "MSFT_StorageReliabilityCounter";
    internal const string IdentitySource = "Win32_DiskDrive";
    internal const string ElevationReason = "reliability counters require elevation";
    internal const string AbsentReason = "reliability counters are not reported for this device";
    internal const string FailedReason = "reliability counters could not be read";

    public static IReadOnlyList<StorageHealth> Compose(IReadOnlyList<StorageDisk> identities, StorageHealthSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(snapshot);
        switch (snapshot.State)
        {
            case StorageHealthProviderState.NamespaceUnsupported:
                return IdentityOnly(identities, "unsupported",
                    "The Windows Storage provider namespace is unavailable on this host; Win32_DiskDrive supplies disk identity only.");
            case StorageHealthProviderState.Failed:
                return IdentityOnly(identities, "unknown", snapshot.Detail ?? "Storage health provider unavailable.");
            default:
                return snapshot.Rows.Count == 0
                    ? IdentityOnly(identities, "unknown", "MSFT_PhysicalDisk returned no rows.")
                    : snapshot.Rows.Select(row => Map(identities, row)).ToArray();
        }
    }

    private static StorageHealth Map(IReadOnlyList<StorageDisk> identities, PhysicalDiskEvidence evidence)
    {
        var row = evidence.Disk;
        var matched = identities.SingleOrDefault(disk => WindowsStorageCollector.DiskIndex(disk.Id) == row.DeviceId);
        var device = matched?.Id ?? (row.DeviceId is null ? row.FriendlyName ?? "unknown" : $@"\\.\PHYSICALDRIVE{row.DeviceId}");
        var (health, detail) = row.HealthStatus switch
        {
            0 => ("healthy", (string?)null),
            1 => ("warning", null),
            2 => ("unhealthy", null),
            5 => ("unknown", "HealthStatus reported Unknown (5)."),
            null => ("unknown", "HealthStatus unavailable."),
            var other => ("unknown", $"Unexpected HealthStatus code {other}."),
        };

        var reliability = evidence.ReliabilityState == ReliabilityState.Available ? evidence.Reliability : null;
        var partialReason = evidence.ReliabilityState switch
        {
            ReliabilityState.AccessDenied => ElevationReason,
            ReliabilityState.Absent => AbsentReason,
            ReliabilityState.Failed => FailedReason,
            _ => null,
        };

        return new StorageHealth(
            device, health, row.OperationalStatus, reliability?.Temperature, reliability?.PowerOnHours, null, null,
            reliability?.Wear, false, PhysicalDiskSource, detail)
        {
            Partial = partialReason is not null,
            PartialReason = partialReason,
            ReliabilitySource = reliability is null ? null : ReliabilitySource,
            TemperatureMaxC = reliability?.TemperatureMax,
            ReadErrorsTotal = reliability?.ReadErrorsTotal,
            WriteErrorsTotal = reliability?.WriteErrorsTotal,
        };
    }

    private static StorageHealth[] IdentityOnly(IReadOnlyList<StorageDisk> identities, string health, string detail) =>
        identities.Select(disk => new StorageHealth(disk.Id, health, null, null, null, null, null, null, false, IdentitySource, detail)).ToArray();
}
