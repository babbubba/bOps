// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
using bOps.Abstractions;
namespace bOps.Packages.Storage.Core;
public abstract class StorageIoToolBase : ITool
{
    private readonly TimeProvider clock;
    protected StorageIoToolBase(string platform, TimeProvider? clock = null) { Manifest = StorageManifests.Io(platform); this.clock = clock ?? TimeProvider.System; }
    public ToolManifest Manifest { get; }
    protected abstract Task<IReadOnlyList<StorageIoSample>> SampleAsync(string? device, CancellationToken ct);
    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        if (!StorageArguments.TryReadIo(arguments, out var device, out var milliseconds, out var bytes, out var error)) return ToolCallResult.Failure(error!);
        var first = await SampleAsync(device, ct);
        var start = clock.GetTimestamp();
        await Task.Delay(TimeSpan.FromMilliseconds(milliseconds), clock, ct);
        var second = await SampleAsync(device, ct);
        return ToolCallResult.Success(StorageFormatting.Io(StorageCalculations.ComputeIo(first, second, clock.GetElapsedTime(start).TotalSeconds), milliseconds, bytes));
    }
}
