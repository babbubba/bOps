// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Packages.Sys.Core;

/// <summary>
/// The tool shell for <c>system.info</c>: manifest and result formatting shared, data
/// collection left to the OS package (agentic/01-architecture-rules.md, rule A8).
/// </summary>
public abstract class SystemInfoToolBase(string platform) : ITool
{
    /// <inheritdoc />
    public ToolManifest Manifest { get; } = SystemToolManifests.Info(platform);

    /// <summary>Collects this machine's OS description, hostname, and uptime.</summary>
    protected abstract Task<SystemInfoResult> CollectAsync(CancellationToken ct);

    /// <inheritdoc />
    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) =>
        ToolCallResult.Success(SystemToolFormatting.Format(await CollectAsync(ct)));
}

/// <summary>The tool shell for <c>system.cpu</c>.</summary>
public abstract class CpuUsageToolBase(string platform) : ITool
{
    /// <inheritdoc />
    public ToolManifest Manifest { get; } = SystemToolManifests.Cpu(platform);

    /// <summary>Samples current CPU utilization.</summary>
    protected abstract Task<CpuUsageResult> CollectAsync(CancellationToken ct);

    /// <inheritdoc />
    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) =>
        ToolCallResult.Success(SystemToolFormatting.Format(await CollectAsync(ct)));
}

/// <summary>The tool shell for <c>system.memory</c>.</summary>
public abstract class MemoryUsageToolBase(string platform) : ITool
{
    /// <inheritdoc />
    public ToolManifest Manifest { get; } = SystemToolManifests.Memory(platform);

    /// <summary>Collects total and available physical memory.</summary>
    protected abstract Task<MemoryUsageResult> CollectAsync(CancellationToken ct);

    /// <inheritdoc />
    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) =>
        ToolCallResult.Success(SystemToolFormatting.Format(await CollectAsync(ct)));
}

/// <summary>The tool shell for <c>system.disk</c>.</summary>
public abstract class DiskUsageToolBase(string platform) : ITool
{
    /// <inheritdoc />
    public ToolManifest Manifest { get; } = SystemToolManifests.Disk(platform);

    /// <summary>Collects space usage for every ready, mounted volume.</summary>
    protected abstract Task<IReadOnlyList<DiskUsageResult>> CollectAsync(CancellationToken ct);

    /// <inheritdoc />
    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) =>
        ToolCallResult.Success(SystemToolFormatting.Format(await CollectAsync(ct)));
}

/// <summary>The tool shell for <c>process.list</c>.</summary>
public abstract class ProcessListToolBase(string platform) : ITool
{
    private const int DefaultLimit = 20;

    /// <inheritdoc />
    public ToolManifest Manifest { get; } = SystemToolManifests.ProcessList(platform);

    /// <summary>Collects up to <paramref name="limit"/> processes, sorted by memory usage descending.</summary>
    protected abstract Task<IReadOnlyList<ProcessSummary>> CollectAsync(int limit, CancellationToken ct);

    /// <inheritdoc />
    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var limit = arguments.TryGet<int>("limit", out var requested) ? requested : DefaultLimit;
        return ToolCallResult.Success(SystemToolFormatting.Format(await CollectAsync(limit, ct)));
    }
}

/// <summary>The tool shell for <c>system.swap</c>.</summary>
public abstract class SwapUsageToolBase(string platform) : ITool
{
    /// <inheritdoc />
    public ToolManifest Manifest { get; } = SystemToolManifests.Swap(platform);

    /// <summary>Collects total and used swap (paging file) space.</summary>
    protected abstract Task<SwapUsageResult> CollectAsync(CancellationToken ct);

    /// <inheritdoc />
    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) =>
        ToolCallResult.Success(SystemToolFormatting.Format(await CollectAsync(ct)));
}

/// <summary>The tool shell for <c>system.io</c>.</summary>
public abstract class IoUsageToolBase(string platform) : ITool
{
    /// <inheritdoc />
    public ToolManifest Manifest { get; } = SystemToolManifests.Io(platform);

    /// <summary>Samples disk I/O throughput for every device over a short interval.</summary>
    protected abstract Task<IReadOnlyList<IoUsageResult>> CollectAsync(CancellationToken ct);

    /// <inheritdoc />
    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) =>
        ToolCallResult.Success(SystemToolFormatting.Format(await CollectAsync(ct)));
}

/// <summary>The tool shell for <c>process.inspect</c>.</summary>
public abstract class ProcessInspectToolBase(string platform) : ITool
{
    /// <inheritdoc />
    public ToolManifest Manifest { get; } = SystemToolManifests.ProcessInspect(platform);

    /// <summary>Inspects the single process identified by <paramref name="pid"/>.</summary>
    protected abstract Task<ProcessInspectResult> CollectAsync(int pid, CancellationToken ct);

    /// <inheritdoc />
    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var pid = arguments.GetRequired<int>("pid");
        return ToolCallResult.Success(SystemToolFormatting.Format(await CollectAsync(pid, ct)));
    }
}
