// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
using System.Runtime.InteropServices;
using bOps.Packages.Firewall.Core;
namespace bOps.Packages.Firewall.Windows;

/// <summary>Uses the documented HNetCfg.FwPolicy2 COM policy API; no PowerShell, netsh, or registry scraping.</summary>
internal static class WindowsFirewallCollector
{
    private const int Domain=1, Private=2, Public=4;
    internal static Task<FirewallStatusSnapshot> StatusAsync(CancellationToken ct) => Task.FromResult(Status());
    internal static Task<FirewallRulesSnapshot> RulesAsync(CancellationToken ct) => Task.FromResult(Rules());
    private static FirewallStatusSnapshot Status()
    {
        if(!OperatingSystem.IsWindows())return new(null,null,null,[],"unavailable","HNetCfg.FwPolicy2",false,"Windows Firewall COM is unavailable on this platform.");
        object? policy=null;try { policy=Create(); dynamic p=policy; IReadOnlyList<FirewallProfile> profiles=[Profile(p,"domain",Domain),Profile(p,"private",Private),Profile(p,"public",Public)]; return new(profiles.All(x=>x.Enabled==true),null,null,profiles,"windows-firewall","HNetCfg.FwPolicy2",true); } catch(COMException ex){return new(null,null,null,[],"unavailable","HNetCfg.FwPolicy2",false,ex.Message);} finally{Release(policy);}
    }
    private static FirewallRulesSnapshot Rules()
    {
        if(!OperatingSystem.IsWindows())return new([],"unavailable","HNetCfg.FwPolicy2",false,Warning:"Windows Firewall COM is unavailable on this platform.");
        object? policy=null;var rules=new List<FirewallRule>();try { policy=Create(); dynamic p=policy; foreach(var item in p.Rules){object rule=item;try{rules.Add(Map(rule));}finally{Release(rule);}} return new(rules,"windows-firewall","HNetCfg.FwPolicy2",true); } catch(COMException ex){return new(rules,"windows-firewall","HNetCfg.FwPolicy2",false,Warning:ex.Message);} finally{Release(policy);}
    }
    private static object Create() => Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2") ?? throw new InvalidOperationException("HNetCfg.FwPolicy2 is not registered."))!;
    private static FirewallProfile Profile(dynamic p,string name,int mask) => new(name,(bool)p.get_FirewallEnabled(mask),Inbound((int)p.get_DefaultInboundAction(mask)),Inbound((int)p.get_DefaultOutboundAction(mask)));
    private static FirewallRule Map(object native)
    {
        dynamic r=native; var protocol=Protocol((int)r.Protocol);var profiles=Profiles((int)r.Profiles); var localAddresses=Split((string?)r.LocalAddresses);var remoteAddresses=Split((string?)r.RemoteAddresses);var localPorts=Split((string?)r.LocalPorts);var remotePorts=Split((string?)r.RemotePorts);var interfaces=Interface((object?)r.Interfaces);
        return new("",(string?)r.Name,(bool)r.Enabled,Direction((int)r.Direction),Action((int)r.Action),protocol,localAddresses,remoteAddresses,localPorts,remotePorts,profiles,(string?)r.ApplicationName,(string?)r.serviceName,interfaces,null,"windows-firewall");
    }
    private static string Direction(int v)=>v==1?"inbound":v==2?"outbound":"unknown"; private static string Action(int v)=>v==1?"block":v==0?"allow":"unknown"; private static string Inbound(int v)=>Action(v); private static string Protocol(int v)=>v==6?"tcp":v==17?"udp":v is 1 or 58?"icmp":v==256?"any":"native-unknown";
    private static List<string> Profiles(int mask){var x=new List<string>();if((mask&Domain)!=0)x.Add("domain");if((mask&Private)!=0)x.Add("private");if((mask&Public)!=0)x.Add("public");return x;}
    private static string[] Split(string? s)=>string.IsNullOrWhiteSpace(s)||s=="*"?[]:s.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
    private static string? Interface(object? value)=>value is Array a?string.Join(',',a.Cast<object?>().Select(x=>x?.ToString()).Where(x=>x is not null)):value?.ToString();
    private static void Release(object? o){if(o is not null&&Marshal.IsComObject(o))Marshal.FinalReleaseComObject(o);}
}
