// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Abstractions;

/// <summary>How a tool call is authorized. Tools at <see cref="RiskLevel.Critical"/> are always <see cref="Forbidden"/> — see agentic/03-security-rules.md, rule S3.</summary>
public enum PolicyMode
{
    /// <summary>The call executes without a human decision.</summary>
    Automatic,

    /// <summary>The call requires a human decision before it executes.</summary>
    Approval,

    /// <summary>The call never executes, regardless of approval.</summary>
    Forbidden,
}

/// <summary>
/// How much a package is trusted, independent of what its own manifest claims. Applied as a
/// ceiling on top of the per-tool decision — a package can never raise its own ceiling
/// (agentic/01-architecture-rules.md, rule B7; agentic/03-security-rules.md, rule S3).
/// </summary>
public enum PackageTrustLevel
{
    /// <summary>Not reviewed. Newly discovered packages start here and stay disabled until an operator enables them.</summary>
    Unverified,

    /// <summary>Reviewed informally, by the community or the operator.</summary>
    Community,

    /// <summary>Reviewed and verified by a trusted party.</summary>
    Verified,

    /// <summary>Published and maintained as part of bOps itself.</summary>
    Official,
}

/// <summary>
/// The scale of a policy decision's potential effect (ADR-0023) — a magnitude, never a literal
/// count or a named resource (rule A1: the core names no concrete target).
/// </summary>
#pragma warning disable CA1720 // "Single" is exactly the right name for one target, the same way ToolParameterType's own members are; see docs/architecture/suppressions.md.
public enum BlastRadius
{
    /// <summary>Affects one target.</summary>
    Single,

    /// <summary>Affects a bounded, named-in-advance set of targets.</summary>
    Multiple,

    /// <summary>Affects an entire fleet or environment.</summary>
    Fleet,
}
#pragma warning restore CA1720

/// <summary>
/// Everything a policy engine needs to decide a call: which node and package it runs on, how
/// much that package is trusted, what the tool declares, the arguments, and who is asking.
/// <see cref="SkillId"/>, <see cref="CapabilityName"/>, <see cref="Target"/>,
/// <see cref="Environment"/> and <see cref="BlastRadius"/> are optional, Skill-call-only context
/// added in ADR-0023 — the plain tool-call path (<c>AgentRunner.ExecuteStepAsync</c>) supplies
/// none of them and they default to absent. <see cref="RiskLevel.Critical"/> stays unconditionally
/// <see cref="PolicyMode.Forbidden"/> regardless of any of these fields (rule S3).
/// </summary>
public sealed record PolicyContext
{
    /// <summary>Creates a policy context.</summary>
    /// <param name="Node">The node the tool would execute on.</param>
    /// <param name="Package">The package that contributed the tool.</param>
    /// <param name="Trust">How much the package is trusted, independent of what it claims about itself.</param>
    /// <param name="Manifest">The manifest of the tool being called.</param>
    /// <param name="Arguments">The arguments the call was made with.</param>
    /// <param name="Actor">Who is asking for this call to run.</param>
    public PolicyContext(NodeId Node, PackageId Package, PackageTrustLevel Trust, ToolManifest Manifest, ToolArguments Arguments, ActorIdentity Actor)
    {
        this.Node = Node;
        this.Package = Package;
        this.Trust = Trust;
        this.Manifest = Manifest;
        this.Arguments = Arguments;
        this.Actor = Actor;
    }

    /// <summary>The node the tool would execute on.</summary>
    public NodeId Node { get; init; }

    /// <summary>The package that contributed the tool.</summary>
    public PackageId Package { get; init; }

    /// <summary>How much the package is trusted, independent of what it claims about itself.</summary>
    public PackageTrustLevel Trust { get; init; }

    /// <summary>The manifest of the tool being called.</summary>
    public ToolManifest Manifest { get; init; }

    /// <summary>The arguments the call was made with.</summary>
    public ToolArguments Arguments { get; init; }

    /// <summary>Who is asking for this call to run.</summary>
    public ActorIdentity Actor { get; init; }

    /// <summary>The Skill this call is part of, when it is one (ADR-0023). <c>null</c> for a plain tool call.</summary>
    public string? SkillId { get; init; }

