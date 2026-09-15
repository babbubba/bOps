// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;
using Xunit;

namespace bOps.Packages.Service.Conformance;

/// <summary>
/// Assertions any <c>service.*</c> tool implementation must satisfy, regardless of which OS
/// package produced it — mirrors <c>bOps.Packages.Sys.Conformance.SystemToolConformance</c>. Two
/// OS packages contributing <c>service.list</c> must produce the same normalized status
/// vocabulary (agentic/01-architecture-rules.md, rule A8; ADR-0021).
/// </summary>
public static class ServiceToolConformance
{
    private static readonly string[] NormalizedStatuses = ["running", "stopped", "failed", "unknown"];

    /// <summary>Checks that a manifest is well-formed for the given platform and tool name, and that a <c>service.*</c> tool is <see cref="RiskLevel.Read"/> with no verification to declare.</summary>
    public static void AssertManifestIsWellFormed(ToolManifest manifest, string expectedPlatform, string expectedName)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        Assert.Equal(expectedName, manifest.Name);
        Assert.False(string.IsNullOrWhiteSpace(manifest.Description));
        Assert.Equal(RiskLevel.Read, manifest.Risk);
        Assert.Contains(expectedPlatform, manifest.Platforms);
        Assert.Null(manifest.Verification);
    }

    /// <summary>Runs <paramref name="tool"/> and asserts its <c>service.list</c> output shape: a header row, then well-formed name/status rows with a normalized status.</summary>
    public static async Task AssertServiceListConformsAsync(ITool tool, string platform)
    {
        ArgumentNullException.ThrowIfNull(tool);

        AssertManifestIsWellFormed(tool.Manifest, platform, "service.list");

        var result = await tool.ExecuteAsync(ToolArguments.Empty);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.NotNull(result.Output);

        if (result.Output == "No services found.")
        {
            // A minimal or sandboxed host can legitimately have zero installed services; the
            // shape check below has nothing to check in that case.
            return;
        }

        var lines = result.Output!.Split('\n');
        Assert.True(lines.Length > 1, "service.list must report a header row plus at least one service.");
        Assert.StartsWith("Name\tStatus", lines[0], StringComparison.Ordinal);

        foreach (var line in lines.Skip(1))
        {
            var parts = line.Split('\t');
            Assert.Equal(2, parts.Length);
            Assert.False(string.IsNullOrEmpty(parts[0]), "Service name must not be empty.");
            Assert.Contains(parts[1], NormalizedStatuses);
        }
    }

    /// <summary>Runs <paramref name="tool"/> against a name very unlikely to be a real service and asserts it reports a clean absence.</summary>
    public static async Task AssertServiceStatusReportsMissingAsync(ITool tool, string platform, string missingName)
    {
        ArgumentNullException.ThrowIfNull(tool);

        AssertManifestIsWellFormed(tool.Manifest, platform, "service.status");
        Assert.Contains(tool.Manifest.Parameters, p => p.Name == "name" && p.Required);

        var result = await tool.ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["name"] = missingName }));

        Assert.True(result.Succeeded, result.ErrorMessage);
        var json = JsonNode.Parse(result.Output!)!;
        Assert.Equal(missingName, json["name"]!.GetValue<string>());
        Assert.False(json["exists"]!.GetValue<bool>());
    }

    /// <summary>Runs <paramref name="tool"/> against a service known to exist on the calling machine and asserts a well-formed, existing observation.</summary>
    public static async Task AssertServiceStatusReportsExistingAsync(ITool tool, string platform, string realServiceName)
    {
        ArgumentNullException.ThrowIfNull(tool);

        AssertManifestIsWellFormed(tool.Manifest, platform, "service.status");

        var result = await tool.ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["name"] = realServiceName }));

        Assert.True(result.Succeeded, result.ErrorMessage);
        var json = JsonNode.Parse(result.Output!)!;
        Assert.True(json["exists"]!.GetValue<bool>());
        Assert.Contains(json["status"]!.GetValue<string>(), NormalizedStatuses);
    }
}
