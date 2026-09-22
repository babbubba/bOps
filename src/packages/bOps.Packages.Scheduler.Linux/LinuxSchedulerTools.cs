using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Scheduler.Core;

namespace bOps.Packages.Scheduler.Linux;

public sealed class LinuxSchedulerListTool : SchedulerListToolBase
{
    private readonly ILinuxProcessRunner systemctl; private readonly LinuxCronInventoryReader cron;
    public LinuxSchedulerListTool() : this(new LinuxProcessRunner(), new LinuxCronInventoryReader()) { }
    internal LinuxSchedulerListTool(ILinuxProcessRunner runner, LinuxCronInventoryReader inventory) { systemctl = runner; cron = inventory; }
    protected override async Task<SchedulerCollection<SchedulerListItem>> CollectAsync(string source, int limit, CancellationToken ct)
    {
        var items = new List<SchedulerListItem>(); var complete = true; var truncated = false;
        if (source is "all" or "systemd-timer") { var discovered = await LinuxSystemd.DiscoverAsync(systemctl, ct); complete &= discovered.Complete; foreach (var id in discovered.Ids) { try { items.Add(LinuxProjection.List(await LinuxSystemd.ShowAsync(systemctl, id, ct))); } catch { complete = false; } } }
        if (source is "all" or "cron") { var inventory = cron.Read(); items.AddRange(inventory.Items); complete &= inventory.Complete; truncated |= inventory.Truncated; }
        var sorted = items.OrderBy(x => x.Source, StringComparer.Ordinal).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray(); return new(sorted, complete, truncated || sorted.Length > limit);
    }
}

public sealed class LinuxSchedulerInspectTool : SchedulerInspectToolBase
{
    private readonly ILinuxProcessRunner systemctl; private readonly LinuxCronInventoryReader cron;
    public LinuxSchedulerInspectTool() : this(new LinuxProcessRunner(), new LinuxCronInventoryReader()) { }
    internal LinuxSchedulerInspectTool(ILinuxProcessRunner runner, LinuxCronInventoryReader inventory) { systemctl = runner; cron = inventory; }
    protected override async Task<SchedulerInspectResult> CollectAsync(string id, CancellationToken ct)
    {
        if (id.StartsWith("cron:", StringComparison.Ordinal)) { var item = cron.Read().Items.SingleOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException(id); return LinuxProjection.Inspect(item, cron.Read().Complete); }
        if (!LinuxSystemd.IsTimerName(id)) throw new KeyNotFoundException(id);
        return LinuxProjection.Inspect(await LinuxSystemd.ShowAsync(systemctl, id, ct));
    }
}

public sealed class LinuxSchedulerHistoryTool : SchedulerHistoryToolBase
{
    private readonly ILinuxProcessRunner runner; private readonly LinuxCronInventoryReader cron;
    public LinuxSchedulerHistoryTool() : this(new LinuxProcessRunner(), new LinuxCronInventoryReader()) { }
    internal LinuxSchedulerHistoryTool(ILinuxProcessRunner runner, LinuxCronInventoryReader inventory) { this.runner = runner; cron = inventory; }
    protected override async Task<SchedulerHistoryCollection> CollectAsync(string id, int sinceMinutes, int limit, CancellationToken ct)
    {
        if (id.StartsWith("cron:", StringComparison.Ordinal)) return cron.Read().Items.Any(x => x.Id == id) ? new([], false, false) : new([], false, false);
        if (!LinuxSystemd.IsTimerName(id)) return new([], false, false);
        var since = DateTimeOffset.UtcNow.AddMinutes(-sinceMinutes); var args = new[] { "--no-pager", "--output=json", "--reverse", $"--unit={id}", $"--since=@{since.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}", $"--lines={limit + 1}" };
        LinuxProcessResult result; try { result = await runner.RunAsync("journalctl", args, ct); } catch { return new([], false, false); }
        if (result.ExitCode != 0) return new([], false, false);
        var rows = new List<SchedulerHistoryRow>(); var malformed = false;
        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)) { if (rows.Count > limit) break; if (!LinuxJournal.TryParse(line, out var row)) { malformed = true; continue; } rows.Add(row); }
        var hasMore = rows.Count > limit; if (hasMore) rows = rows.Take(limit).ToList(); return new(rows, !malformed && !hasMore, hasMore);
    }
}

