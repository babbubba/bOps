// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.ServiceProcess;
using bOps.Abstractions;
using bOps.Packages.Service.Core;

namespace bOps.Packages.Service.Windows;

/// <summary>Collects <c>service.stop</c> data on Windows via <see cref="ServiceController"/> (ADR-0021).</summary>
public sealed class WindowsServiceStopTool() : ServiceStopToolBase("windows")
{
    protected override Task<ToolCallResult> StopAsync(string name, CancellationToken ct)
    {
        try
        {
            using var controller = new ServiceController(name);
            controller.Stop();
            return Task.FromResult(ToolCallResult.Success($"Requested stop of service '{name}'."));
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or ArgumentException)
        {
            return Task.FromResult(ToolCallResult.Failure($"Could not stop service '{name}': {ex.Message}"));
        }
    }
}
