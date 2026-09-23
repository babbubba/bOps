using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Scheduler.Core;

namespace bOps.Packages.Scheduler.Windows;

public sealed class WindowsSchedulerListTool : SchedulerListToolBase
{
    private readonly IWindowsSchedulerAdapter adapter;
    public WindowsSchedulerListTool() : this(new ComWindowsSchedulerAdapter()) { }
    internal WindowsSchedulerListTool(IWindowsSchedulerAdapter adapter) => this.adapter = adapter;
    protected override Task<SchedulerCollection<SchedulerListItem>> CollectAsync(string source, int limit, CancellationToken ct) =>
        Task.FromResult(source is not ("all" or "task-scheduler") ? new SchedulerCollection<SchedulerListItem>([]) : WindowsSchedulerProjection.List(adapter.Enumerate(), limit));
}

public sealed class WindowsSchedulerInspectTool : SchedulerInspectToolBase
{
    private readonly IWindowsSchedulerAdapter adapter;
    public WindowsSchedulerInspectTool() : this(new ComWindowsSchedulerAdapter()) { }
    internal WindowsSchedulerInspectTool(IWindowsSchedulerAdapter adapter) => this.adapter = adapter;
    protected override Task<SchedulerInspectResult> CollectAsync(string id, CancellationToken ct) =>
        Task.FromResult(WindowsSchedulerProjection.Inspect(adapter.GetTask(WindowsSchedulerValidation.RequirePath(id))));
}

public sealed class WindowsSchedulerHistoryTool : SchedulerHistoryToolBase
{
    private readonly IWindowsSchedulerAdapter adapter;
    public WindowsSchedulerHistoryTool() : this(new ComWindowsSchedulerAdapter()) { }
    internal WindowsSchedulerHistoryTool(IWindowsSchedulerAdapter adapter) => this.adapter = adapter;
    protected override Task<SchedulerHistoryCollection> CollectAsync(string id, int sinceMinutes, int limit, CancellationToken ct) =>
        Task.FromResult(adapter.History(WindowsSchedulerValidation.RequirePath(id), sinceMinutes, limit));
}

public sealed class WindowsSchedulerEnableTool : SchedulerEnableToolBase
{
    private readonly IWindowsSchedulerAdapter adapter;
    public WindowsSchedulerEnableTool() : this(new ComWindowsSchedulerAdapter()) { }
    internal WindowsSchedulerEnableTool(IWindowsSchedulerAdapter adapter) => this.adapter = adapter;
    protected override Task<ToolCallResult> EnableAsync(string id, CancellationToken ct) => Task.FromResult(Change(id, true));
    private ToolCallResult Change(string id, bool enabled) => adapter.SetEnabled(WindowsSchedulerValidation.RequirePath(id), enabled);
}

public sealed class WindowsSchedulerDisableTool : SchedulerDisableToolBase
{
    private readonly IWindowsSchedulerAdapter adapter;
    public WindowsSchedulerDisableTool() : this(new ComWindowsSchedulerAdapter()) { }
    internal WindowsSchedulerDisableTool(IWindowsSchedulerAdapter adapter) => this.adapter = adapter;
    protected override Task<ToolCallResult> DisableAsync(string id, CancellationToken ct) => Task.FromResult(Change(id, false));
    private ToolCallResult Change(string id, bool enabled) => adapter.SetEnabled(WindowsSchedulerValidation.RequirePath(id), enabled);
}

internal static class WindowsSchedulerValidation
{
    public static string RequirePath(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id[0] != '\\' || id.Contains('\0') || id.Contains('/'))
            throw new ArgumentException("id must be a canonical Task Scheduler path beginning with '\\'.", nameof(id));
        return id.Trim();
    }
}

internal sealed record WindowsTaskSnapshot(
    string Path, string Name, bool Enabled, DateTimeOffset? NextRunUtc, DateTimeOffset? LastRunUtc,
    int? LastResult, string? User, string? Description, IReadOnlyList<WindowsActionSnapshot> Actions,
    IReadOnlyList<WindowsTriggerSnapshot> Triggers);
