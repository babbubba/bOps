// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Windows;

/// <summary>Reads Windows Update Agent history through the fixed isolated helper.</summary>
public sealed class WindowsUpdateHistoryTool : SystemUpdateHistoryToolBase
{
    private readonly IWindowsUpdateHistoryCollector collector;

    public WindowsUpdateHistoryTool() : this(new WindowsUpdateHelperClient()) { }
    internal WindowsUpdateHistoryTool(IWindowsUpdateHistoryCollector collector) : base("windows") => this.collector = collector;
    protected override Task<MaintenanceSnapshot<UpdateHistoryRecord>> CollectAsync(MaintenanceArguments arguments, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return collector.CollectHistoryAsync(arguments.SinceDays!.Value, arguments.Limit, ct);
    }
}
