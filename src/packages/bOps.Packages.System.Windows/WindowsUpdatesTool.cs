// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Windows;

/// <summary>Reads pending Windows Update Agent evidence without downloading, installing, or changing update configuration.</summary>
public sealed class WindowsUpdatesTool : SystemUpdatesToolBase
{
    private readonly IWindowsUpdateCollector collector;

    /// <summary>Creates the tool over the fixed, isolated Windows Update Agent helper.</summary>
    public WindowsUpdatesTool()
        : this(new WindowsUpdateHelperClient())
    {
    }

    internal WindowsUpdatesTool(IWindowsUpdateCollector collector)
        : base("windows")
    {
        this.collector = collector;
    }

    /// <inheritdoc />
    protected override Task<MaintenanceSnapshot<UpdateRecord>> CollectAsync(MaintenanceArguments arguments, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return collector.CollectAsync(arguments.Kind!, arguments.Limit, ct);
    }
}
