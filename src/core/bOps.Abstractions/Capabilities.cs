// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Abstractions;

/// <summary>
/// Declares one operator-facing operation a Skill offers (ADR-0023) — richer than one
/// <see cref="ToolManifest"/>, because a single Capability is routinely realized by several typed
/// tool calls (an <see cref="ExecutionPlan"/>), never by free commands or SQL. This is the
/// declarative shape only; the interface a Skill implements to actually run a Capability is
/// deliberately deferred to a follow-up ADR (ADR-0023, "Deferred to a follow-up ADR") — committing
/// to an execution contract before a real runner has exercised it risks getting the shape wrong on
/// a frozen <c>1.0.0</c> surface (ADR-0022).
/// </summary>
public sealed record CapabilityManifest
{
    /// <summary>Creates a capability manifest.</summary>
    /// <param name="Name">This capability's stable identity, e.g. <c>"postgres.diagnose_bloat"</c>.</param>
    /// <param name="Version">This capability's own version, independent of its package's version.</param>
    /// <param name="Description">What this capability does, for an operator deciding whether to invoke it.</param>
    /// <param name="Risk">The highest risk this capability's realized <see cref="ExecutionPlan"/> can carry.</param>
    /// <param name="RequiredPermissions">Generic permission tags this capability needs — never a literal command or a concrete resource name (rule A1).</param>
    /// <param name="InputSchema">The typed input this capability accepts, reusing <see cref="ToolParameter"/>'s shape.</param>
    /// <param name="OutputSchema">The typed output this capability produces, reusing <see cref="ToolParameter"/>'s shape.</param>
    /// <param name="Timeout">How long this capability's realized plan is allowed to run in total.</param>
    /// <param name="SupportsDryRun">Whether this capability can report what it would do without doing it.</param>
    /// <param name="Verification">What will be checked after this capability runs, if anything — shown to the operator before approval, exactly like <see cref="ToolManifest.Verification"/>.</param>
    /// <param name="RollbackDescription">Human-readable guidance on reversing this capability's effect, if it has one. Rollback is itself just another Capability or Tool call — never a special mechanism.</param>
    public CapabilityManifest(
        string Name,
        string Version,
        string Description,
        RiskLevel Risk,
        IReadOnlyList<string> RequiredPermissions,
        IReadOnlyList<ToolParameter> InputSchema,
        IReadOnlyList<ToolParameter> OutputSchema,
        TimeSpan Timeout,
        bool SupportsDryRun,
        VerificationSpec? Verification = null,
        string? RollbackDescription = null)
    {
        this.Name = Name;
        this.Version = Version;
        this.Description = Description;
        this.Risk = Risk;
        this.RequiredPermissions = RequiredPermissions;
        this.InputSchema = InputSchema;
        this.OutputSchema = OutputSchema;
        this.Timeout = Timeout;
        this.SupportsDryRun = SupportsDryRun;
        this.Verification = Verification;
        this.RollbackDescription = RollbackDescription;
    }

    /// <summary>This capability's stable identity, e.g. <c>"postgres.diagnose_bloat"</c>.</summary>
    public string Name { get; init; }

    /// <summary>This capability's own version, independent of its package's version.</summary>
    public string Version { get; init; }

    /// <summary>What this capability does, for an operator deciding whether to invoke it.</summary>
    public string Description { get; init; }

    /// <summary>The highest risk this capability's realized <see cref="ExecutionPlan"/> can carry.</summary>
    public RiskLevel Risk { get; init; }

    /// <summary>Generic permission tags this capability needs — never a literal command or a concrete resource name (rule A1).</summary>
    public IReadOnlyList<string> RequiredPermissions { get; init; }

    /// <summary>The typed input this capability accepts, reusing <see cref="ToolParameter"/>'s shape.</summary>
    public IReadOnlyList<ToolParameter> InputSchema { get; init; }

    /// <summary>The typed output this capability produces, reusing <see cref="ToolParameter"/>'s shape.</summary>
    public IReadOnlyList<ToolParameter> OutputSchema { get; init; }

    /// <summary>How long this capability's realized plan is allowed to run in total.</summary>
    public TimeSpan Timeout { get; init; }

    /// <summary>Whether this capability can report what it would do without doing it.</summary>
    public bool SupportsDryRun { get; init; }

    /// <summary>What will be checked after this capability runs, if anything.</summary>
    public VerificationSpec? Verification { get; init; }

    /// <summary>Human-readable guidance on reversing this capability's effect, if it has one.</summary>
    public string? RollbackDescription { get; init; }

    /// <summary>The package that contributed this capability — stamped by the host, never self-claimed (rule A11), exactly like <see cref="ToolManifest.Package"/>.</summary>
    public PackageId Package { get; init; }
}
