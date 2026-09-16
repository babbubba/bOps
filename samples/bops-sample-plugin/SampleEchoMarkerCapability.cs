using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace Acme.SamplePlugin;

/// <summary>A harmless demonstration Skill that bases a typed marker plan on real echo evidence.</summary>
public sealed class SampleEchoMarkerCapability(TimeProvider timeProvider) : ICapability
{
    public CapabilityManifest Manifest { get; } = new(
        "sample.echo-marker",
        "1.0.0",
        "Collects echo evidence and prepares one bounded, verified marker-file action.",
        RiskLevel.Low,
        RequiredPermissions: ["sample.marker"],
        InputSchema:
        [
            new ToolParameter("message", ToolParameterType.String, "Text used as read-only evidence."),
            new ToolParameter("markerName", ToolParameterType.String, "A simple marker identity; never a path."),
        ],
        OutputSchema:
        [
            new ToolParameter("echo", ToolParameterType.String, "The observed normalized echo."),
            new ToolParameter("markerStatus", ToolParameterType.String, "Whether the marker exists after execution."),
        ],
        Timeout: TimeSpan.FromSeconds(10),
        SupportsDryRun: true,
        Verification: SampleMarkerTools.Verification,
        RollbackDescription: "Delete the demonstration marker from the bOps sample directory under the OS temporary directory.");

    public async Task<SkillReport> PrepareAsync(
        CapabilityRequest request,
        IToolInvoker toolInvoker,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(toolInvoker);

        var message = request.Input.GetRequired<string>("message");
        var markerName = request.Input.GetRequired<string>("markerName");
        if (!SampleMarkerTools.IsValidMarkerName(markerName))
        {
            throw new ArgumentException("markerName must contain 1-64 ASCII letters, digits, dots, hyphens or underscores.", nameof(request));
        }

        var echo = await toolInvoker.InvokeAsync(
            "sample.echo", ToolArguments.FromJson(new JsonObject { ["message"] = message }), ct);
        if (!echo.Succeeded)
        {
            throw new InvalidOperationException($"Could not collect sample echo evidence: {echo.ErrorMessage}");
        }

        var evidence = new Evidence(
            "sample-echo",
            EvidenceKind.Fact,
            "The sample echo tool normalized the supplied message.",
            echo.Output,
            "sample.echo",
            timeProvider.GetUtcNow());
        var finding = new Finding(
            "sample-marker-recommended",
            "The demonstration marker can be created from the observed sample input.",
            [evidence.Id],
            RiskLevel.Low);
        var plan = new ExecutionPlan(
            Manifest.Name,
            Manifest.Version,
            "Create one bounded demonstration marker and verify it independently.",
            [
                new ExecutionPlanStep(
                    0,
                    "sample.marker.create",
                    ToolArguments.FromJson(new JsonObject { ["markerName"] = markerName }),
                    "Create the bounded demonstration marker."),
            ]);

        return new SkillReport([evidence], [finding], plan);
    }
}
