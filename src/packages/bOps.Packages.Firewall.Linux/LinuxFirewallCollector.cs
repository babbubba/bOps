// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using bOps.Packages.Firewall.Core;
namespace bOps.Packages.Firewall.Linux;

/// <summary>Runs only the three fixed read-only firewall commands; no caller input reaches a process.</summary>
internal static class LinuxFirewallCollector
{
    internal static async Task<FirewallStatusSnapshot> StatusAsync(CancellationToken ct)
    {
        var snapshot=await EvidenceAsync(ct);
        var policies=snapshot.Rules.Where(r=>r.Name?.StartsWith("policy:",StringComparison.Ordinal)==true).ToArray();
        var inbound=policies.FirstOrDefault(r=>r.Direction=="inbound")?.Action;
        var outbound=policies.FirstOrDefault(r=>r.Direction=="outbound")?.Action;
        return new(null,inbound,outbound,[],snapshot.Backend,snapshot.Source,snapshot.Complete,snapshot.Warning);
    }
    internal static async Task<FirewallRulesSnapshot> RulesAsync(CancellationToken ct) => await EvidenceAsync(ct);
    private static async Task<FirewallRulesSnapshot> EvidenceAsync(CancellationToken ct)
    {
        var nft=await RunAsync("nft",["-j","list","ruleset"],ct); var ip4=await RunAsync("iptables-save",[],ct); var ip6=await RunAsync("ip6tables-save",[],ct);
        var nftComplete=false; var nftRules=nft is null?null:NftParser.Parse(nft,out nftComplete);
        var ipRules=new List<FirewallRule>(); var ipComplete=true;
        if(ip4 is not null) ipRules.AddRange(IptablesParser.Parse(ip4,"ipv4",out var complete4)); else ipComplete=false;
        if(ip6 is not null) ipRules.AddRange(IptablesParser.Parse(ip6,"ipv6",out var complete6)); else ipComplete=false;
        if(nftRules is not null && ipRules.Count>0) return new([..nftRules,..ipRules],"multiple","nft -j list ruleset; iptables-save; ip6tables-save",false,Warning:"nftables and iptables evidence may overlap; no backend was selected as authoritative.");
        if(nftRules is not null) return new(nftRules,"nftables","nft -j list ruleset",nftComplete);
        if(ipRules.Count>0) return new(ipRules,"iptables","iptables-save; ip6tables-save",ipComplete);
        return new([],"unavailable","nft -j list ruleset; iptables-save; ip6tables-save",false,Warning:"No supported local firewall backend could be read.");
    }
    private static async Task<string?> RunAsync(string fileName,string[] args,CancellationToken ct)
    {
        var psi=new ProcessStartInfo{FileName=fileName,RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false}; foreach(var arg in args)psi.ArgumentList.Add(arg);
        using var process=new Process{StartInfo=psi}; try {process.Start();} catch(Win32Exception){return null;}
        var output=await process.StandardOutput.ReadToEndAsync(ct); await process.WaitForExitAsync(ct);
        return process.ExitCode==0 && output.Length<=FirewallLimits.MaximumOutputBytes*4 ? output : null;
    }
}

