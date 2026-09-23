using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using bOps.Abstractions;
using bOps.Packages.Scheduler.Core;

namespace bOps.Packages.Scheduler.Windows;

/// <summary>Small, late-bound boundary for Task Scheduler 2.0. Dynamic COM never leaves this file.</summary>
internal sealed class ComWindowsSchedulerAdapter : IWindowsSchedulerAdapter
{
    public WindowsFolderSnapshot Enumerate()
    {
        dynamic? service = null;
        try
        {
            service = CreateService();
            service.Connect();
            return ReadFolder(service.GetFolder("\\"), 0);
        }
        catch (COMException ex) when (IsUnavailable(ex)) { throw new InvalidOperationException("Windows Task Scheduler is unavailable.", ex); }
        finally { Release(service); }
    }

    public WindowsTaskSnapshot GetTask(string path)
    {
        var valid = WindowsSchedulerValidation.RequirePath(path);
        dynamic? service = null; dynamic? task = null;
        try
        {
            service = CreateService();
            service.Connect();
            task = service.GetFolder(FolderPath(valid)).GetTask(TaskName(valid));
            return ReadTask(task);
        }
        catch (COMException ex) when (IsNotFound(ex)) { throw new KeyNotFoundException($"Task '{valid}' does not exist.", ex); }
        catch (COMException ex) when (IsAccessDenied(ex)) { throw new UnauthorizedAccessException($"Task '{valid}' cannot be read.", ex); }
        finally { Release(task); Release(service); }
    }

    public ToolCallResult SetEnabled(string path, bool enabled)
    {
        var valid = WindowsSchedulerValidation.RequirePath(path);
        dynamic? service = null; dynamic? task = null;
        try
        {
            service = CreateService();
            service.Connect();
            task = service.GetFolder(FolderPath(valid)).GetTask(TaskName(valid));
            task.Enabled = enabled;
            return ToolCallResult.Success($"Updated enablement for task '{valid}'.");
        }
        catch (COMException ex) when (IsNotFound(ex)) { return ToolCallResult.Failure($"Task '{valid}' does not exist."); }
        catch (COMException ex) when (IsAccessDenied(ex)) { return ToolCallResult.Failure($"Task '{valid}' cannot be changed: access denied."); }
        catch (COMException ex) { return ToolCallResult.Failure($"Could not change task '{valid}': {ex.Message}"); }
        finally { Release(task); Release(service); }
    }

    public SchedulerHistoryCollection History(string path, int sinceMinutes, int limit) => WindowsTaskHistory.Read(path, sinceMinutes, limit);

    private static object CreateService() => Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service") ?? throw new PlatformNotSupportedException("Task Scheduler COM is unavailable."))!;

    private static WindowsFolderSnapshot ReadFolder(dynamic folder, int depth)
    {
        var path = (string)folder.Path;
        try
        {
            var tasks = new List<WindowsTaskSnapshot>();
            var complete = true;
            dynamic? taskCollection = null;
            try
            {
                taskCollection = folder.GetTasks(1);
                for (var i = 1; i <= (int)taskCollection.Count; i++)
                {
                    dynamic? task = null;
                    try { task = taskCollection[i]; tasks.Add(ReadTask(task)); }
                    catch (COMException) { complete = false; }
                    finally { Release(task); }
                }
            }
            finally { Release(taskCollection); }

            var children = new List<WindowsFolderSnapshot>();
            dynamic? folders = null;
            try
            {
                folders = folder.GetFolders(0);
                for (var i = 1; i <= (int)folders.Count; i++)
                {
                    dynamic? child = null;
                    try { child = folders[i]; children.Add(ReadFolder(child, depth + 1)); }
                    catch (COMException ex) when (IsAccessDenied(ex)) { complete = false; children.Add(new WindowsFolderSnapshot(path + "\\<denied>", [], [], true, false)); }
                    finally { Release(child); }
                }
            }
            finally { Release(folders); }
            return new(path, tasks, children, false, complete);
        }
        catch (COMException ex) when (IsAccessDenied(ex)) { return new(path, [], [], true, false); }
    }

    private static WindowsTaskSnapshot ReadTask(dynamic task)
    {
        var actions = new List<WindowsActionSnapshot>();
        dynamic? definition = null; dynamic? actionCollection = null; dynamic? triggers = null;
        try
        {
            definition = task.Definition;
            actionCollection = definition.Actions;
            for (var i = 1; i <= (int)actionCollection.Count; i++)
            {
                dynamic action = actionCollection[i];
                try
                {
                    var type = ActionType((int)action.Type);
                    actions.Add(new(type, type == "exec" ? SafeString(action.Path) : null, type == "exec" ? SafeString(action.WorkingDirectory) : null));
                }
                finally { Release(action); }
            }
            var triggerValues = new List<WindowsTriggerSnapshot>();
            triggers = definition.Triggers;
            for (var i = 1; i <= (int)triggers.Count; i++)
            {
                dynamic trigger = triggers[i];
                try { triggerValues.Add(new(TriggerType((int)trigger.Type), (bool)trigger.Enabled, ToUtc(SafeString(trigger.StartBoundary)), ToUtc(SafeString(trigger.EndBoundary)))); }
                finally { Release(trigger); }
            }
            var path = (string)task.Path;
            return new(path, (string)task.Name, (bool)task.Enabled, ToUtc(task.NextRunTime), ToUtc(task.LastRunTime), SafeInt(task.LastTaskResult),
                SafeString(definition.Principal.UserId), SafeString(definition.RegistrationInfo.Description), actions, triggerValues);
        }
        finally { Release(triggers); Release(actionCollection); Release(definition); }
    }

