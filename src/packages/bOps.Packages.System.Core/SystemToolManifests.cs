using bOps.Abstractions;

namespace bOps.Packages.Sys.Core;

/// <summary>
/// The manifests for every <c>system.*</c> and <c>process.*</c> tool, shared between the
/// Windows and Linux packages (agentic/01-architecture-rules.md, rule A8) — each OS package
/// contributes the same manifest with its own platform id, so the LLM sees one consistent tool
/// shape regardless of which OS package actually answered.
/// </summary>
public static class SystemToolManifests
{
    /// <summary>The manifest for <c>system.info</c> on the given platform.</summary>
    public static ToolManifest Info(string platform) => new()
    {
        Name = "system.info",
        Description = "Reports basic information about this machine: OS description, hostname, and uptime.",
        Risk = RiskLevel.Read,
        Platforms = [platform],
        Requires = [],
        Parameters = [],
    };

    /// <summary>The manifest for <c>system.cpu</c> on the given platform.</summary>
    public static ToolManifest Cpu(string platform) => new()
    {
        Name = "system.cpu",
        Description = "Reports current CPU utilization as a percentage, sampled over a short interval.",
        Risk = RiskLevel.Read,
        Platforms = [platform],
        Requires = [],
        Parameters = [],
    };

    /// <summary>The manifest for <c>system.memory</c> on the given platform.</summary>
    public static ToolManifest Memory(string platform) => new()
    {
        Name = "system.memory",
        Description = "Reports total and available physical memory, in megabytes.",
        Risk = RiskLevel.Read,
        Platforms = [platform],
        Requires = [],
        Parameters = [],
    };

    /// <summary>The manifest for <c>system.disk</c> on the given platform.</summary>
    public static ToolManifest Disk(string platform) => new()
    {
        Name = "system.disk",
        Description = "Reports space usage for every ready, mounted volume, in megabytes.",
        Risk = RiskLevel.Read,
        Platforms = [platform],
        Requires = [],
        Parameters = [],
    };

    /// <summary>The manifest for <c>process.list</c> on the given platform.</summary>
    public static ToolManifest ProcessList(string platform) => new()
    {
        Name = "process.list",
        Description = "Lists running processes sorted by memory usage, with PID, name, and working set.",
        Risk = RiskLevel.Read,
        Platforms = [platform],
        Requires = [],
        Parameters =
        [
            new ToolParameter("limit", ToolParameterType.Integer, "Maximum number of processes to return.", Required: false),
        ],
    };
}
