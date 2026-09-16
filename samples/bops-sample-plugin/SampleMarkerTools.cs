using System.Text.RegularExpressions;
using bOps.Abstractions;

namespace Acme.SamplePlugin;

internal static partial class SampleMarkerTools
{
    public static VerificationSpec Verification { get; } = new(
        "sample.marker.status", ["markerName"], "Confirms that the bounded sample marker exists.");

    public static string GetPath(string markerName) =>
        Path.Combine(Path.GetTempPath(), "bops-sample-skill", $"{markerName}.marker");

    public static bool IsValidMarkerName(string markerName) => MarkerNamePattern().IsMatch(markerName);

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex MarkerNamePattern();
}

/// <summary>Reads whether one bounded sample marker exists.</summary>
public sealed class SampleMarkerStatusTool : ITool
{
    public ToolManifest Manifest { get; } = new()
    {
        Name = "sample.marker.status",
        Description = "Reports whether a named demonstration marker exists in the bOps sample temp directory.",
        Risk = RiskLevel.Read,
        Platforms = ["windows", "linux"],
        Requires = [],
        Parameters = [new ToolParameter("markerName", ToolParameterType.String, "A simple marker identity; never a path.")],
    };

    public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var markerName = arguments.GetRequired<string>("markerName");
        return Task.FromResult(!SampleMarkerTools.IsValidMarkerName(markerName)
            ? ToolCallResult.Failure("markerName is invalid.")
            : ToolCallResult.Success(File.Exists(SampleMarkerTools.GetPath(markerName)) ? "present" : "absent"));
    }
}

/// <summary>Creates one bounded sample marker and evaluates its independent status verification.</summary>
public sealed class SampleMarkerCreateTool : IVerifiableTool
{
    public ToolManifest Manifest { get; } = new()
    {
        Name = "sample.marker.create",
        Description = "Creates one demonstration marker in the bOps sample temp directory.",
        Risk = RiskLevel.Low,
        Platforms = ["windows", "linux"],
        Requires = [],
        Parameters = [new ToolParameter("markerName", ToolParameterType.String, "A simple marker identity; never a path.")],
        Verification = SampleMarkerTools.Verification,
    };

    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var markerName = arguments.GetRequired<string>("markerName");
        if (!SampleMarkerTools.IsValidMarkerName(markerName))
        {
            return ToolCallResult.Failure("markerName is invalid.");
        }

        var path = SampleMarkerTools.GetPath(markerName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "bOps sample marker", ct);
        return ToolCallResult.Success("created");
    }

    public Task<VerificationOutcome> EvaluateVerificationAsync(
        ToolArguments originalArguments,
        ToolCallResult verificationToolResult,
        CancellationToken ct = default) =>
        Task.FromResult(verificationToolResult is { Outcome: ToolOutcome.Success, Output: "present" }
            ? new VerificationOutcome(VerificationStatus.Confirmed, "The sample marker exists.")
            : new VerificationOutcome(VerificationStatus.Refuted, "The sample marker was not observed."));
}
