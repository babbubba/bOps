// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Packages.Sys.Core;

/// <summary>Shared shell for read-only pending-update evidence.</summary>
public abstract class SystemUpdatesToolBase(string platform) : ITool
{
    /// <inheritdoc />
    public ToolManifest Manifest { get; } = SystemToolManifests.Updates(platform);

    /// <summary>Collects normalized evidence after the caller's bounded arguments have been validated.</summary>
    protected abstract Task<MaintenanceSnapshot<UpdateRecord>> CollectAsync(MaintenanceArguments arguments, CancellationToken ct);

    /// <inheritdoc />
    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!SystemMaintenanceArguments.TryReadUpdates(arguments, out var query, out var error))
        {
            return ToolCallResult.Failure(error!);
        }

        var snapshot = await CollectAsync(query, ct).ConfigureAwait(false);
        return ToolCallResult.Success(SystemMaintenanceFormatting.Updates(snapshot, query.Limit));
    }
}

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

/// <summary>The shared bounded tool shell for <c>system.apps</c>.</summary>
public abstract class ApplicationInventoryToolBase(string platform) : ITool
{
    /// <inheritdoc />
    public ToolManifest Manifest { get; } = SystemToolManifests.Applications(platform);

    /// <summary>Collects applications from this platform without exceeding <paramref name="collectionLimit"/> observations.</summary>
    protected abstract Task<InventorySnapshot<ApplicationInventoryItem>> CollectAsync(
        int collectionLimit,
        CancellationToken ct);

    /// <inheritdoc />
    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!InventoryToolArguments.TryRead(arguments, out var limit, out var maxOutputBytes, out var error))
        {
            return ToolCallResult.Failure(error!);
        }

        ct.ThrowIfCancellationRequested();
        var snapshot = await CollectAsync(InventoryToolLimits.CollectionItems, ct);
        return ToolCallResult.Success(SystemInventoryFormatting.FormatApplications(snapshot, limit, maxOutputBytes));
    }
}

/// <summary>The shared bounded tool shell for <c>system.devices</c>.</summary>
public abstract class DeviceInventoryToolBase(string platform) : ITool
{
    /// <inheritdoc />
    public ToolManifest Manifest { get; } = SystemToolManifests.Devices(platform);

    /// <summary>Collects devices from this platform without exceeding <paramref name="collectionLimit"/> observations.</summary>
    protected abstract Task<InventorySnapshot<DeviceInventoryItem>> CollectAsync(
        int collectionLimit,
        CancellationToken ct);

    /// <inheritdoc />
    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!InventoryToolArguments.TryRead(arguments, out var limit, out var maxOutputBytes, out var error))
        {
            return ToolCallResult.Failure(error!);
        }

        ct.ThrowIfCancellationRequested();
        var snapshot = await CollectAsync(InventoryToolLimits.CollectionItems, ct);
        return ToolCallResult.Success(SystemInventoryFormatting.FormatDevices(snapshot, limit, maxOutputBytes));
    }
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
        if (!ProcessDiagnosticsArguments.TryReadPid(arguments, out var pid, out var error))
        {
            return ToolCallResult.Failure(error!);
        }

        return ToolCallResult.Success(SystemToolFormatting.Format(await CollectAsync(pid, ct)));
    }
}

/// <summary>
/// The tool shell for <c>process.metrics</c> (ADR-0034). Each OS package contributes one
/// instantaneous reading of a process's cumulative counters; the interval, the difference and the
/// host normalization of CPU are computed here, so both platforms report the same numbers the same
/// way (agentic/01-architecture-rules.md, rule A8).
/// </summary>
public abstract class ProcessMetricsToolBase : ITool
{
    private const int BytesPerMb = 1024 * 1024;

    private readonly TimeProvider clock;

    /// <summary>Creates the shell for <paramref name="platform"/>; <paramref name="clock"/> defaults to the system clock.</summary>
    protected ProcessMetricsToolBase(string platform, TimeProvider? clock = null)
    {
        Manifest = SystemToolManifests.ProcessMetrics(platform);
        this.clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public ToolManifest Manifest { get; }

    /// <summary>
    /// Reads this process's cumulative counters once. A counter this identity may not read is
    /// <c>null</c>; a process that is gone is <see cref="ProcessSample.Exists"/> <c>false</c>.
    /// </summary>
    protected abstract Task<ProcessSample> SampleAsync(int pid, CancellationToken ct);

    /// <inheritdoc />
    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!ProcessDiagnosticsArguments.TryReadMetrics(arguments, out var pid, out var sampleMilliseconds, out var error))
        {
            return ToolCallResult.Failure(error!);
        }

