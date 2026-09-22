// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Packages.Storage.Linux;

public sealed class LinuxStorageToolProvider : IToolProvider
{
    public IEnumerable<ITool> GetTools() =>
    [
        new LinuxStorageDisksTool(),
        new LinuxStoragePartitionsTool(),
        new LinuxStorageMountsTool(),
        new LinuxStorageIoTool(),
        new LinuxStorageHealthTool(),
    ];
}
