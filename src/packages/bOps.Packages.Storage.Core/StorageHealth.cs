// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
namespace bOps.Packages.Storage.Core;
public sealed record StorageHealth(string Device, string Health, string? OperationalStatus, double? TemperatureC, long? PowerOnHours, long? MediaErrors, long? ReallocatedSectors, double? WearPercent, bool SmartAvailable, string Source, string? Detail);
