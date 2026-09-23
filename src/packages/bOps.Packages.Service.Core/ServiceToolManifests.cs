// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Packages.Service.Core;

/// <summary>
/// The manifests for every <c>service.*</c> tool, shared between the Windows and Linux packages
/// (agentic/01-architecture-rules.md, rule A8).
/// </summary>
public static class ServiceToolManifests
{
    public static ToolManifest Config(string platform) => new()
    {
        Name = "service.config", Description = "Reports normalized service configuration.", Risk = RiskLevel.Read,
        Platforms = [platform], Requires = [], Parameters = [new ToolParameter("name", ToolParameterType.String, "The service name to inspect.")],
    };

    public static ToolManifest Dependencies(string platform) => new()
    {
        Name = "service.dependencies", Description = "Reports normalized service dependencies.", Risk = RiskLevel.Read,
        Platforms = [platform], Requires = [], Parameters = [
            new ToolParameter("name", ToolParameterType.String, "The service name to inspect."),
            new ToolParameter("direction", ToolParameterType.String, "both, requires, or dependents. Defaults to both.", Required: false),
            new ToolParameter("limit", ToolParameterType.Integer, "Maximum relations, from 1 to 1000. Defaults to 100.", Required: false),
        ],
    };

    public static ToolManifest Enable(string platform) => Mutation("service.enable", "Enables a service.", platform, "enable");
    public static ToolManifest Disable(string platform) => Mutation("service.disable", "Disables a service.", platform, "disable");

    private static ToolManifest Mutation(string name, string description, string platform, string action) => new()
    {
        Name = name, Description = description, Risk = RiskLevel.Medium, Platforms = [platform], Requires = [],
        Parameters = [new ToolParameter("name", ToolParameterType.String, $"The service name to {action}.")],
        Verification = new VerificationSpec("service.config", ["name"], $"Confirms the service is {action}d through service.config."),
    };

    /// <summary>The manifest for <c>service.list</c> on the given platform.</summary>
    public static ToolManifest List(string platform) => new()
    {
        Name = "service.list",
        Description = "Lists installed services with a normalized status (running, stopped, failed, or unknown).",
        Risk = RiskLevel.Read,
        Platforms = [platform],
        Requires = [],
        Parameters = [],
    };

    /// <summary>The manifest for <c>service.status</c> on the given platform.</summary>
    public static ToolManifest Status(string platform) => new()
    {
        Name = "service.status",
        Description = "Reports whether a named service exists and, if so, its normalized status and description, as single-line JSON.",
        Risk = RiskLevel.Read,
        Platforms = [platform],
        Requires = [],
        Parameters = [new ToolParameter("name", ToolParameterType.String, "The service name to inspect.")],
    };

    /// <summary>The manifest for <c>service.start</c> on the given platform.</summary>
    public static ToolManifest Start(string platform) => new()
    {
        Name = "service.start",
        Description = "Starts a stopped service.",
        Risk = RiskLevel.Medium,
        Platforms = [platform],
        Requires = [],
        Parameters = [new ToolParameter("name", ToolParameterType.String, "The service name to start.")],
        Verification = new VerificationSpec("service.status", ["name"], "Confirms the service's status is \"running\" afterward."),
    };

    /// <summary>The manifest for <c>service.stop</c> on the given platform.</summary>
    public static ToolManifest Stop(string platform) => new()
    {
        Name = "service.stop",
        Description = "Stops a running service.",
        Risk = RiskLevel.Medium,
        Platforms = [platform],
        Requires = [],
        Parameters = [new ToolParameter("name", ToolParameterType.String, "The service name to stop.")],
        Verification = new VerificationSpec("service.status", ["name"], "Confirms the service's status is \"stopped\" afterward."),
    };

    /// <summary>The manifest for <c>service.restart</c> on the given platform.</summary>
    public static ToolManifest Restart(string platform) => new()
    {
        Name = "service.restart",
        Description = "Restarts a service: stops it if running, then starts it.",
        Risk = RiskLevel.Medium,
        Platforms = [platform],
        Requires = [],
        Parameters = [new ToolParameter("name", ToolParameterType.String, "The service name to restart.")],
        Verification = new VerificationSpec("service.status", ["name"], "Confirms the service's status is \"running\" afterward."),
    };
}
