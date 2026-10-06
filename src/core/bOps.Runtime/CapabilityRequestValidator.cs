// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>
/// Whether a named change can be started at all (ADR-0044 section 9.2): the Skill and the Capability are activated, and the
/// input conforms to the Capability's <see cref="CapabilityManifest.InputSchema"/>. The API and the CLI call it before a run
/// exists, the orchestrator again before any role starts, and the preparation path once more before Capability code runs; the
/// validation itself is the runtime's one <see cref="ArgumentSchema"/>, so no host carries a second copy of it.
/// </summary>
public static class CapabilityRequestValidator
{
    /// <summary>The refusal code of a (Skill, Capability) pair that is not in the activated catalog.</summary>
    public const string UnknownCapability = "unknown_capability";

    /// <summary>The refusal code of input that does not conform to the Capability's input schema.</summary>
    public const string CapabilityInputInvalid = "capability_input_invalid";

    private const int MaximumMessageLength = 500;

    /// <summary>
    /// Checks a named change against the activated catalog of <paramref name="skills"/>. <c>null</c> when it can be started. The
    /// message never contains a submitted value of a <see cref="ToolParameter.Sensitive"/> parameter, and is at most 500 characters.
    /// </summary>
    /// <param name="skills">The host's Skill registry, or <c>null</c> when the host has none (every change is then unknown).</param>
    /// <param name="skillId">The Skill named by the request.</param>
    /// <param name="capabilityName">The Capability named by the request.</param>
    /// <param name="input">The Capability input.</param>
    public static CapabilityRequestRefusal? Check(ISkillRegistry? skills, string skillId, string capabilityName, ToolArguments input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var capability = skills is not null && !string.IsNullOrWhiteSpace(skillId) && !string.IsNullOrWhiteSpace(capabilityName)
            ? skills.Resolve(skillId, capabilityName)
            : null;
        if (capability is null)
        {
            return new CapabilityRequestRefusal(
                UnknownCapability, "The Skill and Capability named are not an activated pair in this host's catalog.", null);
        }

        return ArgumentSchema.Validate(capability.Manifest.InputSchema, input) is { } violation
            ? new CapabilityRequestRefusal(CapabilityInputInvalid, Bounded($"Capability input is not valid: {violation.Message}"), BoundedName(violation.Parameter))
            : null;
    }

    private static string Bounded(string message) =>
        message.Length <= MaximumMessageLength ? message : message[..MaximumMessageLength];

    private static string? BoundedName(string? name) =>
        name is { Length: > 128 } ? name[..128] : name;
}

/// <summary>Why a named change cannot be started.</summary>
/// <param name="Code">A stable snake_case code: <see cref="CapabilityRequestValidator.UnknownCapability"/> or <see cref="CapabilityRequestValidator.CapabilityInputInvalid"/>.</param>
/// <param name="Message">A bounded English explanation that names the parameter and the constraint, never a sensitive value.</param>
/// <param name="Parameter">The input parameter concerned, when there is one.</param>
public sealed record CapabilityRequestRefusal(string Code, string Message, string? Parameter);
