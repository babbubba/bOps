using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using bOps.Packages.Identity.Core;

namespace bOps.Packages.Identity.Linux;

internal static class LinuxIdentityFiles
{
    internal static IEnumerable<string> Lines(string path)
    {
        try { return File.ReadLines(path).Take(10_000).ToArray(); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }
    internal static string[] Fields(string line) => line.Split(':');
}

public sealed class LinuxCurrentIdentityTool() : IdentityCurrentToolBase("linux")
{
    [DllImport("libc", EntryPoint = "getuid")][DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint GetUid();
    [DllImport("libc", EntryPoint = "geteuid")][DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint GetEuid();
    protected override Task<IdentityCurrentResult> CollectAsync(CancellationToken ct)
    {
        var uid = GetUid(); var euid = GetEuid();
        var name = LinuxIdentityFiles.Lines("/etc/passwd").Select(LinuxIdentityFiles.Fields).Where(x => x.Length >= 3 && x[2] == uid.ToString(CultureInfo.InvariantCulture)).Select(x => x[0]).FirstOrDefault() ?? Environment.UserName;
        var groups = LinuxIdentityFiles.Lines("/etc/group").Select(LinuxIdentityFiles.Fields).Where(x => x.Length >= 4 && x[3].Split(',', StringSplitOptions.RemoveEmptyEntries).Contains(name, StringComparer.Ordinal)).Select(x => x[0]).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        return Task.FromResult(new IdentityCurrentResult(name, uid.ToString(CultureInfo.InvariantCulture), euid != uid, false, groups.Take(100).ToArray(), groups.Length, groups.Length > 100, "unix"));
    }
}

public sealed class LinuxUsersTool() : IdentityUsersToolBase("linux")
{
    protected override Task<IReadOnlyList<IdentityUser>> CollectAsync(CancellationToken ct)
    {
        var rows = LinuxIdentityFiles.Lines("/etc/passwd").Select(line => LinuxIdentityFiles.Fields(line)).Where(x => x.Length >= 7 && x[0].Length > 0 && !x[0].Contains('\0'))
            .Select(x => new IdentityUser(x[0], x[2], null, true, x[5].Length == 0 ? null : x[5], x[6].Length == 0 ? null : x[6], null, "linux.passwd"))
            .ToArray();
        return Task.FromResult<IReadOnlyList<IdentityUser>>(rows);
    }
}

public sealed class LinuxGroupsTool() : IdentityGroupsToolBase("linux")
{
    protected override Task<IReadOnlyList<IdentityGroup>> CollectAsync(CancellationToken ct)
    {
        var rows = LinuxIdentityFiles.Lines("/etc/group").Select(LinuxIdentityFiles.Fields).Where(x => x.Length >= 4 && x[0].Length > 0)
            .Select(x => { var members = x[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal).ToArray(); return new IdentityGroup(x[0], x[2], members.Length, members.Take(100).ToArray(), members.Length > 100); }).ToArray();
        return Task.FromResult<IReadOnlyList<IdentityGroup>>(rows);
    }
}

public sealed class LinuxSessionsTool() : IdentitySessionsToolBase("linux")
{
    protected override async Task<IReadOnlyList<IdentitySession>> CollectAsync(CancellationToken ct)
    {
        var list = await LinuxLoginCtl.RunAsync(["list-sessions"], ct);
        if (list is null) return [];
        var rows = new List<IdentitySession>();
        foreach (var line in list.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(500))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries); if (fields.Length < 2) continue;
            var detail = await LinuxLoginCtl.RunAsync(["show-session", fields[0], "--property=Name,Type,Remote,RemoteHost,TTY,State,TimestampUSec"], ct) ?? "";
            var map = detail.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=', 2)).Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1], StringComparer.Ordinal);
            DateTimeOffset? login = null; if (map.TryGetValue("TimestampUSec", out var stamp) && long.TryParse(stamp, out var micros)) login = DateTimeOffset.UnixEpoch.AddTicks(micros * 10);
            rows.Add(new IdentitySession(fields[0], map.GetValueOrDefault("Name", fields[1]), map.GetValueOrDefault("State"), login, bool.TryParse(map.GetValueOrDefault("Remote"), out var remote) ? remote : null, map.GetValueOrDefault("RemoteHost"), map.GetValueOrDefault("TTY"), "linux.logind"));
        }
        return rows;
    }
}

internal static class LinuxLoginCtl
{
    internal static async Task<string?> RunAsync(string[] args, CancellationToken ct)
    {
        try
        {
            using var process = new Process { StartInfo = new ProcessStartInfo("loginctl") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
            foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
            if (!process.Start()) return null;
            var output = await process.StandardOutput.ReadToEndAsync(ct); using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(3)); await process.WaitForExitAsync(timeout.Token); return process.ExitCode == 0 ? output[..Math.Min(output.Length, 64_000)] : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException) { return null; }
    }
}
