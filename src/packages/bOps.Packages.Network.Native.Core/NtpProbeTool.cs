// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Packages.Network.Native.Core;

/// <summary>
/// <c>network.ntp_probe</c> (V1.3-D, ADR-0035). Needs no OS-specific collection: it is a plain
/// UDP client (<see cref="SntpClient"/>). Both native packages contribute the same instance under
/// their own platform id.
/// </summary>
public sealed class NtpProbeTool(string platform) : ITool
{
    /// <inheritdoc />
    public ToolManifest Manifest { get; } = NetworkNativeManifests.NtpProbe(platform);

    /// <inheritdoc />
    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!NetworkNativeArguments.TryReadNtpProbe(arguments, out var host, out var timeoutMilliseconds, out var error))
        {
            return ToolCallResult.Failure(error!);
        }

        ct.ThrowIfCancellationRequested();
        var result = await SntpClient.ProbeAsync(host, timeoutMilliseconds, ct);
        return ToolCallResult.Success(NetworkNativeFormatting.Format(result));
    }
}
