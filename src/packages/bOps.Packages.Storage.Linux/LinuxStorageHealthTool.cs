// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Storage.Core;

namespace bOps.Packages.Storage.Linux;

internal sealed class LinuxStorageHealthTool : StorageHealthToolBase
{
    public LinuxStorageHealthTool()
        : base("linux")
    {
    }

    protected override Task<IReadOnlyList<StorageHealth>> CollectAsync(CancellationToken ct) =>
        LinuxStorageCollector.HealthAsync(ct);
}
