// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Packages.Storage.Windows;

public sealed class WindowsStorageToolProvider : IToolProvider
{
    public IEnumerable<ITool> GetTools() =>
    [
        new WindowsStorageDisksTool(),
        new WindowsStoragePartitionsTool(),
        new WindowsStorageMountsTool(),
        new WindowsStorageIoTool(),
        new WindowsStorageHealthTool(),
    ];
}
