// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Windows;

/// <summary>Internal boundary between the tool and its isolated WUA collector.</summary>
internal interface IWindowsUpdateCollector
{
    Task<MaintenanceSnapshot<UpdateRecord>> CollectAsync(string kind, int limit, CancellationToken ct);
}

/// <summary>Internal boundary between the history tool and its isolated WUA collector.</summary>
internal interface IWindowsUpdateHistoryCollector
{
    Task<MaintenanceSnapshot<UpdateHistoryRecord>> CollectHistoryAsync(int sinceDays, int limit, CancellationToken ct);
}
