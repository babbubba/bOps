// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Policy;

/// <summary>
/// Serves the role profiles of a loaded <see cref="PolicyConfig"/> to the runtime through the SDK's
/// <see cref="IRoleProfileSource"/> (ADR-0031 section 5), so <c>bOps.Runtime</c> depends on the interface and never
/// on this project. A role with no profile is <c>null</c>, which is how delegation is refused (rule S3): this type
/// never guesses a profile and never throws to say a role has none. <see cref="PolicyConfig.SafeDefault"/> and
/// <see cref="PolicyConfig.AllForbidden"/> carry none, so delegation is off until an operator writes a valid
/// <c>delegation</c> section.
/// </summary>
public sealed class PolicyRoleProfileSource : IRoleProfileSource
{
    private readonly Dictionary<AgentRoleKind, RoleProfile> profiles;

    /// <summary>Creates a source over the profiles of a loaded policy.</summary>
    /// <param name="config">The loaded policy. Its profiles are copied.</param>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> is <c>null</c>.</exception>
    public PolicyRoleProfileSource(PolicyConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        // Keyed by the profile's own role, so a profile is only ever returned for the role it is for.
        profiles = config.RoleProfiles.ToDictionary(profile => profile.Role);
    }

    /// <inheritdoc />
    public RoleProfile? GetProfile(AgentRoleKind role) => profiles.GetValueOrDefault(role);
}
