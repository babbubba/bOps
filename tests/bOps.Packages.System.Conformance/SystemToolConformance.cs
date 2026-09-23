// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bOps.Abstractions;
using Xunit;

namespace bOps.Packages.Sys.Conformance;

/// <summary>
/// Assertions any <c>system.*</c> / <c>process.list</c> tool implementation must satisfy,
/// regardless of which OS package produced it. Two OS packages contributing <c>system.cpu</c>
/// must produce the *same shape*, because the LLM reads that output
/// (agentic/01-architecture-rules.md, rule A8). Asserts structure and invariants, not values —
/// the result parses, required fields are present, percentages are within 0–100, memory figures
/// are self-consistent (agentic/04-testing-rules.md).
/// </summary>
public static partial class SystemToolConformance
{
    private static readonly string[] InventoryStatuses = ["complete", "partial", "unavailable"];
    private static readonly string[] MaintenanceStatuses = ["complete", "partial", "unavailable"];
    private static readonly string[] InventorySourceStatuses = ["available", "partial", "unavailable", "unsupported", "notApplicable"];

    /// <summary>Checks that a manifest is well-formed for the given platform and tool name, and that a <c>system.*</c> tool is <see cref="RiskLevel.Read"/> with no verification to declare.</summary>
    public static void AssertManifestIsWellFormed(ToolManifest manifest, string expectedPlatform, string expectedName)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        Assert.Equal(expectedName, manifest.Name);
        Assert.False(string.IsNullOrWhiteSpace(manifest.Description));
        Assert.Equal(RiskLevel.Read, manifest.Risk);
        Assert.Contains(expectedPlatform, manifest.Platforms);
        Assert.Null(manifest.Verification);
    }

    /// <summary>Checks the serialized shared maintenance evidence envelope, including unavailable and truncated semantics.</summary>
    public static JsonObject AssertMaintenanceEnvelope(string output, IReadOnlyList<string> rowFields, int maximumRows)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(rowFields);
        var root = JsonNode.Parse(output)!.AsObject();
        Assert.Equal(1, root["schemaVersion"]!.GetValue<int>());
        var status = root["status"]!.GetValue<string>();
        Assert.Contains(status, MaintenanceStatuses);
        var complete = root["complete"]!.GetValue<bool>();
        var truncated = root["truncated"]!.GetValue<bool>();
        Assert.False(truncated && complete);
        Assert.False(status == "unavailable" && complete);
        Assert.InRange(root["returnedItems"]!.GetValue<int>(), 0, maximumRows);
        Assert.True(root["observedItems"]!.GetValue<int>() >= root["returnedItems"]!.GetValue<int>());
        Assert.NotNull(root["sources"]);
        Assert.NotNull(root["warnings"]);
        Assert.True(root["warnings"]!.AsArray().Count <= 32);
        foreach (var item in root["items"]!.AsArray())
            Assert.Equal(rowFields, item!.AsObject().Select(pair => pair.Key).ToArray());
        return root;
    }

    /// <summary>Runs <paramref name="tool"/> and asserts its <c>system.info</c> output shape.</summary>
    public static async Task AssertSystemInfoConformsAsync(ITool tool, string platform)
    {
        ArgumentNullException.ThrowIfNull(tool);

        AssertManifestIsWellFormed(tool.Manifest, platform, "system.info");

        var result = await tool.ExecuteAsync(ToolArguments.Empty);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.NotNull(result.Output);
        Assert.Contains("OS:", result.Output, StringComparison.Ordinal);
        Assert.Contains("Host:", result.Output, StringComparison.Ordinal);
        Assert.Contains("Uptime:", result.Output, StringComparison.Ordinal);
        Assert.Contains("Hardware model:", result.Output, StringComparison.Ordinal);
    }

    /// <summary>Runs <paramref name="tool"/> and asserts the bounded cross-platform <c>system.apps</c> JSON shape.</summary>
    public static async Task AssertApplicationsConformAsync(ITool tool, string platform)
    {
        ArgumentNullException.ThrowIfNull(tool);
        AssertManifestIsWellFormed(tool.Manifest, platform, "system.apps");
        AssertInventoryParameters(tool.Manifest);

        var result = await tool.ExecuteAsync(ToolArguments.FromJson(new JsonObject
        {
            ["limit"] = 5,
            ["maxOutputBytes"] = 4_096,
        }));

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.True(Encoding.UTF8.GetByteCount(result.Output!) <= 4_096);
        var root = AssertInventoryEnvelope(result.Output!, maximumItems: 5);
        foreach (var item in root["items"]!.AsArray())
        {
            var app = item!.AsObject();
            Assert.False(string.IsNullOrWhiteSpace(app["identity"]!.GetValue<string>()));
            Assert.False(string.IsNullOrWhiteSpace(app["name"]!.GetValue<string>()));
            Assert.False(string.IsNullOrWhiteSpace(app["source"]!.GetValue<string>()));
            Assert.True(app.ContainsKey("version"));
            Assert.True(app.ContainsKey("publisher"));
        }
    }

    /// <summary>Runs <paramref name="tool"/> and asserts the bounded cross-platform <c>system.devices</c> JSON shape.</summary>
    public static async Task AssertDevicesConformAsync(ITool tool, string platform)
    {
        ArgumentNullException.ThrowIfNull(tool);
        AssertManifestIsWellFormed(tool.Manifest, platform, "system.devices");
        AssertInventoryParameters(tool.Manifest);

        var result = await tool.ExecuteAsync(ToolArguments.FromJson(new JsonObject
        {
            ["limit"] = 5,
            ["maxOutputBytes"] = 4_096,
        }));

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.True(Encoding.UTF8.GetByteCount(result.Output!) <= 4_096);
        var root = AssertInventoryEnvelope(result.Output!, maximumItems: 5);
        foreach (var item in root["items"]!.AsArray())
        {
            var device = item!.AsObject();
            Assert.False(string.IsNullOrWhiteSpace(device["identity"]!.GetValue<string>()));
            Assert.False(string.IsNullOrWhiteSpace(device["category"]!.GetValue<string>()));
            Assert.False(string.IsNullOrWhiteSpace(device["name"]!.GetValue<string>()));
            Assert.False(string.IsNullOrWhiteSpace(device["source"]!.GetValue<string>()));
            Assert.True(device.ContainsKey("vendor"));
            Assert.True(device.ContainsKey("model"));
            Assert.True(device.ContainsKey("status"));
        }
    }

    /// <summary>Runs <paramref name="tool"/> and asserts its <c>system.cpu</c> output shape: a single percentage within 0–100.</summary>
    public static async Task AssertCpuUsageConformsAsync(ITool tool, string platform)
    {
        ArgumentNullException.ThrowIfNull(tool);

        AssertManifestIsWellFormed(tool.Manifest, platform, "system.cpu");

        var result = await tool.ExecuteAsync(ToolArguments.Empty);

        Assert.True(result.Succeeded, result.ErrorMessage);
        var match = CpuOutputPattern().Match(result.Output ?? string.Empty);
        Assert.True(match.Success, $"'{result.Output}' did not match the expected system.cpu output shape.");

        var percent = double.Parse(match.Groups["percent"].Value, CultureInfo.InvariantCulture);
        Assert.InRange(percent, 0, 100);
    }

    /// <summary>Runs <paramref name="tool"/> and asserts its <c>system.memory</c> output shape: total, used and available are self-consistent.</summary>
    public static async Task AssertMemoryUsageConformsAsync(ITool tool, string platform)
    {
        ArgumentNullException.ThrowIfNull(tool);

        AssertManifestIsWellFormed(tool.Manifest, platform, "system.memory");

        var result = await tool.ExecuteAsync(ToolArguments.Empty);

        Assert.True(result.Succeeded, result.ErrorMessage);
        var match = MemoryOutputPattern().Match(result.Output ?? string.Empty);
        Assert.True(match.Success, $"'{result.Output}' did not match the expected system.memory output shape.");

        var used = long.Parse(match.Groups["used"].Value, CultureInfo.InvariantCulture);
        var total = long.Parse(match.Groups["total"].Value, CultureInfo.InvariantCulture);
        var available = long.Parse(match.Groups["available"].Value, CultureInfo.InvariantCulture);

        Assert.True(total > 0, "Total memory must be positive.");
        Assert.True(used >= 0, "Used memory must not be negative.");
        Assert.True(available >= 0, "Available memory must not be negative.");
        Assert.Equal(total, used + available);
    }

    /// <summary>Runs <paramref name="tool"/> and asserts its <c>system.disk</c> output shape: every reported volume has consistent totals.</summary>
    public static async Task AssertDiskUsageConformsAsync(ITool tool, string platform)
    {
        ArgumentNullException.ThrowIfNull(tool);

        AssertManifestIsWellFormed(tool.Manifest, platform, "system.disk");

        var result = await tool.ExecuteAsync(ToolArguments.Empty);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.NotNull(result.Output);
        // Every machine this suite runs on has at least one ready, mounted volume.
        Assert.NotEqual("No ready volumes found.", result.Output);

        var matches = DiskOutputLinePattern().Matches(result.Output!);
        Assert.NotEmpty(matches);

        foreach (Match match in matches)
        {
            var total = long.Parse(match.Groups["total"].Value, CultureInfo.InvariantCulture);
            var free = long.Parse(match.Groups["free"].Value, CultureInfo.InvariantCulture);
            Assert.True(total >= 0, "Total disk space must not be negative.");
            Assert.True(free >= 0, "Free disk space must not be negative.");
            Assert.True(total >= free, "Free disk space cannot exceed total disk space.");
        }
    }

    /// <summary>Runs <paramref name="tool"/> and asserts its <c>process.list</c> output shape: a header row, then well-formed PID/name/working-set rows.</summary>
    public static async Task AssertProcessListConformsAsync(ITool tool, string platform)
    {
        ArgumentNullException.ThrowIfNull(tool);

        AssertManifestIsWellFormed(tool.Manifest, platform, "process.list");
        Assert.Contains(tool.Manifest.Parameters, p => p.Name == "limit" && !p.Required);

        var result = await tool.ExecuteAsync(ToolArguments.Empty);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.NotNull(result.Output);
        var lines = result.Output!.Split('\n');
        Assert.True(lines.Length > 1, "process.list must report a header row plus at least one process.");
        Assert.StartsWith("PID\tName\tWorkingSet", lines[0], StringComparison.Ordinal);

        foreach (var line in lines.Skip(1))
        {
            var parts = line.Split('\t');
            Assert.Equal(3, parts.Length);
            Assert.True(int.TryParse(parts[0], out _), $"'{parts[0]}' is not a valid PID.");
            Assert.False(string.IsNullOrEmpty(parts[1]), "Process name must not be empty.");
        }
    }

    /// <summary>Runs <paramref name="tool"/> respecting a <c>limit</c> argument and asserts <c>process.list</c> honors it.</summary>
    public static async Task AssertProcessListRespectsLimitAsync(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        var result = await tool.ExecuteAsync(ToolArguments.FromJson(new System.Text.Json.Nodes.JsonObject { ["limit"] = 1 }));

        Assert.True(result.Succeeded, result.ErrorMessage);
        var lines = result.Output!.Split('\n');
        Assert.Equal(2, lines.Length); // header + exactly one process
    }

    /// <summary>Runs <paramref name="tool"/> and asserts its <c>system.swap</c> output shape. Total may legitimately be 0 on a machine configured with no swap.</summary>
    public static async Task AssertSwapUsageConformsAsync(ITool tool, string platform)
    {
        ArgumentNullException.ThrowIfNull(tool);

        AssertManifestIsWellFormed(tool.Manifest, platform, "system.swap");

        var result = await tool.ExecuteAsync(ToolArguments.Empty);

        Assert.True(result.Succeeded, result.ErrorMessage);
        var match = SwapOutputPattern().Match(result.Output ?? string.Empty);
        Assert.True(match.Success, $"'{result.Output}' did not match the expected system.swap output shape.");

        var used = long.Parse(match.Groups["used"].Value, CultureInfo.InvariantCulture);
        var total = long.Parse(match.Groups["total"].Value, CultureInfo.InvariantCulture);

        Assert.True(total >= 0, "Total swap must not be negative.");
        Assert.True(used >= 0, "Used swap must not be negative.");
        Assert.True(used <= total, "Used swap cannot exceed total swap.");
    }

    /// <summary>Runs <paramref name="tool"/> and asserts its <c>system.io</c> output shape: every reported device has non-negative throughput.</summary>
    public static async Task AssertIoUsageConformsAsync(ITool tool, string platform)
    {
        ArgumentNullException.ThrowIfNull(tool);

        AssertManifestIsWellFormed(tool.Manifest, platform, "system.io");

        var result = await tool.ExecuteAsync(ToolArguments.Empty);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.NotNull(result.Output);

        if (result.Output == "No disk devices found.")
        {
            // A sandboxed CI container can legitimately expose zero block devices; the shape
            // check below has nothing to check in that case.
            return;
        }

        var matches = IoOutputLinePattern().Matches(result.Output!);
        Assert.NotEmpty(matches);

        foreach (Match match in matches)
        {
            var read = double.Parse(match.Groups["read"].Value, CultureInfo.InvariantCulture);
            var write = double.Parse(match.Groups["write"].Value, CultureInfo.InvariantCulture);
            Assert.True(read >= 0, "Read throughput must not be negative.");
            Assert.True(write >= 0, "Write throughput must not be negative.");
        }
    }

    /// <summary>Runs <paramref name="tool"/> against the calling process's own PID and asserts its <c>process.inspect</c> output shape reports it as existing.</summary>
    public static async Task AssertProcessInspectConformsAsync(ITool tool, string platform)
    {
        ArgumentNullException.ThrowIfNull(tool);

        AssertManifestIsWellFormed(tool.Manifest, platform, "process.inspect");
        Assert.Contains(tool.Manifest.Parameters, p => p.Name == "pid" && p.Required);

        var ownPid = Environment.ProcessId;
        var result = await tool.ExecuteAsync(ToolArguments.FromJson(new System.Text.Json.Nodes.JsonObject { ["pid"] = ownPid }));

        Assert.True(result.Succeeded, result.ErrorMessage);
        var json = System.Text.Json.Nodes.JsonNode.Parse(result.Output!)!;
        Assert.Equal(ownPid, json["pid"]!.GetValue<int>());
        Assert.True(json["exists"]!.GetValue<bool>());
        Assert.False(string.IsNullOrEmpty(json["name"]?.GetValue<string>()));
    }

    /// <summary>Runs <paramref name="tool"/> against a PID very unlikely to be running and asserts it reports a clean absence.</summary>
    public static async Task AssertProcessInspectReportsMissingAsync(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        // Not a real guarantee, but the same pragmatic trick network.dns's tests use for
        // "definitely not there": a PID this large is not a real running process on any
        // machine this suite runs on.
        const int veryUnlikelyPid = 2_000_000;
        var result = await tool.ExecuteAsync(ToolArguments.FromJson(new System.Text.Json.Nodes.JsonObject { ["pid"] = veryUnlikelyPid }));

        Assert.True(result.Succeeded, result.ErrorMessage);
        var json = System.Text.Json.Nodes.JsonNode.Parse(result.Output!)!;
        Assert.False(json["exists"]!.GetValue<bool>());
    }

    private static void AssertInventoryParameters(ToolManifest manifest)
    {
        Assert.Contains(manifest.Parameters, parameter => parameter is { Name: "limit", Required: false });
        Assert.Contains(manifest.Parameters, parameter => parameter is { Name: "maxOutputBytes", Required: false });
    }

    private static JsonObject AssertInventoryEnvelope(string output, int maximumItems)
    {
        var root = JsonNode.Parse(output)!.AsObject();
        Assert.Contains(root["status"]!.GetValue<string>(), InventoryStatuses);
        Assert.True(root["observedItems"]!.GetValue<int>() >= 0);
        Assert.InRange(root["returnedItems"]!.GetValue<int>(), 0, maximumItems);
        Assert.NotNull(root["truncated"]);

        var items = root["items"]!.AsArray();
        Assert.Equal(items.Count, root["returnedItems"]!.GetValue<int>());
        var sources = root["sources"]!.AsArray();
        Assert.NotEmpty(sources);
        foreach (var source in sources)
        {
            var sourceObject = source!.AsObject();
            Assert.False(string.IsNullOrWhiteSpace(sourceObject["name"]!.GetValue<string>()));
            Assert.Contains(sourceObject["status"]!.GetValue<string>(), InventorySourceStatuses);
            Assert.True(sourceObject.ContainsKey("detail"));
        }

        return root;
    }

    [GeneratedRegex(@"^CPU usage: (?<percent>\d+(\.\d+)?)%$")]
    private static partial Regex CpuOutputPattern();

    [GeneratedRegex(@"^Memory: (?<used>\d+) MB used of (?<total>\d+) MB total \((?<percent>\d+(\.\d+)?)%\), (?<available>\d+) MB available\.$")]
    private static partial Regex MemoryOutputPattern();

    [GeneratedRegex(@"^(?<name>.+): (?<used>\d+) MB used of (?<total>\d+) MB total, (?<free>\d+) MB free\.$", RegexOptions.Multiline)]
    private static partial Regex DiskOutputLinePattern();

    [GeneratedRegex(@"^Swap: (?<used>\d+) MB used of (?<total>\d+) MB total \((?<percent>\d+(\.\d+)?)%\)\.$")]
    private static partial Regex SwapOutputPattern();

    [GeneratedRegex(@"^(?<name>.+): read (?<read>\d+(\.\d+)?) KB/s, write (?<write>\d+(\.\d+)?) KB/s\.$", RegexOptions.Multiline)]
    private static partial Regex IoOutputLinePattern();
}
