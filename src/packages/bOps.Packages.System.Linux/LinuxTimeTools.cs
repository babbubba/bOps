using System.Diagnostics;
using System.Text;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Linux;

public sealed class LinuxSystemTimeTool() : SystemTimeToolBase("linux")
{
    protected override async Task<SystemTimeResult> CollectAsync(CancellationToken ct)
    {
        var utc = DateTimeOffset.UtcNow; var local = utc.ToLocalTime(); var zone = TimeZoneInfo.Local;
        var output = await LinuxTimedateCtl.RunAsync(ct); var values = ParseProperties(output) ?? [];
        var complete = HasRequiredEvidence(output);
        return new SystemTimeResult(utc, local, values.GetValueOrDefault("Timezone", zone.Id), (int)local.Offset.TotalMinutes, zone.IsDaylightSavingTime(local.DateTime), "bcl", ParseBool(values, "NTP"), ParseBool(values, "NTPSynchronized"), values.GetValueOrDefault("NTPService"), output is null ? "linux.bcl" : "linux.timedatectl", complete);
    }
    internal static bool? ParseBool(Dictionary<string, string> values, string key) => TryBool(values, key, out var parsed) ? parsed : null;
    internal static bool HasRequiredEvidence(string? output) { var values = ParseProperties(output); return values is not null && values.TryGetValue("Timezone", out var tz) && !string.IsNullOrWhiteSpace(tz) && TryBool(values, "NTP", out _) && TryBool(values, "NTPSynchronized", out _); }
    internal static Dictionary<string, string>? ParseProperties(string? output) { if (output is null) return null; var values = new Dictionary<string, string>(StringComparer.Ordinal); foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries)) { var fields = line.Split('=', 2); if (fields.Length == 2 && !values.TryAdd(fields[0], fields[1])) return null; } return values; }
    private static bool TryBool(Dictionary<string, string> values, string key, out bool parsed) { parsed = false; if (!values.TryGetValue(key, out var value)) return false; switch (value.Trim().ToLowerInvariant()) { case "yes": case "true": parsed = true; return true; case "no": case "false": return true; default: return false; } }
}

public sealed class LinuxRebootPendingTool() : RebootPendingToolBase("linux")
{
    protected override async Task<RebootPendingResult> CollectAsync(CancellationToken ct)
    {
        var probe = LinuxRebootDetector.Probe("/etc/os-release", ["/var/run/reboot-required", "/run/reboot-required"], ct);
        var result = await probe;
        return new RebootPendingResult(result.Pending, result.Reasons, result.Source, result.Complete);
    }
}

internal static class LinuxRebootDetector
{
    internal sealed record ProbeResult(bool Pending, IReadOnlyList<string> Reasons, string Source, bool Complete);
    internal static async Task<ProbeResult> Probe(string osRelease, string[] markers, CancellationToken ct)
    {
        string text; try { text = await File.ReadAllTextAsync(osRelease, ct); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(false, [], "linux.os-release.unavailable", false); }
        var idLine = text.Split('\n').FirstOrDefault(x => x.StartsWith("ID=", StringComparison.Ordinal));
        var id = idLine is null ? "" : idLine[3..].Trim().Trim('"', '\'');
        var supported = id is "ubuntu";
        if (!supported) return new(false, [], "linux.distro.unsupported", false);
        var markerStates = new List<bool?>();
        foreach (var marker in markers.Distinct(StringComparer.Ordinal))
        { try { _ = File.GetAttributes(marker); markerStates.Add(true); } catch (FileNotFoundException) { markerStates.Add(false); } catch (DirectoryNotFoundException) { markerStates.Add(false); } catch (UnauthorizedAccessException) { markerStates.Add(null); } catch (IOException) { markerStates.Add(null); } }
        return EvaluateMarkers(markerStates);
    }
    internal static ProbeResult EvaluateMarkers(IEnumerable<bool?> markers) { var states = markers.ToArray(); var complete = states.All(x => x.HasValue); var pending = states.Any(x => x == true); return new(pending, pending ? ["linux.reboot-required"] : [], "linux.reboot-required", complete); }
}

internal static class LinuxTimedateCtl
{
    internal static async Task<string?> RunAsync(CancellationToken ct)
    {
        try { using var p = new Process { StartInfo = new ProcessStartInfo("timedatectl") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = false, CreateNoWindow = true } }; p.StartInfo.ArgumentList.Add("show"); p.StartInfo.ArgumentList.Add("--property=Timezone,NTP,NTPSynchronized,NTPService"); if (!p.Start()) return null; using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(3)); var outputTask = ReadBoundedOutputAsync(p.StandardOutput, 4096, timeout.Token); var exitTask = p.WaitForExitAsync(timeout.Token); var first = await Task.WhenAny(outputTask, exitTask); if (first == outputTask && await outputTask is null) { try { p.Kill(true); } catch (InvalidOperationException) { } return null; } await exitTask; var text = await outputTask; return p.ExitCode == 0 ? text : null; }
        catch (Exception ex) when ((ex is IOException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception) && !ct.IsCancellationRequested) { return null; }
    }
    internal static async Task<string?> ReadBoundedOutputAsync(TextReader reader, int maximumCharacters, CancellationToken ct)
    { var builder = new StringBuilder(Math.Min(maximumCharacters, 1024)); var buffer = new char[1024]; while (true) { var read = await reader.ReadAsync(buffer.AsMemory(), ct); if (read == 0) return builder.ToString(); if (builder.Length + read > maximumCharacters) return null; builder.Append(buffer, 0, read); } }
}
