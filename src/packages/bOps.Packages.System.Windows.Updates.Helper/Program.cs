// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using bOps.Packages.Sys.Core;
using bOps.Packages.Sys.Windows;

namespace bOps.Packages.Sys.Windows.Updates.Helper;

/// <summary>Private fixed-operation WUA host. It is not a bOps tool or a public capability.</summary>
internal static class Program
{
    private const string SearchCriteria = "IsInstalled=0 and IsHidden=0";
    private const int MaximumNativeScan = 10_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "pending-updates" && SystemMaintenanceArguments.UpdateKinds.Contains(args[1], StringComparer.Ordinal) && int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var limit) && limit is >= 1 and <= SystemMaintenanceLimits.MaximumUpdates)
        {
            Console.Out.Write(JsonSerializer.Serialize(Collect(args[1], limit), JsonOptions));
            return 0;
        }
        if (args.Length == 3 && args[0] == "update-history" && int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var sinceDays) && sinceDays is >= 1 and <= SystemMaintenanceLimits.MaximumSinceDays && int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var historyLimit) && historyLimit is >= 1 and <= SystemMaintenanceLimits.MaximumHistory)
        {
            Console.Out.Write(JsonSerializer.Serialize(CollectHistory(sinceDays, historyLimit), JsonOptions));
            return 0;
        }
        else
        {
            Console.Error.WriteLine("Only fixed WUA operations with normalized bounded arguments are supported.");
            return 2;
        }
    }

    private static MaintenanceSnapshot<UpdateHistoryRecord> CollectHistory(int sinceDays, int limit)
    {
        object? session = null; object? searcher = null; object? entries = null;
        try
        {
            session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session") ?? throw new InvalidOperationException("Windows Update Agent is not registered."));
            searcher = Invoke(session!, "CreateUpdateSearcher");
            var total = Convert.ToInt32(Invoke(searcher!, "GetTotalHistoryCount"), CultureInfo.InvariantCulture);
            var count = Math.Min(total, SystemMaintenanceLimits.MaximumHistory);
            entries = Invoke(searcher!, "QueryHistory", 0, count);
            var cutoff = DateTimeOffset.UtcNow.AddDays(-sinceDays);
            var rows = new List<UpdateHistoryRecord>();
            var truncated = total > count;
            for (var index = 0; index < count; index++)
            {
                object? entry = null; object? identity = null;
                try
                {
                    entry = Property(entries!, "Item", index); identity = Property(entry!, "UpdateIdentity");
                    var timestamp = new DateTimeOffset(((DateTime)Property(entry!, "Date")!).ToUniversalTime());
                    if (timestamp < cutoff) continue;
                    var updateId = (string?)Property(identity!, "UpdateID") ?? "unknown";
                    var revision = Convert.ToInt32(Property(identity!, "RevisionNumber"), CultureInfo.InvariantCulture);
                    var resultCode = Convert.ToInt32(Property(entry!, "ResultCode"), CultureInfo.InvariantCulture);
                    // WUA OperationResultCode: 2=Succeeded, 4=Failed; every other code is not success evidence.
                    var result = resultCode switch { 2 => "success", 4 => "failure", _ => "unknown" };
                    rows.Add(new(timestamp, updateId + ":" + revision.ToString(CultureInfo.InvariantCulture), (string?)Property(entry!, "Title") ?? string.Empty, null, result, "windows-update-agent"));
                }
                finally { Release(identity); Release(entry); }
            }
            return new(rows, [new("windows-update-agent", InventorySourceStatus.Available)], CollectionTruncated: truncated);
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or MissingMethodException or UnauthorizedAccessException or System.Reflection.TargetInvocationException)
        {
            const string warning = "windows-update-agent.history-unavailable";
            return new([], [new("windows-update-agent", InventorySourceStatus.Unavailable, warning)], [warning]);
        }
        finally { Release(entries); Release(searcher); Release(session); }
    }

    private static MaintenanceSnapshot<UpdateRecord> Collect(string kind, int limit)
    {
        object? session = null;
        object? searcher = null;
        object? result = null;
        try
        {
            session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session") ?? throw new InvalidOperationException("Windows Update Agent is not registered."));
            searcher = Invoke(session!, "CreateUpdateSearcher");
            result = Invoke(searcher!, "Search", SearchCriteria);
            var updates = Property(result!, "Updates");
            var count = Convert.ToInt32(Property(updates!, "Count"), CultureInfo.InvariantCulture);
            var rows = new List<UpdateRecord>();
            var truncated = count > MaximumNativeScan;
            for (var index = 0; index < Math.Min(count, MaximumNativeScan); index++)
            {
                object? update = null;
                try
                {
                    update = Property(updates!, "Item", index);
                    var row = WindowsUpdateRecordMapper.Map(Read(update!));
                    if (WindowsUpdateRecordMapper.MatchesKind(row, kind))
                    {
                        if (rows.Count == limit)
                        {
                            truncated = true;
                            break;
                        }
                        rows.Add(row);
                    }
                }
                finally { Release(update); }
            }
            Release(updates);
            return new MaintenanceSnapshot<UpdateRecord>(rows, [new("windows-update-agent", InventorySourceStatus.Available)], CollectionTruncated: truncated);
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or MissingMethodException or UnauthorizedAccessException or System.Reflection.TargetInvocationException)
        {
            var warning = "windows-update-agent.unavailable";
            return new MaintenanceSnapshot<UpdateRecord>([], [new("windows-update-agent", InventorySourceStatus.Unavailable, warning)], [warning]);
        }
        finally
        {
            Release(result);
            Release(searcher);
            Release(session);
        }
    }

    private static WindowsUpdateNativeRecord Read(object update)
    {
        object? identity = null;
        object? categories = null;
        object? behavior = null;
        try
        {
            identity = Property(update, "Identity");
            categories = Property(update, "Categories");
            var categoryIds = new List<string>();
            var count = Convert.ToInt32(Property(categories!, "Count"), CultureInfo.InvariantCulture);
            for (var index = 0; index < count; index++)
            {
                object? category = null;
                try
                {
                    category = Property(categories!, "Item", index);
                    if (Property(category!, "CategoryID") is string id) categoryIds.Add(id);
                }
                finally { Release(category); }
            }
            behavior = Property(update, "InstallationBehavior");
            var reboot = Convert.ToInt32(Property(behavior!, "RebootBehavior"), CultureInfo.InvariantCulture);
            return new WindowsUpdateNativeRecord(
                (string)Property(identity!, "UpdateID")!,
                Convert.ToInt32(Property(identity!, "RevisionNumber"), CultureInfo.InvariantCulture),
                (string?)Property(update, "Title") ?? string.Empty,
                categoryIds,
                reboot switch { 0 => false, 1 or 2 => true, _ => null });
        }
        finally
        {
            Release(behavior);
            Release(categories);
            Release(identity);
        }
    }

    private static object? Property(object target, string name, params object[] arguments) => target.GetType().InvokeMember(name, System.Reflection.BindingFlags.GetProperty, null, target, arguments);
    private static object? Invoke(object target, string name, params object[] arguments) => target.GetType().InvokeMember(name, System.Reflection.BindingFlags.InvokeMethod, null, target, arguments);
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
}
