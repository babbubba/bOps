using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Scheduler.Core;
using bOps.Packages.Scheduler.Windows;

namespace bOps.Packages.Scheduler.Windows.Tests;

[Trait("Platform", "Windows")]
public sealed class WindowsSchedulerRealTaskTests
{
    [TaskSchedulerIntegrationFact]
    public async Task TestOwnedTask_ListInspectDisableEnableAndHistory()
    {
        dynamic? service = null; dynamic? root = null; dynamic? folder = null;
        var folderName = $"bOps.Tests.{Guid.NewGuid():N}";
        var taskName = "Lifecycle";
        var path = $"\\{folderName}\\{taskName}";
        try
        {
            service = CreateService();
            service.Connect();
            root = service.GetFolder("\\");
            folder = root.CreateFolder(folderName, null);
            dynamic definition = service.NewTask(0);
            try
            {
                definition.RegistrationInfo.Description = "bOps test-owned scheduler task";
                definition.Settings.Enabled = true;
                definition.Settings.Hidden = false;
                definition.Principal.UserId = WindowsIdentity.GetCurrent().Name;
                definition.Principal.LogonType = 3; // TASK_LOGON_INTERACTIVE_TOKEN; no password is persisted.
                dynamic action = definition.Actions.Create(0); // TASK_ACTION_EXEC; this action is never run.
                try { action.Path = Environment.SystemDirectory + "\\cmd.exe"; }
                finally { Release(action); }
                dynamic trigger = definition.Triggers.Create(8); // TASK_TRIGGER_TIME
                try { trigger.StartBoundary = DateTime.Now.AddHours(1).ToString("s"); trigger.Enabled = true; }
                finally { Release(trigger); }
                dynamic registered = folder.RegisterTaskDefinition(taskName, definition, 6, null, null, 3, null); // create/update + interactive token
                Release(registered);
            }
            finally { Release(definition); }

            var listResult = await new WindowsSchedulerListTool().ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["source"] = "task-scheduler", ["limit"] = 2000 }));
            Assert.True(listResult.Succeeded, listResult.ErrorMessage);
            var list = JsonNode.Parse(listResult.Output!)!.AsObject()["items"]!.AsArray();
            Assert.Contains(list, item => item!["id"]!.GetValue<string>() == path);

            var inspectTool = new WindowsSchedulerInspectTool();
            var inspectArgs = ToolArguments.FromJson(new JsonObject { ["id"] = path });
            var inspected = await inspectTool.ExecuteAsync(inspectArgs);
            Assert.True(inspected.Succeeded, inspected.ErrorMessage);
            Assert.Equal(path, JsonNode.Parse(inspected.Output!)!["id"]!.GetValue<string>());

            Assert.True((await new WindowsSchedulerDisableTool().ExecuteAsync(inspectArgs)).Succeeded);
            var disabled = await inspectTool.ExecuteAsync(inspectArgs);
            Assert.False(JsonNode.Parse(disabled.Output!)!["enabled"]!.GetValue<bool>());
            Assert.True((await new WindowsSchedulerEnableTool().ExecuteAsync(inspectArgs)).Succeeded);
            var enabled = await inspectTool.ExecuteAsync(inspectArgs);
            Assert.True(JsonNode.Parse(enabled.Output!)!["enabled"]!.GetValue<bool>());

            var history = await new WindowsSchedulerHistoryTool().ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["id"] = path, ["sinceMinutes"] = 10, ["limit"] = 10 }));
            Assert.True(history.Succeeded, history.ErrorMessage);
            var historyJson = JsonNode.Parse(history.Output!)!.AsObject();
            Assert.Contains("complete", historyJson);
            if (!historyJson["complete"]!.GetValue<bool>()) Assert.Empty(historyJson["rows"]!.AsArray());
        }
        finally
        {
            try { if (folder is not null) folder.DeleteTask(taskName, 0); } catch (Exception ex) when (ex is COMException or FileNotFoundException) { }
            try { if (root is not null) root.DeleteFolder(folderName, 0); } catch (Exception ex) when (ex is COMException or FileNotFoundException) { }
            Release(folder); Release(root); Release(service);
        }
    }

    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
    private static object CreateService() => Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service") ?? throw new PlatformNotSupportedException())!;
}
