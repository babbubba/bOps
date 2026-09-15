// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Packages.Service.Core;

/// <summary>The tool shell for <c>service.list</c>: manifest and result formatting shared, data collection left to the OS package (rule A8).</summary>
public abstract class ServiceListToolBase(string platform) : ITool
{
    /// <inheritdoc />
    public ToolManifest Manifest { get; } = ServiceToolManifests.List(platform);

    /// <summary>Collects every installed service and its normalized status.</summary>
    protected abstract Task<IReadOnlyList<ServiceSummary>> CollectAsync(CancellationToken ct);

    /// <inheritdoc />
    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) =>
        ToolCallResult.Success(ServiceToolFormatting.Format(await CollectAsync(ct)));
}

/// <summary>The tool shell for <c>service.status</c>.</summary>
public abstract class ServiceStatusToolBase(string platform) : ITool
{
    /// <inheritdoc />
    public ToolManifest Manifest { get; } = ServiceToolManifests.Status(platform);

    /// <summary>Inspects the single service identified by <paramref name="name"/>.</summary>
    protected abstract Task<ServiceStatusResult> CollectAsync(string name, CancellationToken ct);

    /// <inheritdoc />
    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var name = arguments.GetRequired<string>("name");
        return ToolCallResult.Success(ServiceToolFormatting.Format(await CollectAsync(name, ct)));
    }
}

/// <summary>
/// The tool shell for <c>service.start</c>, verified via <c>service.status</c> afterward. Both
/// platforms expect the same fact (status is <c>"running"</c>), so the verification evaluation is
/// shared here rather than duplicated per OS — mirrors <see cref="bOps.Packages.Sys.Core.ProcessStopToolBase"/>'s pattern.
/// </summary>
public abstract class ServiceStartToolBase(string platform) : IVerifiableTool
{
    /// <inheritdoc />
    public ToolManifest Manifest { get; } = ServiceToolManifests.Start(platform);

    /// <summary>Starts the service identified by <paramref name="name"/>.</summary>
    protected abstract Task<ToolCallResult> StartAsync(string name, CancellationToken ct);

    /// <inheritdoc />
    public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var name = arguments.GetRequired<string>("name");
        return StartAsync(name, ct);
    }

    /// <inheritdoc />
    public Task<VerificationOutcome> EvaluateVerificationAsync(
        ToolArguments originalArguments, ToolCallResult verificationToolResult, CancellationToken ct = default) =>
        EvaluateExpectedStatus(verificationToolResult, "running", "start");

    internal static Task<VerificationOutcome> EvaluateExpectedStatus(ToolCallResult verificationToolResult, string expectedStatus, string actionVerb)
    {
        ArgumentNullException.ThrowIfNull(verificationToolResult);

        if (!verificationToolResult.Succeeded)
        {
            return Task.FromResult(new VerificationOutcome(
                VerificationStatus.Inconclusive, $"Could not confirm the {actionVerb}: {verificationToolResult.ErrorMessage}"));
        }

        var status = ServiceStatusOutput.TryReadStatus(verificationToolResult.Output);
        return Task.FromResult(status switch
        {
            null => new VerificationOutcome(VerificationStatus.Inconclusive, "service.status's output could not be read."),
            _ when status == expectedStatus => new VerificationOutcome(VerificationStatus.Confirmed, null),
            _ => new VerificationOutcome(VerificationStatus.Refuted, $"service.status reports status \"{status}\", not \"{expectedStatus}\"."),
        });
    }
}

/// <summary>The tool shell for <c>service.stop</c>, verified via <c>service.status</c> afterward.</summary>
public abstract class ServiceStopToolBase(string platform) : IVerifiableTool
{
    /// <inheritdoc />
    public ToolManifest Manifest { get; } = ServiceToolManifests.Stop(platform);

    /// <summary>Stops the service identified by <paramref name="name"/>.</summary>
    protected abstract Task<ToolCallResult> StopAsync(string name, CancellationToken ct);

    /// <inheritdoc />
    public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var name = arguments.GetRequired<string>("name");
        return StopAsync(name, ct);
    }

    /// <inheritdoc />
    public Task<VerificationOutcome> EvaluateVerificationAsync(
        ToolArguments originalArguments, ToolCallResult verificationToolResult, CancellationToken ct = default) =>
        ServiceStartToolBase.EvaluateExpectedStatus(verificationToolResult, "stopped", "stop");
}

/// <summary>The tool shell for <c>service.restart</c>, verified via <c>service.status</c> afterward.</summary>
public abstract class ServiceRestartToolBase(string platform) : IVerifiableTool
{
    /// <inheritdoc />
    public ToolManifest Manifest { get; } = ServiceToolManifests.Restart(platform);

    /// <summary>Restarts the service identified by <paramref name="name"/>: stops it if running, then starts it.</summary>
    protected abstract Task<ToolCallResult> RestartAsync(string name, CancellationToken ct);

    /// <inheritdoc />
    public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var name = arguments.GetRequired<string>("name");
        return RestartAsync(name, ct);
    }

    /// <inheritdoc />
    public Task<VerificationOutcome> EvaluateVerificationAsync(
        ToolArguments originalArguments, ToolCallResult verificationToolResult, CancellationToken ct = default) =>
        ServiceStartToolBase.EvaluateExpectedStatus(verificationToolResult, "running", "restart");
}
