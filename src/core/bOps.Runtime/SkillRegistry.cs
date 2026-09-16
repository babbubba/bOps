// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>
/// Node-local registry for activated Skill providers. Registration is atomic and stamps package
/// identity before any Capability becomes visible (ADR-0025; rules A4 and A11).
/// </summary>
public sealed class SkillRegistry : ISkillRegistry
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, RegisteredSkill> _skills = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _capabilityOwners = new(StringComparer.Ordinal);

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
                        .OrderBy(manifest => manifest.Name, StringComparer.Ordinal)
                        .ToArray()))
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
                    capability => string.Equals(capability.Manifest.Name, capabilityName, StringComparison.Ordinal))
                : null;
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

        if (manifest.Risk != RiskLevel.Read && manifest.Verification is null)
        {
            throw new SkillRegistrationException(
                $"Capability '{manifest.Name}' is {manifest.Risk}-risk but declares no verification.");
        }
    }

    private sealed record RegisteredSkill(
        ISkillProvider Provider,
        PackageId Package,
        PackageTrustLevel Trust,
        IReadOnlyList<ICapability> Capabilities);
}
