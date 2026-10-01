// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
namespace bOps.Packages.Storage.Core;

/// <summary>
/// One device's health evidence. The init-only members are additive evidence fields: <see cref="Partial"/> is an
/// evidence-completeness flag and is independent of <see cref="Health"/>.
/// </summary>
public sealed record StorageHealth(string Device, string Health, string? OperationalStatus, double? TemperatureC, long? PowerOnHours, long? MediaErrors, long? ReallocatedSectors, double? WearPercent, bool SmartAvailable, string Source, string? Detail)
{
    public bool Partial { get; init; }
    public string? PartialReason { get; init; }
    public string? ReliabilitySource { get; init; }
    public double? TemperatureMaxC { get; init; }
    public long? ReadErrorsTotal { get; init; }
    public long? WriteErrorsTotal { get; init; }
}
