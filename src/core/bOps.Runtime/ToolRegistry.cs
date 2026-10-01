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

        foreach (var parameter in manifest.Parameters)
        {
            ValidateConstraints(manifest.Name, parameter);
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
        if (DescribeInvalidConstraint(parameter) is { } problem)
        {
            throw new ToolRegistrationException(toolName, $"Parameter '{parameter.Name}' ({parameter.Type}) declares invalid constraints: {problem}.");
        }
    }

    /// <summary>ADR-0022: a constraint must fit the parameter type it is declared on and be consistent with its counterpart.</summary>
    private static string? DescribeInvalidConstraint(ToolParameter parameter)
    {
        if ((parameter.Minimum is not null || parameter.Maximum is not null) && parameter.Type is not (ToolParameterType.Integer or ToolParameterType.Number))
        {
            return "Minimum/Maximum apply only to Integer and Number";
        }

        if ((parameter.MinLength is not null || parameter.MaxLength is not null) && parameter.Type is not (ToolParameterType.String or ToolParameterType.Path))
        {
            return "MinLength/MaxLength apply only to String and Path";
        }

        if ((parameter.MinItems is not null || parameter.MaxItems is not null) && parameter.Type != ToolParameterType.PathList)
        {
            return "MinItems/MaxItems apply only to PathList";
        }

        if ((parameter.Minimum is { } minimum && !double.IsFinite(minimum)) || (parameter.Maximum is { } maximum && !double.IsFinite(maximum)))
        {
            return "Minimum and Maximum must be finite numbers";
        }

        if (parameter.MinLength < 0 || parameter.MaxLength < 0 || parameter.MinItems < 0 || parameter.MaxItems < 0)
        {
            return "lengths and item counts cannot be negative";
        }

        if (parameter.Minimum > parameter.Maximum || parameter.MinLength > parameter.MaxLength || parameter.MinItems > parameter.MaxItems)
        {
            return "a minimum exceeds its maximum";
        }

        return null;
    }

    /// <inheritdoc />
    public async Task RefreshCapabilitiesAsync(CancellationToken ct = default)
    {
        var requiredCapabilities = _tools.Values
            .SelectMany(registered => registered.Tool.Manifest.Requires)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var capability in requiredCapabilities)
        {
            _capabilitySnapshot[capability] = await capabilityProbe.IsAvailableAsync(capability, ct);
        }
    }

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

    private bool IsVisible(RegisteredTool registered)
    {
        var manifest = registered.Tool.Manifest;

        if (!_packageEnabled.GetValueOrDefault(registered.Package.Value, true))
        {
            return false;
        }

        if (!manifest.Platforms.Contains(CurrentPlatform.Id, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return manifest.Requires.Count == 0
            || manifest.Requires.All(capability => _capabilitySnapshot.GetValueOrDefault(capability, false));
    }

    private sealed record RegisteredTool(
        ITool Tool,
        PackageId Package,
        PackageTrustLevel Trust,
        EntitlementRequirement Entitlement);
}
