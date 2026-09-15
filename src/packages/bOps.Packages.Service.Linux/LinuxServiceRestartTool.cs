// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Packages.Service.Core;

namespace bOps.Packages.Service.Linux;

/// <summary>
/// Collects <c>service.restart</c> data on Linux via a fixed <c>systemctl restart</c> invocation
/// (ADR-0021) — systemd sequences the stop-then-start itself, so unlike the Windows package this
/// needs no manual stop/wait/start choreography.
/// </summary>
public sealed class LinuxServiceRestartTool() : ServiceRestartToolBase("linux")
{
    protected override async Task<ToolCallResult> RestartAsync(string name, CancellationToken ct)
    {
        if (!ServiceUnitName.IsPlausible(name))
        {
            return ToolCallResult.Failure($"'{name}' is not a plausible systemd unit name.");
        }

        await SystemctlInvoker.RunAsync(["restart", name, "--no-pager"], ct);
        return ToolCallResult.Success($"Requested restart of service '{name}'.");
    }
}
