// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.AgentFramework.Troubleshooting;

namespace bOps.Cli;

/// <summary>
/// The specialists the CLI registers for the MAF troubleshooting workflow (C01-R23). A specialist is data: a name, what it declares it
/// can investigate, its instructions, and a selector over the read-only tools visible on the node. The workflow's tool authority for
/// each is the exact set the selector yields when a run starts, never a pattern the runtime matches later.
/// </summary>
internal static class CliDiagnosticSpecialists
{
    /// <summary>The package-id prefixes of the first-party families the System specialist may read.</summary>
    private static readonly string[] SystemPackageFamilies =
    [
        "bops.packages.system.", "bops.packages.service.", "bops.packages.network", "bops.packages.storage.",
        "bops.packages.scheduler.", "bops.packages.identity.", "bops.packages.docker",
    ];

    /// <summary>The default registrations: the System specialist, and the PostgreSQL specialist, which is available only when PostgreSQL tools are present.</summary>
    internal static DiagnosticSpecialistCatalog Default() => new(
    [
        new DiagnosticSpecialist(
            "system",
            "Investigates the Windows or Linux host and what runs on it.",
            ["event logs and journal", "services and processes", "CPU, memory and disk", "stability, crashes and reboots", "networking and ports", "Docker containers and their state"],
            "You investigate the machine: its logs and events, services and processes, resource use, stability and crashes, network state and containers. "
            + "Start from what the objective asks, read what is relevant, and follow up on what the results show instead of collecting everything.",
            manifest => SystemPackageFamilies.Any(family => manifest.Package.Value.StartsWith(family, StringComparison.OrdinalIgnoreCase))),
        new DiagnosticSpecialist(
            "postgresql",
            "Investigates a PostgreSQL instance.",
            ["instance state", "sessions and connections", "active and slow queries", "locks and blocking", "indexes and statistics", "parameters and configuration", "availability and performance indicators"],
            "You investigate a PostgreSQL instance: its state, sessions, active and slow queries, locks and blocking, indexes and statistics, configuration, availability and performance. "
            + "Read the minimum that answers the objective and follow up on what the results show.",
            manifest => manifest.Name.StartsWith("postgres.", StringComparison.Ordinal)),
    ]);
}
