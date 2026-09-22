// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Storage.Core;

namespace bOps.Packages.Storage.Windows;

internal sealed class WindowsStorageMountsTool : StorageMountsToolBase
{
    public WindowsStorageMountsTool()
        : base("windows")
    {
    }

    protected override Task<IReadOnlyList<StorageMount>> CollectAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(WindowsStorageCollector.Mounts());
    }
}
