// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Abstractions;

/// <summary>
/// The outcome of evaluating a post-action verification. <see cref="Inconclusive"/> and
/// <see cref="NotApplicable"/> are never treated as success — a verification that cannot tell
/// success from failure must say so, not default to <see cref="Confirmed"/>
/// (agentic/03-security-rules.md, rule S4).
/// </summary>
public enum VerificationStatus
{
    /// <summary>The action's effect was observed and matches what was expected.</summary>
    Confirmed,

    /// <summary>The action's effect was observed and does not match what was expected.</summary>
    Refuted,

    /// <summary>The verification could not determine success or failure. Never treated as success.</summary>
    Inconclusive,

    /// <summary>No verification applies (used only where the manifest legitimately has none — see rule B3).</summary>
    NotApplicable,
}

/// <summary>The result of evaluating a tool's declared verification.</summary>
public sealed record VerificationOutcome
{
    /// <summary>Creates a verification outcome.</summary>
    /// <param name="Status">Whether the action's effect was confirmed.</param>
    /// <param name="Detail">A human-readable explanation, fed back to the model as an observation.</param>
    public VerificationOutcome(VerificationStatus Status, string? Detail)
    {
        this.Status = Status;
        this.Detail = Detail;
    }

    /// <summary>Whether the action's effect was confirmed.</summary>
    public VerificationStatus Status { get; init; }

    /// <summary>A human-readable explanation, fed back to the model as an observation.</summary>
    public string? Detail { get; init; }
}

/// <summary>
/// Implemented by any tool whose <see cref="ToolManifest.Risk"/> is not
/// <see cref="RiskLevel.Read"/>. The registry refuses to register a non-<c>Read</c> tool that
/// does not implement this interface and declare a <see cref="VerificationSpec"/>
/// (agentic/01-architecture-rules.md, rule B3) — this is what makes "every side-effecting
/// action is verified" a structural guarantee rather than a convention a package can skip.
/// </summary>
public interface IVerifiableTool : ITool
{
    /// <summary>
    /// Interprets the result of calling <see cref="VerificationSpec.VerifyToolName"/> after the
    /// original action executed, and decides whether the effect actually happened.
    /// </summary>
    /// <param name="originalArguments">The arguments the original, non-<c>Read</c> call was made with.</param>
    /// <param name="verificationToolResult">The result of executing the tool named in <see cref="VerificationSpec.VerifyToolName"/>.</param>
    /// <param name="ct">Cancelled if the verification's own timeout elapses.</param>
    Task<VerificationOutcome> EvaluateVerificationAsync(
        ToolArguments originalArguments,
        ToolCallResult verificationToolResult,
        CancellationToken ct = default);
}
