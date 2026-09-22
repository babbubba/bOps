// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text.Json;
using bOps.Abstractions;
using bOps.Packages.Service.Core;
using Microsoft.Win32.SafeHandles;

namespace bOps.Packages.Service.Windows;

public sealed class WindowsServiceConfigTool() : ServiceConfigToolBase("windows")
{
    protected override Task<ServiceConfigResult> CollectAsync(string name, CancellationToken ct) =>
        Task.FromResult(WindowsServiceNative.ReadConfiguration(name));
}

public sealed class WindowsServiceDependenciesTool() : ServiceDependenciesToolBase("windows")
{
    protected override Task<ServiceDependenciesResult> CollectAsync(string name, string direction, int limit, CancellationToken ct) =>
        Task.FromResult(WindowsServiceNative.ReadDependencies(name, direction));
}

public sealed class WindowsServiceEnableTool() : ServiceEnableToolBase("windows")
{
    protected override Task<ToolCallResult> EnableAsync(string name, CancellationToken ct) =>
        Task.FromResult(WindowsServiceNative.ChangeEnablement(name, enable: true));
}

public sealed class WindowsServiceDisableTool() : ServiceDisableToolBase("windows")
{
    protected override Task<ToolCallResult> DisableAsync(string name, CancellationToken ct) =>
        Task.FromResult(WindowsServiceNative.ChangeEnablement(name, enable: false));
}

internal static class WindowsServiceNative
{
    private const uint ScmConnect = 0x0001;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceNoChange = 0xFFFFFFFF;
    private const uint ServiceAutoStart = 0x00000002;
    private const uint ServiceDemandStart = 0x00000003;
    private const uint ServiceDisabled = 0x00000004;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint ErrorServiceDoesNotExist = 1060;
    private const uint ServiceConfigDescription = 1;
    private const uint ServiceConfigFailureActions = 2;
    private const uint ServiceConfigDelayedAutoStartInfo = 3;
    private const uint ScActionRestart = 1;

    public static ServiceConfigResult ReadConfiguration(string name)
    {
        using var scm = OpenScm();
        using var service = OpenService(scm, name, ServiceQueryConfig);
        if (service.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorServiceDoesNotExist)
            {
                return new ServiceConfigResult(false, name, null, null, null, null, null, null, "unknown", null,
                    "unknown", null, [], [], "windows-scm", true);
            }

            throw new Win32Exception(error, $"Could not open service '{name}'.");
        }

        var complete = true;
        NativeMethods.QueryServiceConfigData config;
        try
        {
            config = QueryConfig(service);
        }
        catch (Win32Exception)
        {
            // The service was opened successfully, but the configuration source was not fully readable.
            return new ServiceConfigResult(true, name, null, null, null, null, null, null, "unknown", null,
                "unknown", null, [], [], "windows-scm", false);
        }

        var binaryPath = PtrToString(config.BinaryPathName);
        var parsed = WindowsCommandLine.Parse(binaryPath);
        complete &= parsed.Complete;

        string? description = null;
        try { description = QueryDescription(service); }
        catch (Win32Exception) { complete = false; }

        var delayed = false;
        if (config.StartType == ServiceAutoStart)
        {
            try { delayed = QueryDelayedAutoStart(service); }
            catch (Win32Exception) { complete = false; }
        }

        string? restartPolicy = null;
        try { restartPolicy = QueryFailureActions(service); }
        catch (Win32Exception) { complete = false; }

        var (startupType, enabled, enablementState) = MapStartType(config.StartType, delayed);
        var dependencies = ReadMultiSz(config.Dependencies).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        string[] dependents;
        try { dependents = ReadDependentNames(name); }
        catch (Win32Exception) { dependents = []; complete = false; }
        catch (InvalidOperationException) { dependents = []; complete = false; }

