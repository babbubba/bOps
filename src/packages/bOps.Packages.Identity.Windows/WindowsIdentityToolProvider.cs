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
        var groups = identity.Groups?.Select(x => { try { return x.Translate(typeof(NTAccount)).Value; } catch (IdentityNotMappedException) { return x.Value; } }).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        bool? serviceAccount = identity.IsSystem || string.Equals(identity.Name, @"NT AUTHORITY\LOCAL SERVICE", StringComparison.OrdinalIgnoreCase) || string.Equals(identity.Name, @"NT AUTHORITY\NETWORK SERVICE", StringComparison.OrdinalIgnoreCase) || identity.Name?.StartsWith(@"NT SERVICE\", StringComparison.OrdinalIgnoreCase) == true;
        return Task.FromResult(new IdentityCurrentResult(identity.Name, identity.User?.Value, principal.IsInRole(WindowsBuiltInRole.Administrator), serviceAccount, groups.Take(500).ToArray(), groups.Length, groups.Length > 500, identity.AuthenticationType));
    }
}

public sealed class WindowsUsersTool() : IdentityUsersToolBase("windows")
{
    protected override Task<IdentityObservation<IdentityUser>> CollectObservationAsync(CancellationToken ct) => Task.FromResult(WindowsNetApi.ReadUsers());
}
public sealed class WindowsGroupsTool() : IdentityGroupsToolBase("windows")
{
    protected override Task<IdentityObservation<IdentityGroup>> CollectObservationAsync(CancellationToken ct) => Task.FromResult(WindowsNetApi.ReadGroups());
}