        ct.ThrowIfCancellationRequested();
        var first = await SampleAsync(pid, ct);
        var startedAt = clock.GetTimestamp();
        await Task.Delay(TimeSpan.FromMilliseconds(sampleMilliseconds), clock, ct);
        var second = await SampleAsync(pid, ct);
        var elapsed = clock.GetElapsedTime(startedAt);

        return ToolCallResult.Success(ProcessDiagnosticsFormatting.Format(
            Compute(pid, sampleMilliseconds, first, second, elapsed)));
    }

    internal static ProcessMetricsResult Compute(
        int pid, int sampleMilliseconds, ProcessSample first, ProcessSample second, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        if (!first.Exists || !second.Exists)
        {
            // A process that appeared or disappeared between the samples has no rate at all: two
            // readings of different things are not a difference.
            return new ProcessMetricsResult
            {
                Pid = pid,
                Exists = false,
                SampleMilliseconds = sampleMilliseconds,
                Partial = true,
            };
        }

        var seconds = elapsed.TotalSeconds;
        var cpuPercent = first.CpuTotal is { } before && second.CpuTotal is { } after && seconds > 0
            ? Math.Clamp((after - before).TotalSeconds / seconds / Environment.ProcessorCount * 100.0, 0, 100)
            : (double?)null;

        var result = new ProcessMetricsResult
        {
            Pid = pid,
            Exists = true,
            SampleMilliseconds = sampleMilliseconds,
            CpuPercent = cpuPercent,
            WorkingSetMb = second.WorkingSetBytes / BytesPerMb,
            PrivateMemoryMb = second.PrivateMemoryBytes / BytesPerMb,
            VirtualMemoryMb = second.VirtualMemoryBytes / BytesPerMb,
            ThreadCount = second.ThreadCount,
            HandleOrFdCount = second.HandleOrFdCount,
            ReadBytesPerSec = Rate(first.ReadBytes, second.ReadBytes, seconds),
            WriteBytesPerSec = Rate(first.WriteBytes, second.WriteBytes, seconds),
            PageFaultsPerSec = Rate(first.PageFaults, second.PageFaults, seconds),
            Partial = false,
        };

        return result with { Partial = IsPartial(result) };
    }

    private static bool IsPartial(ProcessMetricsResult result) =>
        result.CpuPercent is null
        || result.WorkingSetMb is null
        || result.PrivateMemoryMb is null
        || result.VirtualMemoryMb is null
        || result.ThreadCount is null
        || result.HandleOrFdCount is null
        || result.ReadBytesPerSec is null
        || result.WriteBytesPerSec is null
        || result.PageFaultsPerSec is null;

    // A cumulative counter never decreases, but two reads of a counter the kernel updates per-thread
    // can arrive out of order; a negative rate is a reading artefact, not a fact about the process.
    private static double? Rate(long? before, long? after, double seconds) =>
        before is { } start && after is { } end && seconds > 0 ? Math.Max(0, (end - start) / seconds) : null;
}

/// <summary>
/// The tool shell for <c>process.tree</c> (ADR-0034). The OS package observes parents and names;
/// the walk, its bounds, its order and its completeness are decided here. Users are resolved only
/// for the rows that are actually returned, because on Windows that is a separate query per
/// process and the whole point of the bound is not to make hundreds of them.
/// </summary>
public abstract class ProcessTreeToolBase(string platform) : ITool
{
    /// <inheritdoc />
    public ToolManifest Manifest { get; } = SystemToolManifests.ProcessTree(platform);

    /// <summary>Observes every process this identity can see, with its parent and name.</summary>
    protected abstract Task<ProcessTreeSnapshot> CollectAsync(CancellationToken ct);

    /// <summary>
    /// Resolves the owning identity of each of <paramref name="pids"/>. A PID that is gone, or
    /// whose owner this identity may not read, is absent from the result or maps to <c>null</c>.
    /// </summary>
    protected abstract Task<IReadOnlyDictionary<int, string?>> ResolveUsersAsync(IReadOnlyList<int> pids, CancellationToken ct);

    /// <inheritdoc />
    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!ProcessDiagnosticsArguments.TryReadTree(arguments, out var rootPid, out var maxDepth, out var limit, out var error))
        {
            return ToolCallResult.Failure(error!);
        }

        ct.ThrowIfCancellationRequested();
        var snapshot = await CollectAsync(ct);
        var selection = ProcessTreeBuilder.Build(snapshot, rootPid, maxDepth, limit);
        var users = selection.Rows.Count == 0
            ? new Dictionary<int, string?>()
            : await ResolveUsersAsync(selection.Rows.Select(row => row.Pid).ToArray(), ct);

        return ToolCallResult.Success(ProcessDiagnosticsFormatting.FormatTree(selection, snapshot, rootPid, maxDepth, users));
    }
}

