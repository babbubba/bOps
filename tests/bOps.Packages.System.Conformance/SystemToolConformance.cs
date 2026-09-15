using System.Globalization;
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
    /// <summary>Checks that a manifest is well-formed for the given platform and tool name, and that a <c>system.*</c> tool is <see cref="RiskLevel.Read"/> with no verification to declare.</summary>
    public static void AssertManifestIsWellFormed(ToolManifest manifest, string expectedPlatform, string expectedName)
    {
        Assert.Equal(expectedName, manifest.Name);
        Assert.False(string.IsNullOrWhiteSpace(manifest.Description));
        Assert.Equal(RiskLevel.Read, manifest.Risk);
        Assert.Contains(expectedPlatform, manifest.Platforms);
        Assert.Null(manifest.Verification);
    }

    /// <summary>Runs <paramref name="tool"/> and asserts its <c>system.info</c> output shape.</summary>
    public static async Task AssertSystemInfoConformsAsync(ITool tool, string platform)
    {
        AssertManifestIsWellFormed(tool.Manifest, platform, "system.info");

        var result = await tool.ExecuteAsync(ToolArguments.Empty);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.NotNull(result.Output);
        Assert.Contains("OS:", result.Output, StringComparison.Ordinal);
        Assert.Contains("Host:", result.Output, StringComparison.Ordinal);
        Assert.Contains("Uptime:", result.Output, StringComparison.Ordinal);
    }

    /// <summary>Runs <paramref name="tool"/> and asserts its <c>system.cpu</c> output shape: a single percentage within 0–100.</summary>
    public static async Task AssertCpuUsageConformsAsync(ITool tool, string platform)
    {
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
        var result = await tool.ExecuteAsync(ToolArguments.FromJson(new System.Text.Json.Nodes.JsonObject { ["limit"] = 1 }));

        Assert.True(result.Succeeded, result.ErrorMessage);
        var lines = result.Output!.Split('\n');
        Assert.Equal(2, lines.Length); // header + exactly one process
    }

    [GeneratedRegex(@"^CPU usage: (?<percent>\d+(\.\d+)?)%$")]
    private static partial Regex CpuOutputPattern();

    [GeneratedRegex(@"^Memory: (?<used>\d+) MB used of (?<total>\d+) MB total \((?<percent>\d+(\.\d+)?)%\), (?<available>\d+) MB available\.$")]
    private static partial Regex MemoryOutputPattern();

    [GeneratedRegex(@"^(?<name>.+): (?<used>\d+) MB used of (?<total>\d+) MB total, (?<free>\d+) MB free\.$", RegexOptions.Multiline)]
    private static partial Regex DiskOutputLinePattern();
}
