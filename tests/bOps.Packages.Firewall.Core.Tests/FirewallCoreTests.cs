using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Firewall.Core;
namespace bOps.Packages.Firewall.Core.Tests;
public sealed class FirewallCoreTests
{
    [Fact] public void Rules_DefaultsAndBounds_AreExplicit() { Assert.True(FirewallArguments.TryReadRules(ToolArguments.Empty,out _,out _,out _,out _,out _,out _,out var limit,out var bytes,out _)); Assert.Equal(500,limit);Assert.Equal(65536,bytes); Assert.False(FirewallArguments.TryReadRules(A(new(){["limit"]=5001}),out _,out _,out _,out _,out _,out _,out _,out _,out _)); Assert.False(FirewallArguments.TryReadRules(A(new(){["maxOutputBytes"]=262145}),out _,out _,out _,out _,out _,out _,out _,out _,out _)); }
    [Fact] public void ObservationId_IsOrderingStableAndEvidenceSensitive() { var rule=R(local:["2","1"]);Assert.Equal(FirewallObservationId.Create(rule),FirewallObservationId.Create(rule with{LocalPorts=["1","2"]}));Assert.NotEqual(FirewallObservationId.Create(rule),FirewallObservationId.Create(rule with{Action="block"})); }
    [Fact] public void Rules_UnknownCondition_ForcesIncompleteAndInspectRoundTrips() { var rule=R(condition:"meta mark");var snapshot=new FirewallRulesSnapshot([rule],"nftables","fixture",true);using var doc=JsonDocument.Parse(FirewallFormatting.Rules(snapshot,"all","all","all",null,null,null,500,65536));var row=doc.RootElement.GetProperty("rules")[0];Assert.False(doc.RootElement.GetProperty("complete").GetBoolean());var id=row.GetProperty("id").GetString()!;using var inspect=JsonDocument.Parse(FirewallFormatting.Inspect(snapshot,id,65536));Assert.True(inspect.RootElement.GetProperty("found").GetBoolean());Assert.Equal(id,inspect.RootElement.GetProperty("rules")[0].GetProperty("id").GetString()); }
    [Fact] public void Rules_OutputLimit_ReturnsValidBoundedJson() { var snapshot=new FirewallRulesSnapshot(Enumerable.Range(0,20).Select(i=>R(name:new string('x',1000)+i)).ToArray(),"fixture","fixture",true);var output=FirewallFormatting.Rules(snapshot,"all","all","all",null,null,null,500,1500);Assert.True(System.Text.Encoding.UTF8.GetByteCount(output)<=1500);using var _=JsonDocument.Parse(output); }
    private static ToolArguments A(JsonObject json)=>ToolArguments.FromJson(json); private static FirewallRule R(string? name="rule",string? condition=null,IReadOnlyList<string>? local=null)=>new("",name,true,"inbound","allow","tcp",[],[],local??["443"],[],[],null,null,null,null,"fixture",condition);
}
