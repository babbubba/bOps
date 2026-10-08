// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Runtime;

/// <summary>Which registered components depend on one prerequisite, and how. Decides a transition message's severity.</summary>
/// <param name="RequiredBy">Components that require it, ordered.</param>
/// <param name="OptionalBy">Components that use it optionally, ordered.</param>
public sealed record PrerequisiteUsage(IReadOnlyList<ComponentReference> RequiredBy, IReadOnlyList<ComponentReference> OptionalBy)
{
    /// <summary>No component depends on the prerequisite.</summary>
    public static PrerequisiteUsage None { get; } = new([], []);

    /// <summary>Collects the usage of <paramref name="prerequisiteId"/> from component readiness snapshots.</summary>
    /// <param name="prerequisiteId">The prerequisite id.</param>
    /// <param name="components">Readiness of every registered component, e.g. from <see cref="ToolRegistry.GetReadiness"/> and <see cref="SkillRegistry.GetReadiness"/>.</param>
    public static PrerequisiteUsage For(string prerequisiteId, IEnumerable<ComponentReadiness> components)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prerequisiteId);
        ArgumentNullException.ThrowIfNull(components);

        var snapshot = components.ToArray();
        return new PrerequisiteUsage(
            Select(snapshot, readiness => readiness.Requires, prerequisiteId),
            Select(snapshot, readiness => readiness.OptionalRequires, prerequisiteId));
    }

    private static ComponentReference[] Select(
        IEnumerable<ComponentReadiness> components, Func<ComponentReadiness, IReadOnlyList<string>> declared, string prerequisiteId) =>
        components
            .Where(readiness => declared(readiness).Contains(prerequisiteId, StringComparer.OrdinalIgnoreCase))
            .Select(readiness => readiness.Component)
            .Distinct()
            .OrderBy(component => component.ToString(), StringComparer.Ordinal)
            .ToArray();
}
