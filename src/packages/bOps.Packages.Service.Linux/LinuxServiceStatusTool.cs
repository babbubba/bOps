// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Service.Core;

namespace bOps.Packages.Service.Linux;

/// <summary>Collects <c>service.status</c> data on Linux via <c>systemctl show --property=</c> (ADR-0021).</summary>
public sealed class LinuxServiceStatusTool() : ServiceStatusToolBase("linux")
{
    protected override async Task<ServiceStatusResult> CollectAsync(string name, CancellationToken ct)
    {
        // A name that cannot possibly be a systemd unit is reported as "not found" without ever
        // invoking systemctl — the same fact an operator asking about a typo'd name would get
        // either way (ADR-0021).
        if (!ServiceUnitName.IsPlausible(name))
        {
            return new ServiceStatusResult(name, Exists: false, "unknown", null);
        }

        var output = await SystemctlInvoker.RunAsync(
            ["show", name, "--property=LoadState,ActiveState,SubState,Description", "--no-pager"], ct);

        var properties = ParseProperties(output);
        if (!properties.TryGetValue("LoadState", out var loadState) || string.Equals(loadState, "not-found", StringComparison.Ordinal))
        {
            return new ServiceStatusResult(name, Exists: false, "unknown", null);
        }

        var activeState = properties.GetValueOrDefault("ActiveState", string.Empty);
        var description = properties.GetValueOrDefault("Description");
        return new ServiceStatusResult(
            name, Exists: true, NormalizeActiveState(activeState), string.IsNullOrEmpty(description) ? null : description);
    }

    /// <summary>Normalizes a systemd <c>ActiveState</c> value to the shared vocabulary (ADR-0021).</summary>
    internal static string NormalizeActiveState(string activeState) => activeState switch
    {
        "active" => "running",
        "inactive" => "stopped",
        "failed" => "failed",
        _ => "unknown",
    };

    private static Dictionary<string, string> ParseProperties(string output)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = line.IndexOf('=', StringComparison.Ordinal);
            if (separatorIndex <= 0)
            {
                continue;
            }

            result[line[..separatorIndex]] = line[(separatorIndex + 1)..];
        }

        return result;
    }
}
