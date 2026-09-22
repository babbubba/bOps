// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
namespace bOps.Packages.Storage.Core;
public sealed record StorageMount(string Source, string MountPoint, string? FileSystemType, long? TotalBytes, long? FreeBytes, double? UsedPercent, bool ReadOnly, string? MountOptions, long? InodeTotal, long? InodeFree, double? InodeUsedPercent);
