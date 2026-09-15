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
/// Everything a policy engine needs to decide a call: which node and package it runs on, how
/// much that package is trusted, what the tool declares, the arguments, and who is asking.
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
