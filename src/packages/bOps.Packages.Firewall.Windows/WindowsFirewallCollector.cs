// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
using bOps.Packages.Firewall.Core;
namespace bOps.Packages.Firewall.Windows;

internal static class WindowsFirewallCollector
{
    internal static Task<FirewallStatusSnapshot> StatusAsync(CancellationToken ct)=>WindowsFirewallHelperClient.StatusAsync(ct);
    internal static Task<FirewallRulesSnapshot> RulesAsync(CancellationToken ct)=>WindowsFirewallHelperClient.RulesAsync(ct);
    internal static string Direction(int v)=>v==1?"inbound":v==2?"outbound":"unknown"; internal static string Action(int v)=>v==1?"block":v==0?"allow":"unknown"; internal static string Protocol(int v)=>v==6?"tcp":v==17?"udp":v is 1 or 58?"icmp":v==256?"any":"native-unknown";
}