internal sealed record WindowsActionSnapshot(string Type, string? Path, string? WorkingDirectory);
internal sealed record WindowsTriggerSnapshot(string Type, bool Enabled, DateTimeOffset? StartUtc, DateTimeOffset? EndUtc);
internal sealed record WindowsFolderSnapshot(string Path, IReadOnlyList<WindowsTaskSnapshot> Tasks, IReadOnlyList<WindowsFolderSnapshot> Children, bool AccessDenied = false, bool Complete = true);

internal interface IWindowsSchedulerAdapter
{
    WindowsFolderSnapshot Enumerate();
    WindowsTaskSnapshot GetTask(string path);
    ToolCallResult SetEnabled(string path, bool enabled);
    SchedulerHistoryCollection History(string path, int sinceMinutes, int limit);
}

internal static class WindowsSchedulerProjection
{
    public static SchedulerCollection<SchedulerListItem> List(WindowsFolderSnapshot root, int limit)
    {
        var items = new List<SchedulerListItem>();
        var folders = 0;
        var tasks = 0;
        var incomplete = false;
        Visit(root, 0);
        var sorted = items.OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
        return new(sorted, !incomplete, incomplete);

        void Visit(WindowsFolderSnapshot folder, int depth)
        {
            if (folders >= 512 || depth >= 64) { incomplete = true; return; }
            folders++;
            if (folder.AccessDenied || !folder.Complete) { incomplete = true; return; }
            foreach (var task in folder.Tasks.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Path, StringComparer.Ordinal))
            {
                if (tasks++ >= 4096) { incomplete = true; return; }
                items.Add(ToList(task));
            }
            foreach (var child in folder.Children.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Path, StringComparer.Ordinal)) Visit(child, depth + 1);
        }
    }

    public static SchedulerInspectResult Inspect(WindowsTaskSnapshot task) =>
        new(task.Path, task.Name, "task-scheduler", task.Enabled, TriggerSummary(task.Triggers), task.NextRunUtc, task.LastRunUtc,
            task.LastResult?.ToString(CultureInfo.InvariantCulture), ActionTarget(task.Actions), task.User, WorkingDirectory(task.Actions),
            Bound(task.Description, 2048), TriggerSummary(task.Triggers), true);

    private static SchedulerListItem ToList(WindowsTaskSnapshot task) =>
        new(task.Path, task.Name, "task-scheduler", task.Enabled, TriggerSummary(task.Triggers), task.NextRunUtc, task.LastRunUtc,
            task.LastResult?.ToString(CultureInfo.InvariantCulture), ActionTarget(task.Actions), task.User);

    private static string? ActionTarget(IReadOnlyList<WindowsActionSnapshot> actions)
    {
        if (actions.Count == 0) return null;
        var values = actions.Select(a => a.Type.Equals("exec", StringComparison.OrdinalIgnoreCase) ? $"exec:{a.Path}" : a.Type).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        return Bound(string.Join(" | ", values), 2048);
    }
    private static string? WorkingDirectory(IReadOnlyList<WindowsActionSnapshot> actions) =>
        actions.Count == 1 && actions[0].Type.Equals("exec", StringComparison.OrdinalIgnoreCase) ? actions[0].WorkingDirectory : null;
    private static string? TriggerSummary(IReadOnlyList<WindowsTriggerSnapshot> triggers)
    {
        if (triggers.Count == 0) return null;
        var values = triggers.OrderBy(x => x.Type, StringComparer.Ordinal).ThenBy(x => x.StartUtc).Select(x =>
            $"type={x.Type};enabled={x.Enabled.ToString().ToLowerInvariant()};start={Format(x.StartUtc)};end={Format(x.EndUtc)}");
        return Bound(string.Join(" | ", values), 2048);
    }
    private static string Format(DateTimeOffset? value) => value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "null";
    private static string? Bound(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}
