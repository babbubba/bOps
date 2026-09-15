// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.ServiceProcess;
using bOps.Abstractions;
using bOps.Packages.Service.Core;

namespace bOps.Packages.Service.Windows;

/// <summary>
/// Collects <c>service.restart</c> data on Windows. <see cref="ServiceController"/> has no native
/// restart operation — unlike <c>systemctl restart</c> on Linux, which sequences stop-then-start
/// atomically inside systemd itself — so this stops (tolerating "already stopped" or "cannot
/// stop"), waits briefly for the stop to actually land, then starts. If the wait times out this
/// still proceeds to the start attempt; a service genuinely stuck mid-stop is exactly what the
/// separate, deferred <c>service.status</c> verification exists to catch (ADR-0021).
/// </summary>
public sealed class WindowsServiceRestartTool() : ServiceRestartToolBase("windows")
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(15);

    protected override Task<ToolCallResult> RestartAsync(string name, CancellationToken ct)
    {
        try
        {
            using var controller = new ServiceController(name);
            controller.Refresh();

            if (controller.Status != ServiceControllerStatus.Stopped)
            {
                try
                {
                    controller.Stop();
                    controller.WaitForStatus(ServiceControllerStatus.Stopped, StopTimeout);
                }
                catch (System.ServiceProcess.TimeoutException)
                {
                    // Proceed to the start attempt regardless; see the type's own remarks.
                }
                catch (InvalidOperationException)
                {
                    // CanStop is false, or it was already transitioning — proceed regardless.
                }
            }

            controller.Start();
            return Task.FromResult(ToolCallResult.Success($"Requested restart of service '{name}'."));
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or ArgumentException)
        {
            return Task.FromResult(ToolCallResult.Failure($"Could not restart service '{name}': {ex.Message}"));
        }
    }
}
