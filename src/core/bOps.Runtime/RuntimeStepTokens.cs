// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Runtime;

/// <summary>
/// <see cref="Abstractions.PlanStep.Description"/> tokens the runtime itself writes. They are reserved: a tool cannot be
/// registered under one, so a step description equal to a token always denotes the runtime's own step and never a tool.
/// </summary>
internal static class RuntimeStepTokens
{
    /// <summary>
    /// The description of every pre-execution rejection (policy, operator, entitlement, envelope or unknown tool). With
    /// <see cref="Abstractions.ToolFailureKind.Validation"/> it identifies an unknown-tool rejection (ADR-0042 §5).
    /// </summary>
    internal const string Denied = "Denied";
}
