// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

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

    /// <summary>The manifest for <c>system.swap</c> on the given platform.</summary>
    public static ToolManifest Swap(string platform) => new()
    {
        Name = "system.swap",
        Description = "Reports total and used swap (paging file) space, in megabytes.",
        Risk = RiskLevel.Read,
        Platforms = [platform],
        Requires = [],
        Parameters = [],
    };

    /// <summary>The manifest for <c>system.io</c> on the given platform.</summary>
    public static ToolManifest Io(string platform) => new()
    {
        Name = "system.io",
        Description = "Reports disk I/O throughput (read and write, in KB/s) for every device, sampled over a short interval.",
        Risk = RiskLevel.Read,
        Platforms = [platform],
        Requires = [],
        Parameters = [],
    };

    /// <summary>The manifest for <c>process.inspect</c> on the given platform.</summary>
    public static ToolManifest ProcessInspect(string platform) => new()
    {
        Name = "process.inspect",
        Description = "Reports whether a process with the given PID exists and, if so, its name, working set, thread count, and start time (UTC), as single-line JSON.",
        Risk = RiskLevel.Read,
        Platforms = [platform],
        Requires = [],
        Parameters = [new ToolParameter("pid", ToolParameterType.Integer, "The process ID to inspect.")],
    };

    /// <summary>The manifest for <c>process.stop</c> on the given platform.</summary>
    public static ToolManifest ProcessStop(string platform) => new()
    {
        Name = "process.stop",
        Description = "Requests a process stop gracefully (a close/terminate request, not forced). May fail if no graceful-stop mechanism is available for this process on this platform — see process.kill for a forced stop.",
        Risk = RiskLevel.Medium,
        Platforms = [platform],
        Requires = [],
        Parameters = [new ToolParameter("pid", ToolParameterType.Integer, "The process ID to stop.")],
        Verification = new VerificationSpec("process.inspect", ["pid"], "Confirms the process no longer exists afterward."),
    };

    /// <summary>The manifest for <c>process.kill</c> on the given platform.</summary>
    public static ToolManifest ProcessKill(string platform) => new()
    {
        Name = "process.kill",
        Description = "Forcibly terminates a process immediately (SIGKILL on Linux, TerminateProcess on Windows). Prefer process.stop first when a graceful stop is possible.",
        Risk = RiskLevel.High,
        Platforms = [platform],
        Requires = [],
        Parameters = [new ToolParameter("pid", ToolParameterType.Integer, "The process ID to terminate.")],
        Verification = new VerificationSpec("process.inspect", ["pid"], "Confirms the process no longer exists afterward."),
    };
}
