// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>
/// The node-local tool registry. Enforces, at registration time, that every non-<see cref="RiskLevel.Read"/>
/// tool declares a <see cref="VerificationSpec"/> and implements <see cref="IVerifiableTool"/>
/// (agentic/01-architecture-rules.md, rule B3) — this is what makes "every side-effecting
/// action is verified" a structural guarantee instead of a convention a package can skip.
/// </summary>
public sealed class ToolRegistry(ICapabilityProbe capabilityProbe) : IToolRegistry
{
    private readonly ConcurrentDictionary<string, RegisteredTool> _tools = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _packageEnabled = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _capabilitySnapshot = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public void Register(PackageId package, ITool tool) =>
        Register(package, PackageTrustLevel.Official, new EntitlementRequirement(EntitlementApplicability.NotGoverned), tool);

    /// <inheritdoc />
    public void Register(PackageId package, PackageTrustLevel trust, ITool tool) =>
        Register(package, trust, new EntitlementRequirement(EntitlementApplicability.NotGoverned), tool);

    /// <inheritdoc />
    public void Register(PackageId package, PackageTrustLevel trust, EntitlementRequirement entitlement, ITool tool)
    {
        ArgumentNullException.ThrowIfNull(entitlement);
        ArgumentNullException.ThrowIfNull(tool);

        var manifest = tool.Manifest;

        // ADR-0042 §5: the description of a runtime rejection is a reserved token, so no step description equal to it can
        // be a tool's. Without this a tool of that name would be indistinguishable from an unknown-tool rejection.
        if (string.Equals(manifest.Name, RuntimeStepTokens.Denied, StringComparison.Ordinal))
        {
            throw new ToolRegistrationException(manifest.Name,
                $"the name is reserved: the runtime writes '{RuntimeStepTokens.Denied}' as the description of every call it refuses.");
        }

        foreach (var parameter in manifest.Parameters)
        {
            ValidateConstraints(manifest.Name, parameter);
        }

        if (DescribeInvalidPrerequisites(manifest.Requires, manifest.OptionalRequires) is { } prerequisiteProblem)
        {
            throw new ToolRegistrationException(manifest.Name, prerequisiteProblem);
        }

        if (manifest.Risk != RiskLevel.Read)
        {
            if (manifest.Verification is null)
            {
                throw new ToolRegistrationException(manifest.Name,
                    $"risk is {manifest.Risk} but no VerificationSpec is declared. Every non-Read tool " +
                    "must declare how its effect is verified (agentic/01-architecture-rules.md, rule B3).");
            }

            if (tool is not IVerifiableTool)
            {
                throw new ToolRegistrationException(manifest.Name,
                    $"risk is {manifest.Risk} but the tool does not implement {nameof(IVerifiableTool)} " +
                    "(agentic/01-architecture-rules.md, rule B3).");
            }
        }

        // Rule A11: the package identity is assigned here, by the registry — never by the package itself.
        manifest.Package = package;

        var registered = new RegisteredTool(tool, package, trust, entitlement);
        if (!_tools.TryAdd(manifest.Name, registered))
        {
            throw new ToolRegistrationException(manifest.Name, "a tool with this name is already registered.");
        }

        _packageEnabled.TryAdd(package.Value, true);
    }

    private static void ValidateConstraints(string toolName, ToolParameter parameter)
    {
        if (ArgumentSchema.DescribeInvalidConstraint(parameter) is { } problem)
        {
            throw new ToolRegistrationException(toolName, $"Parameter '{parameter.Name}' ({parameter.Type}) declares invalid constraints: {problem}.");
        }
    }

