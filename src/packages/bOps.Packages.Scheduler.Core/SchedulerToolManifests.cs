using bOps.Abstractions;

namespace bOps.Packages.Scheduler.Core;

public static class SchedulerToolManifests
{
    public static ToolManifest List() => new() { Name="scheduler.list", Description="Lists scheduled work.", Risk=RiskLevel.Read, Platforms=["windows","linux"], Requires=[], Parameters=[
        new("source", ToolParameterType.Enum, "Source filter.", Required: false, AllowedValues: ["all","task-scheduler","systemd-timer","cron"]),
        new("limit", ToolParameterType.Integer, "Maximum returned items, 1..2000.", false)] };
    public static ToolManifest Inspect() => new() { Name="scheduler.inspect", Description="Inspects one scheduled item.", Risk=RiskLevel.Read, Platforms=["windows","linux"], Requires=[], Parameters=[new("id", ToolParameterType.String, "Stable scheduler id.")] };
    public static ToolManifest History() => new() { Name="scheduler.history", Description="Returns scheduler history.", Risk=RiskLevel.Read, Platforms=["windows","linux"], Requires=[], Parameters=[new("id", ToolParameterType.String, "Stable scheduler id."), new("sinceMinutes", ToolParameterType.Integer, "Lookback in minutes, 1..10080.", false), new("limit", ToolParameterType.Integer, "Maximum returned rows, 1..1000.", false)] };
    public static ToolManifest Enable() => Mutation("scheduler.enable", "Enables scheduled work.", "enable");
    public static ToolManifest Disable() => Mutation("scheduler.disable", "Disables scheduled work.", "disable");
    private static ToolManifest Mutation(string name, string description, string action) => new() { Name=name, Description=description, Risk=RiskLevel.Medium, Platforms=["windows","linux"], Requires=[], Parameters=[new("id", ToolParameterType.String, $"Stable scheduler id to {action}.")], Verification=new VerificationSpec("scheduler.inspect", ["id"], $"Confirms scheduler item is {action}d through scheduler.inspect.") };
}