internal static class WindowsNetApi
{
    private const int NerrSuccess = 0; private const int ErrorMoreData = 234; private const int PageBytes = 16_384; internal const int MembersInfoLevel = 3;
    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int NetUserEnum(string? servername, int level, int filter, out IntPtr bufptr, int prefmaxlen, out int entriesread, out int totalentries, ref int resumeHandle);
    [DllImport("Netapi32.dll")][DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int NetApiBufferFree(IntPtr buffer);
    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int NetLocalGroupEnum(string? servername, int level, out IntPtr bufptr, int prefmaxlen, out int entriesread, out int totalentries, ref int resumeHandle);
    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int NetLocalGroupGetMembers(string? servername, string localgroupname, int level, out IntPtr bufptr, int prefmaxlen, out int entriesread, out int totalentries, ref UIntPtr resumeHandle);
    [StructLayout(LayoutKind.Sequential)] internal struct DomainRoleInfo { public int MachineRole; public int Flags; public IntPtr DomainNameFlat; public IntPtr DomainNameDns; public IntPtr DomainForest; public Guid DomainGuid; public IntPtr DomainSid; }
    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "DsRoleGetPrimaryDomainInformation")][DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int DsRoleGetPrimaryDomainInformation(string? server, int level, out IntPtr buffer);
    [DllImport("Netapi32.dll", EntryPoint = "DsRoleFreeMemory")][DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern void DsRoleFreeMemory(IntPtr buffer);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct UserInfo1 { public IntPtr Name; public IntPtr Password; public int PasswordAge; public int Priv; public IntPtr HomeDir; public IntPtr Comment; public int Flags; public IntPtr ScriptPath; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct GroupInfo0 { public IntPtr Name; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct GroupMemberInfo3 { public IntPtr DomainAndName; }
    internal static bool IsDomainController()
    { if (!OperatingSystem.IsWindows()) return false; var rc = DsRoleGetPrimaryDomainInformation(null, 1, out var buffer); if (rc != 0) throw new InvalidOperationException($"DsRoleGetPrimaryDomainInformation failed: {rc}"); try { var role = Marshal.PtrToStructure<DomainRoleInfo>(buffer).MachineRole; return IsDomainControllerRole(role); } finally { DsRoleFreeMemory(buffer); } }
    internal static bool IsDomainControllerRole(int role) => role is 4 or 5;
    internal static IdentityObservation<IdentityUser> ReadUsers()
    {
        if (!OperatingSystem.IsWindows()) return new([], false, "windows.netapi32.unavailable");
        bool dc; try { dc = IsDomainController(); } catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException) { return new([], false, "windows.domain-controller.detection-failed"); }
        return ReadUsersForRole(dc, ReadUsersMemberServer);
    }
    internal static IdentityObservation<T> ReadUsersForRole<T>(bool isDomainController, Func<IdentityObservation<T>> enumerate) => isDomainController ? new([], false, "windows.domain-controller.unsupported") : enumerate();
    private static IdentityObservation<IdentityUser> ReadUsersMemberServer()
    {
        var rows = new List<IdentityUser>(); var resume = 0; var complete = true;
        try
        {
            do
            {
                var rc = NetUserEnum(null, 1, 0, out var buffer, PageBytes, out var read, out _, ref resume);
                try
                {
                    if (rc is not (NerrSuccess or ErrorMoreData)) return new(rows, false, "windows.netapi32.error");
                    var size = Marshal.SizeOf<UserInfo1>();
                    for (var i = 0; i < read; i++) { var item = Marshal.PtrToStructure<UserInfo1>(buffer + i * size); var name = Marshal.PtrToStringUni(item.Name); if (!string.IsNullOrWhiteSpace(name)) rows.Add(new IdentityUser(name, null, (item.Flags & 2) != 0 ? false : true, true, Marshal.PtrToStringUni(item.HomeDir), Marshal.PtrToStringUni(item.ScriptPath), null, "windows.netapi32")); }
                }
                finally { if (buffer != IntPtr.Zero) _ = NetApiBufferFree(buffer); }
            } while (resume != 0 && rows.Count < 2000);
            if (resume != 0) complete = false;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return new(rows, false, "windows.netapi32.unavailable"); }
        if (rows.Count > 2000) { rows.RemoveRange(2000, rows.Count - 2000); complete = false; }
        return new(rows, complete, "windows.netapi32");
    }
    internal static IdentityObservation<IdentityGroup> ReadGroups()
    {
        if (!OperatingSystem.IsWindows()) return new([], false, "windows.netapi32.unavailable");
        bool dc; try { dc = IsDomainController(); } catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException) { return new([], false, "windows.domain-controller.detection-failed"); }
        return ReadGroupsForRole(dc, ReadGroupsMemberServer);
    }
    internal static IdentityObservation<T> ReadGroupsForRole<T>(bool isDomainController, Func<IdentityObservation<T>> enumerate) => isDomainController ? new([], false, "windows.domain-controller.unsupported") : enumerate();
    private static IdentityObservation<IdentityGroup> ReadGroupsMemberServer()
    {
        var rows = new List<IdentityGroup>(); var resume = 0; var complete = true;
        try
        {
            do
            {
                var rc = NetLocalGroupEnum(null, 0, out var buffer, PageBytes, out var read, out _, ref resume);
                try
                {
                    if (rc is not (NerrSuccess or ErrorMoreData)) return new(rows, false, "windows.netapi32.error");
                    var size = Marshal.SizeOf<GroupInfo0>();
                    for (var i = 0; i < read; i++) { var item = Marshal.PtrToStructure<GroupInfo0>(buffer + i * size); var name = Marshal.PtrToStringUni(item.Name); if (!string.IsNullOrWhiteSpace(name)) { var members = ReadMembers(name); rows.Add(new IdentityGroup(name, null, members.memberCount, members.members, members.truncated)); } }
                }
                finally { if (buffer != IntPtr.Zero) _ = NetApiBufferFree(buffer); }
            } while (resume != 0 && rows.Count < 2000);
            if (resume != 0) complete = false;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return new(rows, false, "windows.netapi32.unavailable"); }
        if (rows.Any(x => x.MemberCount is null)) complete = false;
        if (rows.Count > 2000) { rows.RemoveRange(2000, rows.Count - 2000); complete = false; }
        return new(rows, complete, "windows.netapi32");
    }

    private static (IReadOnlyList<string> members, int? memberCount, bool truncated) ReadMembers(string groupName)
    {
        var members = new List<string>(); UIntPtr resume = UIntPtr.Zero; var total = 0; var more = false;
        try
        {
            do
            {
                var rc = NetLocalGroupGetMembers(null, groupName, MembersInfoLevel, out var buffer, PageBytes, out var read, out var totalEntries, ref resume);
                try
                {
                    if (rc is not (NerrSuccess or ErrorMoreData)) return ([], null, false);
                    total = Math.Max(total, totalEntries);
                    var size = Marshal.SizeOf<GroupMemberInfo3>();
                    for (var i = 0; i < read; i++)
                    {
                        if (members.Count >= 101) { more = true; break; }
                        var item = Marshal.PtrToStructure<GroupMemberInfo3>(buffer + i * size);
                        var value = Marshal.PtrToStringUni(item.DomainAndName);
                        if (!string.IsNullOrWhiteSpace(value)) members.Add(value);
                    }
                }
                finally { if (buffer != IntPtr.Zero) _ = NetApiBufferFree(buffer); }
                if (members.Count >= 101) { more = true; break; }
            } while (resume != UIntPtr.Zero);
        }
        catch (DllNotFoundException) { return ([], null, false); }
        catch (EntryPointNotFoundException) { return ([], null, false); }
        var ordered = members.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var count = total > 0 ? total : ordered.Length;
        return (ordered.Take(100).ToArray(), count, more || count > 100);
    }
}
public sealed class WindowsSessionsTool() : IdentitySessionsToolBase("windows")
{
    protected override Task<IdentityObservation<IdentitySession>> CollectObservationAsync(CancellationToken ct) => Task.FromResult(WindowsSessionsNative.Read());
}

internal static class WindowsSessionsNative
{
    internal enum WtsInfoClass { InitialProgram = 0, ApplicationName = 1, WorkingDirectory = 2, OemId = 3, SessionId = 4, UserName = 5, WinStationName = 6, DomainName = 7, ConnectState = 8, ClientBuildNumber = 9, ClientName = 10, ClientDirectory = 11, ClientProductId = 12, ClientHardwareId = 13, ClientAddress = 14, ClientDisplay = 15, ClientProtocolType = 16, IdleTime = 17, LogonTime = 18, IncomingBytes = 19, OutgoingBytes = 20, IncomingFrames = 21, OutgoingFrames = 22, ClientInfo = 23, SessionInfo = 24, SessionInfoEx = 25, ConfigInfo = 26, ValidationInfo = 27, SessionAddressV4 = 28, IsRemoteSession = 29 }
    private enum WtsState { Active, Connected, ConnectQuery, Shadow, Disconnected, Idle, Listen, Reset, Down, Init }
    [StructLayout(LayoutKind.Sequential)] private struct SessionInfo { public int SessionId; public IntPtr WinStationName; public WtsState State; }
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "WTSEnumerateSessionsW", ExactSpelling = true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "WTSQuerySessionInformationW", ExactSpelling = true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, WtsInfoClass infoClass, out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32.dll")][DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern void WTSFreeMemory(IntPtr memory);
    [StructLayout(LayoutKind.Sequential)] internal struct WtsClientAddress { public uint AddressFamily; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)] public byte[] Address; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct WtsInfoExLevel1 { public uint SessionId; public int SessionState; public int SessionFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)] public string WinStationName; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)] public string UserName; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 18)] public string DomainName; public long LogonTime; public long ConnectTime; public long DisconnectTime; public long LastInputTime; public long CurrentTime; public uint IncomingBytes; public uint OutgoingBytes; public uint IncomingFrames; public uint OutgoingFrames; }
    [StructLayout(LayoutKind.Sequential)] internal struct WtsInfoEx { public uint Level; public WtsInfoExLevel1 Level1; }
    internal static IdentityObservation<IdentitySession> Read()
    {
        if (!OperatingSystem.IsWindows()) return new([], false, "windows.wts.unavailable");
        var rows = new List<IdentitySession>(); var sourceAvailable = true; var truncated = false;
        try { if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var buffer, out var count)) return CreateObservation(rows, false, false); try { truncated = count > 500; var size = Marshal.SizeOf<SessionInfo>(); for (var i = 0; i < count && i < 500; i++) { var item = Marshal.PtrToStructure<SessionInfo>(buffer + i * size); var user = QueryText(item.SessionId, WtsInfoClass.UserName); var station = Marshal.PtrToStringUni(item.WinStationName); var clientAddress = QueryAddress(item.SessionId); var login = QueryLogon(item.SessionId); var remote = QueryBool(item.SessionId, WtsInfoClass.IsRemoteSession); if (login is null || remote is null) sourceAvailable = false; rows.Add(new IdentitySession(item.SessionId.ToString(CultureInfo.InvariantCulture), string.IsNullOrWhiteSpace(user) ? null : user, item.State.ToString(), login, remote, clientAddress, station, "windows.wts")); } } finally { WTSFreeMemory(buffer); } } catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return CreateObservation(rows, false, false); }
        return CreateObservation(rows, sourceAvailable, truncated);
    }
    internal static IdentityObservation<IdentitySession> CreateObservation(IReadOnlyList<IdentitySession> rows, bool sourceAvailable, bool truncated) => new(rows, sourceAvailable && !truncated, "windows.wts");
    private static string? QueryText(int id, WtsInfoClass info)
    { try { if (!WTSQuerySessionInformation(IntPtr.Zero, id, info, out var buffer, out _)) return null; try { return Marshal.PtrToStringUni(buffer); } finally { WTSFreeMemory(buffer); } } catch (DllNotFoundException) { return null; } }
    private static string? QueryAddress(int id)
    {
        try
        {
            if (!WTSQuerySessionInformation(IntPtr.Zero, id, WtsInfoClass.ClientAddress, out var buffer, out var bytes)) return null;
            try { if (bytes < Marshal.SizeOf<WtsClientAddress>()) return null; var parsed = Marshal.PtrToStructure<WtsClientAddress>(buffer); return ParseClientAddress(parsed); }
            finally { WTSFreeMemory(buffer); }
        }
        catch (DllNotFoundException) { return null; }
    }
    private static DateTimeOffset? QueryLogon(int id)
    { try { if (!WTSQuerySessionInformation(IntPtr.Zero, id, WtsInfoClass.SessionInfoEx, out var buffer, out var bytes)) return null; try { if (bytes < Marshal.SizeOf<WtsInfoEx>()) return null; var info = Marshal.PtrToStructure<WtsInfoEx>(buffer); return info.Level == 1 ? ParseLogonTime(info.Level1.LogonTime) : null; } finally { WTSFreeMemory(buffer); } } catch (Exception ex) when (ex is DllNotFoundException or ArgumentException) { return null; } }
    internal static DateTimeOffset? ParseLogonTime(long fileTime) { if (fileTime <= 0) return null; try { return new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime)); } catch (ArgumentOutOfRangeException) { return null; } }
    private static bool? QueryBool(int id, WtsInfoClass info)
    { try { if (!WTSQuerySessionInformation(IntPtr.Zero, id, info, out var buffer, out var bytes)) return null; try { return bytes < 4 ? null : Marshal.ReadInt32(buffer) != 0; } finally { WTSFreeMemory(buffer); } } catch (DllNotFoundException) { return null; } }
    internal static string? ParseClientAddress(WtsClientAddress value) => value.AddressFamily switch { 2 => new System.Net.IPAddress(value.Address.Skip(2).Take(4).ToArray()).ToString(), 23 => new System.Net.IPAddress(value.Address.Skip(2).Take(16).ToArray()).ToString(), _ => null };
}
