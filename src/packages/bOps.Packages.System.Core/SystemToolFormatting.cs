using System.Globalization;

namespace bOps.Packages.Sys.Core;

/// <summary>
/// Formats <c>system.*</c> results as text for the model to read. This lives in the shared
/// library, not in either OS package, precisely because two OS packages producing
/// <c>system.cpu</c> must produce the same shape — the LLM reads this output, so a difference
/// in shape is a difference in behaviour (agentic/01-architecture-rules.md, rule A8).
/// </summary>
public static class SystemToolFormatting
{
    /// <summary>Formats a <see cref="SystemInfoResult"/>.</summary>
    public static string Format(SystemInfoResult info) =>
        string.Create(CultureInfo.InvariantCulture,
            $"OS: {info.OsDescription}\nHost: {info.HostName}\nUptime: {info.Uptime.Days}d {info.Uptime.Hours}h {info.Uptime.Minutes}m");

    /// <summary>Formats a <see cref="CpuUsageResult"/>.</summary>
    public static string Format(CpuUsageResult cpu) =>
        string.Create(CultureInfo.InvariantCulture, $"CPU usage: {cpu.Percent:F1}%");

    /// <summary>Formats a <see cref="MemoryUsageResult"/>.</summary>
    public static string Format(MemoryUsageResult memory) =>
        string.Create(CultureInfo.InvariantCulture,
            $"Memory: {memory.TotalMb - memory.AvailableMb} MB used of {memory.TotalMb} MB total ({memory.UsedPercent:F1}%), {memory.AvailableMb} MB available.");

    /// <summary>Formats a list of <see cref="DiskUsageResult"/>.</summary>
    public static string Format(IReadOnlyList<DiskUsageResult> disks)
    {
        if (disks.Count == 0)
        {
            return "No ready volumes found.";
        }

        var lines = disks.Select(disk => string.Create(CultureInfo.InvariantCulture,
            $"{disk.Name}: {disk.TotalMb - disk.FreeMb} MB used of {disk.TotalMb} MB total, {disk.FreeMb} MB free."));
        return string.Join('\n', lines);
    }

    /// <summary>Formats a list of <see cref="ProcessSummary"/>.</summary>
    public static string Format(IReadOnlyList<ProcessSummary> processes)
    {
        if (processes.Count == 0)
        {
            return "No processes found.";
        }

        var lines = processes.Select(process => string.Create(CultureInfo.InvariantCulture,
            $"{process.Pid}\t{process.Name}\t{process.WorkingSetMb} MB"));
        return "PID\tName\tWorkingSet\n" + string.Join('\n', lines);
    }
}
