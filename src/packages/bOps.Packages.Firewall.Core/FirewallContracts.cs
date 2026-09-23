// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Packages.Firewall.Core;

public static class FirewallLimits
{
    public const int DefaultRows = 500;
    public const int MaximumRows = 5000;
    public const int DefaultOutputBytes = 65_536;
    public const int MaximumOutputBytes = 262_144;
    public const int MaximumNativeDetailCharacters = 4096;
}

public sealed record FirewallRule(
    string Id, string? Name, bool? Enabled, string Direction, string Action, string Protocol,
    IReadOnlyList<string> LocalAddresses, IReadOnlyList<string> RemoteAddresses,
    IReadOnlyList<string> LocalPorts, IReadOnlyList<string> RemotePorts, IReadOnlyList<string> Profiles,
    string? Application, string? Service, string? Interface, string? PriorityOrOrder, string Source,
    string? UnparsedCondition = null, string? NativeDetail = null);

public sealed record FirewallProfile(string Name, bool? Enabled, string? DefaultInbound, string? DefaultOutbound);
public sealed record FirewallStatusSnapshot(bool? Enabled, string? DefaultInbound, string? DefaultOutbound,
    IReadOnlyList<FirewallProfile> Profiles, string Backend, string Source, bool Complete, string? Warning = null);
public sealed record FirewallRulesSnapshot(IReadOnlyList<FirewallRule> Rules, string Backend, string Source, bool Complete,
    bool CollectionTruncated = false, string? Warning = null);

public static class FirewallManifests
{
    public static ToolManifest Status(string platform) => new()
    {
        Name = "firewall.status", Description = "Reports local firewall posture and evidence completeness; it never infers a remote firewall or packet decision.",
        Risk = RiskLevel.Read, Platforms = [platform], Requires = [], Parameters = [],
    };
    public static ToolManifest Rules(string platform) => new()
    {
        Name = "firewall.rules", Description = "Lists bounded, normalized local firewall-rule evidence. Complex native conditions are preserved and make completeness false.",
        Risk = RiskLevel.Read, Platforms = [platform], Requires = [], Parameters =
        [
            new("direction", ToolParameterType.Enum, "all, inbound, or outbound", Required:false, AllowedValues:["all", "inbound", "outbound"]),
            new("action", ToolParameterType.Enum, "all, allow, or block", Required:false, AllowedValues:["all", "allow", "block"]),
            new("protocol", ToolParameterType.Enum, "all, tcp, udp, icmp, or any", Required:false, AllowedValues:["all", "tcp", "udp", "icmp", "any"]),
            new("localPort", ToolParameterType.Integer, "Local port 1-65535", Required:false), new("remotePort", ToolParameterType.Integer, "Remote port 1-65535", Required:false),
            new("enabled", ToolParameterType.Boolean, "Rule enabled state", Required:false),
            new("limit", ToolParameterType.Integer, "Maximum rows (1-5000, default 500)", Required:false),
            new("maxOutputBytes", ToolParameterType.Integer, "Maximum JSON bytes (1-262144, default 65536)", Required:false),
        ],
    };
    public static ToolManifest Inspect(string platform) => new()
    {
        Name = "firewall.rule.inspect", Description = "Re-enumerates local firewall evidence and returns the rule with this observation id. An observation id is not a mutation token.",
        Risk = RiskLevel.Read, Platforms = [platform], Requires = [], Parameters = [new("id", ToolParameterType.String, "An id returned by firewall.rules.")],
    };
}

