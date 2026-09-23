using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using bOps.Packages.Identity.Core;

namespace bOps.Packages.Identity.Linux;

internal static class LinuxIdentityFiles
{
    internal static LinuxFileReadResult ReadLines(string path, int maxLines = 10_000)
    {
        try { var all = File.ReadLines(path).Take(maxLines + 1).ToArray(); return new(all.Take(maxLines).ToArray(), all.Length <= maxLines ? LinuxFileReadStatus.Readable : LinuxFileReadStatus.Truncated); }
        catch (FileNotFoundException) { return new([], LinuxFileReadStatus.Unavailable); }
        catch (DirectoryNotFoundException) { return new([], LinuxFileReadStatus.Unavailable); }
        catch (UnauthorizedAccessException) { return new([], LinuxFileReadStatus.Denied); }
        catch (IOException) { return new([], LinuxFileReadStatus.Error); }
    }
    internal static string[] Fields(string line) => line.Split(':');
}

internal enum LinuxFileReadStatus { Readable, Truncated, Unavailable, Denied, Error }
internal sealed record LinuxFileReadResult(string[] Lines, LinuxFileReadStatus Status);
internal readonly record struct LinuxEffectiveIdentity(uint RealUid, uint EffectiveUid, uint RealGid, uint EffectiveGid);
internal static class LinuxIdentitySource
{
    internal static string Format(string source, LinuxFileReadStatus status) => status switch { LinuxFileReadStatus.Readable => source + ".local-directory-incomplete", LinuxFileReadStatus.Truncated => source + ".truncated", LinuxFileReadStatus.Unavailable => source + ".unavailable", LinuxFileReadStatus.Denied => source + ".denied", _ => source + ".error" };
}

