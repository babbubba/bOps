// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>
/// Computes the authority of a delegated run: the objective's root envelope, and each role's envelope as
/// <c>parent ∩ role profile ∩ request</c>, dimension by dimension (ADR-0030 section 3, ADR-0031). Pure
/// functions of their inputs and an explicit instant, so the result is deterministic and can be tested
/// without a clock. Nothing here executes anything: enforcement of the resulting envelope belongs to the
/// step-execution path (V1.2-C2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Reduce-only.</b> Every set is an intersection, every ceiling a minimum, every window an
/// intersection and every budget a minimum, so a child is never broader than its parent, its profile or
/// the request. A dimension that is not applicable to the role (ADR-0031 section 1) is forced to empty or
/// zero, which only removes authority. An empty required dimension is a refusal, never a fallback to a
/// default or to the parent's value.
/// </para>
/// <para>
/// <b>Grant versus exhaustion (ADR-0031 section 3).</b> Budgets and the deadline here are ceilings that
/// the profile and the request <em>grant</em>. Whether the amount a run has already used, or the instant
/// that has already passed, leaves room for another role is not decided here: the caller (V1.2-E) checks
/// that before it asks for a role's envelope and ends the run as <c>BudgetExceeded</c> or
/// <c>DeadlineExceeded</c>. So a parent whose token ceiling is zero is refused for the roles that need
/// tokens, and granted, with none, to the roles that never make a model call.
/// </para>
/// </remarks>
internal static class EnvelopeReducer
{
    /// <summary>
    /// Derives the objective's root envelope (depth 0) from the four role profiles and the operator's
    /// request, and proves at the start that every role can be derived from it. ADR-0030 section 3 says
    /// the root is derived "in the same way"; with one profile per role it is the most a delegation, as
    /// configured, could ever exercise: sets are the union of the profiles', ceilings the highest any role
    /// may reach (a read-only role counting as <see cref="RiskLevel.Read"/>), and the budgets the sum
    /// across the roles, because they run one after another and each reserves from the run's total. The
    /// request then only narrows it. A refusal here happens before any role has spent a token or the
    /// operator has been asked to approve anything.
    /// </summary>
    /// <param name="profiles">Where the role profiles come from. A role with none refuses the whole delegation.</param>
    /// <param name="request">What the operator limited the objective to. It can never grant.</param>
    /// <param name="originator">The operator on whose authority the run executes.</param>
    /// <param name="now">The instant delegation starts.</param>
    internal static EnvelopeReduction DeriveRoot(
        IRoleProfileSource profiles, DelegationAuthorityRequest request, ActorIdentity originator, DateTimeOffset now) =>
        DeriveRootAttributed(profiles, request, originator, now).Reduction;

    /// <summary>
    /// <see cref="DeriveRoot"/> that also says which role a refusal is about, for the orchestrator's audit event (V1.2-D).
    /// A refusal of the request itself, its deadline or its window, belongs to no role and reports <c>null</c>.
    /// </summary>
    internal static (EnvelopeReduction Reduction, AgentRoleKind? Role) DeriveRootAttributed(
        IRoleProfileSource profiles, DelegationAuthorityRequest request, ActorIdentity originator, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(originator);

        var roleProfiles = new List<RoleProfile>(RoleRequirements.Pipeline.Count);
        foreach (var role in RoleRequirements.Pipeline)
        {
            var profile = profiles.GetProfile(role);
            if (profile is null)
            {
                return (NoProfile(role), role);
            }

            if (profile.Role != role)
            {
                return (WrongProfile(role, profile), role);
            }

            roleProfiles.Add(profile);
        }

        if (request.DeadlineUtc is { } requestedDeadline && requestedDeadline <= now)
        {
            return (EnvelopeReduction.Denied(EnvelopeDimension.Deadline, "The requested deadline has already passed."), null);
        }

        if (request.Window is { } requestedWindow && requestedWindow.EndUtc <= now)
        {
            return (EnvelopeReduction.Denied(EnvelopeDimension.MaintenanceWindow, "The requested maintenance window has already ended."), null);
        }

        var root = BuildRoot(roleProfiles, request, originator, now);

        // Fail at the start: a role that would be refused later would waste the roles before it and an
        // operator's approval. A role whose own window has already closed can never act either.
        foreach (var profile in roleProfiles)
        {
            var reduction = ReduceForRole(root, profile.Role, profile, request, now);
            if (reduction.IsDenied)
            {
                return (reduction, profile.Role);
            }

            if (reduction.Envelope!.Window is { } window && window.EndUtc <= now)
            {
                return (EnvelopeReduction.Denied(
                    EnvelopeDimension.MaintenanceWindow, $"{profile.Role} role: its maintenance window has already ended."), profile.Role);
            }
        }

        return (EnvelopeReduction.Granted(root, NarrowedDimensions(BuildRoot(roleProfiles, new DelegationAuthorityRequest(), originator, now), root)), null);
    }

