// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.ServiceProcess;
using bOps.Packages.Service.Core;

namespace bOps.Packages.Service.Windows;

/// <summary>Collects <c>service.list</c> data on Windows via <see cref="ServiceController"/> (ADR-0021).</summary>
public sealed class WindowsServiceListTool() : ServiceListToolBase("windows")
{
    protected override Task<IReadOnlyList<ServiceSummary>> CollectAsync(CancellationToken ct)
    {
        var controllers = ServiceController.GetServices();
        try
        {
            IReadOnlyList<ServiceSummary> services = controllers
                .Select(controller => new ServiceSummary(controller.ServiceName, NormalizeStatus(controller.Status)))
                .OrderBy(service => service.Name, StringComparer.Ordinal)
                .ToList();
            return Task.FromResult(services);
        }
        finally
        {
            foreach (var controller in controllers)
            {
                controller.Dispose();
            }
        }
    }

    /// <summary>Normalizes a <see cref="ServiceControllerStatus"/> to the shared vocabulary (ADR-0021).</summary>
    internal static string NormalizeStatus(ServiceControllerStatus status) => status switch
    {
        ServiceControllerStatus.Running => "running",
        ServiceControllerStatus.Stopped => "stopped",
        _ => "unknown",
    };
}
