using Docker.DotNet;

namespace bOps.Packages.Docker;

/// <summary>
/// The capability name every <c>docker.*</c> manifest declares in <c>Requires</c>, and the check
/// the host registers against <c>ICapabilityProbe</c> (agentic/01-architecture-rules.md, rule B4)
/// — a daemon that is not running makes every <c>docker.*</c> tool disappear from
/// <see cref="bOps.Abstractions.IToolRegistry.GetAvailableManifests"/>, rather than failing only
/// once the model tries to call one.
/// </summary>
public static class DockerCapability
{
    /// <summary>The capability identifier: <c>"docker"</c>.</summary>
    public const string Name = "docker";

    /// <summary>Pings the daemon. Any failure — not running, wrong endpoint, no permission — means the capability is unavailable, never an exception the caller must handle.</summary>
    public static async Task<bool> IsAvailableAsync(IDockerClientFactory clientFactory, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);

        try
        {
            using var client = clientFactory.Create();
            await client.System.PingAsync(ct);
            return true;
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or TimeoutException or IOException)
        {
            return false;
        }
    }
}