/// <summary>The tool shell for <c>process.modules</c> (ADR-0034).</summary>
public abstract class ProcessModulesToolBase(string platform) : ITool
{
    /// <inheritdoc />
    public ToolManifest Manifest { get; } = SystemToolManifests.ProcessModules(platform);

    /// <summary>
    /// Reads the modules of <paramref name="pid"/>, examining at most
    /// <paramref name="collectionLimit"/> native records. A read this identity may not perform is
    /// an explicit status, never an empty list.
    /// </summary>
    protected abstract Task<ProcessModuleSnapshot> CollectAsync(int pid, int collectionLimit, CancellationToken ct);

    /// <inheritdoc />
    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!ProcessDiagnosticsArguments.TryReadModules(arguments, out var pid, out var limit, out var maxOutputBytes, out var error))
        {
            return ToolCallResult.Failure(error!);
        }

        ct.ThrowIfCancellationRequested();
        var snapshot = await CollectAsync(pid, ProcessDiagnosticsLimits.ModuleScanCeiling, ct);
        return ToolCallResult.Success(ProcessDiagnosticsFormatting.FormatModules(snapshot, pid, limit, maxOutputBytes));
    }
}

/// <summary>
/// The tool shell for <c>process.stop</c>: a graceful termination request, verified via
/// <c>process.inspect</c> afterward — both platforms expect the same fact (the process is gone),
/// so the verification evaluation is shared here rather than duplicated per OS.
/// </summary>
public abstract class ProcessStopToolBase(string platform) : IVerifiableTool
{
    /// <inheritdoc />
    public ToolManifest Manifest { get; } = SystemToolManifests.ProcessStop(platform);

    /// <summary>
    /// Requests <paramref name="pid"/> stop gracefully. Returns <see cref="ToolOutcome.Failure"/>
    /// if no graceful-stop mechanism is available for this process on this platform — this is an
    /// honest platform limitation (Windows has no generic SIGTERM equivalent for a process without
    /// a main window), not something to paper over by silently forcing a kill instead.
    /// </summary>
    protected abstract Task<ToolCallResult> RequestStopAsync(int pid, CancellationToken ct);

    /// <inheritdoc />
    public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var pid = arguments.GetRequired<int>("pid");
        return RequestStopAsync(pid, ct);
    }

    /// <inheritdoc />
    public Task<VerificationOutcome> EvaluateVerificationAsync(
        ToolArguments originalArguments, ToolCallResult verificationToolResult, CancellationToken ct = default) =>
        EvaluateProcessAbsence(verificationToolResult, "stop");

    internal static Task<VerificationOutcome> EvaluateProcessAbsence(ToolCallResult verificationToolResult, string actionVerb)
    {
        ArgumentNullException.ThrowIfNull(verificationToolResult);

        if (!verificationToolResult.Succeeded)
        {
            return Task.FromResult(new VerificationOutcome(
                VerificationStatus.Inconclusive, $"Could not confirm the {actionVerb}: {verificationToolResult.ErrorMessage}"));
        }

        return Task.FromResult(ProcessInspectOutput.TryReadExists(verificationToolResult.Output) switch
        {
            false => new VerificationOutcome(VerificationStatus.Confirmed, null),
            true => new VerificationOutcome(VerificationStatus.Refuted, $"process.inspect reports the process still exists after the {actionVerb}."),
            null => new VerificationOutcome(VerificationStatus.Inconclusive, "process.inspect's output could not be read."),
        });
    }
}

/// <summary>The tool shell for <c>process.kill</c>: forced termination, verified via <c>process.inspect</c> afterward.</summary>
public abstract class ProcessKillToolBase(string platform) : IVerifiableTool
{
    /// <inheritdoc />
    public ToolManifest Manifest { get; } = SystemToolManifests.ProcessKill(platform);

    /// <summary>Forcibly terminates <paramref name="pid"/>.</summary>
    protected abstract Task<ToolCallResult> KillAsync(int pid, CancellationToken ct);

    /// <inheritdoc />
    public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var pid = arguments.GetRequired<int>("pid");
        return KillAsync(pid, ct);
    }

    /// <inheritdoc />
    public Task<VerificationOutcome> EvaluateVerificationAsync(
        ToolArguments originalArguments, ToolCallResult verificationToolResult, CancellationToken ct = default) =>
        ProcessStopToolBase.EvaluateProcessAbsence(verificationToolResult, "kill");
}
