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
