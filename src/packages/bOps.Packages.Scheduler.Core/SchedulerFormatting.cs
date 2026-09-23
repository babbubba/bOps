using System.Text;
using System.Text.Json.Nodes;

namespace bOps.Packages.Scheduler.Core;

public static class SchedulerFormatting
{
    public static string FormatList(SchedulerCollection<SchedulerListItem> c, int limit, int maxOutputBytes=32_768)
    { var items=c.Items.Take(limit).Select(Item).ToList(); var root=new JsonObject { ["schemaVersion"]=1,["observedItems"]=c.Items.Count,["returnedItems"]=items.Count,["truncated"]=c.CollectionTruncated||items.Count<c.Items.Count,["complete"]=c.Complete&&!(c.CollectionTruncated||items.Count<c.Items.Count),["items"]=new JsonArray(items.Select(x=>(JsonNode)x).ToArray()) }; return Bound(root,maxOutputBytes); }
    public static string FormatInspect(SchedulerInspectResult x) => new JsonObject { ["id"]=B(x.Id,512),["name"]=B(x.Name,512),["source"]=B(x.Source,128),["enabled"]=x.Enabled,["schedule"]=B(x.Schedule,1024),["nextRunUtc"]=Utc(x.NextRunUtc),["lastRunUtc"]=Utc(x.LastRunUtc),["lastResult"]=B(x.LastResult,256),["target"]=B(x.Target,2048),["user"]=B(x.User,512),["workingDirectory"]=B(x.WorkingDirectory,1024),["description"]=B(x.Description,2048),["triggerSummary"]=B(x.TriggerSummary,2048),["complete"]=x.Complete }.ToJsonString();
    public static string FormatHistory(SchedulerHistoryCollection c,int limit,int maxOutputBytes=32_768)
    { var rows=c.Rows.OrderByDescending(x=>x.TimestampUtc).Take(limit).Select(x=>new JsonObject { ["timestampUtc"]=x.TimestampUtc.ToUniversalTime().ToString("O"),["event"]=B(x.Event,256),["result"]=B(x.Result,256),["message"]=B(x.Message,2048) }).ToList(); var root=new JsonObject { ["schemaVersion"]=1,["observedItems"]=c.Rows.Count,["returnedItems"]=rows.Count,["truncated"]=c.CollectionTruncated||rows.Count<c.Rows.Count,["complete"]=c.Complete&&!(c.CollectionTruncated||rows.Count<c.Rows.Count),["rows"]=new JsonArray(rows.Select(x=>(JsonNode)x).ToArray()) }; return Bound(root,maxOutputBytes); }
    private static JsonObject Item(SchedulerListItem x)=>new() { ["id"]=B(x.Id,512),["name"]=B(x.Name,512),["source"]=B(x.Source,128),["enabled"]=x.Enabled,["schedule"]=B(x.Schedule,1024),["nextRunUtc"]=Utc(x.NextRunUtc),["lastRunUtc"]=Utc(x.LastRunUtc),["lastResult"]=B(x.LastResult,256),["target"]=B(x.Target,2048),["user"]=B(x.User,512) };
    private static string? Utc(DateTimeOffset? x)=>x?.ToUniversalTime().ToString("O");
    private static string? B(string? x,int max)=>x is null||x.Length<=max?x:x[..max];
    private static string Bound(JsonObject root,int max) { var a=root["items"]?.AsArray()??root["rows"]?.AsArray(); var output=root.ToJsonString(); while(Encoding.UTF8.GetByteCount(output)>max&&a is { Count:>0 }) { a.RemoveAt(a.Count-1); root["returnedItems"]=a.Count; root["truncated"]=true; root["complete"]=false; output=root.ToJsonString(); } if(Encoding.UTF8.GetByteCount(output)>max) throw new InvalidOperationException($"Scheduler metadata exceeds {max} bytes."); return output; }
}
