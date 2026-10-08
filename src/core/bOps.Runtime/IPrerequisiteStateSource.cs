// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>
/// A host-side, I/O-free source of the last recorded <see cref="PrerequisiteState"/> of a prerequisite. <see cref="ToolRegistry"/> and
/// <see cref="SkillRegistry"/> snapshot it so that <see cref="PrerequisiteState.Degraded"/> is kept as such instead of being reduced to
/// the boolean of <see cref="ICapabilityProbe"/> (ADR-0049 section 4). It is not a package-facing contract.
/// </summary>
public interface IPrerequisiteStateSource
{
    /// <summary>The last recorded state; <see cref="PrerequisiteState.Unknown"/> if unregistered or never checked.</summary>
    /// <param name="prerequisiteId">The prerequisite id.</param>
    PrerequisiteState GetState(string prerequisiteId);
}
