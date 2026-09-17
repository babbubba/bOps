// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using System.Security;
using bOps.Packages.Sys.Core;
using Microsoft.Win32;

namespace bOps.Packages.Sys.Windows;

/// <summary>Collects <c>system.info</c> data on Windows.</summary>
public sealed class WindowsSystemInfoTool() : SystemInfoToolBase("windows")
{
    protected override Task<SystemInfoResult> CollectAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var result = new SystemInfoResult(
            RuntimeInformation.OSDescription,
            Environment.MachineName,
            uptime,
            ReadHardwareModel());
        return Task.FromResult(result);
    }

    private static string? ReadHardwareModel()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            return ReadString(key, "SystemProductName") ?? ReadString(key, "BaseBoardProduct");
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException or IOException)
        {
            return null;
        }
    }

    private static string? ReadString(RegistryKey? key, string name) =>
        key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string is { } value
            && !string.IsNullOrWhiteSpace(value)
                ? value.Trim()
                : null;
}
