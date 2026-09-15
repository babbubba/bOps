// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Packages.Service.Core;

namespace bOps.Packages.Service.Linux;

/// <summary>Collects <c>service.stop</c> data on Linux via a fixed <c>systemctl stop</c> invocation (ADR-0021).</summary>
public sealed class LinuxServiceStopTool() : ServiceStopToolBase("linux")
{
    protected override async Task<ToolCallResult> StopAsync(string name, CancellationToken ct)
    {
        if (!ServiceUnitName.IsPlausible(name))
        {
            return ToolCallResult.Failure($"'{name}' is not a plausible systemd unit name.");
        }

        await SystemctlInvoker.RunAsync(["stop", name, "--no-pager"], ct);
        return ToolCallResult.Success($"Requested stop of service '{name}'.");
    }
}
