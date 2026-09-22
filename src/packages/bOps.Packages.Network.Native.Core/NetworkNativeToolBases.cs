// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Packages.Network.Native.Core;

/// <summary>The tool shell for <c>network.sockets</c> (V1.3-D, ADR-0035).</summary>
public abstract class SocketsToolBase(string platform) : ITool
{
    /// <inheritdoc />
    public ToolManifest Manifest { get; } = NetworkNativeManifests.Sockets(platform);

    /// <summary>Observes every TCP/UDP socket this identity can see, unfiltered by argument.</summary>
    protected abstract Task<SocketsSnapshot> CollectAsync(CancellationToken ct);

    /// <inheritdoc />
    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!NetworkNativeArguments.TryReadSockets(arguments, out var protocol, out var state, out var pid, out var localPort, out var limit, out var error))
        {
            return ToolCallResult.Failure(error!);
        }

        ct.ThrowIfCancellationRequested();
        var snapshot = await CollectAsync(ct);
        return ToolCallResult.Success(NetworkNativeFormatting.FormatSockets(snapshot, protocol, state, pid, localPort, limit));
    }
}

/// <summary>The tool shell for <c>network.routes</c> (V1.3-D, ADR-0035).</summary>
public abstract class RoutesToolBase(string platform) : ITool
{
    /// <inheritdoc />
    public ToolManifest Manifest { get; } = NetworkNativeManifests.Routes(platform);

    /// <summary>Observes the full OS routing table.</summary>
    protected abstract Task<RoutesSnapshot> CollectAsync(CancellationToken ct);

    /// <inheritdoc />
    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!NetworkNativeArguments.TryReadRoutes(arguments, out var addressFamily, out var limit, out var error))
        {
            return ToolCallResult.Failure(error!);
        }

        ct.ThrowIfCancellationRequested();
        var snapshot = await CollectAsync(ct);
        return ToolCallResult.Success(NetworkNativeFormatting.FormatRoutes(snapshot, addressFamily, limit));
    }
}

/// <summary>The tool shell for <c>network.neighbors</c> (V1.3-D, ADR-0035).</summary>
public abstract class NeighborsToolBase(string platform) : ITool
{
    /// <inheritdoc />
    public ToolManifest Manifest { get; } = NetworkNativeManifests.Neighbors(platform);

    /// <summary>Observes the OS neighbor (ARP/NDP) cache.</summary>
    protected abstract Task<NeighborsSnapshot> CollectAsync(CancellationToken ct);

    /// <inheritdoc />
    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!NetworkNativeArguments.TryReadNeighbors(arguments, out var addressFamily, out var interfaceName, out var limit, out var error))
        {
            return ToolCallResult.Failure(error!);
        }

        ct.ThrowIfCancellationRequested();
        var snapshot = await CollectAsync(ct);
        return ToolCallResult.Success(NetworkNativeFormatting.FormatNeighbors(snapshot, addressFamily, interfaceName, limit));
    }
}

/// <summary>
/// The tool shell for <c>network.interface_stats</c> (V1.3-D, ADR-0035). Two samples and the rate
/// arithmetic between them are computed here, exactly as <c>process.metrics</c> does for a process
/// (ADR-0034 precedent), so both platforms report the same numbers the same way.
/// </summary>
public abstract class InterfaceStatsToolBase : ITool
{
    private readonly TimeProvider clock;

    /// <summary>Creates the shell for <paramref name="platform"/>; <paramref name="clock"/> defaults to the system clock.</summary>
    protected InterfaceStatsToolBase(string platform, TimeProvider? clock = null)
    {
        Manifest = NetworkNativeManifests.InterfaceStats(platform);
        this.clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public ToolManifest Manifest { get; }

    /// <summary>
    /// Reads every interface's cumulative counters once, optionally restricted to
    /// <paramref name="interfaceName"/>.
    /// </summary>
    protected abstract Task<IReadOnlyList<InterfaceCounterSample>> SampleAsync(string? interfaceName, CancellationToken ct);

    /// <inheritdoc />
    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!NetworkNativeArguments.TryReadInterfaceStats(arguments, out var interfaceName, out var sampleMilliseconds, out var error))
        {
            return ToolCallResult.Failure(error!);
        }

        ct.ThrowIfCancellationRequested();
        var first = await SampleAsync(interfaceName, ct);
        var startedAt = clock.GetTimestamp();
        await Task.Delay(TimeSpan.FromMilliseconds(sampleMilliseconds), clock, ct);
        var second = await SampleAsync(interfaceName, ct);
        var elapsed = clock.GetElapsedTime(startedAt);

        return ToolCallResult.Success(NetworkNativeFormatting.FormatInterfaceStats(Compute(first, second, elapsed.TotalSeconds), sampleMilliseconds));
    }

    internal static IReadOnlyList<InterfaceStatsResult> Compute(
        IReadOnlyList<InterfaceCounterSample> first, IReadOnlyList<InterfaceCounterSample> second, double seconds)
    {
        var before = first.ToDictionary(sample => sample.InterfaceName, StringComparer.OrdinalIgnoreCase);

        return second.Select(after =>
        {
            if (!before.TryGetValue(after.InterfaceName, out var earlier))
            {
                return new InterfaceStatsResult(after.InterfaceName, null, null, null, null, null, null, null, null, after.SpeedMbps, after.OperationalStatus);
            }

            return new InterfaceStatsResult(
                after.InterfaceName,
                Rate(earlier.BytesReceived, after.BytesReceived, seconds),
                Rate(earlier.BytesSent, after.BytesSent, seconds),
                Rate(earlier.PacketsReceived, after.PacketsReceived, seconds),
                Rate(earlier.PacketsSent, after.PacketsSent, seconds),
                Rate(earlier.ReceiveErrors, after.ReceiveErrors, seconds),
                Rate(earlier.SendErrors, after.SendErrors, seconds),
                Rate(earlier.ReceiveDrops, after.ReceiveDrops, seconds),
                Rate(earlier.SendDrops, after.SendDrops, seconds),
                after.SpeedMbps,
                after.OperationalStatus);
        }).ToArray();
    }

    // A cumulative counter never decreases in practice, but a counter that wrapped or an interface
    // that was reset between the two samples would otherwise produce a nonsensical negative rate.
    private static double? Rate(long? beforeValue, long? afterValue, double seconds) =>
        beforeValue is { } start && afterValue is { } end && seconds > 0 ? Math.Max(0, (end - start) / seconds) : null;
}
