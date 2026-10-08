// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Api;

/// <summary>
/// Time-based retention of system messages (ADR-0049 section 8): one bounded purge when the host starts, then one every
/// <see cref="SystemMessageOptions.RetentionInterval"/>. Never on message insertion. The store deletes in small batches.
/// </summary>
internal sealed class SystemMessageRetentionWorker(
    ISystemMessageStore store,
    SystemMessageOptions options,
    TimeProvider timeProvider,
    ILogger<SystemMessageRetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.RetentionInterval, timeProvider);
        try
        {
            do
            {
                await PurgeAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }

    /// <summary>Deletes every message older than the retention period; a store failure is logged and retried at the next interval.</summary>
    internal async Task PurgeAsync(CancellationToken ct)
    {
        try
        {
            var removed = await store.PurgeOlderThanAsync(timeProvider.GetUtcNow() - options.Retention, ct);
            var days = options.RetentionDays;
            if (removed > 0 && logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Removed {Count} system messages older than {Days} days.", removed, days);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "System message retention could not run; it will retry at the next interval.");
        }
    }
}
