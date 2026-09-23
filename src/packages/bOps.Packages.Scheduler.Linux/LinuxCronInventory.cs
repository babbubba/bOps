using System.Text;
using bOps.Packages.Scheduler.Core;

namespace bOps.Packages.Scheduler.Linux;

internal static class LinuxSchedulerLimits
{
    public const int MaximumCronFilesScanned = 2048;
    public const int MaximumCronFileBytes = 1_048_576;
    public const int MaximumCronLinesPerFile = 10_000;
    public const int MaximumPeriodicEntriesPerDirectory = 2048;
}

internal sealed record LinuxCronPaths(string Crontab, string DropIn, string Hourly, string Daily, string Weekly, string Monthly, string[] Spools)
{
    public static LinuxCronPaths Default { get; } = new("/etc/crontab", "/etc/cron.d", "/etc/cron.hourly", "/etc/cron.daily", "/etc/cron.weekly", "/etc/cron.monthly", ["/var/spool/cron/crontabs", "/var/spool/cron"]);
}

internal sealed record LinuxCronInventory(IReadOnlyList<SchedulerListItem> Items, bool Complete, bool Truncated);

internal sealed class LinuxCronInventoryReader
{
    private readonly LinuxCronPaths paths;
    public LinuxCronInventoryReader(LinuxCronPaths? paths = null) => this.paths = paths ?? LinuxCronPaths.Default;
    public LinuxCronInventory Read()
    {
        var rows = new List<SchedulerListItem>(); var complete = true; var truncated = false; var files = 0;
        AddFile(paths.Crontab, CronSourceForm.SystemCrontab, null);
        AddDirectory(paths.DropIn, CronSourceForm.SystemCrontab, false);
        AddDirectory(paths.Hourly, CronSourceForm.UserCrontab, true, "@hourly"); AddDirectory(paths.Daily, CronSourceForm.UserCrontab, true, "@daily"); AddDirectory(paths.Weekly, CronSourceForm.UserCrontab, true, "@weekly"); AddDirectory(paths.Monthly, CronSourceForm.UserCrontab, true, "@monthly");
        foreach (var spool in paths.Spools) AddDirectory(spool, CronSourceForm.UserCrontab, false, null, true);
        return new(rows.OrderBy(x => x.Source, StringComparer.Ordinal).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray(), complete, truncated);

        void AddFile(string path, CronSourceForm form, string? owner, string? periodic = null)
        {
            if (files >= LinuxSchedulerLimits.MaximumCronFilesScanned) { complete = false; truncated = true; return; }
            if (!TryRegular(path, out var exists, out var safe)) { complete = false; return; } if (!exists) return; if (!safe) { complete = false; return; } files++;
            if (periodic is not null) { AddPeriodic(path, periodic); return; }
            try { var text = ReadBounded(path, out var bounded); if (bounded) { complete = false; truncated = true; } AddParsed(path, text, form, owner, !bounded); } catch { complete = false; }
        }
        void AddDirectory(string path, CronSourceForm form, bool periodic, string? alias = null, bool userSpool = false)
        {
            if (!TryDirectory(path, out var exists, out var safe)) { complete = false; return; } if (!exists) return; if (!safe) { complete = false; return; }
            string[] entries; try { entries = Directory.EnumerateFileSystemEntries(path).OrderBy(x => x, StringComparer.Ordinal).Take(LinuxSchedulerLimits.MaximumPeriodicEntriesPerDirectory + 1).ToArray(); } catch { complete = false; return; }
            if (entries.Length > LinuxSchedulerLimits.MaximumPeriodicEntriesPerDirectory) { complete = false; truncated = true; entries = entries[..LinuxSchedulerLimits.MaximumPeriodicEntriesPerDirectory]; }
            foreach (var entry in entries) { if (periodic) AddFile(entry, form, null, alias); else { var owner = userSpool ? Path.GetFileName(entry) : null; if (userSpool && !ValidUser(owner)) { complete = false; continue; } AddFile(entry, form, owner); } }
        }
        void AddParsed(string path, string text, CronSourceForm form, string? owner, bool sourceComplete)
        {
            CronParseResult parsed; try { parsed = CronParser.Parse(text, form); } catch { complete = false; return; } complete &= parsed.Complete && sourceComplete; truncated |= parsed.Truncated;
            var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var entry in parsed.Entries) { var schedule = entry.Schedule ?? ""; var command = entry.Command; var identity = schedule + "\0" + command + "\0" + (entry.User ?? owner ?? ""); var ordinal = ordinals.GetValueOrDefault(identity) + 1; ordinals[identity] = ordinal; var user = entry.User ?? owner; var id = CronStableId.Create(form == CronSourceForm.SystemCrontab ? "system-crontab" : "user-crontab", Path.GetFullPath(path), user, schedule, command, ordinal); rows.Add(new(id, id, "cron", entry.IsValid ? true : null, entry.Schedule, null, null, null, command, user)); }
        }
        void AddPeriodic(string path, string alias) { var canonical = Path.GetFullPath(path); var id = CronStableId.Create("periodic-directory", canonical, "", alias, canonical, 1); rows.Add(new(id, id, "cron", null, alias, null, null, null, canonical, null)); }
        static bool TryRegular(string path, out bool exists, out bool safe) { exists = File.Exists(path) || Directory.Exists(path); safe = false; if (!exists) return true; try { var a = File.GetAttributes(path); safe = (a & FileAttributes.ReparsePoint) == 0 && (a & FileAttributes.Directory) == 0; } catch { } return true; }
        static bool TryDirectory(string path, out bool exists, out bool safe) { exists = Directory.Exists(path); safe = false; if (!exists) return true; try { safe = (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0; } catch { } return true; }
        static bool ValidUser(string? value) => !string.IsNullOrWhiteSpace(value) && value is not "." and not ".." && value.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-');
        static string ReadBounded(string path, out bool truncatedFile) { var length = new FileInfo(path).Length; truncatedFile = length > LinuxSchedulerLimits.MaximumCronFileBytes; var count = (int)Math.Min(length, LinuxSchedulerLimits.MaximumCronFileBytes); using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); var bytes = new byte[count]; var read = stream.Read(bytes, 0, count); var text = Encoding.UTF8.GetString(bytes, 0, read); var lines = text.Split('\n'); if (lines.Length > LinuxSchedulerLimits.MaximumCronLinesPerFile) { truncatedFile = true; text = string.Join('\n', lines.Take(LinuxSchedulerLimits.MaximumCronLinesPerFile)); } return text; }
    }
}
