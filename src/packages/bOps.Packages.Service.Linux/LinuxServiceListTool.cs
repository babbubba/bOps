// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Service.Core;

namespace bOps.Packages.Service.Linux;

/// <summary>Collects <c>service.list</c> data on Linux via <c>systemctl list-units --type=service</c> (ADR-0021).</summary>
public sealed class LinuxServiceListTool() : ServiceListToolBase("linux")
{
    protected override async Task<IReadOnlyList<ServiceSummary>> CollectAsync(CancellationToken ct)
    {
        var output = await SystemctlInvoker.RunAsync(
            ["list-units", "--type=service", "--all", "--no-legend", "--no-pager", "--plain"], ct);

        IReadOnlyList<ServiceSummary> services = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseLine)
            .Where(summary => summary is not null)
            .Select(summary => summary!)
            .OrderBy(summary => summary.Name, StringComparer.Ordinal)
            .ToList();

        return services;
    }

    // UNIT LOAD ACTIVE SUB DESCRIPTION, whitespace-separated; DESCRIPTION may itself contain
    // spaces, so the line is never split into more than five fields.
    private static ServiceSummary? ParseLine(string line)
    {
        var fields = line.Split(' ', 5, StringSplitOptions.RemoveEmptyEntries);
        return fields.Length < 3 ? null : new ServiceSummary(fields[0], LinuxServiceStatusTool.NormalizeActiveState(fields[2]));
    }
}
