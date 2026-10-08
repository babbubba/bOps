// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Runtime;

/// <summary>
/// Whether one registered component can run right now (ADR-0049 section 1). <see cref="Registered"/> and
/// <see cref="Available"/> are independent: a registered component whose required prerequisites are not satisfied is not
/// available and is never offered or executed. An available component with an unsatisfied optional prerequisite is
/// <see cref="Degraded"/>.
/// </summary>
/// <param name="Component">The component.</param>
/// <param name="Registered">Whether bOps has loaded and validated the component on this node.</param>
/// <param name="Available">Whether every required prerequisite is satisfied.</param>
/// <param name="Degraded">Whether it is available while an optional prerequisite is not satisfied.</param>
/// <param name="Requires">The declared required prerequisite ids.</param>
/// <param name="OptionalRequires">The declared optional prerequisite ids.</param>
/// <param name="UnsatisfiedRequired">The required prerequisite ids that are not satisfied, in declaration order.</param>
/// <param name="UnsatisfiedOptional">The optional prerequisite ids that are not satisfied, in declaration order.</param>
public sealed record ComponentReadiness(
    ComponentReference Component,
    bool Registered,
    bool Available,
    bool Degraded,
    IReadOnlyList<string> Requires,
    IReadOnlyList<string> OptionalRequires,
    IReadOnlyList<string> UnsatisfiedRequired,
    IReadOnlyList<string> UnsatisfiedOptional)
{
    /// <summary>Evaluates a registered component's readiness from its declarations and a satisfaction lookup.</summary>
    /// <param name="component">The component.</param>
    /// <param name="requires">The declared required prerequisite ids.</param>
    /// <param name="optionalRequires">The declared optional prerequisite ids.</param>
    /// <param name="isSatisfied">Whether a prerequisite id is currently satisfied. Unknown ids must return <c>false</c>.</param>
    public static ComponentReadiness Evaluate(
        ComponentReference component,
        IReadOnlyList<string> requires,
        IReadOnlyList<string> optionalRequires,
        Func<string, bool> isSatisfied)
    {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(requires);
        ArgumentNullException.ThrowIfNull(optionalRequires);
        ArgumentNullException.ThrowIfNull(isSatisfied);

        var unsatisfiedRequired = requires.Where(id => !isSatisfied(id)).ToArray();
        var unsatisfiedOptional = optionalRequires.Where(id => !isSatisfied(id)).ToArray();
        var available = unsatisfiedRequired.Length == 0;

        return new ComponentReadiness(
            component,
            Registered: true,
            available,
            Degraded: available && unsatisfiedOptional.Length > 0,
            requires.ToArray(),
            optionalRequires.ToArray(),
            unsatisfiedRequired,
            unsatisfiedOptional);
    }
}
