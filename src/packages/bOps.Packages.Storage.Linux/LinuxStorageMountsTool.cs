// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Storage.Core;

namespace bOps.Packages.Storage.Linux;

internal sealed class LinuxStorageMountsTool : StorageMountsToolBase
{
    public LinuxStorageMountsTool()
        : base("linux")
    {
    }

    protected override Task<IReadOnlyList<StorageMount>> CollectAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(LinuxStorageCollector.Mounts());
    }
}
