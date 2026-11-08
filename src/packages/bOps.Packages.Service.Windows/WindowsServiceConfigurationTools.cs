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
    private const uint ServiceWin32 = 0x00000030;
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
        (uint ServiceType, uint StartType, string? BinaryPath, string? DisplayName, string? User, string[] Dependencies) config;
        try
        {
            config = QueryConfig(service, static (data, buffer, size) => (
                data.ServiceType,
                data.StartType,
                ReadString(data.BinaryPathName, buffer, size),
                ReadString(data.DisplayName, buffer, size),
                ReadString(data.ServiceStartName, buffer, size),
                ReadString(data.Dependencies, buffer, size, multiString: true)?
                    .Split('\0', StringSplitOptions.RemoveEmptyEntries) ?? []));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidDataException)
        {
            // The service was opened successfully, but the configuration source was not fully readable.
            return new ServiceConfigResult(true, name, null, null, null, null, null, null, "unknown", null,
                "unknown", null, [], [], "windows-scm", false);
        }

        var parsed = WindowsCommandLine.Parse(config.BinaryPath);
        complete &= parsed.Complete && !string.IsNullOrWhiteSpace(config.DisplayName);
        complete &= (config.ServiceType & ServiceWin32) == 0 || !string.IsNullOrWhiteSpace(config.User);

        string? description = null;
        try { description = QueryDescription(service); }
        catch (Exception ex) when (ex is Win32Exception or InvalidDataException) { complete = false; }

        var delayed = false;
        if (config.StartType == ServiceAutoStart)
        {
            try { delayed = QueryDelayedAutoStart(service); }
            catch (Exception ex) when (ex is Win32Exception or InvalidDataException) { complete = false; }
        }

        string? restartPolicy = null;
        try { restartPolicy = QueryFailureActions(service); }
        catch (Exception ex) when (ex is Win32Exception or InvalidDataException) { complete = false; }

        var (startupType, enabled, enablementState) = MapStartType(config.StartType, delayed);
        complete &= enabled.HasValue;
        var dependencies = config.Dependencies.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        string[] dependents;
        try { dependents = ReadDependentNames(name); }
        catch (Win32Exception) { dependents = []; complete = false; }
        catch (InvalidOperationException) { dependents = []; complete = false; }

        return new ServiceConfigResult(true, name, config.DisplayName, description, parsed.Executable,
            parsed.Arguments, null, config.User, startupType, enabled, enablementState,
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
            var current = QueryConfig(service, static (data, _, _) => data.StartType);
            var target = enable && current == ServiceDisabled ? ServiceDemandStart : enable ? current : ServiceDisabled;
            if (!NativeMethods.ChangeServiceConfig(service, ServiceNoChange, target, ServiceNoChange, IntPtr.Zero,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not change service '{name}'.");
            return ToolCallResult.Success($"Updated enablement for service '{name}'.");
        }
        catch (Win32Exception ex) { return ToolCallResult.Failure($"Could not change service '{name}': {ex.Message}"); }
        catch (InvalidDataException ex) { return ToolCallResult.Failure($"Could not read service '{name}': {ex.Message}"); }
        catch (InvalidOperationException ex) { return ToolCallResult.Failure($"Could not change service '{name}': {ex.Message}"); }
    }

    private static SafeServiceHandle OpenScm() =>
        NativeMethods.OpenSCManager(null, null, ScmConnect) is var handle && !handle.IsInvalid
            ? handle
            : throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the Service Control Manager.");

    private static SafeServiceHandle OpenService(SafeServiceHandle scm, string name, uint access) =>
        NativeMethods.OpenService(scm, name, access);

    private static T QueryConfig<T>(SafeServiceHandle service, Func<NativeMethods.QueryServiceConfigData, IntPtr, int, T> reader)
    {
        NativeMethods.QueryServiceConfig(service, IntPtr.Zero, 0, out var required);
        var error = Marshal.GetLastWin32Error();
        if (error != ErrorInsufficientBuffer) throw new Win32Exception(error);
        var buffer = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            if (!NativeMethods.QueryServiceConfig(service, buffer, required, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var size = checked((int)required);
            return reader(ReadStructure<NativeMethods.QueryServiceConfigData>(buffer, size), buffer, size);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string? QueryDescription(SafeServiceHandle service) =>
        QueryConfig2(service, ServiceConfigDescription, static (ptr, size) =>
            ReadString(ReadStructure<NativeMethods.ServiceDescription>(ptr, size).Description, ptr, size));

    private static bool QueryDelayedAutoStart(SafeServiceHandle service) =>
        QueryConfig2(service, ServiceConfigDelayedAutoStartInfo, static (ptr, size) =>
            ReadStructure<NativeMethods.DelayedAutoStartInfo>(ptr, size).Delayed);

    private static string? QueryFailureActions(SafeServiceHandle service) => QueryConfig2(service, ServiceConfigFailureActions, (ptr, size) =>
    {
        var actions = ReadStructure<NativeMethods.FailureActions>(ptr, size);
        var actionSize = Marshal.SizeOf<NativeMethods.FailureAction>();
        var values = new List<object>();
        if (actions.Count > 0)
        {
            if (actions.Count > BytesRemaining(actions.Actions, ptr, size) / actionSize)
                throw new InvalidDataException("SCM returned an incomplete failure action array.");

            for (var i = 0; i < actions.Count; i++)
            {
                var action = Marshal.PtrToStructure<NativeMethods.FailureAction>(IntPtr.Add(actions.Actions, i * actionSize));
                values.Add(new { type = action.Type == ScActionRestart ? "restart" : action.Type switch { 0u => "none", 2u => "reboot", _ => "unknown" }, delayMilliseconds = action.Delay });
            }
        }
        return JsonSerializer.Serialize(new { resetPeriodSeconds = actions.ResetPeriod, actions = values });
    });

    private static T QueryConfig2<T>(SafeServiceHandle service, uint level, Func<IntPtr, int, T> reader)
    {
        NativeMethods.QueryServiceConfig2(service, level, IntPtr.Zero, 0, out var required);
        var error = Marshal.GetLastWin32Error();
        if (error != ErrorInsufficientBuffer) throw new Win32Exception(error);
        var buffer = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            if (!NativeMethods.QueryServiceConfig2(service, level, buffer, required, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return reader(buffer, checked((int)required));
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

    private static T ReadStructure<T>(IntPtr buffer, int size) where T : struct =>
        size >= Marshal.SizeOf<T>() ? Marshal.PtrToStructure<T>(buffer)
            : throw new InvalidDataException("SCM returned an incomplete configuration structure.");

    private static int BytesRemaining(IntPtr value, IntPtr buffer, int size)
    {
        var offset = value.ToInt64() - buffer.ToInt64();
        if (offset < 0 || offset >= size)
            throw new InvalidDataException("SCM returned a pointer outside the configuration buffer.");
        return size - (int)offset;
    }

    internal static string? ReadString(IntPtr value, IntPtr buffer, int size, bool multiString = false)
    {
        if (value == IntPtr.Zero) return null;

        var length = BytesRemaining(value, buffer, size) / sizeof(char);
        for (var i = 0; i < length; i++)
        {
            if (Marshal.ReadInt16(value, i * sizeof(char)) != 0) continue;
            if (!multiString || i == 0 ||
                (i + 1 < length && Marshal.ReadInt16(value, (i + 1) * sizeof(char)) == 0))
                return Marshal.PtrToStringUni(value, i);
        }

        throw new InvalidDataException("SCM returned an unterminated configuration string.");
    }

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
        [StructLayout(LayoutKind.Sequential)] internal struct QueryServiceConfigData { internal uint ServiceType; internal uint StartType; internal uint ErrorControl; internal IntPtr BinaryPathName; internal IntPtr LoadOrderGroup; internal uint TagId; internal IntPtr Dependencies; internal IntPtr ServiceStartName; internal IntPtr DisplayName; }
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
        [DllImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryServiceConfig(SafeServiceHandle service, IntPtr buffer, uint size, out uint bytesNeeded);
        [DllImport("advapi32.dll", EntryPoint = "QueryServiceConfig2W", ExactSpelling = true, SetLastError = true)]
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
        if (string.IsNullOrWhiteSpace(executable)) return new(null, null, false);
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
