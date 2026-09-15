// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Hosting;

namespace bOps.TestFixtures.WindowsService;

/// <summary>Does nothing; exists only to make this a valid, well-behaved Windows Service the SCM can start and stop.</summary>
#pragma warning disable CA1812 // Instantiated by DI via AddHostedService<NoOpWorker>(), not by direct construction; see docs/architecture/suppressions.md.
internal sealed class NoOpWorker : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Expected when the SCM stops the service.
        }
    }
}
#pragma warning restore CA1812
