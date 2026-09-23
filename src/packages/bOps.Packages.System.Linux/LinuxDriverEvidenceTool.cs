// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Linux;

/// <summary>Reads loaded kernel modules from <c>/proc/modules</c>, with optional known sysfs version metadata.</summary>
public sealed class LinuxDriverEvidenceTool : SystemDriversToolBase
{
    private const string SourceName = "linux-proc-modules";
    private const int MaximumBytes = 1_048_576;
    private const int MaximumLines = SystemMaintenanceLimits.MaximumDrivers + 1;
    private readonly Func<string?> readModules;
    private readonly Func<string, string?> readVersion;

    public LinuxDriverEvidenceTool() : this(ReadModules, ReadVersion) { }
    internal LinuxDriverEvidenceTool(Func<string?> readModules, Func<string, string?> readVersion) : base("linux") { this.readModules = readModules; this.readVersion = readVersion; }

    protected override Task<MaintenanceSnapshot<DriverRecord>> CollectAsync(MaintenanceArguments arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string? text;
        try { text = readModules(); }
        catch (IOException) { return Task.FromResult(Unavailable("/proc/modules is not readable.")); }
        catch (UnauthorizedAccessException) { return Task.FromResult(Unavailable("/proc/modules is not readable.")); }
        if (text is null || text.Length > MaximumBytes) return Task.FromResult(Unavailable(text is null ? "/proc/modules is not readable." : "/proc/modules exceeded the bounded input size."));
        var rows = new List<DriverRecord>();
        var warnings = new List<string>();
        var malformed = false;
        var truncated = false;
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var examined = 0;
        foreach (var line in lines)
        {
            ct.ThrowIfCancellationRequested();
            if (++examined > MaximumLines || line.Length > 4096) { truncated = true; break; }
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 5 || !long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size < 0) { malformed = true; continue; }
            var module = fields[0];
            if (module.Length == 0 || module.Length > SystemMaintenanceLimits.NameCharacters) { malformed = true; continue; }
            string? version = null;
            try { version = Bound(readVersion(module)); }
            catch (IOException) { warnings.Add("Module version metadata was unavailable for one row."); }
            catch (UnauthorizedAccessException) { warnings.Add("Module version metadata was unavailable for one row."); }
            rows.Add(new DriverRecord(module, module, version, null, fields[4], true, size.ToString(CultureInfo.InvariantCulture), SourceName));
        }
        if (malformed) warnings.Add("One or more /proc/modules rows were malformed.");
        if (truncated) warnings.Add("Module inventory was truncated by the bounded input limit.");
        var partial = malformed || truncated || warnings.Count > 0;
        return Task.FromResult(new MaintenanceSnapshot<DriverRecord>(rows, [new(SourceName, partial ? InventorySourceStatus.Partial : InventorySourceStatus.Available)], warnings, truncated));
    }

    private static MaintenanceSnapshot<DriverRecord> Unavailable(string warning) => new([], [new(SourceName, InventorySourceStatus.Unavailable, "unavailable")], [warning]);
    private static string? ReadModules()
    {
        using var stream = File.OpenRead("/proc/modules");
        using var reader = new StreamReader(stream);
        var text = new StringBuilder();
        var buffer = new char[4096];
        while (reader.Read(buffer, 0, buffer.Length) is var read && read > 0)
        {
            if (text.Length + read > MaximumBytes) return new string('x', MaximumBytes + 1);
            text.Append(buffer, 0, read);
        }
        return text.ToString();
    }
    private static string? ReadVersion(string module)
    {
        var path = "/sys/module/" + module + "/version";
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }
    private static string? Bound(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Length <= 256 ? value : value[..256];
}
