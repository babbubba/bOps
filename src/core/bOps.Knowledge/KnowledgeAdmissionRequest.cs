// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Knowledge;

/// <summary>What the host asks the admission service to do for one task.</summary>
public sealed class KnowledgeAdmissionRequest
{
    /// <summary>The tenant and task, issued by the host's authority.</summary>
    public required KnowledgeTaskScope Scope { get; init; }

    /// <summary>The actor the task runs for; recorded in the audit event.</summary>
    public required ActorIdentity Actor { get; init; }

    /// <summary>The source to read from. It must be an instance the host registered.</summary>
    public required IKnowledgePackSource Source { get; init; }

    /// <summary>The exact pack and version.</summary>
    public required PackReference Reference { get; init; }

    /// <summary>
    /// The pin of an earlier admission when a task is resumed: the verified content must be exactly the pinned content for the same
    /// tenant, or the admission is refused.
    /// </summary>
    public KnowledgePin? ExpectedPin { get; init; }
}
