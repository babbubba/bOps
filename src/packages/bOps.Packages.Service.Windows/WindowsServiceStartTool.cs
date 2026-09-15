// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.ServiceProcess;
using bOps.Abstractions;
using bOps.Packages.Service.Core;

namespace bOps.Packages.Service.Windows;

/// <summary>Collects <c>service.start</c> data on Windows via <see cref="ServiceController"/> (ADR-0021).</summary>
public sealed class WindowsServiceStartTool() : ServiceStartToolBase("windows")
{
    protected override Task<ToolCallResult> StartAsync(string name, CancellationToken ct)
    {
        try
        {
            using var controller = new ServiceController(name);
            controller.Start();
            return Task.FromResult(ToolCallResult.Success($"Requested start of service '{name}'."));
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or ArgumentException)
        {
            return Task.FromResult(ToolCallResult.Failure($"Could not start service '{name}': {ex.Message}"));
        }
    }
}
