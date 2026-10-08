// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>
/// Node-local registry for activated Skill providers. Registration is atomic and stamps package
/// identity before any Capability becomes visible (ADR-0025; rules A4 and A11).
/// </summary>
/// <remarks>
/// A Capability whose required prerequisites (<see cref="CapabilityManifest.Requires"/>) are not satisfied stays registered
/// but is withheld from <see cref="GetAvailableSkills"/> and <see cref="Resolve"/> (ADR-0049). Without a probe, such a
/// Capability fails closed; one that declares no prerequisite behaves exactly as before.
/// </remarks>
public sealed class SkillRegistry : ISkillRegistry
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, RegisteredSkill> _skills = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _capabilityOwners = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PrerequisiteState> _prerequisiteSnapshot = new(StringComparer.OrdinalIgnoreCase);
    private readonly ICapabilityProbe? _capabilityProbe;
    private readonly IPrerequisiteStateSource? _stateSource;

    /// <summary>Creates a registry with no prerequisite probe: Capabilities that declare required prerequisites stay unavailable.</summary>
    public SkillRegistry()
    {
    }

    /// <summary>Creates a registry whose Capability prerequisites are checked through <paramref name="capabilityProbe"/>.</summary>
    public SkillRegistry(ICapabilityProbe capabilityProbe)
    {
        ArgumentNullException.ThrowIfNull(capabilityProbe);
        _capabilityProbe = capabilityProbe;
    }

    /// <summary>Creates a registry that snapshots the full <see cref="PrerequisiteState"/> of each Capability prerequisite from the host-owned source.</summary>
    public SkillRegistry(IPrerequisiteStateSource stateSource)
    {
        ArgumentNullException.ThrowIfNull(stateSource);
        _stateSource = stateSource;
    }

    /// <inheritdoc />
    public void Register(PackageId package, ISkillProvider provider) =>
        Register(package, PackageTrustLevel.Official, provider);

    /// <inheritdoc />
    public void Register(PackageId package, PackageTrustLevel trust, ISkillProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        if (string.IsNullOrWhiteSpace(package.Value))
        {
            throw new SkillRegistrationException("The host-assigned package id cannot be blank.");
        }

        if (string.IsNullOrWhiteSpace(provider.SkillId))
        {
            throw new SkillRegistrationException("A Skill provider's SkillId cannot be blank.");
        }

        IReadOnlyList<ICapability> supplied;
        try
        {
            supplied = provider.GetCapabilities()
                ?? throw new SkillRegistrationException($"Skill '{provider.SkillId}' returned a null Capability collection.");
        }
        catch (SkillRegistrationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SkillRegistrationException(
                $"Skill '{provider.SkillId}' threw while declaring its Capabilities.", ex);
        }

        if (supplied.Count == 0)
        {
            throw new SkillRegistrationException($"Skill '{provider.SkillId}' must declare at least one Capability.");
        }

        var capabilities = supplied.ToArray();
        var localNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capability in capabilities)
        {
            if (capability is null)
            {
                throw new SkillRegistrationException($"Skill '{provider.SkillId}' returned a null Capability.");
            }

            ValidateManifest(provider.SkillId, capability.Manifest);
            if (!localNames.Add(capability.Manifest.Name))
            {
                throw new SkillRegistrationException(
                    $"Skill '{provider.SkillId}' declares Capability '{capability.Manifest.Name}' more than once.");
            }
        }

        lock (_gate)
        {
            if (_skills.ContainsKey(provider.SkillId))
            {
                throw new SkillRegistrationException($"A Skill with id '{provider.SkillId}' is already registered.");
            }

            foreach (var capability in capabilities)
            {
                if (_capabilityOwners.TryGetValue(capability.Manifest.Name, out var owner))
                {
                    throw new SkillRegistrationException(
                        $"Capability '{capability.Manifest.Name}' is already registered by Skill '{owner}'.");
                }
            }

            foreach (var capability in capabilities)
            {
                capability.Manifest.Package = package;
                _capabilityOwners.Add(capability.Manifest.Name, provider.SkillId);
            }

            _skills.Add(provider.SkillId, new RegisteredSkill(provider, package, trust, capabilities));
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<SkillDescriptor> GetAvailableSkills()
    {
        lock (_gate)
        {
            return _skills.Values
                .OrderBy(skill => skill.Provider.SkillId, StringComparer.Ordinal)
                .Select(skill => new SkillDescriptor(
                    skill.Provider.SkillId,
                    skill.Package,
                    skill.Trust,
                    skill.Capabilities.Select(capability => capability.Manifest)
                        .Where(IsAvailable)
                        .OrderBy(manifest => manifest.Name, StringComparer.Ordinal)
                        .ToArray()))
                .Where(descriptor => descriptor.Capabilities.Count > 0)
                .ToArray();
        }
    }

    /// <inheritdoc />
    public ICapability? Resolve(string skillId, string capabilityName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityName);

        lock (_gate)
        {
            return _skills.TryGetValue(skillId, out var skill)
                ? skill.Capabilities.FirstOrDefault(
                    capability => string.Equals(capability.Manifest.Name, capabilityName, StringComparison.Ordinal)
                        && IsAvailable(capability.Manifest))
                : null;
        }
    }

    /// <summary>
    /// Re-probes every prerequisite a registered Capability declares, required or optional, and updates the snapshot that
    /// <see cref="GetAvailableSkills"/>, <see cref="Resolve"/> and <see cref="GetReadiness"/> read. Without a probe it does nothing.
    /// </summary>
    public async Task RefreshPrerequisitesAsync(CancellationToken ct = default)
    {
        if (_capabilityProbe is null && _stateSource is null)
        {
            return;
        }

        string[] ids;
        lock (_gate)
        {
            ids = _skills.Values
                .SelectMany(skill => skill.Capabilities)
                .SelectMany(capability => capability.Manifest.Requires.Concat(capability.Manifest.OptionalRequires))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        foreach (var id in ids)
        {
            _prerequisiteSnapshot[id] = _stateSource is not null
                ? _stateSource.GetState(id)
                : await _capabilityProbe!.IsAvailableAsync(id, ct) ? PrerequisiteState.Available : PrerequisiteState.Unavailable;
        }
    }

    /// <summary>
    /// Readiness of every registered Capability, available or not, as of the last <see cref="RefreshPrerequisitesAsync"/>,
    /// ordered by Skill id and Capability name (ADR-0049 section 1).
    /// </summary>
    public IReadOnlyList<ComponentReadiness> GetReadiness()
    {
        lock (_gate)
        {
            return _skills.Values
                .OrderBy(skill => skill.Provider.SkillId, StringComparer.Ordinal)
                .SelectMany(skill => skill.Capabilities
                    .Select(capability => capability.Manifest)
                    .OrderBy(manifest => manifest.Name, StringComparer.Ordinal)
                    .Select(manifest => ComponentReadiness.Evaluate(
                        ComponentReference.SkillCapability(skill.Provider.SkillId, manifest.Name),
                        manifest.Requires,
                        manifest.OptionalRequires,
                        StateOf)))
                .ToArray();
        }
    }

    /// <inheritdoc />
    public PackageId GetPackage(string skillId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);
        lock (_gate)
        {
            return _skills.TryGetValue(skillId, out var skill) ? skill.Package : PackageId.Unknown;
        }
    }

    /// <inheritdoc />
    public PackageTrustLevel GetTrust(string skillId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);
        lock (_gate)
        {
            return _skills.TryGetValue(skillId, out var skill) ? skill.Trust : PackageTrustLevel.Unverified;
        }
    }

    /// <inheritdoc />
    public void Unregister(PackageId package)
    {
        lock (_gate)
        {
            var removed = _skills.Values
                .Where(skill => skill.Package == package)
                .ToArray();

            foreach (var skill in removed)
            {
                _skills.Remove(skill.Provider.SkillId);
                foreach (var capability in skill.Capabilities)
                {
                    _capabilityOwners.Remove(capability.Manifest.Name);
                }
            }
        }
    }

    private static void ValidateManifest(string skillId, CapabilityManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            throw new SkillRegistrationException($"Skill '{skillId}' declares a blank Capability name.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Version))
        {
            throw new SkillRegistrationException($"Capability '{manifest.Name}' declares a blank version.");
        }

        if (manifest.Timeout <= TimeSpan.Zero)
        {
            throw new SkillRegistrationException($"Capability '{manifest.Name}' must declare a positive timeout.");
        }

        if (ToolRegistry.DescribeInvalidPrerequisites(manifest.Requires, manifest.OptionalRequires) is { } prerequisiteProblem)
        {
            throw new SkillRegistrationException($"Capability '{manifest.Name}' has invalid prerequisites: {prerequisiteProblem}");
        }

        if (manifest.Risk != RiskLevel.Read && manifest.Verification is null)
        {
            throw new SkillRegistrationException(
                $"Capability '{manifest.Name}' is {manifest.Risk}-risk but declares no verification.");
        }

        // ADR-0044 section 9.2 (review N-7): the input schema is authoritative wherever the Capability is invoked, and a client
        // renders a form from it, so it must be internally valid before the Capability becomes active. Refused here, never left
        // to fail later in a lookup or a validator.
        if (ArgumentSchema.DescribeInvalidInputSchema(manifest.InputSchema) is { } problem)
        {
            throw new SkillRegistrationException($"Capability '{manifest.Name}' has an invalid input schema: {problem}.");
        }
    }

    private bool IsAvailable(CapabilityManifest manifest) => manifest.Requires.All(id => StateOf(id).IsSatisfied());

    private PrerequisiteState StateOf(string prerequisiteId) => _prerequisiteSnapshot.GetValueOrDefault(prerequisiteId, PrerequisiteState.Unknown);

    private sealed record RegisteredSkill(
        ISkillProvider Provider,
        PackageId Package,
        PackageTrustLevel Trust,
        IReadOnlyList<ICapability> Capabilities);
}
