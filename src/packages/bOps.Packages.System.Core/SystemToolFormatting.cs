// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json.Nodes;

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
    public static string Format(SystemInfoResult info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return string.Create(CultureInfo.InvariantCulture,
            $"OS: {info.OsDescription}\nHost: {info.HostName}\nUptime: {info.Uptime.Days}d {info.Uptime.Hours}h {info.Uptime.Minutes}m\nHardware model: {info.HardwareModel ?? "unknown"}");
    }

    /// <summary>Formats a <see cref="CpuUsageResult"/>.</summary>
    public static string Format(CpuUsageResult cpu)
    {
        ArgumentNullException.ThrowIfNull(cpu);
        return string.Create(CultureInfo.InvariantCulture, $"CPU usage: {cpu.Percent:F1}%");
    }

    /// <summary>Formats a <see cref="MemoryUsageResult"/>.</summary>
    public static string Format(MemoryUsageResult memory)
    {
        ArgumentNullException.ThrowIfNull(memory);
        return string.Create(CultureInfo.InvariantCulture,
            $"Memory: {memory.TotalMb - memory.AvailableMb} MB used of {memory.TotalMb} MB total ({memory.UsedPercent:F1}%), {memory.AvailableMb} MB available.");
    }

    /// <summary>Formats a list of <see cref="DiskUsageResult"/>.</summary>
    public static string Format(IReadOnlyList<DiskUsageResult> disks)
    {
        ArgumentNullException.ThrowIfNull(disks);

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
        ArgumentNullException.ThrowIfNull(processes);

        if (processes.Count == 0)
        {
            return "No processes found.";
        }

        var lines = processes.Select(process => string.Create(CultureInfo.InvariantCulture,
            $"{process.Pid}\t{process.Name}\t{process.WorkingSetMb} MB"));
        return "PID\tName\tWorkingSet\n" + string.Join('\n', lines);
    }

    /// <summary>Formats a <see cref="SwapUsageResult"/>.</summary>
    public static string Format(SwapUsageResult swap)
    {
        ArgumentNullException.ThrowIfNull(swap);
        return string.Create(CultureInfo.InvariantCulture,
            $"Swap: {swap.UsedMb} MB used of {swap.TotalMb} MB total ({swap.UsedPercent:F1}%).");
    }

    /// <summary>Formats a list of <see cref="IoUsageResult"/>.</summary>
    public static string Format(IReadOnlyList<IoUsageResult> io)
    {
        ArgumentNullException.ThrowIfNull(io);

        if (io.Count == 0)
        {
            return "No disk devices found.";
        }

        var lines = io.Select(device => string.Create(CultureInfo.InvariantCulture,
            $"{device.DeviceName}: read {device.ReadKbPerSec:F1} KB/s, write {device.WriteKbPerSec:F1} KB/s."));
        return string.Join('\n', lines);
    }

    /// <summary>Formats a <see cref="ProcessInspectResult"/> as single-line JSON (see the type's own remarks).</summary>
    public static string Format(ProcessInspectResult process)
    {
        ArgumentNullException.ThrowIfNull(process);
        var json = new JsonObject
        {
            ["pid"] = process.Pid,
            ["exists"] = process.Exists,
            ["name"] = process.Name,
            ["workingSetMb"] = process.WorkingSetMb,
            ["threadCount"] = process.ThreadCount,
            ["startTimeUtc"] = process.StartTimeUtc,
            ["parentPid"] = process.ParentPid,
            ["executablePath"] = SystemInventoryFormatting.Bounded(process.ExecutablePath, ProcessDiagnosticsLimits.PathCharacters),
            ["commandLine"] = SystemInventoryFormatting.Bounded(process.CommandLine, ProcessDiagnosticsLimits.CommandLineCharacters),
            ["user"] = SystemInventoryFormatting.Bounded(process.User, ProcessDiagnosticsLimits.UserCharacters),
            ["privateMemoryMb"] = process.PrivateMemoryMb,
            ["virtualMemoryMb"] = process.VirtualMemoryMb,
            ["handleOrFdCount"] = process.HandleOrFdCount,
            ["cpuTotalMs"] = process.CpuTotalMs,
            ["ioReadBytes"] = process.IoReadBytes,
            ["ioWriteBytes"] = process.IoWriteBytes,
        };
        return json.ToJsonString();
    }
}
