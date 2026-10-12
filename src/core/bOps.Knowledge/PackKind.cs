// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace bOps.Knowledge;

/// <summary>What a pack carries. The kind is declared in the signed manifest and must equal the kind the host granted.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PackKind>))]
public enum PackKind
{
    /// <summary>Reference documentation: curated, shareable between the tenants it was granted to.</summary>
    Knowledge = 0,

    /// <summary>Operational experience: tenant-private by grant; served only inside the tenant it was granted to.</summary>
    Experience = 1,

    /// <summary>Skill instructions and their resources. Never executable: scripts are refused (ADR-0054 section 5.3).</summary>
    Skill = 2,
}
