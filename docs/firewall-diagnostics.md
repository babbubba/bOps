# Firewall diagnostics

V1.3-I adds read-only local evidence: `firewall.status`, `firewall.rules`, and
`firewall.rule.inspect`. The rule id is a deterministic observation id for a particular native
observation. It is **not** a mutation token and cannot be used to modify a firewall.

Use the evidence path `network.dns_query` → `network.routes` → `network.sockets` →
`firewall.status` / `firewall.rules` → `network.port_check`. These tools do not evaluate a
packet-filter program, infer a remote firewall, or turn an unavailable/partial backend into a
claim that the firewall is disabled. Complex native conditions are returned as bounded
`unparsedCondition` and make the observation incomplete.

On Windows the source is the `HNetCfg.FwPolicy2` COM API and profile evidence remains separate
for domain, private and public. On Linux the fixed direct sources are `nft -j list ruleset`,
`iptables-save`, and `ip6tables-save`; simultaneous nftables and iptables evidence is ambiguous.
