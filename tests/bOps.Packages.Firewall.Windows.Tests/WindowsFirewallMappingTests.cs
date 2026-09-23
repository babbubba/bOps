using bOps.Packages.Firewall.Windows;
using System.Diagnostics;
namespace bOps.Packages.Firewall.Windows.Tests;
public sealed class WindowsFirewallMappingTests
{
 [Fact] public void HelperPath_IsFixedUnderApplicationBaseDirectory()=>Assert.Equal(Path.Combine(AppContext.BaseDirectory,WindowsFirewallHelperClient.HelperFileName),WindowsFirewallHelperClient.HelperPath);
 [Fact] public async Task Timeout_KillsAndWaitsForTestChild(){var result=await WindowsFirewallHelperClient.RunTestChildAsync(Path.Combine(Environment.SystemDirectory,"cmd.exe"),["/c","ping 127.0.0.1 -n 30 > nul"],TimeSpan.FromMilliseconds(250),CancellationToken.None);Assert.True(result.TimedOut);Assert.Equal("windows.firewall.timeout",result.Source);Assert.True(result.Exited);Assert.False(ProcessExists(result.ProcessId));}
 [Fact] public async Task Cancellation_KillsAndWaitsForTestChild(){using var cancellation=new CancellationTokenSource(TimeSpan.FromMilliseconds(250));var result=await WindowsFirewallHelperClient.RunTestChildAsync(Path.Combine(Environment.SystemDirectory,"cmd.exe"),["/c","ping 127.0.0.1 -n 30 > nul"],TimeSpan.FromSeconds(5),cancellation.Token);Assert.True(result.TimedOut);Assert.Equal("windows.firewall.timeout",result.Source);Assert.True(result.Exited);Assert.False(ProcessExists(result.ProcessId));}
 [Theory][InlineData(1,"inbound")][InlineData(2,"outbound")][InlineData(99,"unknown")] public void DirectionMapping_IsSemantic(int value,string expected)=>Assert.Equal(expected,WindowsFirewallCollector.Direction(value));
 [Theory][InlineData(0,"allow")][InlineData(1,"block")][InlineData(2,"unknown")] public void ActionMapping_IsSemantic(int value,string expected)=>Assert.Equal(expected,WindowsFirewallCollector.Action(value));
 [Theory][InlineData(6,"tcp")][InlineData(17,"udp")][InlineData(1,"icmp")][InlineData(58,"icmp")][InlineData(256,"any")][InlineData(47,"native-unknown")] public void ProtocolMapping_IsSemantic(int value,string expected)=>Assert.Equal(expected,WindowsFirewallCollector.Protocol(value));
 private static bool ProcessExists(int id){try{using var process=Process.GetProcessById(id);return !process.HasExited;}catch(ArgumentException){return false;}}
}