    /// <summary>
    /// Describes why a component's prerequisite declarations are invalid, or returns <c>null</c>: an optional list that is
    /// missing or has a blank entry, or an id declared both required and optional (ADR-0049 section 3). Required entries
    /// keep their pre-ADR-0049 acceptance, so no existing manifest is newly refused.
    /// </summary>
    internal static string? DescribeInvalidPrerequisites(IReadOnlyList<string>? requires, IReadOnlyList<string>? optionalRequires)
    {
        if (requires is null || optionalRequires is null)
        {
            return "its prerequisite lists must not be null.";
        }

        if (optionalRequires.Any(string.IsNullOrWhiteSpace))
        {
            return "it declares a blank optional prerequisite.";
        }

        return optionalRequires.FirstOrDefault(id => requires.Contains(id, StringComparer.OrdinalIgnoreCase)) is { } both
            ? $"prerequisite '{both}' is declared both required and optional."
            : null;
    }

    /// <inheritdoc />
    /// <remarks>Optional prerequisites are probed too, so <see cref="GetReadiness"/> can report degraded tools; they never hide one.</remarks>
    public async Task RefreshCapabilitiesAsync(CancellationToken ct = default)
    {
        var capabilities = _tools.Values
            .SelectMany(registered => registered.Tool.Manifest.Requires.Concat(registered.Tool.Manifest.OptionalRequires))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var capability in capabilities)
        {
            _capabilitySnapshot[capability] = await capabilityProbe.IsAvailableAsync(capability, ct);
        }
    }

    /// <summary>
    /// Readiness of every registered tool on this node — package enabled and platform matched — whether or not it is
    /// currently available, as of the last <see cref="RefreshCapabilitiesAsync"/>, ordered by name (ADR-0049 section 1).
    /// </summary>
    public IReadOnlyList<ComponentReadiness> GetReadiness() =>
        _tools.Values
            .Where(IsRegisteredOnThisNode)
            .Select(registered => registered.Tool.Manifest)
            .OrderBy(manifest => manifest.Name, StringComparer.Ordinal)
            .Select(manifest => ComponentReadiness.Evaluate(
                ComponentReference.Tool(manifest.Name), manifest.Requires, manifest.OptionalRequires, IsSatisfied))
            .ToArray();

    /// <inheritdoc />
    public IReadOnlyList<ToolManifest> GetAvailableManifests() =>
        _tools.Values
            .Where(IsVisible)
            .Select(registered => registered.Tool.Manifest)
            .ToList();

    /// <inheritdoc />
    public ITool? Resolve(string toolName) =>
        _tools.TryGetValue(toolName, out var registered) && IsVisible(registered)
            ? registered.Tool
            : null;

    /// <inheritdoc />
    public ToolExecutionRegistration? ResolveForExecution(string toolName) =>
        _tools.TryGetValue(toolName, out var registered) && IsVisible(registered)
            ? new ToolExecutionRegistration(registered.Tool, registered.Package, registered.Trust, registered.Entitlement)
            : null;

    /// <inheritdoc />
    public PackageTrustLevel GetTrust(PackageId package) =>
        _tools.Values.FirstOrDefault(registered => registered.Package == package)?.Trust
        ?? PackageTrustLevel.Unverified;

    /// <inheritdoc />
    public void SetEnabled(PackageId package, bool enabled) => _packageEnabled[package.Value] = enabled;

    /// <inheritdoc />
    public void Unregister(PackageId package)
    {
        foreach (var (name, registered) in _tools)
        {
            if (registered.Package == package)
            {
                _tools.TryRemove(name, out _);
            }
        }

        _packageEnabled.TryRemove(package.Value, out _);
    }

    private bool IsVisible(RegisteredTool registered) =>
        IsRegisteredOnThisNode(registered) && registered.Tool.Manifest.Requires.All(IsSatisfied);

    private bool IsRegisteredOnThisNode(RegisteredTool registered) =>
        _packageEnabled.GetValueOrDefault(registered.Package.Value, true)
        && registered.Tool.Manifest.Platforms.Contains(CurrentPlatform.Id, StringComparer.OrdinalIgnoreCase);

    private bool IsSatisfied(string capability) => _capabilitySnapshot.GetValueOrDefault(capability, false);

    private sealed record RegisteredTool(
        ITool Tool,
        PackageId Package,
        PackageTrustLevel Trust,
        EntitlementRequirement Entitlement);
}
