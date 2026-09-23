// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
using bOps.Abstractions;
using bOps.Packages.Firewall.Core;
namespace bOps.Packages.Firewall.Linux;
public sealed class LinuxFirewallToolProvider : IToolProvider
{
    public IEnumerable<ITool> GetTools() => [new Status(),new Rules(),new Inspect()];
    private sealed class Status():FirewallStatusToolBase("linux"){protected override Task<FirewallStatusSnapshot> CollectAsync(CancellationToken ct)=>LinuxFirewallCollector.StatusAsync(ct);}
    private sealed class Rules():FirewallRulesToolBase("linux"){protected override Task<FirewallRulesSnapshot> CollectAsync(CancellationToken ct)=>LinuxFirewallCollector.RulesAsync(ct);}
    private sealed class Inspect():FirewallInspectToolBase("linux"){protected override Task<FirewallRulesSnapshot> CollectAsync(CancellationToken ct)=>LinuxFirewallCollector.RulesAsync(ct);}
}
