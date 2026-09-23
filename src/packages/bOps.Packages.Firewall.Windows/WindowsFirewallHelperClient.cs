using System.Diagnostics;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using bOps.Packages.Firewall.Core;
namespace bOps.Packages.Firewall.Windows;
internal sealed class WindowsFirewallHelperClient
{
    internal const string HelperFileName="bOps.Packages.Firewall.Windows.Helper.exe";internal const int TimeoutSeconds=5;private const int MaxOut=1024*1024,MaxErr=64*1024;private static readonly JsonSerializerOptions JsonOptions=new(JsonSerializerDefaults.Web);internal static string HelperPath=>Path.Combine(AppContext.BaseDirectory,HelperFileName);
    internal static async Task<FirewallStatusSnapshot>StatusAsync(CancellationToken ct){var result=await InvokeAsync<FirewallStatusSnapshot>("status",ct).ConfigureAwait(false);return result.Response??new(null,null,null,[],"windows-firewall",result.Failure!,false);}
    internal static async Task<FirewallRulesSnapshot>RulesAsync(CancellationToken ct){var result=await InvokeAsync<FirewallRulesSnapshot>("rules",ct).ConfigureAwait(false);return result.Response??new([],"windows-firewall",result.Failure!,false);}
    private static async Task<(T? Response,string? Failure)> InvokeAsync<T>(string operation,CancellationToken ct) where T:class
    {if(operation is not("status" or "rules"))throw new ArgumentOutOfRangeException(nameof(operation));if(!File.Exists(HelperPath))return (null,"windows.firewall.helper-unavailable");using var p=new Process{StartInfo=new ProcessStartInfo(HelperPath){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true}};p.StartInfo.ArgumentList.Add(operation);try{if(!p.Start())return (null,"windows.firewall.helper-error");using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));var output=ReadBoundedAsync(p.StandardOutput,MaxOut,deadline.Token);var error=ReadBoundedAsync(p.StandardError,MaxErr,deadline.Token);try{await p.WaitForExitAsync(deadline.Token).ConfigureAwait(false);}catch(OperationCanceledException){KillAndWait(p);return (null,"windows.firewall.timeout");}var text=await output.ConfigureAwait(false);var diagnostic=await error.ConfigureAwait(false);if(text is null||diagnostic is null){KillAndWait(p);return (null,"windows.firewall.helper-invalid-response");}if(p.ExitCode!=0)return (null,"windows.firewall.helper-error");try{return (JsonSerializer.Deserialize<T>(text,JsonOptions),"windows.firewall.helper-invalid-response");}catch(JsonException){return (null,"windows.firewall.helper-invalid-response");}}catch(Win32Exception){return (null,"windows.firewall.helper-error");}}
    private static void KillAndWait(Process p){try{if(!p.HasExited)p.Kill(true);}catch(InvalidOperationException){}p.WaitForExit();}
    private static async Task<string?> ReadBoundedAsync(StreamReader reader,int maximumBytes,CancellationToken ct){var buffer=new char[4096];var output=new StringBuilder();while(true){var count=await reader.ReadAsync(buffer.AsMemory(),ct).ConfigureAwait(false);if(count==0)return output.ToString();output.Append(buffer,0,count);if(Encoding.UTF8.GetByteCount(output.ToString())>maximumBytes)return null;}}
}
