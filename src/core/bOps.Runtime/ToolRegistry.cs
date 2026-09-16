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
    public void Register(PackageId package, ITool tool) => Register(package, PackageTrustLevel.Official, tool);

    /// <inheritdoc />
    public void Register(PackageId package, PackageTrustLevel trust, ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        var manifest = tool.Manifest;

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

        var registered = new RegisteredTool(tool, package, trust);
        if (!_tools.TryAdd(manifest.Name, registered))
        {
            throw new ToolRegistrationException(manifest.Name, "a tool with this name is already registered.");
        }

        _packageEnabled.TryAdd(package.Value, true);
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

    private sealed record RegisteredTool(ITool Tool, PackageId Package, PackageTrustLevel Trust);
}