public sealed class LinuxCurrentIdentityTool() : IdentityCurrentToolBase("linux")
{
    [DllImport("libc", EntryPoint = "geteuid")][DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint GetEuid();
    [DllImport("libc", EntryPoint = "getegid")][DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint GetEgid();
    [DllImport("libc", EntryPoint = "getgroups")][DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int GetGroups(int size, [Out] uint[] groups);
    [StructLayout(LayoutKind.Sequential)] private struct GroupEntry { public IntPtr Name; public uint Gid; public IntPtr Members; }
    [DllImport("libc", EntryPoint = "getgrgid")][DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern IntPtr GetGroupById(uint gid);
    protected override Task<IdentityCurrentResult> CollectAsync(CancellationToken ct)
    {
        var euid = GetEuid(); var effectiveGid = GetEgid();
        var passwd = LinuxIdentityFiles.ReadLines("/etc/passwd");
        var name = passwd.Lines.Select(LinuxIdentityFiles.Fields).Where(x => x.Length >= 3 && x[2] == euid.ToString(CultureInfo.InvariantCulture)).Select(x => x[0]).FirstOrDefault() ?? Environment.UserName;
        var count = GetGroups(0, []); var capacity = count > 0 ? Math.Min(count, 4096) : 0; var gids = capacity == 0 ? [] : new uint[capacity];
        var actual = capacity == 0 ? 0 : GetGroups(gids.Length, gids);
        var groupIds = actual >= 0 ? DistinctGroupIds(gids.Take(Math.Min(actual, gids.Length)), effectiveGid) : [effectiveGid];
        var groups = groupIds.Select(ResolveGroup).Where(x => x is not null).Cast<string>().Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var countKnown = count >= 0 && count <= 4096 && actual == count;
        var total = countKnown ? groupIds.Length : (int?)null;
        return Task.FromResult(new IdentityCurrentResult(name, euid.ToString(CultureInfo.InvariantCulture), IsElevated(euid), null, groups.Take(500).ToArray(), total, groupIds.Length > 500 || count > 4096, "unix"));
    }
    private static string? ResolveGroup(uint gid) { var ptr = GetGroupById(gid); return ptr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(Marshal.PtrToStructure<GroupEntry>(ptr).Name); }
    internal static bool IsElevated(uint effectiveUid) => effectiveUid == 0;
    internal static uint[] DistinctGroupIds(IEnumerable<uint> supplementary, uint primary) => supplementary.Append(primary).Distinct().ToArray();
    internal static string[] BuildGroups(IEnumerable<uint> supplementary, uint primary, Func<uint, string?> resolve) => DistinctGroupIds(supplementary, primary).Select(resolve).Where(x => x is not null).Cast<string>().Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
    internal static IdentityCurrentResult BuildEffectiveResult(LinuxEffectiveIdentity ids, IEnumerable<uint> supplementary, Func<uint, string?> resolveUser, Func<uint, string?> resolveGroup)
    {
        var groups = BuildGroups(supplementary, ids.EffectiveGid, resolveGroup);
        return new(resolveUser(ids.EffectiveUid), ids.EffectiveUid.ToString(CultureInfo.InvariantCulture), IsElevated(ids.EffectiveUid), null, groups, groups.Length, false, "unix");
    }
}

public sealed class LinuxUsersTool() : IdentityUsersToolBase("linux")
{
    protected override Task<IdentityObservation<IdentityUser>> CollectObservationAsync(CancellationToken ct)
    {
        return Task.FromResult(Observe(LinuxIdentityFiles.ReadLines("/etc/passwd")));
    }
    internal static IdentityObservation<IdentityUser> Observe(LinuxFileReadResult read)
    {
        var rows = read.Lines.Select(line => LinuxIdentityFiles.Fields(line)).Where(x => x.Length >= 7 && x[0].Length > 0 && !x[0].Contains('\0'))
            .Select(x => new IdentityUser(x[0], x[2], null, true, x[5].Length == 0 ? null : x[5], x[6].Length == 0 ? null : x[6], null, "linux.passwd"))
            .ToArray();
        return new(rows, false, LinuxIdentitySource.Format("linux.passwd", read.Status));
    }
}

public sealed class LinuxGroupsTool() : IdentityGroupsToolBase("linux")
{
    protected override Task<IdentityObservation<IdentityGroup>> CollectObservationAsync(CancellationToken ct)
    {
        return Task.FromResult(Observe(LinuxIdentityFiles.ReadLines("/etc/group")));
    }
    internal static IdentityObservation<IdentityGroup> Observe(LinuxFileReadResult read)
    {
        var rows = read.Lines.Select(LinuxIdentityFiles.Fields).Where(x => x.Length >= 4 && x[0].Length > 0)
            .Select(x => { var members = x[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal).ToArray(); return new IdentityGroup(x[0], x[2], members.Length, members.Take(100).ToArray(), members.Length > 100); }).ToArray();
        return new(rows, false, LinuxIdentitySource.Format("linux.group", read.Status));
    }
}

public sealed class LinuxSessionsTool() : IdentitySessionsToolBase("linux")
{
    protected override async Task<IdentityObservation<IdentitySession>> CollectObservationAsync(CancellationToken ct)
        => await LinuxSessionCollector.ObserveAsync(
            token => LinuxLoginCtl.RunAsync(["list-sessions", "--no-legend", "--no-pager"], token),
            (id, token) => LinuxLoginCtl.RunAsync(["show-session", id, "--no-pager", "--property=Name,Type,Remote,RemoteHost,TTY,State,Timestamp"], token),
            token => LinuxLoginCtl.RunCommandAsync("who", [], token), ct);
}

internal static class LinuxSessionCollector
{
    internal static bool? ParseBool(string? value) => value?.ToLowerInvariant() switch { "yes" or "true" => true, "no" or "false" => false, _ => null };
    internal static async Task<IdentityObservation<IdentitySession>> ObserveAsync(Func<CancellationToken, Task<string?>> list, Func<string, CancellationToken, Task<string?>> show, Func<CancellationToken, Task<string?>> fallback, CancellationToken ct)
    {
        var listing = await list(ct);
        if (listing is null)
        {
            var fallbackOutput = await fallback(ct);
            if (fallbackOutput is null) return new([], false, "linux.loginctl+who.unavailable");
            var fallbackRows = fallbackOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(500).Select((line, index) => { var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries); return new IdentitySession("who-" + index.ToString(CultureInfo.InvariantCulture), fields.ElementAtOrDefault(0), "active", null, null, null, fields.ElementAtOrDefault(1), "linux.who.fallback"); }).ToArray();
            return new(fallbackRows, false, "linux.who.fallback");
        }
        var rows = new List<IdentitySession>(); var lines = listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); var complete = lines.Length <= 500;
        foreach (var line in lines.Take(500))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries); if (fields.Length < 3 || !LinuxLoginCtl.IsSessionId(fields[0])) { complete = false; continue; }
            var detail = await show(fields[0], ct); if (detail is null) { complete = false; continue; }
            var map = detail.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=', 2)).Where(x => x.Length == 2).GroupBy(x => x[0], StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Last()[1], StringComparer.Ordinal);
            if (!map.ContainsKey("Name") || !map.ContainsKey("State") || ParseBool(map.GetValueOrDefault("Remote")) is null) complete = false;
            DateTimeOffset? login = null; if (map.TryGetValue("Timestamp", out var stamp)) { if (long.TryParse(stamp, NumberStyles.None, CultureInfo.InvariantCulture, out var micros)) { try { login = DateTimeOffset.UnixEpoch.AddTicks(checked(micros * 10)); } catch (ArgumentOutOfRangeException) { complete = false; } catch (OverflowException) { complete = false; } } else { complete = false; } }
            rows.Add(new IdentitySession(fields[0], map.GetValueOrDefault("Name", fields[2]), map.GetValueOrDefault("State"), login, ParseBool(map.GetValueOrDefault("Remote")), map.GetValueOrDefault("RemoteHost"), map.GetValueOrDefault("TTY"), "linux.logind"));
        }
        return new(rows, complete, "linux.logind");
    }
}

internal static class LinuxLoginCtl
{
    internal static bool IsSessionId(string value) => uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _) || value.Length > 1 && value[0] is 'c' or 'C' or 's' or 'S' && value[1..].All(char.IsAsciiDigit);
    internal static Task<string?> RunAsync(string[] args, CancellationToken ct) => RunCommandAsync("loginctl", args, ct);
    internal static async Task<string?> RunCommandAsync(string command, string[] args, CancellationToken ct)
    {
        try
        {
            using var process = new Process { StartInfo = new ProcessStartInfo(command) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = false, CreateNoWindow = true } };
            foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
            if (!process.Start()) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var outputTask = ReadBoundedOutputAsync(process.StandardOutput, 64_000, timeout.Token); var exitTask = process.WaitForExitAsync(timeout.Token);
            var completedFirst = await Task.WhenAny(outputTask, exitTask);
            if (completedFirst == outputTask && await outputTask is null) { try { process.Kill(true); } catch (InvalidOperationException) { } return null; }
            await exitTask; var output = await outputTask; return process.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when ((ex is IOException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception) && !ct.IsCancellationRequested) { return null; }
    }
    internal static async Task<string?> ReadBoundedOutputAsync(TextReader reader, int maximumCharacters, CancellationToken ct)
    { var builder = new StringBuilder(Math.Min(maximumCharacters, 4096)); var buffer = new char[4096]; while (true) { var read = await reader.ReadAsync(buffer.AsMemory(), ct); if (read == 0) return builder.ToString(); if (builder.Length + read > maximumCharacters) return null; builder.Append(buffer, 0, read); } }
}
