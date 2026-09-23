using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Scheduler.Core;
using Xunit;

namespace bOps.Packages.Scheduler.Conformance;
public static class SchedulerConformance
{
    public static void AssertManifests()
    {
        Assert.Equal(RiskLevel.Read, SchedulerToolManifests.List().Risk); Assert.Equal(RiskLevel.Read, SchedulerToolManifests.Inspect().Risk); Assert.Equal(RiskLevel.Read, SchedulerToolManifests.History().Risk);
        Assert.Equal(RiskLevel.Medium, SchedulerToolManifests.Enable().Risk); Assert.Equal(RiskLevel.Medium, SchedulerToolManifests.Disable().Risk);
        Assert.Equal("scheduler.inspect", SchedulerToolManifests.Enable().Verification!.VerifyToolName); Assert.Equal("scheduler.inspect", SchedulerToolManifests.Disable().Verification!.VerifyToolName);
    }
    public static void AssertListShape(string json) { var o=JsonNode.Parse(json)!.AsObject(); Assert.Contains("items",o); Assert.Contains("observedItems",o); Assert.Contains("returnedItems",o); Assert.Contains("truncated",o); Assert.Contains("complete",o); }
    public static void AssertInspectShape(string json) { var o=JsonNode.Parse(json)!.AsObject(); foreach(var n in new[]{"id","name","source","enabled","schedule","nextRunUtc","lastRunUtc","lastResult","target","user","workingDirectory","description","triggerSummary","complete"}) Assert.Contains(n,o); }
    public static void AssertHistoryShape(string json) { var o=JsonNode.Parse(json)!.AsObject(); Assert.Contains("rows",o); Assert.Contains("observedItems",o); Assert.Contains("returnedItems",o); Assert.Contains("truncated",o); Assert.Contains("complete",o); }
}
