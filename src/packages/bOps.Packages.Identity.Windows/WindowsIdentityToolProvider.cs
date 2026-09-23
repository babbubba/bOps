using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Globalization;
using bOps.Abstractions;
using bOps.Packages.Identity.Core;

namespace bOps.Packages.Identity.Windows;

public sealed class WindowsIdentityToolProvider : IToolProvider
{
    public IEnumerable<ITool> GetTools() => [new WindowsCurrentIdentityTool(), new WindowsUsersTool(), new WindowsGroupsTool(), new WindowsSessionsTool()];
}

public sealed class WindowsCurrentIdentityTool() : IdentityCurrentToolBase("windows")
{
    protected override Task<IdentityCurrentResult> CollectAsync(CancellationToken ct)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        var groups = identity.Groups?.Select(x => x.Translate(typeof(NTAccount)).Value).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        var serviceAccount = identity.IsSystem || string.Equals(identity.Name, @"NT AUTHORITY\LOCAL SERVICE", StringComparison.OrdinalIgnoreCase) || string.Equals(identity.Name, @"NT AUTHORITY\NETWORK SERVICE", StringComparison.OrdinalIgnoreCase);
        return Task.FromResult(new IdentityCurrentResult(identity.Name, identity.User?.Value, principal.IsInRole(WindowsBuiltInRole.Administrator), serviceAccount, groups.Take(100).ToArray(), groups.Length, groups.Length > 100, identity.AuthenticationType));
    }
}

public sealed class WindowsUsersTool() : IdentityUsersToolBase("windows")
{
    protected override Task<IReadOnlyList<IdentityUser>> CollectAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<IdentityUser>>(WindowsNetApi.ReadUsers());
}
public sealed class WindowsGroupsTool() : IdentityGroupsToolBase("windows")
{
    protected override Task<IReadOnlyList<IdentityGroup>> CollectAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<IdentityGroup>>(WindowsNetApi.ReadGroups());
}

internal static class WindowsNetApi
{
    private const int NerrSuccess = 0; private const int ErrorMoreData = 234; private const int MaxPreferred = -1;
    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int NetUserEnum(string? servername, int level, int filter, out IntPtr bufptr, int prefmaxlen, out int entriesread, out int totalentries, ref int resumeHandle);
    [DllImport("Netapi32.dll")][DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int NetApiBufferFree(IntPtr buffer);
    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int NetLocalGroupEnum(string? servername, int level, out IntPtr bufptr, int prefmaxlen, out int entriesread, out int totalentries, ref int resumeHandle);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct UserInfo1 { public IntPtr Name; public IntPtr Password; public int PasswordAge; public int Priv; public IntPtr HomeDir; public IntPtr Comment; public int Flags; public IntPtr ScriptPath; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct GroupInfo0 { public IntPtr Name; }
    internal static IReadOnlyList<IdentityUser> ReadUsers()
    {
        var rows = new List<IdentityUser>(); var resume = 0;
        try { do { var rc = NetUserEnum(null, 1, 0, out var buffer, MaxPreferred, out var read, out _, ref resume); if (rc != NerrSuccess && rc != ErrorMoreData) return rows; try { var size = Marshal.SizeOf<UserInfo1>(); for (var i = 0; i < read; i++) { var item = Marshal.PtrToStructure<UserInfo1>(buffer + i * size); var name = Marshal.PtrToStringUni(item.Name); if (!string.IsNullOrWhiteSpace(name)) rows.Add(new IdentityUser(name, null, (item.Flags & 2) != 0 ? false : true, true, Marshal.PtrToStringUni(item.HomeDir), Marshal.PtrToStringUni(item.ScriptPath), null, "windows.netapi32")); } } finally { if (buffer != IntPtr.Zero) _ = NetApiBufferFree(buffer); } } while (resume != 0); } catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
        return rows;
    }
    internal static IReadOnlyList<IdentityGroup> ReadGroups()
    {
        var rows = new List<IdentityGroup>(); var resume = 0;
        try { do { var rc = NetLocalGroupEnum(null, 0, out var buffer, MaxPreferred, out var read, out _, ref resume); if (rc != NerrSuccess && rc != ErrorMoreData) return rows; try { var size = Marshal.SizeOf<GroupInfo0>(); for (var i = 0; i < read; i++) { var item = Marshal.PtrToStructure<GroupInfo0>(buffer + i * size); var name = Marshal.PtrToStringUni(item.Name); if (!string.IsNullOrWhiteSpace(name)) rows.Add(new IdentityGroup(name, null, null, [], false)); } } finally { if (buffer != IntPtr.Zero) _ = NetApiBufferFree(buffer); } } while (resume != 0); } catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
        return rows;
    }
}
public sealed class WindowsSessionsTool() : IdentitySessionsToolBase("windows")
{
    protected override Task<IReadOnlyList<IdentitySession>> CollectAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<IdentitySession>>(WindowsSessionsNative.Read());
}

internal static class WindowsSessionsNative
{
    private enum WtsInfoClass { InitialProgram, ApplicationName, WorkingDirectory, OemId, SessionId, UserName, WinStationName, DomainName, ConnectState, ClientName, ClientDirectory, ClientBuildNumber, ClientProductId, ClientHardwareId, ClientAddress, ClientDisplay, ClientProtocolType, IdleTime, LogonTime }
    private enum WtsState { Active, Connected, ConnectQuery, Shadow, Disconnected, Idle, Listen, Reset, Down, Init }
    [StructLayout(LayoutKind.Sequential)] private struct SessionInfo { public int SessionId; public IntPtr WinStationName; public WtsState State; }
    [DllImport("wtsapi32.dll")][DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);
    [DllImport("wtsapi32.dll")][DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, WtsInfoClass infoClass, out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32.dll")][DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern void WTSFreeMemory(IntPtr memory);
    internal static IReadOnlyList<IdentitySession> Read()
    {
        var rows = new List<IdentitySession>();
        try { if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var buffer, out var count)) return rows; try { var size = Marshal.SizeOf<SessionInfo>(); for (var i = 0; i < count && i < 500; i++) { var item = Marshal.PtrToStructure<SessionInfo>(buffer + i * size); var user = Query(item.SessionId, WtsInfoClass.UserName); var station = Query(item.SessionId, WtsInfoClass.WinStationName) ?? Marshal.PtrToStringUni(item.WinStationName); var remote = Query(item.SessionId, WtsInfoClass.ClientName); rows.Add(new IdentitySession(item.SessionId.ToString(CultureInfo.InvariantCulture), string.IsNullOrWhiteSpace(user) ? null : user, item.State.ToString(), null, remote is not null, remote, station, "windows.wts")); } } finally { WTSFreeMemory(buffer); } } catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
        return rows;
    }
    private static string? Query(int id, WtsInfoClass info)
    { try { if (!WTSQuerySessionInformation(IntPtr.Zero, id, info, out var buffer, out _)) return null; try { return Marshal.PtrToStringUni(buffer); } finally { WTSFreeMemory(buffer); } } catch (DllNotFoundException) { return null; } }
}