    /// <summary>
    /// Derives one role's envelope from its parent's, its profile and the request. Refuses, naming the role
    /// and the dimension, when a required dimension comes out empty (ADR-0031 section 1), when the profile
    /// is missing or for another role, when the windows do not overlap, or when the parent is already a
    /// role's envelope (depth is fixed, ADR-0030 section 2).
    /// </summary>
    /// <param name="parent">The envelope being reduced: the root, whose budgets are ceilings.</param>
    /// <param name="role">The role the envelope is for.</param>
    /// <param name="profile">The role's profile, or <c>null</c> when none is configured.</param>
    /// <param name="request">What the operator limited the objective to.</param>
    /// <param name="now">The instant the role starts, from which its maximum duration is counted.</param>
    internal static EnvelopeReduction ReduceForRole(
        AuthorityEnvelope parent, AgentRoleKind role, RoleProfile? profile, DelegationAuthorityRequest request, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown role kind.");
        }

        if (profile is null)
        {
            return NoProfile(role);
        }

        if (profile.Role != role)
        {
            return WrongProfile(role, profile);
        }

        if (parent.Depth != RoleRequirements.RoleDepth - 1)
        {
            return EnvelopeReduction.Denied(
                EnvelopeDimension.Depth,
                $"{role} role: delegation depth is fixed at {RoleRequirements.RoleDepth}, so an envelope at depth {parent.Depth} cannot delegate further.");
        }

        // Dimensions are derived in the order of EnvelopeDimension, so the first refusal is deterministic.
        if (DeriveSet(role, EnvelopeDimension.Skills, parent.AllowedSkills, profile.AllowedSkills, request.AllowedSkills, out var skills) is { } noSkills)
        {
            return noSkills;
        }

        if (DeriveSet(role, EnvelopeDimension.Capabilities, parent.AllowedCapabilities, profile.AllowedCapabilities, request.AllowedCapabilities, out var capabilities) is { } noCapabilities)
        {
            return noCapabilities;
        }

        if (DeriveSet(role, EnvelopeDimension.Tools, parent.AllowedTools, profile.AllowedTools, request.AllowedTools, out var tools) is { } noTools)
        {
            return noTools;
        }

        var risk = Min(Min(parent.MaxRisk, profile.MaxRisk), request.MaxRisk ?? RiskLevel.Critical);
        risk = Min(risk, RoleRequirements.RiskCap(role));
        if (risk < RoleRequirements.RiskFloor(role))
        {
            return EnvelopeReduction.Denied(
                EnvelopeDimension.Risk,
                $"{role} role: the risk ceiling left by the parent envelope, its profile and the request is {risk}, which leaves it nothing it can execute.");
        }

        var blastRadius = Min(Min(parent.MaxBlastRadius, profile.MaxBlastRadius), request.MaxBlastRadius ?? BlastRadius.Fleet);

        if (DeriveSet(role, EnvelopeDimension.Targets, parent.AllowedTargets, profile.AllowedTargets, request.AllowedTargets, out var targets) is { } noTargets)
        {
            return noTargets;
        }

        if (DeriveSet(role, EnvelopeDimension.Environments, parent.AllowedEnvironments, profile.AllowedEnvironments, request.AllowedEnvironments, out var environments) is { } noEnvironments)
        {
            return noEnvironments;
        }

        if (IntersectWindows(parent.Window, profile.Window, request.Window, out var window))
        {
            return EnvelopeReduction.Denied(
                EnvelopeDimension.MaintenanceWindow,
                $"{role} role: the maintenance windows of the parent envelope, its profile and the request do not overlap.");
        }

        var steps = Math.Min(Math.Min(parent.Budget.MaxSteps, profile.MaxSteps), request.MaxSteps ?? int.MaxValue);
        if (steps < 1)
        {
            return EnvelopeReduction.Denied(EnvelopeDimension.Steps, $"{role} role: the parent envelope, its profile and the request leave it no steps.");
        }

        var tokens = 0;
        if (RoleRequirements.Of(role, EnvelopeDimension.Tokens) != EnvelopeRequirement.NotApplicable)
        {
            tokens = Math.Min(Math.Min(parent.Budget.MaxTokens, profile.MaxTokens), request.MaxTokens ?? int.MaxValue);
            if (tokens < 1)
            {
                return EnvelopeReduction.Denied(EnvelopeDimension.Tokens, $"{role} role: the parent envelope, its profile and the request leave it no model tokens.");
            }
        }

