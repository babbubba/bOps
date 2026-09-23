// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
using bOps.Abstractions;
using bOps.Packages.Firewall.Core;
namespace bOps.Packages.Firewall.Windows;
public sealed class WindowsFirewallToolProvider : IToolProvider
{
    public IEnumerable<ITool> GetTools()=>[new Status(),new Rules(),new Inspect()];
    private sealed class Status():FirewallStatusToolBase("windows"){protected override Task<FirewallStatusSnapshot> CollectAsync(CancellationToken ct)=>WindowsFirewallCollector.StatusAsync(ct);}
    private sealed class Rules():FirewallRulesToolBase("windows"){protected override Task<FirewallRulesSnapshot> CollectAsync(CancellationToken ct)=>WindowsFirewallCollector.RulesAsync(ct);}
    private sealed class Inspect():FirewallInspectToolBase("windows"){protected override Task<FirewallRulesSnapshot> CollectAsync(CancellationToken ct)=>WindowsFirewallCollector.RulesAsync(ct);}
}
