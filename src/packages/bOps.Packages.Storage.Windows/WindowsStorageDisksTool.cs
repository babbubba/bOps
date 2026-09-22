// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Storage.Core;

namespace bOps.Packages.Storage.Windows;

internal sealed class WindowsStorageDisksTool : StorageDisksToolBase
{
    public WindowsStorageDisksTool()
        : base("windows")
    {
    }

    protected override Task<IReadOnlyList<StorageDisk>> CollectAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(WindowsStorageCollector.Disks());
    }
}
