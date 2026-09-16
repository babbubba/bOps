// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Abstractions;

/// <summary>
/// Declares one operator-facing operation a Skill offers (ADR-0023) — richer than one
/// <see cref="ToolManifest"/>, because a single Capability is routinely realized by several typed
/// tool calls (an <see cref="ExecutionPlan"/>), never by free commands or SQL. This is the
/// declarative half of <see cref="ICapability"/>. ADR-0025 defines the executable boundary while
/// keeping every concrete tool call in the ordinary governed runtime pipeline.
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
    [System.Text.Json.Serialization.JsonInclude]
    public PackageId Package { get; internal set; }
}

/// <summary>The typed, contextual input used to prepare one Capability run (ADR-0025).</summary>
public sealed record CapabilityRequest
{
    /// <summary>Creates a Capability request.</summary>
    public CapabilityRequest(
        ToolArguments Input,
        string Target,
        string Environment,
        BlastRadius BlastRadius,
        bool DryRun = false)
    {
        ArgumentNullException.ThrowIfNull(Input);
        ArgumentException.ThrowIfNullOrWhiteSpace(Target);
        ArgumentException.ThrowIfNullOrWhiteSpace(Environment);

        this.Input = Input;
        this.Target = Target;
        this.Environment = Environment;
        this.BlastRadius = BlastRadius;
        this.DryRun = DryRun;
    }

    /// <summary>The Capability's typed input.</summary>
    public ToolArguments Input { get; init; }

    /// <summary>The operator-supplied target label used by contextual policy.</summary>
    public string Target { get; init; }

    /// <summary>The operator-supplied environment label used by contextual policy.</summary>
    public string Environment { get; init; }

    /// <summary>The declared scale of the requested effect.</summary>
    public BlastRadius BlastRadius { get; init; }

    /// <summary>Whether the caller requests preparation without execution.</summary>
    public bool DryRun { get; init; }
}

/// <summary>
/// Invocation-scoped access to evidence tools. The host binds package, actor, task and policy
/// context; callers can provide only the tool name and typed arguments (ADR-0025).
/// </summary>
public interface IToolInvoker
{
    /// <summary>Invokes one same-package, Read-risk tool through validation, policy, timeout and audit.</summary>
    Task<ToolCallResult> InvokeAsync(
        string toolName,
        ToolArguments arguments,
        CancellationToken ct = default);
}

/// <summary>Executable deterministic logic for one <see cref="CapabilityManifest"/>.</summary>
public interface ICapability
{
    /// <summary>The declarative operator-facing contract for this Capability.</summary>
    CapabilityManifest Manifest { get; }

    /// <summary>
    /// Gathers real evidence and prepares findings plus an optional immutable execution plan.
    /// Package code receives no registry or service-provider access.
    /// </summary>
    Task<SkillReport> PrepareAsync(
        CapabilityRequest request,
        IToolInvoker toolInvoker,
        CancellationToken ct = default);
}

/// <summary>
/// One package entry point that contributes deterministic Skills and the tools they own. Extending
/// <see cref="IToolProvider"/> makes same-package evidence ownership structural (ADR-0025).
/// </summary>
public interface ISkillProvider : IToolProvider
{
    /// <summary>The stable Skill identity within the host.</summary>
    string SkillId { get; }

    /// <summary>Returns this Skill's Capabilities. The registry snapshots and validates the result.</summary>
    IReadOnlyList<ICapability> GetCapabilities();
}

/// <summary>A discovered Skill and its host-stamped Capability manifests.</summary>
public sealed record SkillDescriptor(
    string SkillId,
    PackageId Package,
    PackageTrustLevel Trust,
    IReadOnlyList<CapabilityManifest> Capabilities);

/// <summary>How preparation of a terminal V1.1 Skill run ended.</summary>
public enum SkillPreparationStatus
{
    /// <summary>Evidence, findings and an optional plan were prepared successfully.</summary>
    Prepared,

    /// <summary>Deterministic Skill code failed or returned an invalid report.</summary>
    Failed,

    /// <summary>The Capability exceeded its declared preparation timeout.</summary>
    Timeout,
}

/// <summary>
/// The serializable result of preparing one Skill run. It is intentionally not persisted by the
/// runtime in V1.1 and has no resume token (ADR-0025).
/// </summary>
public sealed record PreparedSkillRun(
    Guid RunId,
    string SkillId,
    string CapabilityName,
    CapabilityRequest Request,
    SkillPreparationStatus Status,
    SkillReport Report,
    string? PlanHash,
    string? ErrorMessage);
