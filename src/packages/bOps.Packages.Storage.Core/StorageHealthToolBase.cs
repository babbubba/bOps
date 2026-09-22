// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
using bOps.Abstractions;
namespace bOps.Packages.Storage.Core;
public abstract class StorageHealthToolBase(string platform) : ITool
{
    public ToolManifest Manifest { get; } = StorageManifests.Health(platform);
    protected abstract Task<IReadOnlyList<StorageHealth>> CollectAsync(CancellationToken ct);
    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        if (!StorageArguments.TryReadList(arguments, "device", out var device, out var limit, out var bytes, out var error)) return ToolCallResult.Failure(error!);
        return ToolCallResult.Success(StorageFormatting.Health(await CollectAsync(ct), device, limit, bytes));
    }
}
