// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
using bOps.Abstractions;
namespace bOps.Packages.Storage.Core;
public abstract class StorageMountsToolBase(string platform) : ITool
{
    public ToolManifest Manifest { get; } = StorageManifests.Mounts(platform);
    protected abstract Task<IReadOnlyList<StorageMount>> CollectAsync(CancellationToken ct);
    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        if (!StorageArguments.TryReadList(arguments, null, out _, out var limit, out var bytes, out var error)) return ToolCallResult.Failure(error!);
        return ToolCallResult.Success(StorageFormatting.Mounts(await CollectAsync(ct), limit, bytes));
    }
}