public static class FirewallArguments
{
    public static bool TryReadRules(ToolArguments arguments, out string direction, out string action, out string protocol, out int? localPort, out int? remotePort, out bool? enabled, out int limit, out int maxOutputBytes, out string? error)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        direction = "all"; action = "all"; protocol = "all"; localPort = remotePort = null; enabled = null; limit = FirewallLimits.DefaultRows; maxOutputBytes = FirewallLimits.DefaultOutputBytes;
        if (!Enum(arguments, "direction", ["all", "inbound", "outbound"], ref direction, out error) || !Enum(arguments, "action", ["all", "allow", "block"], ref action, out error) || !Enum(arguments, "protocol", ["all", "tcp", "udp", "icmp", "any"], ref protocol, out error) || !Number(arguments, "localPort", 1, 65535, out localPort, out error) || !Number(arguments, "remotePort", 1, 65535, out remotePort, out error) || !Number(arguments, "limit", 1, FirewallLimits.MaximumRows, FirewallLimits.DefaultRows, out limit, out error) || !Number(arguments, "maxOutputBytes", 1, FirewallLimits.MaximumOutputBytes, FirewallLimits.DefaultOutputBytes, out maxOutputBytes, out error)) return false;
        if (Present(arguments, "enabled")) { if (!arguments.TryGet<bool>("enabled", out var requestedEnabled)) { error = "enabled must be a boolean."; return false; } enabled = requestedEnabled; }
        error = null; return true;
    }
    public static bool TryReadId(ToolArguments arguments, out string id, out string? error)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!arguments.TryGet<string>("id", out id!) || string.IsNullOrWhiteSpace(id) || id.Length > 256) { id = string.Empty; error = "id is required and must be at most 256 characters."; return false; }
        error = null; return true;
    }
    private static bool Present(ToolArguments a, string n) => a.ToJson().ContainsKey(n);
    private static bool Enum(ToolArguments a, string n, string[] allowed, ref string value, out string? error) { if (!Present(a,n)) { error=null; return true; } if (!a.TryGet<string>(n,out var v) || v is null || !allowed.Contains(v,StringComparer.Ordinal)) { error=$"{n} must be one of: {string.Join(", ",allowed)}."; return false; } value=v; error=null; return true; }
    private static bool Number(ToolArguments a, string n, int min, int max, out int? value, out string? error) { value=null; if(!Present(a,n)){error=null;return true;} if(!a.TryGet<int>(n,out var v)||v<min||v>max){error=$"{n} must be between {min} and {max}.";return false;} value=v;error=null;return true; }
    private static bool Number(ToolArguments a, string n, int min, int max, int fallback, out int value, out string? error) { value=fallback; if(!Present(a,n)){error=null;return true;} if(!a.TryGet<int>(n,out var v)||v<min||v>max){error=$"{n} must be between {min} and {max}.";return false;} value=v;error=null;return true; }
}

public static class FirewallObservationId
{
    public static string Create(FirewallRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var evidence = string.Join("\n", rule.Source, rule.Name, rule.Direction, rule.Action, rule.Protocol, string.Join(",", rule.LocalAddresses.Order()), string.Join(",", rule.RemoteAddresses.Order()), string.Join(",", rule.LocalPorts.Order()), string.Join(",", rule.RemotePorts.Order()), string.Join(",", rule.Profiles.Order()), rule.Application, rule.Service, rule.Interface, rule.PriorityOrOrder, rule.UnparsedCondition);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(evidence))).ToLowerInvariant()[..32];
    }
}

