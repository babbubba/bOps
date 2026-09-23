using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Scheduler.Core;
using bOps.Packages.Scheduler.Conformance;

namespace bOps.Packages.Scheduler.Core.Tests;
public sealed class SchedulerCoreTests
{
    [Fact] public void Manifests_Conform() => SchedulerConformance.AssertManifests();
    [Fact] public async Task MutationVerification_IsFailSafe() { var enable=new FakeEnable(); var yes=await enable.EvaluateVerificationAsync(ToolArguments.Empty,ToolCallResult.Success("{\"complete\":true,\"enabled\":true}")); Assert.Equal(VerificationStatus.Confirmed,yes.Status); var no=await enable.EvaluateVerificationAsync(ToolArguments.Empty,ToolCallResult.Success("{\"complete\":true,\"enabled\":false}")); Assert.Equal(VerificationStatus.Refuted,no.Status); var malformed=await enable.EvaluateVerificationAsync(ToolArguments.Empty,ToolCallResult.Success("not-json")); Assert.NotEqual(VerificationStatus.Confirmed,malformed.Status); }
    [Theory] [InlineData(null,"all")] [InlineData("all","all")] [InlineData("task-scheduler","task-scheduler")] [InlineData("systemd-timer","systemd-timer")] [InlineData("cron","cron")]
    public void Source_Normalizes(string? input,string expected) { Assert.True(SchedulerSources.TryNormalize(input,out var actual)); Assert.Equal(expected,actual); }
    [Fact] public void Limits_RejectOutOfRange() { Assert.False(SchedulerArguments.TryList(ToolArguments.FromJson(new JsonObject{{"limit",0}}),out _,out _,out _)); Assert.False(SchedulerArguments.TryList(ToolArguments.FromJson(new JsonObject{{"limit",2001}}),out _,out _,out _)); Assert.False(SchedulerArguments.TryHistory(ToolArguments.FromJson(new JsonObject{{"id","x"},{"sinceMinutes",10081}}),out _,out _,out _,out _)); Assert.False(SchedulerArguments.TryHistory(ToolArguments.FromJson(new JsonObject{{"id","x"},{"limit",1001}}),out _,out _,out _,out _)); }
    [Fact] public void Cron_ParsesFormsAndPreservesInlineHash() { var r=CronParser.Parse("*/5 1 1,5 1-10 1-5/2 /bin/echo # data",CronSourceForm.UserCrontab); Assert.Single(r.Entries); Assert.Equal("*/5 1 1,5 1-10 1-5/2",r.Entries[0].Schedule); Assert.Contains("# data",r.Entries[0].Command); }
    [Theory] [InlineData("@yearly")] [InlineData("@annually")] [InlineData("@monthly")] [InlineData("@weekly")] [InlineData("@daily")] [InlineData("@midnight")] [InlineData("@hourly")] [InlineData("@reboot")]
    public void Cron_AliasesNormalize(string alias) { ArgumentNullException.ThrowIfNull(alias); var r=CronParser.Parse(alias.ToUpperInvariant()+" cmd",CronSourceForm.UserCrontab); Assert.Equal(alias,r.Entries[0].Schedule); }
    [Fact] public void Cron_IgnoresCommentsAndEnvironmentAndKeepsAdjacentEntry() { var r=CronParser.Parse("# c\nFOO=secret\n0 1 * * * echo ok\ninvalid",CronSourceForm.UserCrontab); Assert.Equal(2,r.Entries.Count); Assert.True(r.Entries[0].IsValid); Assert.Null(r.Entries[1].Schedule); Assert.False(r.Complete); }
    [Fact] public void Cron_IdIsCanonicalAndSensitiveToFields() { var a=CronStableId.Create("cron","/etc/crontab","root","0 1 * * *","echo a",1); var b=CronStableId.Create("cron","/etc/crontab","root","0 1 * * *","echo a",1); Assert.Equal(a,b); Assert.Matches("^cron:[0-9a-f]{64}$",a); Assert.NotEqual(a,CronStableId.Create("cron","/etc/crontab","root","0 1 * * *","echo b",1)); Assert.NotEqual(a,CronStableId.Create("cron|x","/etc:crontab","root","0 1 * * *","echo a",1)); }
    [Fact] public void Formatting_IsBoundedAndExplicit() { var item=new SchedulerListItem("id","n","cron",null,new string('x',2000),null,null,null,new string('x',2000),null); var json=SchedulerFormatting.FormatList(new SchedulerCollection<SchedulerListItem>([item,item]),1,1000); var o=JsonNode.Parse(json)!.AsObject(); Assert.True(o["truncated"]!.GetValue<bool>()); Assert.False(o["complete"]!.GetValue<bool>()); }
    private sealed class FakeEnable : SchedulerEnableToolBase { protected override Task<ToolCallResult> EnableAsync(string id,CancellationToken ct) => Task.FromResult(ToolCallResult.Success(null)); }
}
