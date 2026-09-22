// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
namespace bOps.Packages.Storage.Core;
public sealed record StorageDisk(string Id, string Name, string? Model, string? Serial, string? BusType, string? MediaType, long SizeBytes, int? LogicalSectorBytes, int? PhysicalSectorBytes, bool? Rotational, bool Removable, bool ReadOnly);