        var deadline = Min(parent.Budget.DeadlineUtc, AddSaturating(now, profile.MaxDuration));
        if (request.DeadlineUtc is { } requestedDeadline)
        {
            deadline = Min(deadline, requestedDeadline);
        }

        var envelope = new AuthorityEnvelope(
            parent.Originator,
            RoleRequirements.RoleDepth,
            skills,
            capabilities,
            tools,
            risk,
            blastRadius,
            targets,
            environments,
            new DelegationBudget(steps, tokens, deadline),
            window);

        return EnvelopeReduction.Granted(envelope, NarrowedDimensions(parent, envelope));
    }

    private static AuthorityEnvelope BuildRoot(
        IReadOnlyList<RoleProfile> profiles, DelegationAuthorityRequest request, ActorIdentity originator, DateTimeOffset now)
    {
        var maxRisk = profiles.Max(p => Min(p.MaxRisk, RoleRequirements.RiskCap(p.Role)));
        var maxBlastRadius = profiles.Max(p => p.MaxBlastRadius);
        var steps = SaturatingSum(profiles.Select(p => p.MaxSteps));
        var tokens = SaturatingSum(profiles.Select(p => p.MaxTokens));
        var duration = SaturatingSum(profiles.Select(p => p.MaxDuration));
        var deadline = AddSaturating(now, duration);

        return new AuthorityEnvelope(
            originator,
            Depth: RoleRequirements.RoleDepth - 1,
            NarrowTo(Union(profiles.Select(p => p.AllowedSkills)), request.AllowedSkills),
            NarrowTo(Union(profiles.Select(p => p.AllowedCapabilities)), request.AllowedCapabilities),
            NarrowTo(Union(profiles.Select(p => p.AllowedTools)), request.AllowedTools),
            request.MaxRisk is { } risk ? Min(risk, maxRisk) : maxRisk,
            request.MaxBlastRadius is { } blast ? Min(blast, maxBlastRadius) : maxBlastRadius,
            NarrowTo(Union(profiles.Select(p => p.AllowedTargets)), request.AllowedTargets),
            NarrowTo(Union(profiles.Select(p => p.AllowedEnvironments)), request.AllowedEnvironments),
            new DelegationBudget(
                request.MaxSteps is { } requestedSteps ? Math.Min(requestedSteps, steps) : steps,
                request.MaxTokens is { } requestedTokens ? Math.Min(requestedTokens, tokens) : tokens,
                request.DeadlineUtc is { } requestedDeadline ? Min(requestedDeadline, deadline) : deadline),
            request.Window);
    }

    /// <summary>
    /// Derives one set dimension. Returns a refusal when the role requires the dimension and the result is
    /// empty; otherwise <c>null</c> with the members in <paramref name="members"/>: sorted, de-duplicated and
    /// ordinal, so the same inputs always give the same envelope hash.
    /// </summary>
    private static EnvelopeReduction? DeriveSet(
        AgentRoleKind role,
        EnvelopeDimension dimension,
        IReadOnlyList<string> parent,
        IReadOnlyList<string> profile,
        IReadOnlyList<string>? request,
        out IReadOnlyList<string> members)
    {
        var requirement = RoleRequirements.Of(role, dimension);
        if (requirement == EnvelopeRequirement.NotApplicable)
        {
            members = [];
            return null;
        }

        IEnumerable<string> common = parent.Intersect(profile, StringComparer.Ordinal);
        if (request is not null)
        {
            common = common.Intersect(request, StringComparer.Ordinal);
        }

        members = SortedDistinct(common);
        if (members.Count > 0 || requirement == EnvelopeRequirement.Optional)
        {
            return null;
        }

        // Say who granted nothing: it tells the operator which of the three to change.
        var reason = parent.Count == 0
            ? $"{role} role: the parent envelope grants no {dimension}."
            : profile.Count == 0
                ? $"{role} role: its profile grants no {dimension}."
                : request is { Count: 0 }
                    ? $"{role} role: the request narrows {dimension} to nothing."
                    : $"{role} role: the parent envelope, its profile and the request have no {dimension} in common.";
        return EnvelopeReduction.Denied(dimension, reason);
    }

    /// <summary>Intersects every window that is present. Returns true when they do not overlap; a window ends exclusively, so windows that merely touch do not.</summary>
    private static bool IntersectWindows(MaintenanceWindow? parent, MaintenanceWindow? profile, MaintenanceWindow? request, out MaintenanceWindow? result)
    {
        var present = new[] { parent, profile, request }.OfType<MaintenanceWindow>().ToList();
        if (present.Count == 0)
        {
            result = null;
            return false;
        }

        var start = present.Max(w => w.StartUtc);
        var end = present.Min(w => w.EndUtc);
        if (end <= start)
        {
            result = null;
            return true;
        }

        result = new MaintenanceWindow(start, end);
        return false;
    }

    /// <summary>The dimensions in which <paramref name="after"/> is narrower than <paramref name="before"/>, in <see cref="EnvelopeDimension"/> order. Depth and originator are not narrowings.</summary>
    private static List<EnvelopeDimension> NarrowedDimensions(AuthorityEnvelope before, AuthorityEnvelope after)
    {
        var narrowed = new List<EnvelopeDimension>();

        AddIfNarrower(narrowed, EnvelopeDimension.Skills, before.AllowedSkills, after.AllowedSkills);
        AddIfNarrower(narrowed, EnvelopeDimension.Capabilities, before.AllowedCapabilities, after.AllowedCapabilities);
        AddIfNarrower(narrowed, EnvelopeDimension.Tools, before.AllowedTools, after.AllowedTools);
        if (after.MaxRisk < before.MaxRisk)
        {
            narrowed.Add(EnvelopeDimension.Risk);
        }

        if (after.MaxBlastRadius < before.MaxBlastRadius)
        {
            narrowed.Add(EnvelopeDimension.BlastRadius);
        }

        AddIfNarrower(narrowed, EnvelopeDimension.Targets, before.AllowedTargets, after.AllowedTargets);
        AddIfNarrower(narrowed, EnvelopeDimension.Environments, before.AllowedEnvironments, after.AllowedEnvironments);
        if (WindowIsNarrower(before.Window, after.Window))
        {
            narrowed.Add(EnvelopeDimension.MaintenanceWindow);
        }

        if (after.Budget.MaxSteps < before.Budget.MaxSteps)
        {
            narrowed.Add(EnvelopeDimension.Steps);
        }

        if (after.Budget.MaxTokens < before.Budget.MaxTokens)
        {
            narrowed.Add(EnvelopeDimension.Tokens);
        }

        if (after.Budget.DeadlineUtc < before.Budget.DeadlineUtc)
        {
            narrowed.Add(EnvelopeDimension.Deadline);
        }

        return narrowed;
    }

    // The second set is always a subset of the first (it is derived by intersection), so fewer distinct
    // members is exactly "narrower".
    private static void AddIfNarrower(List<EnvelopeDimension> narrowed, EnvelopeDimension dimension, IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        if (SortedDistinct(after).Count < SortedDistinct(before).Count)
        {
            narrowed.Add(dimension);
        }
    }

    private static bool WindowIsNarrower(MaintenanceWindow? before, MaintenanceWindow? after) =>
        before is null
            ? after is not null
            : after is not null && (after.StartUtc > before.StartUtc || after.EndUtc < before.EndUtc);

    private static List<string> Union(IEnumerable<IReadOnlyList<string>> sets) => SortedDistinct(sets.SelectMany(set => set));

    private static List<string> NarrowTo(List<string> members, IReadOnlyList<string>? request) =>
        request is null ? members : SortedDistinct(members.Intersect(request, StringComparer.Ordinal));

    private static List<string> SortedDistinct(IEnumerable<string> members) =>
        [.. members.Distinct(StringComparer.Ordinal).OrderBy(member => member, StringComparer.Ordinal)];

    // Enum ceilings (RiskLevel, BlastRadius) are ordered by their declared order, which the frozen-values tests pin.
    private static T Min<T>(T first, T second)
        where T : struct, Enum =>
        Comparer<T>.Default.Compare(first, second) <= 0 ? first : second;

    private static DateTimeOffset Min(DateTimeOffset first, DateTimeOffset second) => first <= second ? first : second;

    private static int SaturatingSum(IEnumerable<int> amounts) =>
        (int)Math.Min(amounts.Sum(amount => (long)amount), int.MaxValue);

    private static TimeSpan SaturatingSum(IEnumerable<TimeSpan> durations)
    {
        var ticks = 0L;
        foreach (var duration in durations)
        {
            ticks = duration.Ticks > long.MaxValue - ticks ? long.MaxValue : ticks + duration.Ticks;
        }

        return new TimeSpan(ticks);
    }

    /// <summary>Adds a duration to an instant, stopping at the end of time instead of throwing.</summary>
    private static DateTimeOffset AddSaturating(DateTimeOffset start, TimeSpan duration) =>
        duration > DateTimeOffset.MaxValue - start ? DateTimeOffset.MaxValue : start + duration;

    private static EnvelopeReduction NoProfile(AgentRoleKind role) =>
        EnvelopeReduction.Denied(
            EnvelopeDimension.Profile,
            $"{role} role: no usable profile is configured. Delegation stays off until the operator configures one for every role.");

    private static EnvelopeReduction WrongProfile(AgentRoleKind role, RoleProfile profile) =>
        EnvelopeReduction.Denied(EnvelopeDimension.Profile, $"{role} role: the profile supplied is for the {profile.Role} role.");
}
