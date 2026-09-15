// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Abstractions;

/// <summary>
/// Discovers and resolves tools for the current node. Implementations filter
/// <see cref="GetAvailableManifests"/> by platform and by capability
/// (agentic/01-architecture-rules.md, rule B4), and are scoped per node — never a process-wide
/// singleton shared across nodes (rule A4) — even though V0.1 has exactly one node.
/// </summary>
public interface IToolRegistry
{
    /// <summary>
    /// Registers a tool contributed by <paramref name="package"/>. Throws
    /// <see cref="ToolRegistrationException"/> if the tool's risk is not
    /// <see cref="RiskLevel.Read"/> and it does not declare a <see cref="VerificationSpec"/> and
    /// implement <see cref="IVerifiableTool"/> (rule B3), or if the name is already registered.
    /// </summary>
    /// <param name="package">The package contributing the tool. Stamped onto the manifest — see rule A11.</param>
    /// <param name="tool">The tool to register.</param>
    void Register(PackageId package, ITool tool);

    /// <summary>
    /// Re-probes every capability required by a registered tool and updates the availability
    /// snapshot that <see cref="GetAvailableManifests"/> reads. <see cref="GetAvailableManifests"/>
    /// stays synchronous — the planner calls it every step — so this is the explicit,
    /// host-driven point where capability discovery actually happens. The host calls it once at
    /// startup, and on a timer once a package needs live discovery (rule B4).
    /// </summary>
    Task RefreshCapabilitiesAsync(CancellationToken ct = default);

    /// <summary>All manifests visible on this node: platform-matched and capability-satisfied, as of the last <see cref="RefreshCapabilitiesAsync"/>.</summary>
    IReadOnlyList<ToolManifest> GetAvailableManifests();

    /// <summary>Resolves a tool by name, or <c>null</c> if unknown or disabled.</summary>
    /// <param name="toolName">The tool's name, as it appears in its manifest.</param>
    ITool? Resolve(string toolName);

    /// <summary>Enables or disables every tool contributed by a package, without restarting bOps.</summary>
    /// <param name="package">The package whose tools to enable or disable.</param>
    /// <param name="enabled">Whether the package's tools should be visible and callable.</param>
    void SetEnabled(PackageId package, bool enabled);

    /// <summary>
    /// Permanently removes every tool contributed by a package, unlike <see cref="SetEnabled"/>
    /// which only hides them. A dynamically loaded package (V0.10, ADR-0020) needs this on
    /// disable or remove: while any reference to its tools remains, nothing releases the
    /// isolated <see cref="System.Runtime.Loader.AssemblyLoadContext"/> that loaded them, and a
    /// later re-enable of the same id would otherwise collide with a still-registered name.
    /// </summary>
    /// <param name="package">The package whose tools to remove entirely.</param>
    void Unregister(PackageId package);
}

/// <summary>
/// Probes whether a named capability is available on this node (for example, whether a Docker
/// daemon responds). Results should be cached with a short TTL, not resolved once at startup —
/// a daemon that starts later must become visible without a restart.
/// </summary>
public interface ICapabilityProbe
{
    /// <summary>Checks whether a capability is currently available.</summary>
    /// <param name="capability">The capability identifier, as named in <see cref="ToolManifest.Requires"/>.</param>
    /// <param name="ct">Cancelled if the probe should abandon its check.</param>
    Task<bool> IsAvailableAsync(string capability, CancellationToken ct = default);
}

/// <summary>
/// Thrown when a tool cannot be registered: a non-<see cref="RiskLevel.Read"/> tool without a
/// declared verification, a duplicate name, or a manifest that fails basic validation.
/// </summary>
public sealed class ToolRegistrationException : Exception
{
    /// <summary>Creates a tool registration exception for the given tool and reason.</summary>
    public ToolRegistrationException(string toolName, string reason)
        : base($"Cannot register tool '{toolName}': {reason}")
    {
        ToolName = toolName;
    }

    /// <summary>Creates a tool registration exception with no message. Prefer the overload that takes a tool name and reason — CA1032 requires this constructor to exist, not that it be used.</summary>
    public ToolRegistrationException()
    {
        ToolName = string.Empty;
    }

    /// <summary>Creates a tool registration exception with a plain message.</summary>
    public ToolRegistrationException(string message)
        : base(message)
    {
        ToolName = string.Empty;
    }

    /// <summary>Creates a tool registration exception wrapping an underlying failure.</summary>
    public ToolRegistrationException(string message, Exception innerException)
        : base(message, innerException)
    {
        ToolName = string.Empty;
    }

    /// <summary>The name of the tool that could not be registered.</summary>
    public string ToolName { get; }
}
