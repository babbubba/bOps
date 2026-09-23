using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Scheduler.Conformance;
using bOps.Packages.Scheduler.Core;
using bOps.Packages.Scheduler.Windows;

namespace bOps.Packages.Scheduler.Windows.Tests;

public sealed class WindowsSchedulerDeterministicTests
{
    private static WindowsTaskSnapshot Task(string path, string? target = "C:\\bin\\job.exe", string? args = null, string? working = "C:\\bin") =>
        new(path, path[(path.LastIndexOf('\\') + 1)..], true, DateTimeOffset.Parse("2026-09-22T10:00:00Z"), null, 0, "user", "description", [new("exec", target, working)], [new("daily", true, DateTimeOffset.Parse("2026-09-22T09:00:00Z"), null)]);

    [Fact] public void Provider_ExposesExactlyFiveTools()
    {
        var names = new WindowsSchedulerToolProvider().GetTools().Select(x => x.Manifest.Name).ToArray();
        Assert.Equal(["scheduler.list", "scheduler.inspect", "scheduler.history", "scheduler.enable", "scheduler.disable"], names);
        SchedulerConformance.AssertManifests();
    }

    [Fact] public async Task List_IsSortedBoundedAndCanonical()
    {
        var root = new WindowsFolderSnapshot("\\", [Task("\\z"), Task("\\a")], [new("\\b", [Task("\\b\\x")], [])]);
        var result = await new WindowsSchedulerListTool(new FakeAdapter(root)).ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["limit"] = 2 }));
        Assert.True(result.Succeeded); SchedulerConformance.AssertListShape(result.Output!);
        var output = JsonNode.Parse(result.Output!)!.AsObject();
        Assert.Equal("\\a", output["items"]![0]!["id"]!.GetValue<string>()); Assert.True(output["truncated"]!.GetValue<bool>()); Assert.False(output["complete"]!.GetValue<bool>());
    }

    [Fact] public void EnumerationCeilingsMarkIncomplete()
    {
        var deep = new WindowsFolderSnapshot("\\", [], BuildDepth(65));
        Assert.False(WindowsSchedulerProjection.List(deep, 200).Complete);
        var folders = Enumerable.Range(0, 513).Select(i => new WindowsFolderSnapshot($"\\f{i}", [], [])).ToArray();
        Assert.False(WindowsSchedulerProjection.List(new WindowsFolderSnapshot("\\", [], folders), 200).Complete);
        var tasks = Enumerable.Range(0, 4097).Select(i => Task($"\\t{i}")).ToArray();
        Assert.False(WindowsSchedulerProjection.List(new WindowsFolderSnapshot("\\", tasks, []), 2000).Complete);
    }

    [Fact] public async Task AccessDenied_IsIncomplete_NotNotFound()
    {
        var root = new WindowsFolderSnapshot("\\", [], [new("\\denied", [], [], true)]);
        var list = await new WindowsSchedulerListTool(new FakeAdapter(root)).ExecuteAsync(ToolArguments.Empty);
        Assert.False(JsonNode.Parse(list.Output!)!["complete"]!.GetValue<bool>());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => new WindowsSchedulerInspectTool(new FakeAdapter(root)).ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["id"] = "\\missing" })));
    }

    [Fact] public async Task Projection_DoesNotExposeArguments_AndUsesUtc()
    {
        var task = Task("\\job", args: "--password=secret");
        var inspect = await new WindowsSchedulerInspectTool(new FakeAdapter(new WindowsFolderSnapshot("\\", [task], []))).ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["id"] = "\\job" }));
        Assert.DoesNotContain("password", inspect.Output!, StringComparison.OrdinalIgnoreCase); Assert.Contains("2026-09-22T10:00:00", inspect.Output!); Assert.Contains("workingDirectory", inspect.Output!);
    }

    [Fact] public async Task EnableDisable_OnlyChangeEnabled_AndInspectVerifies()
    {
        var adapter = new FakeAdapter(new WindowsFolderSnapshot("\\", [Task("\\job")], []));
        Assert.True((await new WindowsSchedulerDisableTool(adapter).ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["id"] = "\\job" }))).Succeeded);
        Assert.DoesNotContain("\"enabled\":true", (await new WindowsSchedulerInspectTool(adapter).ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["id"] = "\\job" }))).Output!);
        Assert.True((await new WindowsSchedulerEnableTool(adapter).ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["id"] = "\\job" }))).Succeeded);
    }

    [Fact] public void TriggerSummary_IsBoundedAndDeterministic()
    {
        var task = Task("\\job") with { Triggers = [new("weekly", false, null, null), new("boot", true, null, null)] };
        var result = WindowsSchedulerProjection.Inspect(task);
        Assert.StartsWith("type=boot", result.TriggerSummary); Assert.Contains("type=weekly", result.TriggerSummary);
    }

    [Fact] public async Task History_PreservesUnavailableAndReadableEmptySemantics()
    {
        var unavailable = await new WindowsSchedulerHistoryTool(new FakeAdapter(new WindowsFolderSnapshot("\\", [], []), new([], false, false))).ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["id"] = "\\job" }));
        var unavailableJson = JsonNode.Parse(unavailable.Output!)!.AsObject();
        Assert.False(unavailableJson["complete"]!.GetValue<bool>());
        var empty = await new WindowsSchedulerHistoryTool(new FakeAdapter(new WindowsFolderSnapshot("\\", [], []), new([], true, false))).ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["id"] = "\\job" }));
        Assert.True(JsonNode.Parse(empty.Output!)!["complete"]!.GetValue<bool>());
    }

    private sealed class FakeAdapter(WindowsFolderSnapshot root, SchedulerHistoryCollection? history = null) : IWindowsSchedulerAdapter
    {
        private readonly SchedulerHistoryCollection history = history ?? new([], true, false);
        private readonly Dictionary<string, WindowsTaskSnapshot> tasks = Flatten(root).ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        public WindowsFolderSnapshot Enumerate() => root;
        public WindowsTaskSnapshot GetTask(string path) => tasks.TryGetValue(path, out var task) ? task : throw new KeyNotFoundException(path);
        public ToolCallResult SetEnabled(string path, bool enabled) { var task = GetTask(path); tasks[path] = task with { Enabled = enabled }; return ToolCallResult.Success("changed"); }
        public SchedulerHistoryCollection History(string path, int sinceMinutes, int limit) => history;
        private static IEnumerable<WindowsTaskSnapshot> Flatten(WindowsFolderSnapshot f) => f.Tasks.Concat(f.Children.SelectMany(Flatten));
    }

    private static IReadOnlyList<WindowsFolderSnapshot> BuildDepth(int count)
    {
        IReadOnlyList<WindowsFolderSnapshot> children = [];
        for (var i = count; i >= 1; i--) children = [new WindowsFolderSnapshot($"\\d{i}", [], children)];
        return children;
    }
}
