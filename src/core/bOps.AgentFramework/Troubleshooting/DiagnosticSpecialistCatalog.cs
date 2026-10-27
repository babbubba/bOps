// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.AgentFramework.Troubleshooting;

/// <summary>
/// The specialists a host registered for MAF troubleshooting (C01-R23). It is the extension point: a new specialist is one more
/// registration, with no change to the workflow. Availability is decided per run against the tools actually visible on the node, so a
/// specialist whose package is absent is simply not available and the others still work.
/// </summary>
public sealed class DiagnosticSpecialistCatalog
{
    private readonly List<DiagnosticSpecialist> specialists = [];

    /// <summary>Creates a catalog from the host's registrations.</summary>
    /// <param name="registrations">The specialists to register; names must be unique and not blank.</param>
    public DiagnosticSpecialistCatalog(IEnumerable<DiagnosticSpecialist> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        foreach (var registration in registrations)
        {
            Add(registration);
        }
    }

    /// <summary>Every registered specialist in registration order, available or not.</summary>
    public IReadOnlyList<DiagnosticSpecialist> Registered => specialists;

    /// <summary>
    /// Resolves the specialists that can work on this node: each one's selector is applied to the read-only tools the runner reports visible, and
    /// a specialist left with no tool is reported as unavailable instead of being offered to the triage agent. Never a tool above
    /// <see cref="RiskLevel.Read"/>, whatever a selector accepts.
    /// </summary>
    /// <param name="visibleReadTools">The read-only tools the runner reports visible on this node (<c>WorkflowToolSession.VisibleReadTools</c>).</param>
    /// <param name="unavailable">The names of the registered specialists that have no tool on this node.</param>
    public IReadOnlyList<AvailableSpecialist> Resolve(IReadOnlyList<ToolManifest> visibleReadTools, out IReadOnlyList<string> unavailable)
    {
        ArgumentNullException.ThrowIfNull(visibleReadTools);
        var visible = visibleReadTools
            .Where(manifest => manifest.Risk == RiskLevel.Read)
            .OrderBy(manifest => manifest.Name, StringComparer.Ordinal)
            .ToArray();
        var available = new List<AvailableSpecialist>();
        var missing = new List<string>();
        foreach (var specialist in specialists)
        {
            var tools = visible.Where(specialist.ToolSelector).ToArray();
            if (tools.Length == 0)
            {
                missing.Add(specialist.Name);
                continue;
            }

            available.Add(new AvailableSpecialist(specialist, tools));
        }

        unavailable = missing;
        return available;
    }

    private void Add(DiagnosticSpecialist registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.Name);
        ArgumentNullException.ThrowIfNull(registration.ToolSelector);
        if (registration.Name.Any(character => !(char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-')))
        {
            throw new ArgumentException($"A specialist name is lowercase letters, digits and '-' only (found '{registration.Name}').");
        }

        if (specialists.Any(existing => string.Equals(existing.Name, registration.Name, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"A specialist named '{registration.Name}' is already registered.");
        }

        specialists.Add(registration);
    }
}
