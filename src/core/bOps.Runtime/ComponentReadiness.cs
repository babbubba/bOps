// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

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
/// <param name="Degraded">Whether it is available while a required prerequisite is <c>Degraded</c> or an optional one is not <c>Available</c>.</param>
/// <param name="Requires">The declared required prerequisite ids.</param>
/// <param name="OptionalRequires">The declared optional prerequisite ids.</param>
/// <param name="UnsatisfiedRequired">The required prerequisite ids that are not satisfied, in declaration order.</param>
/// <param name="UnsatisfiedOptional">The optional prerequisite ids that are not <c>Available</c> (degraded ones included), in declaration order.</param>
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
    /// <summary>The required prerequisite ids that are <see cref="PrerequisiteState.Degraded"/>: satisfied, but they degrade the component.</summary>
    public IReadOnlyList<string> DegradedRequired { get; init; } = [];

    /// <summary>
    /// Evaluates a registered component's readiness from its declarations and the rich state of each prerequisite (ADR-0049 section 1):
    /// a required prerequisite that is <c>Available</c> or <c>Degraded</c> keeps the component available (and <c>Degraded</c> degrades
    /// it); any other required state makes it unavailable. An optional prerequisite never makes it unavailable, but anything other than
    /// <c>Available</c> — <c>Degraded</c>, <c>Unavailable</c>, <c>Error</c> or <c>Unknown</c> — degrades it.
    /// </summary>
    /// <param name="component">The component.</param>
    /// <param name="requires">The declared required prerequisite ids.</param>
    /// <param name="optionalRequires">The declared optional prerequisite ids.</param>
    /// <param name="stateOf">The current state of a prerequisite id. Unregistered or never-checked ids must return <see cref="PrerequisiteState.Unknown"/>.</param>
    public static ComponentReadiness Evaluate(
        ComponentReference component,
        IReadOnlyList<string> requires,
        IReadOnlyList<string> optionalRequires,
        Func<string, PrerequisiteState> stateOf)
    {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(requires);
        ArgumentNullException.ThrowIfNull(optionalRequires);
        ArgumentNullException.ThrowIfNull(stateOf);

        var unsatisfiedRequired = requires.Where(id => !stateOf(id).IsSatisfied()).ToArray();
        var degradedRequired = requires.Where(id => stateOf(id) is PrerequisiteState.Degraded).ToArray();
        var unsatisfiedOptional = optionalRequires.Where(id => stateOf(id) is not PrerequisiteState.Available).ToArray();
        var available = unsatisfiedRequired.Length == 0;

        return new ComponentReadiness(
            component,
            Registered: true,
            available,
            Degraded: available && (degradedRequired.Length > 0 || unsatisfiedOptional.Length > 0),
            requires.ToArray(),
            optionalRequires.ToArray(),
            unsatisfiedRequired,
            unsatisfiedOptional)
        {
            DegradedRequired = degradedRequired,
        };
    }

    /// <summary>
    /// Evaluates readiness from the boolean compatibility view: <c>true</c> is <see cref="PrerequisiteState.Available"/>, <c>false</c>
    /// is <see cref="PrerequisiteState.Unavailable"/>.
    /// </summary>
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
        ArgumentNullException.ThrowIfNull(isSatisfied);
        return Evaluate(
            component, requires, optionalRequires, id => isSatisfied(id) ? PrerequisiteState.Available : PrerequisiteState.Unavailable);
    }
}
