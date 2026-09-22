// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
using bOps.Abstractions;
namespace bOps.Packages.Storage.Core;
public abstract class StoragePartitionsToolBase(string platform) : ITool
{
    public ToolManifest Manifest { get; } = StorageManifests.Partitions(platform);
    protected abstract Task<IReadOnlyList<StoragePartition>> CollectAsync(CancellationToken ct);
    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        if (!StorageArguments.TryReadList(arguments, "diskId", out var diskId, out var limit, out var bytes, out var error)) return ToolCallResult.Failure(error!);
        return ToolCallResult.Success(StorageFormatting.Partitions(await CollectAsync(ct), diskId, limit, bytes));
    }
}
