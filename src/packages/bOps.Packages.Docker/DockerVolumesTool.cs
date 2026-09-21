// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using Docker.DotNet;

namespace bOps.Packages.Docker;

/// <summary>
/// Lists the volumes on this machine as bounded, name-ordered JSON (ADR-0033). Read-risk. Mount points and driver options are
/// not returned.
/// </summary>
public sealed class DockerVolumesTool(IDockerClientFactory clientFactory) : ITool
{
    private const int DefaultLimit = 100;
    private const int MaximumLimit = 500;
    private const int DefaultOutputBytes = 32_768;
    private const int MinimumOutputBytes = 4_096;
    private const int MaximumOutputBytes = 65_536;

    public ToolManifest Manifest { get; } = new()
    {
        Name = "docker.volumes",
        Description = "Lists the Docker volumes on this machine: name, driver, scope, creation time, labels and, when the daemon reports it, size and reference count. " +
            "Sorted by name, bounded, with truncated=true when the list was cut.",
        Risk = RiskLevel.Read,
        Platforms = ["windows", "linux"],
        Requires = [DockerCapability.Name],
        Parameters =
        [
            new ToolParameter("limit", ToolParameterType.Integer, $"Volumes to return, 1 to {MaximumLimit}. Defaults to {DefaultLimit}.", Required: false),
            new ToolParameter("maxOutputBytes", ToolParameterType.Integer, $"Size of the result, {MinimumOutputBytes} to {MaximumOutputBytes}. Defaults to {DefaultOutputBytes}.", Required: false),
        ],
    };

    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!TryRead(arguments, "limit", DefaultLimit, 1, MaximumLimit, out var limit, out var error)
            || !TryRead(arguments, "maxOutputBytes", DefaultOutputBytes, MinimumOutputBytes, MaximumOutputBytes, out var maximumBytes, out error))
        {
            return ToolCallResult.Failure(error!);
        }

        using var client = clientFactory.Create();
        try
        {
            var response = await client.Volumes.ListAsync(ct);
            return ToolCallResult.Success(DockerVolumeOutput.List([.. response.Volumes ?? []], limit, maximumBytes));
        }
        catch (DockerApiException ex)
        {
            return ToolCallResult.Failure($"Could not list volumes: {DockerFailure.Describe(ex)}");
        }
        catch (Exception ex) when (DockerFailure.IsUnreachable(ex))
        {
            return ToolCallResult.Failure(DockerFailure.Unreachable(ex));
        }
    }

    private static bool TryRead(ToolArguments arguments, string name, int fallback, int minimum, int maximum, out int value, out string? error)
    {
        value = fallback;
        error = null;
        if (!arguments.ContainsKey(name))
        {
            return true;
        }

        if (!arguments.TryGet<int>(name, out value))
        {
            error = $"{name} must be an integer.";
            return false;
        }

        if (value < minimum || value > maximum)
        {
            error = $"{name} must be between {minimum} and {maximum}.";
            return false;
        }

        return true;
    }
}
