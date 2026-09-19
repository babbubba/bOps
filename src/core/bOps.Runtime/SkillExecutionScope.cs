// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>
/// The Skill context a step of a Skill run carries (ADR-0023, ADR-0025): which run, Skill and Capability it
/// realizes, and the target, environment and blast radius the operator declared for it. The only steps that
/// have one are the evidence calls of a Capability's preparation and the steps of an approved plan; a plain
/// model-proposed call has none, because a tool manifest declares no target. Internal since V1.2-C2, when the
/// authority envelope started to read it as well.
/// </summary>
internal sealed record SkillExecutionScope(
    Guid RunId,
    string SkillId,
    string CapabilityName,
    string Target,
    string Environment,
    BlastRadius BlastRadius,
    string? PlanHash);
