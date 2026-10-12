// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Knowledge;

/// <summary>
/// The trust level the host assigned to a registered provider instance. It narrows what the provider may serve; it never relaxes
/// verification: every pack from every provider is signature- and hash-checked, and all served content is data.
/// </summary>
public enum KnowledgeProviderTrust
{
    /// <summary>A source the operator does not fully control (for example customer-writable storage). It may serve Knowledge and Experience packs, never Skill packs.</summary>
    Untrusted = 0,

    /// <summary>A source the operator controls. It may serve every pack kind the registration lists.</summary>
    Trusted = 1,
}