    private static string FolderPath(string path) => path[..path.LastIndexOf('\\')].Length == 0 ? "\\" : path[..path.LastIndexOf('\\')];
    private static string TaskName(string path) => path[(path.LastIndexOf('\\') + 1)..];
    private static string? SafeString(dynamic value) { try { return value is null ? null : (string?)value; } catch (COMException) { return null; } }
    private static int? SafeInt(dynamic value) { try { return value is null ? null : (int?)value; } catch (COMException) { return null; } }
    private static DateTimeOffset? ToUtc(dynamic value)
    {
        try
        {
            if (value is null) return null;
            var dt = value is DateTime date ? date : DateTime.Parse((string)value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (dt == DateTime.MinValue || dt.Year <= 1601) return null;
            return new DateTimeOffset(dt.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (COMException) { return null; }
        catch (FormatException) { return null; }
    }
    private static string ActionType(int type) => type == 0 ? "exec" : type switch { 5 => "com-handler", 6 => "email", 7 => "message", _ => "unknown" };
    private static string TriggerType(int type) => type switch { 0 => "event", 1 => "time", 2 => "daily", 3 => "weekly", 4 => "monthly", 5 => "monthly-day-of-week", 6 => "idle", 7 => "registration", 8 => "boot", 9 => "logon", 11 => "session-state-change", 12 => "custom", _ => "unknown" };
    private static bool IsNotFound(COMException ex) => ex.HResult is unchecked((int)0x80070002) or unchecked((int)0x8004130F) or unchecked((int)0x8004130E);
    private static bool IsAccessDenied(COMException ex) => ex.HResult is unchecked((int)0x80070005) or unchecked((int)0x8004131F);
    private static bool IsUnavailable(COMException ex) => ex.HResult is unchecked((int)0x80040154) or unchecked((int)0x80041315);
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
}

internal static class WindowsTaskHistory
{
    public static SchedulerHistoryCollection Read(string path, int sinceMinutes, int limit)
    {
        var rows = new List<SchedulerHistoryRow>();
        try
        {
            var escaped = System.Security.SecurityElement.Escape(path);
            var xpath = $"*[System[TimeCreated[timediff(@SystemTime) <= {sinceMinutes * 60000}] and EventData[Data[@Name='TaskName'] = '{escaped}']]]";
            var query = new EventLogQuery("Microsoft-Windows-TaskScheduler/Operational", PathType.LogName, xpath) { ReverseDirection = true, TolerateQueryErrors = false };
            using var reader = new EventLogReader(query);
            var observed = 0;
            while (observed <= limit)
            {
                using var record = reader.ReadEvent();
                if (record is null) break;
                observed++;
                if (rows.Count < limit && record.TimeCreated is { } created && TryRead(record, path, out var row)) rows.Add(row!);
            }
            return new(rows.OrderByDescending(x => x.TimestampUtc).ToArray(), true, observed > limit);
        }
        catch (EventLogNotFoundException) { return new([], false, false); }
        catch (UnauthorizedAccessException) { return new([], false, false); }
        catch (EventLogException) { return new([], false, false); }
    }

    private static bool TryRead(EventRecord record, string path, out SchedulerHistoryRow? row)
    {
        row = null;
        if (record.TimeCreated is not { } created) return false;
        var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var xml = XDocument.Parse(record.ToXml());
            foreach (var data in xml.Descendants().Where(x => x.Name.LocalName == "Data"))
            {
                var name = data.Attribute("Name")?.Value;
                if (!string.IsNullOrWhiteSpace(name)) fields[name] = data.Value;
            }
            if (fields.TryGetValue("TaskName", out var taskName) && !string.Equals(taskName, path, StringComparison.OrdinalIgnoreCase)) return false;
            string? message = null;
            try { message = record.FormatDescription(); } catch (EventLogException) { }
            row = new(new DateTimeOffset(created.ToUniversalTime(), TimeSpan.Zero), record.Id.ToString(CultureInfo.InvariantCulture), First(fields, "ResultCode", "ErrorCode", "ReturnCode"), Bound(message, 2048));
            return true;
        }
        catch (Exception ex) when (ex is EventLogException or InvalidOperationException or System.Xml.XmlException) { return false; }
    }
    private static string? First(Dictionary<string, string?> values, params string[] keys) => keys.Select(k => values.GetValueOrDefault(k)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
    private static string? Bound(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}