public sealed class LinuxSchedulerEnableTool : SchedulerEnableToolBase
{
    private readonly ILinuxProcessRunner runner; public LinuxSchedulerEnableTool() : this(new LinuxProcessRunner()) { } internal LinuxSchedulerEnableTool(ILinuxProcessRunner runner) => this.runner = runner;
    protected override async Task<ToolCallResult> EnableAsync(string id, CancellationToken ct) => await LinuxMutation.ChangeAsync(runner, id, true, ct);
}
public sealed class LinuxSchedulerDisableTool : SchedulerDisableToolBase
{
    private readonly ILinuxProcessRunner runner; public LinuxSchedulerDisableTool() : this(new LinuxProcessRunner()) { } internal LinuxSchedulerDisableTool(ILinuxProcessRunner runner) => this.runner = runner;
    protected override async Task<ToolCallResult> DisableAsync(string id, CancellationToken ct) => await LinuxMutation.ChangeAsync(runner, id, false, ct);
}

internal static class LinuxMutation
{
    public static async Task<ToolCallResult> ChangeAsync(ILinuxProcessRunner runner, string id, bool enable, CancellationToken ct)
    {
        if (id.StartsWith("cron:", StringComparison.Ordinal)) return ToolCallResult.Failure("Cron observations are read-only; scheduler enable/disable is unsupported.");
        if (!LinuxSystemd.IsTimerName(id)) return ToolCallResult.Failure("Only canonical systemd timer ids can be enabled or disabled.");
        LinuxSystemdTimerSnapshot state; try { state = await LinuxSystemd.ShowAsync(runner, id, ct); } catch (KeyNotFoundException) { return ToolCallResult.Failure("The systemd timer was not found."); } catch (Exception e) { return ToolCallResult.Failure($"Could not inspect the systemd timer: {e.Message}"); }
        if (state.UnitFileState is "masked" or "masked-runtime") return ToolCallResult.Failure("Masked systemd timers are not unmasked by scheduler mutations.");
        var current = LinuxSystemd.Enablement(state.UnitFileState); if (current == enable) return ToolCallResult.Success("No change required.");
        if (current is null) return ToolCallResult.Failure("The native systemd enablement state is not safely mutable by this operation.");
        var verb = enable ? "enable" : "disable"; var result = await runner.RunAsync("systemctl", [verb, "--", id], ct); return result.ExitCode == 0 ? ToolCallResult.Success("systemd timer enablement changed.") : ToolCallResult.Failure($"systemctl {verb} failed: {result.StandardError}");
    }
}

internal static class LinuxProjection
{
    public static SchedulerListItem List(LinuxSystemdTimerSnapshot s) => new(s.Id, s.Id, "systemd-timer", LinuxSystemd.Enablement(s.UnitFileState), Schedule(s.Calendar, s.Monotonic), LinuxSystemd.Realtime(s.NextRealtime), LinuxSystemd.Realtime(s.LastTrigger), Bound(s.Result, 256), s.Unit, null);
    public static SchedulerInspectResult Inspect(LinuxSystemdTimerSnapshot s) => new(s.Id, s.Id, "systemd-timer", LinuxSystemd.Enablement(s.UnitFileState), Schedule(s.Calendar, s.Monotonic), LinuxSystemd.Realtime(s.NextRealtime), LinuxSystemd.Realtime(s.LastTrigger), Bound(s.Result, 256), s.Unit, null, null, Bound(s.Description, 2048), Trigger(s.Calendar, s.Monotonic), s.Complete);
    public static SchedulerInspectResult Inspect(SchedulerListItem x, bool complete) => new(x.Id, x.Name, x.Source, x.Enabled, x.Schedule, null, null, null, x.Target, x.User, null, null, x.Schedule, complete);
    private static string? Schedule(string? calendar, string? monotonic) { var values = new[] { calendar, monotonic }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()).ToArray(); return values.Length == 0 ? null : string.Join(" | ", values); }
    private static string? Trigger(string? calendar, string? monotonic) => Schedule(calendar, monotonic);
    private static string? Bound(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}

internal static class LinuxJournal
{
    public static bool TryParse(string line, out SchedulerHistoryRow row)
    {
        row = default!; try { if (JsonNode.Parse(line) is not JsonObject json || !long.TryParse(Text(json, "__REALTIME_TIMESTAMP"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var micro) || micro <= 0 || micro > 253402300799000000L) return false; var time = DateTimeOffset.UnixEpoch.AddTicks(micro * 10); var message = Text(json, "MESSAGE"); if (message is null) return false; row = new(time, Text(json, "MESSAGE_ID") ?? "journal", Text(json, "JOB_RESULT") ?? Text(json, "RESULT"), Bound(message, 2048)); return true; } catch (JsonException) { return false; }
    }
    private static string? Text(JsonObject json, string key) => json[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static string Bound(string value, int max) => value.Length <= max ? value : value[..max];
}
