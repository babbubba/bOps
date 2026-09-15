using System.Text.Json.Nodes;
using Docker.DotNet.Models;

namespace bOps.Packages.Docker;

/// <summary>
/// The single-line JSON shape <c>docker.inspect</c> reports, and the shared reader
/// <see cref="DockerStartTool"/>, <see cref="DockerStopTool"/> and <see cref="DockerRestartTool"/>
/// use to evaluate their own verification: <c>docker.inspect</c> is each tool's declared
/// <see cref="bOps.Abstractions.VerificationSpec"/> target (agentic/01-architecture-rules.md, rule
/// B3), and the runtime hands its <see cref="bOps.Abstractions.ToolCallResult.Output"/> back as
/// plain text — JSON is what lets that text be read back reliably.
/// </summary>
internal static class DockerInspectOutput
{
    public static string Build(ContainerInspectResponse response)
    {
        var json = new JsonObject
        {
            ["id"] = response.ID,
            ["name"] = response.Name.TrimStart('/'),
            ["image"] = response.Config?.Image,
            ["status"] = response.State?.Status,
            ["running"] = response.State?.Running ?? false,
        };
        return json.ToJsonString();
    }

    /// <summary>
    /// Reads the container's status (<c>"running"</c>, <c>"exited"</c>, <c>"created"</c>, ...)
    /// from an inspect call's own output. <c>null</c> means the output could not be read as this
    /// shape — a verification tool that answered with something unparseable has confirmed nothing
    /// (rule S4), exactly as inconclusive as it not answering at all.
    /// </summary>
    public static string? TryReadStatus(string? output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(output) is JsonObject json && json["status"] is JsonNode statusNode
                ? statusNode.GetValue<string>()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
