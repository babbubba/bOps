// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.ServiceProcess;
using bOps.Packages.Service.Core;

namespace bOps.Packages.Service.Windows;

/// <summary>Collects <c>service.status</c> data on Windows via <see cref="ServiceController"/> (ADR-0021).</summary>
public sealed class WindowsServiceStatusTool() : ServiceStatusToolBase("windows")
{
    protected override Task<ServiceStatusResult> CollectAsync(string name, CancellationToken ct)
    {
        try
        {
            using var controller = new ServiceController(name);
            var status = controller.Status; // Throws InvalidOperationException when the service is not installed.
            var result = new ServiceStatusResult(name, Exists: true, WindowsServiceListTool.NormalizeStatus(status), controller.DisplayName);
            return Task.FromResult(result);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return Task.FromResult(new ServiceStatusResult(name, Exists: false, "unknown", null));
        }
    }
}