        return new ServiceConfigResult(true, name, PtrToString(config.DisplayName), description, parsed.Executable,
            parsed.Arguments, null, PtrToString(config.ServiceStartName), startupType, enabled, enablementState,
            restartPolicy, dependencies, dependents, "windows-scm", complete);
    }

    public static ServiceDependenciesResult ReadDependencies(string name, string direction)
    {
        var relations = new List<ServiceDependencyRelation>();
        try
        {
            using var controller = new ServiceController(name);
            if (direction is "both" or "requires")
            {
                foreach (var dependency in controller.ServicesDependedOn)
                {
                    using (dependency) relations.Add(new("requires", dependency.ServiceName, ReadStatus(dependency)));
                }
            }

            if (direction is "both" or "dependents")
            {
                foreach (var dependent in controller.DependentServices)
                {
                    using (dependent) relations.Add(new("dependents", dependent.ServiceName, ReadStatus(dependent)));
                }
            }

            return new ServiceDependenciesResult(relations, relations.Count, false, true, "windows-scm");
        }
        catch (InvalidOperationException ex)
        {
            if (ex.InnerException is Win32Exception win32 && win32.NativeErrorCode == ErrorServiceDoesNotExist)
                return new ServiceDependenciesResult([], 0, false, true, "windows-scm");
            throw;
        }
    }

    public static ToolCallResult ChangeEnablement(string name, bool enable)
    {
        try
        {
            using var scm = OpenScm();
            using var service = OpenService(scm, name, ServiceQueryConfig | ServiceChangeConfig);
            if (service.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not open service '{name}'.");
            var current = QueryConfig(service).StartType;
            var target = enable && current == ServiceDisabled ? ServiceDemandStart : enable ? current : ServiceDisabled;
            if (!NativeMethods.ChangeServiceConfig(service, ServiceNoChange, target, ServiceNoChange, IntPtr.Zero,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not change service '{name}'.");
            return ToolCallResult.Success($"Updated enablement for service '{name}'.");
        }
        catch (Win32Exception ex) { return ToolCallResult.Failure($"Could not change service '{name}': {ex.Message}"); }
        catch (InvalidOperationException ex) { return ToolCallResult.Failure($"Could not change service '{name}': {ex.Message}"); }
    }

    private static SafeServiceHandle OpenScm() =>
        NativeMethods.OpenSCManager(null, null, ScmConnect) is var handle && !handle.IsInvalid
            ? handle
            : throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the Service Control Manager.");

    private static SafeServiceHandle OpenService(SafeServiceHandle scm, string name, uint access) =>
        NativeMethods.OpenService(scm, name, access);

    private static NativeMethods.QueryServiceConfigData QueryConfig(SafeServiceHandle service)
    {
        NativeMethods.QueryServiceConfig(service, IntPtr.Zero, 0, out var required);
        var error = Marshal.GetLastWin32Error();
        if (error != ErrorInsufficientBuffer) throw new Win32Exception(error);
        var buffer = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            if (!NativeMethods.QueryServiceConfig(service, buffer, required, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return Marshal.PtrToStructure<NativeMethods.QueryServiceConfigData>(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string? QueryDescription(SafeServiceHandle service) =>
        QueryConfig2(service, ServiceConfigDescription, static ptr => PtrToString(Marshal.PtrToStructure<NativeMethods.ServiceDescription>(ptr).Description));

    private static bool QueryDelayedAutoStart(SafeServiceHandle service) =>
        QueryConfig2(service, ServiceConfigDelayedAutoStartInfo, static ptr => Marshal.PtrToStructure<NativeMethods.DelayedAutoStartInfo>(ptr).Delayed);

    private static string? QueryFailureActions(SafeServiceHandle service) => QueryConfig2(service, ServiceConfigFailureActions, ptr =>
    {
        var actions = Marshal.PtrToStructure<NativeMethods.FailureActions>(ptr);
        var values = new List<object>();
        if (actions.Actions != IntPtr.Zero)
        {
            for (var i = 0; i < actions.Count; i++)
            {
                var action = Marshal.PtrToStructure<NativeMethods.FailureAction>(IntPtr.Add(actions.Actions, i * Marshal.SizeOf<NativeMethods.FailureAction>()));
                values.Add(new { type = action.Type == ScActionRestart ? "restart" : action.Type switch { 0u => "none", 2u => "reboot", _ => "unknown" }, delayMilliseconds = action.Delay });
            }
        }
        return JsonSerializer.Serialize(new { resetPeriodSeconds = actions.ResetPeriod, actions = values });
    });

    private static T QueryConfig2<T>(SafeServiceHandle service, uint level, Func<IntPtr, T> reader)
    {
        NativeMethods.QueryServiceConfig2(service, level, IntPtr.Zero, 0, out var required);
        var error = Marshal.GetLastWin32Error();
        if (error != ErrorInsufficientBuffer) throw new Win32Exception(error);
        var buffer = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            if (!NativeMethods.QueryServiceConfig2(service, level, buffer, required, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return reader(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string[] ReadDependentNames(string name)
    {
        using var controller = new ServiceController(name);
        return controller.DependentServices.Select(service => service.ServiceName).OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    private static string? ReadStatus(ServiceController service)
    {
        try { return WindowsServiceListTool.NormalizeStatus(service.Status); }
        catch (InvalidOperationException) { return "unknown"; }
    }

    private static string? PtrToString(IntPtr value) => value == IntPtr.Zero ? null : Marshal.PtrToStringUni(value);
    private static string[] ReadMultiSz(IntPtr value) =>
        value == IntPtr.Zero ? [] : (PtrToString(value) ?? string.Empty).Split('\0', StringSplitOptions.RemoveEmptyEntries);

    private static (string StartupType, bool? Enabled, string EnablementState) MapStartType(uint startType, bool delayed) => startType switch
    {
        ServiceAutoStart when delayed => ("automatic-delayed", true, "enabled"),
        ServiceAutoStart => ("automatic", true, "enabled"),
        ServiceDemandStart => ("manual", true, "enabled"),
        ServiceDisabled => ("disabled", false, "disabled"),
        _ => ("unknown", null, "unknown"),
    };

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)] internal struct QueryServiceConfigData { internal uint ServiceType; internal uint StartType; internal uint ErrorControl; internal IntPtr BinaryPathName; internal IntPtr LoadOrderGroup; internal IntPtr TagId; internal IntPtr Dependencies; internal IntPtr ServiceStartName; internal IntPtr DisplayName; }
        [StructLayout(LayoutKind.Sequential)] internal struct ServiceDescription { internal IntPtr Description; }
        [StructLayout(LayoutKind.Sequential)] internal struct DelayedAutoStartInfo { [MarshalAs(UnmanagedType.Bool)] internal bool Delayed; }
        [StructLayout(LayoutKind.Sequential)] internal struct FailureActions { internal uint ResetPeriod; internal IntPtr RebootMessage; internal IntPtr Command; internal uint Count; internal IntPtr Actions; }
        [StructLayout(LayoutKind.Sequential)] internal struct FailureAction { internal uint Type; internal uint Delay; }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern SafeServiceHandle OpenSCManager(string? machine, string? database, uint access);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern SafeServiceHandle OpenService(SafeServiceHandle manager, string name, uint access);
        [DllImport("advapi32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryServiceConfig(SafeServiceHandle service, IntPtr buffer, uint size, out uint bytesNeeded);
        [DllImport("advapi32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryServiceConfig2(SafeServiceHandle service, uint level, IntPtr buffer, uint size, out uint bytesNeeded);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ChangeServiceConfig(SafeServiceHandle service, uint serviceType, uint startType, uint errorControl, IntPtr binaryPath, IntPtr loadOrderGroup, IntPtr tagId, IntPtr dependencies, IntPtr account, IntPtr password, IntPtr displayName);
    }
}

internal sealed class SafeServiceHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
{
    protected override bool ReleaseHandle() => NativeMethods.CloseServiceHandle(handle);

    private static class NativeMethods
    {
        [DllImport("advapi32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseServiceHandle(IntPtr handle);
    }
}

internal static class WindowsCommandLine
{
    internal readonly record struct Result(string? Executable, string? Arguments, bool Complete);

    internal static Result Parse(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return new(null, null, false);
        var text = commandLine.TrimStart();
        var end = text[0] == '"' ? FindQuotedEnd(text) : FindUnquotedEnd(text);
        if (end <= 0) return new(null, null, false);
        var executable = text[0] == '"' ? text[1..end] : text[..end];
        var arguments = (text[0] == '"' ? text[(end + 1)..] : text[end..]).TrimStart();
        return new(executable, string.IsNullOrEmpty(arguments) ? null : arguments, true);
    }

    private static int FindQuotedEnd(string text)
    {
        for (var i = 1; i < text.Length; i++)
        {
            if (text[i] != '"') continue;
            var slashes = 0;
            for (var j = i - 1; j >= 0 && text[j] == '\\'; j--) slashes++;
            if ((slashes & 1) == 0) return i;
        }
        return -1;
    }

    private static int FindUnquotedEnd(string text)
    {
        for (var i = 0; i < text.Length; i++) if (char.IsWhiteSpace(text[i])) return i;
        return text.Length;
    }
}
