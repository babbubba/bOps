using bOps.Abstractions;

namespace bOps.Packages.Scheduler.Core;

public static class SchedulerArguments
{
    public static bool TryList(ToolArguments a, out string source, out int limit, out string? error)
    { source = "all"; limit = 200; error = null; if (a.TryGet<string>("source", out var s) && !SchedulerSources.TryNormalize(s, out source)) { error="source must be all, task-scheduler, systemd-timer, or cron."; return false; } if (a.TryGet<int>("limit", out var l)) limit=l; if (limit is < 1 or > 2000) { error="limit must be between 1 and 2000."; return false; } return true; }
    public static bool TryInspect(ToolArguments a, out string id, out string? error) { id=""; error=null; if (!a.TryGet<string>("id", out var value) || string.IsNullOrWhiteSpace(value)) { error="id is required and must be non-empty."; return false; } id=value.Trim(); return true; }
    public static bool TryHistory(ToolArguments a, out string id, out int sinceMinutes, out int limit, out string? error)
    { id=""; sinceMinutes=1440; limit=100; error=null; if (!TryInspect(a,out id,out error)) return false; if (a.TryGet<int>("sinceMinutes",out var s)) sinceMinutes=s; if (a.TryGet<int>("limit",out var l)) limit=l; if (sinceMinutes is < 1 or > 10080) { error="sinceMinutes must be between 1 and 10080."; return false; } if (limit is < 1 or > 1000) { error="limit must be between 1 and 1000."; return false; } return true; }
}
