using System.Globalization;
using System.Net.NetworkInformation;
using bOps.Abstractions;

namespace bOps.Packages.Network;

/// <summary>Sends one ICMP echo request to a host and reports the reply status and round-trip time. Read-risk.</summary>
public sealed class NetworkPingTool : ITool
{
    private const int DefaultTimeoutMs = 4000;
    private const int MaxTimeoutMs = 10000;

    public ToolManifest Manifest { get; } = new()
    {
        Name = "network.ping",
        Description = "Sends one ICMP echo request to a host and reports whether it replied and the round-trip time.",
        Risk = RiskLevel.Read,
        Platforms = ["windows", "linux"],
        Requires = [],
        Parameters =
        [
            new ToolParameter("host", ToolParameterType.String, "The hostname or IP address to ping."),
            new ToolParameter("timeoutMs", ToolParameterType.Integer, "Milliseconds to wait for a reply. Defaults to 4000, capped at 10000.", Required: false),
        ],
    };

    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var host = arguments.GetRequired<string>("host");
        var requestedTimeout = arguments.TryGet<int>("timeoutMs", out var requested) && requested > 0 ? requested : DefaultTimeoutMs;
        var timeoutMs = Math.Min(requestedTimeout, MaxTimeoutMs);

        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, timeoutMs).WaitAsync(ct);

            return reply.Status == IPStatus.Success
                ? ToolCallResult.Success(string.Format(
                    CultureInfo.InvariantCulture, "{0} replied from {1} in {2} ms.", host, reply.Address, reply.RoundtripTime))
                : ToolCallResult.Failure($"{host} did not reply: {reply.Status}.");
        }
        catch (PingException ex)
        {
            return ToolCallResult.Failure($"Could not ping '{host}': {ex.Message}");
        }
    }
}
