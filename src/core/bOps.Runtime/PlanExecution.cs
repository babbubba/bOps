// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>How the execution of an approved plan ended, as far as the orchestrator needs to know.</summary>
internal enum PlanExecutionStatus
{
    /// <summary>Nothing ran: the plan, its approval or the envelope was refused before the first step.</summary>
    Refused,

    /// <summary>Every step was executed. A tool may still have reported a failure; verification decides what that means.</summary>
    Completed,

    /// <summary>A step was denied, unresolved or rejected, so it and every step after it did not run.</summary>
    Stopped,
}

/// <summary>
/// What executing a plan produced: the report the public methods have always returned, and why it ended. The
/// public entry points return only <see cref="Report"/>; the delegated one hands the orchestrator this, so it can
/// tell a plan that was forbidden from one that ran, which the report alone does not say.
/// </summary>
/// <param name="Report">The evidence of the steps that ran.</param>
/// <param name="Status">How execution ended.</param>
/// <param name="StoppedBy">Why a stopped plan stopped: a policy or envelope denial, an unknown tool or a human's rejection of a step. <c>null</c> otherwise.</param>
/// <param name="Reason">Why nothing ran, for a refused plan. <c>null</c> otherwise.</param>
internal sealed record PlanExecution(
    SkillReport Report,
    PlanExecutionStatus Status,
    AuthorizationKind? StoppedBy = null,
    string? Reason = null);
