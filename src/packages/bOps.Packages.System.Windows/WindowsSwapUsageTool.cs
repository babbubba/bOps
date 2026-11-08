// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Management;
using System.Runtime.InteropServices;
using bOps.Abstractions;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Windows;

/// <summary>Collects current page file occupancy from <c>Win32_PageFileUsage</c>.</summary>
public sealed class WindowsSwapUsageTool() : SwapUsageToolBase("windows")
{
    public override async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        try
        {
            return await base.ExecuteAsync(arguments, ct);
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException
            or InvalidOperationException or InvalidDataException or NotSupportedException)
        {
            ct.ThrowIfCancellationRequested();
            return ToolCallResult.Failure("Page file usage is unavailable from Win32_PageFileUsage.") with
            {
                FailureKind = ToolFailureKind.Environment,
                Completeness = ToolResultCompleteness.Unavailable,
            };
        }
    }

    protected override Task<SwapUsageResult> CollectAsync(CancellationToken ct) => Task.Run(() =>
    {
        using var searcher = new ManagementObjectSearcher("SELECT AllocatedBaseSize, CurrentUsage FROM Win32_PageFileUsage");
        using var pageFiles = searcher.Get();
        long totalMb = 0;
        long usedMb = 0;
        foreach (var pageFile in pageFiles)
        {
            using (pageFile)
            {
                ct.ThrowIfCancellationRequested();
                if (pageFile["AllocatedBaseSize"] is not uint total || pageFile["CurrentUsage"] is not uint used || used > total)
                    throw new InvalidDataException("Win32_PageFileUsage returned invalid or missing size data.");

                totalMb += total;
                usedMb += used;
            }
        }

        ct.ThrowIfCancellationRequested();
        return new SwapUsageResult(totalMb, usedMb);
    }, ct).WaitAsync(ct);
}
