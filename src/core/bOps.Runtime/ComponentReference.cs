// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>Identifies one component that depends on prerequisites: a Tool by name, a Skill Capability as <c>skillId/capabilityName</c>.</summary>
/// <param name="Type">The kind of component.</param>
/// <param name="Id">The component's id within its kind.</param>
public sealed record ComponentReference(SystemComponentType Type, string Id)
{
    /// <summary>A Tool, by manifest name.</summary>
    public static ComponentReference Tool(string toolName) => new(SystemComponentType.Tool, toolName);

    /// <summary>A Skill Capability, by Skill id and Capability name.</summary>
    public static ComponentReference SkillCapability(string skillId, string capabilityName) =>
        new(SystemComponentType.SkillCapability, $"{skillId}/{capabilityName}");

    /// <summary>The compact form used in system message metadata: <c>tool:web.search</c>, <c>skill-capability:skill/capability</c>.</summary>
    public override string ToString() => Type switch
    {
        SystemComponentType.Tool => $"tool:{Id}",
        SystemComponentType.SkillCapability => $"skill-capability:{Id}",
        _ => $"{Type.ToString().ToLowerInvariant()}:{Id}",
    };
}
