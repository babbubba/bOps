using bOps.Packages.Firewall.Linux;
namespace bOps.Packages.Firewall.Linux.Tests;
public sealed class FirewallParserTests
{
    [Fact] public void Nft_UnknownExpression_IsIncomplete_NotIgnored(){var rules=NftParser.Parse("""{"nftables":[{"rule":{"family":"ip","table":"filter","chain":"input","expr":[{"match":{"left":{"payload":{"protocol":"ip","field":"saddr"}},"op":"==","right":"10.0.0.1"}},{"counter":null},{"accept":null}]}}]}""",out var complete);Assert.False(complete);Assert.Contains(rules,r=>r.UnparsedCondition is not null);}
    [Fact] public void Iptables_PoliciesAndUnknownExtension_AreHonest(){var rules=IptablesParser.Parse(":INPUT DROP [0:0]\n:OUTPUT ACCEPT [0:0]\n-A INPUT -p tcp --dport 443 -j ACCEPT\n-A INPUT -m conntrack --ctstate NEW -j ACCEPT","ipv4",out var complete);Assert.False(complete);Assert.Contains(rules,r=>r.Name=="policy:INPUT"&&r.Action=="block");Assert.Contains(rules,r=>r.RemotePorts.Contains("443"));}
    [Fact] public void Nft_MalformedJson_IsIncomplete(){var rules=NftParser.Parse("{",out var complete);Assert.False(complete);Assert.Empty(rules);}
}
