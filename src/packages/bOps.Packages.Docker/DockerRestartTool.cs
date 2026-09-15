using bOps.Abstractions;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace bOps.Packages.Docker;

/// <summary>
/// Restarts a container. <see cref="RiskLevel.Medium"/> (piano-bops.md §11's own worked
/// verification example). Verified via <c>docker.inspect</c>, same predicate as
/// <see cref="DockerStartTool"/>: a restarted container should be <c>"running"</c> afterwards.
/// </summary>
public sealed class DockerRestartTool(IDockerClientFactory clientFactory) : IVerifiableTool
{
    public ToolManifest Manifest { get; } = new()
    {
        Name = "docker.restart",
        Description = "Restarts a container.",
        Risk = RiskLevel.Medium,
        Platforms = ["windows", "linux"],
        Requires = [DockerCapability.Name],
        Parameters = [new ToolParameter("container", ToolParameterType.String, "The container's name or id.")],
        Verification = new VerificationSpec(
            "docker.inspect", ["container"], "Confirms the container's status is \"running\" afterwards."),
    };

    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var container = arguments.GetRequired<string>("container");

        using var client = clientFactory.Create();

        try
        {
            await client.Containers.RestartContainerAsync(container, new ContainerRestartParameters(), ct);
            return ToolCallResult.Success($"Requested restart of container '{container}'.");
        }
        catch (DockerApiException ex)
        {
            return ToolCallResult.Failure($"Could not restart container '{container}': {ex.Message}");
        }
    }

    public Task<VerificationOutcome> EvaluateVerificationAsync(
        ToolArguments originalArguments, ToolCallResult verificationToolResult, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(verificationToolResult);

        if (!verificationToolResult.Succeeded)
        {
            return Task.FromResult(new VerificationOutcome(
                VerificationStatus.Inconclusive, $"Could not confirm the restart: {verificationToolResult.ErrorMessage}"));
        }

        var status = DockerInspectOutput.TryReadStatus(verificationToolResult.Output);
        return Task.FromResult(status switch
        {
            null => new VerificationOutcome(VerificationStatus.Inconclusive, "docker.inspect's output could not be read."),
            "running" => new VerificationOutcome(VerificationStatus.Confirmed, null),
            _ => new VerificationOutcome(VerificationStatus.Refuted, $"docker.inspect reports status \"{status}\", not \"running\"."),
        });
    }
}
