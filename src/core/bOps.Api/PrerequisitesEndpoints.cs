// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Runtime;

namespace bOps.Api;

/// <summary>A component that depends on a prerequisite, by kind and id (<c>tool</c> or <c>skillCapability</c>).</summary>
internal sealed record ComponentRefView(string Type, string Id);

/// <summary>
/// The current state of one prerequisite and the safe operator-facing facts about it. The check implementation, its metadata and any
/// exception text are not exposed; <c>Message</c> is the host-bounded text the check returned (a thrown check yields a fixed one).
/// </summary>
internal sealed record PrerequisiteStatusView(
    string Id,
    string DisplayName,
    string Description,
    string Kind,
    string? Remediation,
    string? Package,
    string State,
    string Code,
    string? Message,
    DateTimeOffset? CheckedAtUtc,
    IReadOnlyList<ComponentRefView> RequiredBy,
    IReadOnlyList<ComponentRefView> OptionalBy);

/// <summary>Whether one registered Tool or Skill Capability can run right now, and which prerequisites decide it.</summary>
internal sealed record ComponentReadinessView(
    string Type,
    string Id,
    bool Registered,
    bool Available,
    bool Degraded,
    IReadOnlyList<string> UnsatisfiedRequired,
    IReadOnlyList<string> DegradedRequired,
    IReadOnlyList<string> UnsatisfiedOptional);

/// <summary><c>GET /api/prerequisites</c>: the current-state projection. It is computed from the live registries, never stored.</summary>
internal sealed record PrerequisiteReadinessView(
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<PrerequisiteStatusView> Prerequisites,
    IReadOnlyList<ComponentReadinessView> Components);

/// <summary>
/// Maps <c>GET /api/prerequisites</c> (ADR-0049): what is unavailable right now, which components it affects, whether it is required or
/// optional, and how to fix it. Viewer role, read-only, no I/O of its own — it projects the last refresh and is not a second state store.
/// A prerequisite a component declares but nobody registered a check for is listed as <c>Error</c>/<c>not-registered</c> once a refresh
/// has observed it (the same state the readiness cycle records and announces), and as <c>Unknown</c>/<c>not-registered</c> before that.
/// </summary>
internal static class PrerequisitesEndpoints
{
    internal static void MapPrerequisitesEndpoints(this WebApplication app) =>
        app.MapGet("/api/prerequisites", (HttpContext http, PrerequisiteRegistry registry, ToolRegistry tools, SkillRegistry skills, TimeProvider clock) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                return Results.Ok(Project(registry, tools, skills, clock.GetUtcNow()));
            })
            .RequireAuthorization(ApiAuthorization.ViewerPolicy);

    internal static PrerequisiteReadinessView Project(PrerequisiteRegistry registry, ToolRegistry tools, SkillRegistry skills, DateTimeOffset now)
    {
        var components = tools.GetReadiness().Concat(skills.GetReadiness()).ToArray();
        var registrations = registry.GetRegistrations();
        var registered = registrations.Select(r => r.Descriptor.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var statuses = registrations
            .Select(registration =>
            {
                var descriptor = registration.Descriptor;
                var result = registry.GetLastResult(descriptor.Id);
                var usage = PrerequisiteUsage.For(descriptor.Id, components);
                return new PrerequisiteStatusView(
                    descriptor.Id,
                    descriptor.DisplayName,
                    descriptor.Description,
                    descriptor.Kind.ToString(),
                    descriptor.Remediation,
                    registration.Package.Value,
                    (result?.State ?? PrerequisiteState.Unknown).ToString(),
                    result?.Code ?? "not-checked",
                    result?.Message,
                    result?.CheckedAtUtc,
                    Refs(usage.RequiredBy),
                    Refs(usage.OptionalBy));
            })
            .ToList();

        // A component can name a prerequisite no package contributed a check for; it can never become available, so say so.
        foreach (var id in components.SelectMany(c => c.Requires.Concat(c.OptionalRequires)).Distinct(StringComparer.OrdinalIgnoreCase)
                     .Where(id => !registered.Contains(id)).Order(StringComparer.Ordinal))
        {
            var usage = PrerequisiteUsage.For(id, components);
            var observed = registry.GetLastResult(id);
            statuses.Add(new PrerequisiteStatusView(
                id, id, "No package registered a check for this prerequisite.", PrerequisiteKind.Other.ToString(), null, null,
                (observed?.State ?? PrerequisiteState.Unknown).ToString(), PrerequisiteCodes.NotRegistered, observed?.Message, observed?.CheckedAtUtc,
                Refs(usage.RequiredBy), Refs(usage.OptionalBy)));
        }

        return new PrerequisiteReadinessView(
            now,
            [.. statuses.OrderBy(status => status.Id, StringComparer.Ordinal)],
            [.. components.Select(c => new ComponentReadinessView(
                ComponentType(c.Component.Type), c.Component.Id, c.Registered, c.Available, c.Degraded,
                c.UnsatisfiedRequired, c.DegradedRequired, c.UnsatisfiedOptional))]);
    }

    private static ComponentRefView[] Refs(IEnumerable<ComponentReference> references) =>
        [.. references.Select(r => new ComponentRefView(ComponentType(r.Type), r.Id))];

    private static string ComponentType(SystemComponentType type) => type switch
    {
        SystemComponentType.Tool => "tool",
        SystemComponentType.SkillCapability => "skillCapability",
        _ => type.ToString().ToLowerInvariant(),
    };
}
