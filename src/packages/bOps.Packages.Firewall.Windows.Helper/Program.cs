using System.Text.Json;
using bOps.Packages.Firewall.Core;
namespace bOps.Packages.Firewall.Windows.Helper;
internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static int Main(string[] args)
    {
        if (args.Length != 1 || args[0] is not ("status" or "rules")) { Console.Error.WriteLine("Only status and rules operations are supported."); return 2; }
        Console.Out.Write(args[0] == "status" ? JsonSerializer.Serialize(WindowsFirewallComCollector.Status(), JsonOptions) : JsonSerializer.Serialize(WindowsFirewallComCollector.Rules(), JsonOptions));
        return 0;
    }
}