public static class FirewallFormatting
{
    public static string Status(FirewallStatusSnapshot status) { ArgumentNullException.ThrowIfNull(status); return new JsonObject { ["schemaVersion"]=1,["enabled"]=status.Enabled,["defaultInbound"]=status.DefaultInbound,["defaultOutbound"]=status.DefaultOutbound,["profiles"]=new JsonArray(status.Profiles.Select(p=>(JsonNode)new JsonObject{{"name",p.Name},{"enabled",p.Enabled},{"defaultInbound",p.DefaultInbound},{"defaultOutbound",p.DefaultOutbound}}).ToArray()),["backend"]=status.Backend,["source"]=status.Source,["complete"]=status.Complete,["warning"]=Bound(status.Warning) }.ToJsonString(); }
    public static string Rules(FirewallRulesSnapshot snapshot, string direction, string action, string protocol, int? localPort, int? remotePort, bool? enabled, int limit, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var rules=snapshot.Rules.Select(WithId).Where(r => (direction=="all"||r.Direction==direction)&&(action=="all"||r.Action==action)&&(protocol=="all"||r.Protocol==protocol)&&(localPort is null||r.LocalPorts.Any(p=>PortMatches(p,localPort.Value)))&&(remotePort is null||r.RemotePorts.Any(p=>PortMatches(p,remotePort.Value)))&&(enabled is null||r.Enabled==enabled)).OrderBy(r=>r.Id,StringComparer.Ordinal).ToList();
        var take=Math.Min(limit,rules.Count); string output;
        do { output=Envelope(rules.Take(take),snapshot,rules.Count,take<rules.Count); take--; } while (Encoding.UTF8.GetByteCount(output)>maxBytes && take>=0);
        return output;
    }
    public static string Inspect(FirewallRulesSnapshot snapshot, string id, int maxBytes) { ArgumentNullException.ThrowIfNull(snapshot); var rule=snapshot.Rules.Select(WithId).SingleOrDefault(r=>r.Id==id); return rule is null ? new JsonObject{{"schemaVersion",1},{"found",false},{"id",id},{"complete",snapshot.Complete}}.ToJsonString() : Envelope([rule],snapshot,1,false, true); }
    private static FirewallRule WithId(FirewallRule r) => string.IsNullOrEmpty(r.Id) ? r with { Id=FirewallObservationId.Create(r) } : r;
    private static string Envelope(IEnumerable<FirewallRule> rules, FirewallRulesSnapshot s, int matched, bool truncated, bool inspect=false) { var selected=rules.ToArray(); return new JsonObject { ["schemaVersion"]=1,["found"]=inspect?true:null,["matchedRules"]=matched,["returnedRules"]=selected.Length,["truncated"]=truncated||s.CollectionTruncated,["backend"]=s.Backend,["source"]=s.Source,["complete"]=s.Complete&&!selected.Any(r=>r.UnparsedCondition is not null),["warning"]=Bound(s.Warning),["rules"]=new JsonArray(selected.Select(Row).ToArray()) }.ToJsonString(); }
    private static JsonNode Row(FirewallRule r) => new JsonObject{{"id",r.Id},{"name",Bound(r.Name)},{"enabled",r.Enabled},{"direction",r.Direction},{"action",r.Action},{"protocol",r.Protocol},{"localAddresses",new JsonArray(r.LocalAddresses.Select(x=>(JsonNode)x).ToArray())},{"remoteAddresses",new JsonArray(r.RemoteAddresses.Select(x=>(JsonNode)x).ToArray())},{"localPorts",new JsonArray(r.LocalPorts.Select(x=>(JsonNode)x).ToArray())},{"remotePorts",new JsonArray(r.RemotePorts.Select(x=>(JsonNode)x).ToArray())},{"profiles",new JsonArray(r.Profiles.Select(x=>(JsonNode)x).ToArray())},{"application",Bound(r.Application)},{"service",Bound(r.Service)},{"interface",Bound(r.Interface)},{"priorityOrOrder",Bound(r.PriorityOrOrder)},{"source",r.Source},{"unparsedCondition",Bound(r.UnparsedCondition)},{"nativeDetail",Bound(r.NativeDetail)}};
    private static string? Bound(string? s) => s is null ? null : s.Length<=FirewallLimits.MaximumNativeDetailCharacters?s:string.Concat(s.AsSpan(0,FirewallLimits.MaximumNativeDetailCharacters),"…");
    private static bool PortMatches(string expression,int port) => int.TryParse(expression,out var single) ? single==port : expression.Split('-') is [var lo,var hi]&&int.TryParse(lo,out var a)&&int.TryParse(hi,out var b)&&port>=a&&port<=b;
}

public abstract class FirewallStatusToolBase(string platform) : ITool { public ToolManifest Manifest { get; }=FirewallManifests.Status(platform); protected abstract Task<FirewallStatusSnapshot> CollectAsync(CancellationToken ct); public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments,CancellationToken ct=default) => ToolCallResult.Success(FirewallFormatting.Status(await CollectAsync(ct))); }
public abstract class FirewallRulesToolBase(string platform) : ITool { public ToolManifest Manifest { get; }=FirewallManifests.Rules(platform); protected abstract Task<FirewallRulesSnapshot> CollectAsync(CancellationToken ct); public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments,CancellationToken ct=default) { if(!FirewallArguments.TryReadRules(arguments,out var d,out var ac,out var p,out var lp,out var rp,out var e,out var l,out var b,out var err))return ToolCallResult.Failure(err!); return ToolCallResult.Success(FirewallFormatting.Rules(await CollectAsync(ct),d,ac,p,lp,rp,e,l,b)); } }
public abstract class FirewallInspectToolBase(string platform) : ITool { public ToolManifest Manifest { get; }=FirewallManifests.Inspect(platform); protected abstract Task<FirewallRulesSnapshot> CollectAsync(CancellationToken ct); public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments,CancellationToken ct=default) { if(!FirewallArguments.TryReadId(arguments,out var id,out var err))return ToolCallResult.Failure(err!); return ToolCallResult.Success(FirewallFormatting.Inspect(await CollectAsync(ct),id,FirewallLimits.MaximumOutputBytes)); } }
