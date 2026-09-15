using System.Net;
using System.Net.Sockets;
using bOps.Abstractions;

namespace bOps.Packages.Network;

/// <summary>Resolves a hostname to its IP addresses. Read-risk.</summary>
public sealed class NetworkDnsTool : ITool
{
    public ToolManifest Manifest { get; } = new()
    {
        Name = "network.dns",
        Description = "Resolves a hostname to its IP addresses.",
        Risk = RiskLevel.Read,
        Platforms = ["windows", "linux"],
        Requires = [],
        Parameters = [new ToolParameter("host", ToolParameterType.String, "The hostname to resolve.")],
    };

    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var host = arguments.GetRequired<string>("host");

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, ct);
            return addresses.Length == 0
                ? ToolCallResult.Failure($"'{host}' resolved to no addresses.")
                : ToolCallResult.Success(string.Join('\n', addresses.Select(a => a.ToString())));
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return ToolCallResult.Failure($"Could not resolve '{host}': {ex.Message}");
        }
    }
}