internal static class NftParser
{
    internal static IReadOnlyList<FirewallRule> Parse(string json,out bool complete)
    {
        complete=true; var rules=new List<FirewallRule>();
        try { using var doc=JsonDocument.Parse(json); foreach(var item in doc.RootElement.GetProperty("nftables").EnumerateArray()) { if(item.TryGetProperty("chain",out var chain)) { var name=Get(chain,"name"); var hook=Get(chain,"hook"); var policy=Get(chain,"policy"); if(policy is not null)rules.Add(new("","policy:"+name,true,Hook(hook),Policy(policy),"any",[],[],[],[],[],null,null,null,null,"nftables")); } if(item.TryGetProperty("rule",out var rule))rules.Add(ParseRule(rule,ref complete)); } } catch(JsonException) { complete=false; } return rules;
    }
    private static FirewallRule ParseRule(JsonElement rule,ref bool complete)
    {
        var protocol="any";var action="unknown";var localA=new List<string>();var remoteA=new List<string>();var localP=new List<string>();var remoteP=new List<string>();var iface=(string?)null;var unknown=new List<string>();
        if(rule.TryGetProperty("expr",out var expr)) foreach(var e in expr.EnumerateArray())
        {
            if(e.TryGetProperty("accept",out _))action="allow"; else if(e.TryGetProperty("drop",out _ )||e.TryGetProperty("reject",out _))action="block";
            else if(e.TryGetProperty("match",out var match)) ParseMatch(match,ref protocol,localA,remoteA,localP,remoteP,ref iface,unknown); else unknown.Add(e.GetRawText());
        }
        var detail=unknown.Count==0?null:string.Join(";",unknown); if(detail is not null)complete=false;
        return new("",Get(rule,"comment"),true,"unknown",action,protocol,localA,remoteA,localP,remoteP,[],null,null,iface,Get(rule,"handle"),"nftables",detail,Bound(rule.GetRawText()));
    }
    private static void ParseMatch(JsonElement m,ref string protocol,List<string> la,List<string> ra,List<string> lp,List<string> rp,ref string? iface,List<string> unknown)
    {
        var left=m.TryGetProperty("left",out var l)?l.GetRawText():""; var right=m.TryGetProperty("right",out var r)?r.GetRawText():"";
        if(left.Contains("l4proto",StringComparison.Ordinal)){ var p=Scalar(right); protocol=p is "tcp" or "udp" or "icmp" or "icmpv6"?p:"native-unknown"; }
        else if(left.Contains("saddr",StringComparison.Ordinal)&&Simple(right))la.Add(Scalar(right)); else if(left.Contains("daddr",StringComparison.Ordinal)&&Simple(right))ra.Add(Scalar(right));
        else if(left.Contains("sport",StringComparison.Ordinal)&&Simple(right))lp.Add(Scalar(right)); else if(left.Contains("dport",StringComparison.Ordinal)&&Simple(right))rp.Add(Scalar(right));
        else if(left.Contains("iifname",StringComparison.Ordinal)&&Simple(right))iface=Scalar(right); else unknown.Add(m.GetRawText());
    }
    private static bool Simple(string s)=>!s.Contains('[')&&!s.Contains('{')&&!s.Contains("range",StringComparison.Ordinal)&&!s.Contains("set",StringComparison.Ordinal);
    private static string Scalar(string json)=>json.Trim('"').Replace("\"",""); private static string? Get(JsonElement e,string p)=>e.TryGetProperty(p,out var v)?v.ToString():null;
    private static string Hook(string? hook)=>hook is "input" or "prerouting"?"inbound":hook is "output" or "postrouting"?"outbound":"unknown"; private static string Policy(string p)=>p is "accept"?"allow":p is "drop" or "reject"?"block":"unknown"; private static string Bound(string s)=>s.Length<=FirewallLimits.MaximumNativeDetailCharacters?s:s[..FirewallLimits.MaximumNativeDetailCharacters];
}

internal static class IptablesParser
{
    internal static IReadOnlyList<FirewallRule> Parse(string text,string family,out bool complete)
    {
        complete=true;var rules=new List<FirewallRule>(); foreach(var line in text.Split('\n',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries)) { if(line.StartsWith(':')) { var p=line.Split(' ',StringSplitOptions.RemoveEmptyEntries); if(p.Length>1)rules.Add(new("","policy:"+p[0][1..],true,p[0] is ":INPUT"?"inbound":p[0] is ":OUTPUT"?"outbound":"unknown",p[1]=="ACCEPT"?"allow":p[1] is "DROP" or "REJECT"?"block":"unknown","any",[],[],[],[],[],null,null,null,null,"iptables-"+family)); } else if(line.StartsWith("-A "))rules.Add(ParseRule(line,family,ref complete)); } return rules;
    }
    private static FirewallRule ParseRule(string line,string family,ref bool complete)
    {
        var parts=line.Split(' ',StringSplitOptions.RemoveEmptyEntries);string chain=parts[1],protocol="any",action="unknown";var la=new List<string>();var ra=new List<string>();var lp=new List<string>();var rp=new List<string>();string? iface=null;var unknown=new List<string>();
        for(var i=2;i<parts.Length;i++){var x=parts[i]; if(x=="-p"&&++i<parts.Length)protocol=parts[i] is "tcp" or "udp" or "icmp" or "icmpv6"?parts[i]:"native-unknown"; else if(x=="-s"&&++i<parts.Length)la.Add(parts[i]);else if(x=="-d"&&++i<parts.Length)ra.Add(parts[i]);else if(x=="-i"&&++i<parts.Length)iface=parts[i];else if(x=="--dport"&&++i<parts.Length)rp.Add(parts[i]);else if(x=="--sport"&&++i<parts.Length)lp.Add(parts[i]);else if(x=="-j"&&++i<parts.Length)action=parts[i] is "ACCEPT"?"allow":parts[i] is "DROP" or "REJECT"?"block":"unknown";else if(x is "-m" or "--comment"){if(++i<parts.Length && x=="-m" && parts[i] is "tcp" or "udp"){} else unknown.Add(x+" "+parts[i]);} else unknown.Add(x);}
        var condition=unknown.Count==0?null:string.Join(' ',unknown);if(condition is not null)complete=false;return new("",chain,true,chain=="INPUT"?"inbound":chain=="OUTPUT"?"outbound":"unknown",action,protocol,la,ra,lp,rp,[],null,null,iface,null,"iptables-"+family,condition,line);
    }
}
