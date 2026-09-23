using bOps.Packages.Firewall.Windows;
namespace bOps.Packages.Firewall.Windows.Tests;
public sealed class WindowsFirewallMappingTests
{
 [Theory][InlineData(1,"inbound")][InlineData(2,"outbound")][InlineData(99,"unknown")] public void DirectionMapping_IsSemantic(int value,string expected)=>Assert.Equal(expected,WindowsFirewallCollector.Direction(value));
 [Theory][InlineData(0,"allow")][InlineData(1,"block")][InlineData(2,"unknown")] public void ActionMapping_IsSemantic(int value,string expected)=>Assert.Equal(expected,WindowsFirewallCollector.Action(value));
 [Theory][InlineData(6,"tcp")][InlineData(17,"udp")][InlineData(1,"icmp")][InlineData(58,"icmp")][InlineData(256,"any")][InlineData(47,"native-unknown")] public void ProtocolMapping_IsSemantic(int value,string expected)=>Assert.Equal(expected,WindowsFirewallCollector.Protocol(value));
}
