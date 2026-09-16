// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Abstractions;

/// <summary>
/// How a piece of <see cref="Evidence"/> was obtained, so a <see cref="Finding"/> built from it
/// can never quietly collapse "the tool reported this" and "the Skill inferred this from it"
/// into one undifferentiated claim (ADR-0023).
/// </summary>
public enum EvidenceKind
{
    /// <summary>Observed directly from a tool's output, unmodified.</summary>
    Fact,

    /// <summary>Derived by the Skill's own logic from one or more <see cref="EvidenceKind.Fact"/> pieces of evidence.</summary>
    Inference,

    /// <summary>A suggested next step, not itself an observation.</summary>
    Recommendation,

    /// <summary>A record that a side-effecting action was actually taken.</summary>
    ExecutedAction,

    /// <summary>The result of checking a prior <see cref="ExecutedAction"/>'s declared effect.</summary>
    Verification,
}

/// <summary>
/// One recorded observation, classified by <see cref="EvidenceKind"/> and attributed to the tool
/// that produced it (ADR-0023). <see cref="Data"/> must already be redacted by whatever
/// assembles this — the same <see cref="ToolArguments.Redact"/> boundary every audit event
/// already goes through (agentic/03-security-rules.md, rule S6) — this type introduces no second
/// redaction mechanism.
/// </summary>
public sealed record Evidence(
    string Id,
    EvidenceKind Kind,
    string Description,
    string? Data,
    string SourceTool,
    DateTimeOffset ObservedAtUtc);

/// <summary>
/// An interpretation built from one or more pieces of <see cref="Evidence"/> — never freestanding
/// (ADR-0023): a claim with no cited evidence is exactly the failure mode this type exists to
/// make structurally impossible, not merely discouraged by convention.
/// </summary>
public sealed record Finding
{
    /// <summary>Creates a finding.</summary>
    /// <param name="Id">A stable identifier for this finding.</param>
    /// <param name="Summary">What was found, in the Skill's own words.</param>
    /// <param name="EvidenceIds">The <see cref="Evidence.Id"/> values this finding is built from. Must not be empty.</param>
    /// <param name="Severity">How serious this finding is, when applicable — not every finding reports a problem.</param>
    /// <exception cref="ArgumentException"><paramref name="EvidenceIds"/> is empty.</exception>
    public Finding(string Id, string Summary, IReadOnlyList<string> EvidenceIds, RiskLevel? Severity = null)
    {
        ArgumentNullException.ThrowIfNull(EvidenceIds);
        if (EvidenceIds.Count == 0)
        {
            throw new ArgumentException("A finding must cite at least one piece of evidence.", nameof(EvidenceIds));
        }

        this.Id = Id;
        this.Summary = Summary;
        this.EvidenceIds = EvidenceIds;
        this.Severity = Severity;
    }

    /// <summary>A stable identifier for this finding.</summary>
    public string Id { get; init; }

    /// <summary>What was found, in the Skill's own words.</summary>
    public string Summary { get; init; }

    /// <summary>The <see cref="Evidence.Id"/> values this finding is built from. Never empty.</summary>
    public IReadOnlyList<string> EvidenceIds { get; init; }

    /// <summary>How serious this finding is, when applicable — not every finding reports a problem.</summary>
    public RiskLevel? Severity { get; init; }
}

/// <summary>
/// A structured report assembled only from recorded <see cref="Abstractions.Evidence"/> and
/// <see cref="Abstractions.Finding"/> — deliberately has no field for free-text narrative, so a
/// renderer has nothing to trust except what was actually recorded (ADR-0023: "never let the
/// model invent proof").
/// </summary>
public sealed record SkillReport(
    IReadOnlyList<Evidence> Evidence,
    IReadOnlyList<Finding> Findings,
    ExecutionPlan? Plan);
