// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Storage.Core;

namespace bOps.Packages.Storage.Windows;

internal sealed class WindowsStorageIoTool : StorageIoToolBase
{
    public WindowsStorageIoTool(TimeProvider? clock = null)
        : base("windows", clock)
    {
    }

    protected override Task<IReadOnlyList<StorageIoSample>> SampleAsync(string? device, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(WindowsStorageCollector.Io(device));
    }
}
