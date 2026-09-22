// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Storage.Core;

namespace bOps.Packages.Storage.Linux;

internal sealed class LinuxStorageIoTool : StorageIoToolBase
{
    public LinuxStorageIoTool(TimeProvider? clock = null)
        : base("linux", clock)
    {
    }

    protected override Task<IReadOnlyList<StorageIoSample>> SampleAsync(string? device, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(LinuxStorageCollector.Io(device));
    }
}
