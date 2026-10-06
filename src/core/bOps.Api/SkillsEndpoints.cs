// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Api;

/// <summary>The body of <c>GET /api/skills</c> (ADR-0044 section 8).</summary>
internal sealed record SkillCatalogView(IReadOnlyList<SkillView> Skills);

/// <summary>One activated Skill: exactly the fields the contract has. A Skill has no version or description.</summary>
internal sealed record SkillView(string SkillId, string Package, string Trust, IReadOnlyList<CapabilityView> Capabilities);

/// <summary>One activated Capability, as a form needs it: no output schema, permissions, timeout, verification or rollback text.</summary>
internal sealed record CapabilityView(string Name, string Version, string Description, string Risk, bool SupportsDryRun, IReadOnlyList<InputParameterView> InputSchema);

/// <summary><see cref="ToolParameter"/> field for field, every member present, <c>null</c> where the manifest has none.</summary>
internal sealed record InputParameterView(
    string Name,
    string Type,
    string Description,
    bool Required,
    bool Sensitive,
    IReadOnlyList<string>? AllowedValues,
    double? Minimum,
    double? Maximum,
    int? MinLength,
    int? MaxLength,
    int? MinItems,
    int? MaxItems);

/// <summary>
/// Maps <c>GET /api/skills</c> (ADR-0044 section 8, ADR-0018 amendment): the activated Skill and Capability catalog, for the
/// Delegations form. Viewer role, safe and idempotent, never cached. It reads <see cref="ISkillRegistry.GetAvailableSkills"/>
/// only, so a disabled, failed or uninstalled plugin contributes nothing, and nothing about a plugin's files, assembly,
/// signature or configuration is exposed.
/// </summary>
internal static class SkillsEndpoints
{
    internal static void MapSkillsEndpoints(this WebApplication app) =>
        app.MapGet("/api/skills", (ISkillRegistry skills, HttpContext http) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                return Results.Ok(Catalog(skills));
            })
            .RequireAuthorization(ApiAuthorization.ViewerPolicy);

    internal static SkillCatalogView Catalog(ISkillRegistry skills)
    {
        ArgumentNullException.ThrowIfNull(skills);

        return new SkillCatalogView(
        [
            .. skills.GetAvailableSkills()
                .OrderBy(skill => skill.SkillId, StringComparer.Ordinal)
                .Select(skill => new SkillView(
                    skill.SkillId,
                    skill.Package.Value,
                    skill.Trust.ToString(),
                    [
                        .. skill.Capabilities
                            .OrderBy(capability => capability.Name, StringComparer.Ordinal)
                            .Select(capability => new CapabilityView(
                                capability.Name,
                                capability.Version,
                                capability.Description,
                                capability.Risk.ToString(),
                                capability.SupportsDryRun,
                                [.. capability.InputSchema.Select(Parameter)])),
                    ])),
        ]);
    }

    private static InputParameterView Parameter(ToolParameter parameter) => new(
        parameter.Name,
        parameter.Type.ToString(),
        parameter.Description,
        parameter.Required,
        parameter.Sensitive,
        parameter.AllowedValues is null ? null : [.. parameter.AllowedValues],
        parameter.Minimum,
        parameter.Maximum,
        parameter.MinLength,
        parameter.MaxLength,
        parameter.MinItems,
        parameter.MaxItems);
}
