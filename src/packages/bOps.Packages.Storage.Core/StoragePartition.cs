// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
namespace bOps.Packages.Storage.Core;
public sealed record StoragePartition(string DiskId, string PartitionId, string Name, long StartBytes, long SizeBytes, string? Type, bool Boot, bool ReadOnly);