    /// <summary>The <see cref="CapabilityManifest.Name"/> this call realizes a step of, when it is one (ADR-0023). <c>null</c> for a plain tool call.</summary>
    public string? CapabilityName { get; init; }

    /// <summary>A generic identifier for what this call would affect — never a literal resource name assumed by the core (rule A1). <c>null</c> when not applicable.</summary>
    public string? Target { get; init; }

    /// <summary>A generic environment label (e.g. "production", "staging") — never a value the core interprets itself. <c>null</c> when not applicable.</summary>
    public string? Environment { get; init; }

    /// <summary>The scale of this call's potential effect, when known.</summary>
    public BlastRadius? BlastRadius { get; init; }

    /// <summary>
    /// Which delegated run and agent this call belongs to, and the hash of the envelope in force (ADR-0030
    /// section 3). <c>null</c> for a call that is not part of a delegated run, which is every call before V1.2.
    /// </summary>
    public DelegationCorrelation? Delegation { get; init; }

    /// <summary>
    /// The authority envelope the call was checked against, so a policy rule can see what the agent may reach.
    /// Present exactly when <see cref="Delegation"/> is. The runtime has already refused the call if it is
    /// outside the envelope: this is context for the decision, never a way to widen it. <c>null</c> for a call
    /// that is not part of a delegated run.
    /// </summary>
    public AuthorityEnvelope? Envelope { get; init; }
}

/// <summary>A policy engine's decision for one call. <see cref="Reason"/> is always audited — a denial without a reason is not useful to anyone investigating it.</summary>
public sealed record PolicyDecision
{
    /// <summary>Creates a policy decision.</summary>
    /// <param name="Mode">The authorization mode for this call.</param>
    /// <param name="Reason">Why this mode was chosen, always audited.</param>
    public PolicyDecision(PolicyMode Mode, string Reason)
    {
        this.Mode = Mode;
        this.Reason = Reason;
    }

    /// <summary>The authorization mode for this call.</summary>
    public PolicyMode Mode { get; init; }

    /// <summary>Why this mode was chosen, always audited.</summary>
    public string Reason { get; init; }
}

/// <summary>
/// Decides the authorization mode for a tool call. An unknown risk level, a missing entry, or
/// any evaluation failure must resolve to <see cref="PolicyMode.Forbidden"/>, never to
/// <see cref="PolicyMode.Automatic"/> (agentic/03-security-rules.md, rule S3 — policy fails closed).
/// </summary>
public interface IPolicyEngine
{
    /// <summary>Evaluates the authorization mode for a call.</summary>
    /// <param name="context">The tool, its package's trust level, the arguments, and who is asking.</param>
    PolicyDecision Evaluate(PolicyContext context);
}

/// <summary>The result of a human approval request. <see cref="Actor"/> is who approved or rejected — an audit log that cannot say who acted is not an audit log.</summary>
public sealed record ApprovalDecision
{
    /// <summary>Creates an approval decision.</summary>
    /// <param name="Approved">Whether the call was approved.</param>
    /// <param name="Actor">Who made this decision.</param>
    /// <param name="Note">An optional note from the approver.</param>
    public ApprovalDecision(bool Approved, ActorIdentity Actor, string? Note)
    {
        this.Approved = Approved;
        this.Actor = Actor;
        this.Note = Note;
    }

    /// <summary>Whether the call was approved.</summary>
    public bool Approved { get; init; }

    /// <summary>Who made this decision.</summary>
    public ActorIdentity Actor { get; init; }

    /// <summary>An optional note from the approver.</summary>
    public string? Note { get; init; }
}

/// <summary>Requests a human decision for a call that policy has placed in <see cref="PolicyMode.Approval"/>.</summary>
public interface IApprovalProvider
{
    /// <summary>Asks an operator to approve or reject a call.</summary>
    /// <param name="manifest">The tool being called.</param>
    /// <param name="arguments">The arguments it would be called with.</param>
    /// <param name="verification">What will be checked afterwards, if anything — shown to the operator.</param>
    /// <param name="reason">Why this call requires approval.</param>
    /// <param name="ct">Cancelled if the request should be abandoned.</param>
    Task<ApprovalDecision> RequestApprovalAsync(
        ToolManifest manifest,
        ToolArguments arguments,
        VerificationSpec? verification,
        string reason,
        CancellationToken ct = default);
}
