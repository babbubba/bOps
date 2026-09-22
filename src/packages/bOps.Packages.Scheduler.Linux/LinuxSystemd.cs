using System.Globalization;

namespace bOps.Packages.Scheduler.Linux;

internal static class LinuxSystemd
{
    internal static readonly string[] Properties = ["Id", "Description", "LoadState", "ActiveState", "UnitFileState", "Unit", "Result", "NextElapseUSecRealtime", "NextElapseUSecMonotonic", "LastTriggerUSec", "TimersCalendar", "TimersMonotonic"];
    public static async Task<(IReadOnlyList<string> Ids, bool Complete)> DiscoverAsync(ILinuxProcessRunner runner, CancellationToken ct)
    {
        var result = await runner.RunAsync("systemctl", ["list-timers", "--all", "--no-pager", "--no-legend", "--full"], ct).ConfigureAwait(false); if (result.ExitCode != 0) return ([], false);
        var ids = new List<string>(); var complete = true; foreach (var raw in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)) { var fields = raw.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries); if (fields.Length < 2 || fields[^2] is not { } unit || !unit.EndsWith(".timer", StringComparison.Ordinal) || unit.Any(char.IsWhiteSpace)) { complete = false; continue; } ids.Add(unit); }
        return (ids.Distinct(StringComparer.Ordinal).ToArray(), complete);
    }
    public static async Task<LinuxSystemdTimerSnapshot> ShowAsync(ILinuxProcessRunner runner, string id, CancellationToken ct)
    {
        if (!IsTimerName(id)) throw new ArgumentException("id must be a canonical .timer unit name.", nameof(id)); var result = await runner.RunAsync("systemctl", ["show", "--no-pager", $"--property={string.Join(',', Properties)}", "--", id], ct).ConfigureAwait(false); if (result.ExitCode != 0) throw new InvalidOperationException($"systemctl show failed for '{id}': {Bound(result.StandardError)}");
        var values = new Dictionary<string, string>(StringComparer.Ordinal); var complete = true; foreach (var line in result.StandardOutput.Split('\n')) { var p = line.IndexOf('='); if (p <= 0) { if (!string.IsNullOrWhiteSpace(line)) complete = false; continue; } values[line[..p]] = line[(p + 1)..].TrimEnd('\r'); }
        var foundId = values.GetValueOrDefault("Id"); if (string.IsNullOrEmpty(foundId) || values.GetValueOrDefault("LoadState") == "not-found") throw new KeyNotFoundException(id); foreach (var property in Properties) if (!values.ContainsKey(property)) complete = false;
        return new(foundId, values.GetValueOrDefault("Description") ?? "", values.GetValueOrDefault("UnitFileState"), values.GetValueOrDefault("Unit"), values.GetValueOrDefault("Result"), Micro(values.GetValueOrDefault("NextElapseUSecRealtime")), Micro(values.GetValueOrDefault("NextElapseUSecMonotonic")), Micro(values.GetValueOrDefault("LastTriggerUSec")), values.GetValueOrDefault("TimersCalendar"), values.GetValueOrDefault("TimersMonotonic"), complete);
    }
    public static bool IsTimerName(string id) => !string.IsNullOrWhiteSpace(id) && id.EndsWith(".timer", StringComparison.Ordinal) && !id.Any(char.IsWhiteSpace) && !id.StartsWith('-') && id.IndexOf('/') < 0;
    public static bool? Enablement(string? state) => state switch { "enabled" or "enabled-runtime" => true, "disabled" or "masked" or "masked-runtime" => false, _ => null };
    public static DateTimeOffset? Realtime(long? value) => value is > 0 and <= 253402300799000000L ? DateTimeOffset.UnixEpoch.AddTicks(value.Value * 10) : null;
    private static long? Micro(string? value) => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
    private static string Bound(string value) => value.Length <= 256 ? value : value[..256];
}
