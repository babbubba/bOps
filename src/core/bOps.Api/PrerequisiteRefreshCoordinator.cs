// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Runtime;

namespace bOps.Api;

/// <summary>
/// The API host's single long-running prerequisite refresh (ADR-0049): every <see cref="PrerequisiteOptions.RefreshInterval"/> it runs
/// one <see cref="PrerequisiteReadinessService"/> cycle — bounded-concurrency checks, transition messages, then the Tool and Skill
/// availability snapshots — so a dependency that recovers (or vanishes) changes what is offered without a restart. Cycles never
/// overlap: the next tick starts only after the previous cycle ended, and the service serialises any other caller. A failing cycle
/// is logged and the loop continues. The boot refresh in the composition root, not this class, makes the first snapshot.
/// </summary>
internal sealed class PrerequisiteRefreshCoordinator(
    PrerequisiteReadinessService readiness,
    PrerequisiteOptions options,
    TimeProvider timeProvider,
    ILogger<PrerequisiteRefreshCoordinator> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.RefreshInterval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunCycleAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }

    /// <summary>Runs one cycle; any failure other than shutdown is logged, never propagated.</summary>
    internal async Task RunCycleAsync(CancellationToken ct)
    {
        try
        {
            await readiness.RefreshAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "A prerequisite refresh cycle failed; the next one will retry.");
        }
    }
}
