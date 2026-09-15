// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Packages.Service.Core;

namespace bOps.Packages.Service.Linux;

/// <summary>Collects <c>service.start</c> data on Linux via a fixed <c>systemctl start</c> invocation (ADR-0021).</summary>
public sealed class LinuxServiceStartTool() : ServiceStartToolBase("linux")
{
    protected override async Task<ToolCallResult> StartAsync(string name, CancellationToken ct)
    {
        if (!ServiceUnitName.IsPlausible(name))
        {
            return ToolCallResult.Failure($"'{name}' is not a plausible systemd unit name.");
        }

        await SystemctlInvoker.RunAsync(["start", name, "--no-pager"], ct);
        return ToolCallResult.Success($"Requested start of service '{name}'.");
    }
}
