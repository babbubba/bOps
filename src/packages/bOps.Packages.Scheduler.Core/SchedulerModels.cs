using System.Text.Json.Serialization;

namespace bOps.Packages.Scheduler.Core;

public enum SchedulerSource { TaskScheduler, SystemdTimer, Cron }

public static class SchedulerSources
{
    public static bool TryNormalize(string? value, out string normalized) => (value ?? "all").Trim().ToLowerInvariant() switch
    {
        "all" => (normalized = "all") == "all",
        "task-scheduler" => (normalized = "task-scheduler") == "task-scheduler",
        "systemd-timer" => (normalized = "systemd-timer") == "systemd-timer",
        "cron" => (normalized = "cron") == "cron",
        _ => (normalized = "") == "all",
    };
}

public sealed record SchedulerListItem(
    string Id, string Name, string Source, bool? Enabled, string? Schedule,
    DateTimeOffset? NextRunUtc, DateTimeOffset? LastRunUtc, string? LastResult,
    string? Target, string? User);

public sealed record SchedulerInspectResult(
    string Id, string Name, string Source, bool? Enabled, string? Schedule,
    DateTimeOffset? NextRunUtc, DateTimeOffset? LastRunUtc, string? LastResult,
    string? Target, string? User, string? WorkingDirectory, string? Description,
    string? TriggerSummary, bool Complete);

public sealed record SchedulerHistoryRow(DateTimeOffset TimestampUtc, string Event, string? Result, string? Message);

public sealed record SchedulerCollection<T>(IReadOnlyList<T> Items, bool Complete = true, bool CollectionTruncated = false);
public sealed record SchedulerHistoryCollection(IReadOnlyList<SchedulerHistoryRow> Rows, bool Complete = true, bool CollectionTruncated = false);
