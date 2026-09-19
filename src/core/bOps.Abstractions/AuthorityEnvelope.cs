// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace bOps.Abstractions;

/// <summary>
/// One dimension of an <see cref="AuthorityEnvelope"/>. Used to say which dimensions a child narrowed
/// and, when delegation is refused, which dimension had an empty intersection (ADR-0030 section 3).
/// The numeric values are persisted and audited and must never be reordered.
/// </summary>
public enum EnvelopeDimension
{
    /// <summary>The operator on whose authority the run executes.</summary>
    Originator,

    /// <summary>How many delegations deep the agent is.</summary>
    Depth,

    /// <summary>The Skills the agent may use.</summary>
    Skills,

    /// <summary>The Capabilities the agent may prepare or execute.</summary>
    Capabilities,

    /// <summary>The tools the agent may invoke.</summary>
    Tools,

    /// <summary>The highest <see cref="RiskLevel"/> the agent may reach.</summary>
    Risk,

    /// <summary>The largest <see cref="BlastRadius"/> the agent may reach.</summary>
    BlastRadius,

    /// <summary>The targets the agent may act on.</summary>
    Targets,

    /// <summary>The environments the agent may act in.</summary>
    Environments,

    /// <summary>The UTC interval in which the agent may act.</summary>
    MaintenanceWindow,

    /// <summary>The number of steps the agent may take.</summary>
    Steps,

    /// <summary>The number of model tokens the agent may consume.</summary>
    Tokens,

    /// <summary>The absolute UTC instant by which the agent must be finished.</summary>
    Deadline,

    /// <summary>
    /// Not a field of the envelope: the role has no usable profile, because none is configured or the
    /// configuration that would define it is malformed (ADR-0031 section 5). Delegation is refused.
    /// Appended after the values persisted since <c>1.2.0-preview.1</c>, so none of them moves.
    /// </summary>
    Profile,
}

/// <summary>An interval of UTC time during which an agent may act, from <see cref="StartUtc"/> inclusive to <see cref="EndUtc"/> exclusive.</summary>
public sealed record MaintenanceWindow
{
    /// <summary>Creates a maintenance window.</summary>
    /// <param name="StartUtc">When the window opens.</param>
    /// <param name="EndUtc">When the window closes. Must be after <paramref name="StartUtc"/>.</param>
    /// <exception cref="ArgumentException">The window is empty or reversed.</exception>
    public MaintenanceWindow(DateTimeOffset StartUtc, DateTimeOffset EndUtc)
    {
        if (EndUtc <= StartUtc)
        {
            throw new ArgumentException("A maintenance window must end after it starts.", nameof(EndUtc));
        }

        this.StartUtc = StartUtc;
        this.EndUtc = EndUtc;
    }

    /// <summary>When the window opens.</summary>
    public DateTimeOffset StartUtc { get; init; }

    /// <summary>When the window closes.</summary>
    public DateTimeOffset EndUtc { get; init; }
}

/// <summary>
/// What an agent may spend (ADR-0030 section 6): a number of steps, a number of model tokens and an
/// absolute deadline. A child's budget is reserved from its parent's remaining amount before the role
/// starts, so the amounts here are ceilings, never top-ups.
/// </summary>
public sealed record DelegationBudget
{
    /// <summary>Creates a budget.</summary>
    /// <param name="MaxSteps">The steps the agent may take. Zero means none.</param>
    /// <param name="MaxTokens">The prompt and completion tokens the agent may consume. Zero means none.</param>
    /// <param name="DeadlineUtc">The absolute UTC instant by which the agent must be finished.</param>
    /// <exception cref="ArgumentOutOfRangeException">An amount is negative.</exception>
    public DelegationBudget(int MaxSteps, int MaxTokens, DateTimeOffset DeadlineUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(MaxSteps);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxTokens);

        this.MaxSteps = MaxSteps;
        this.MaxTokens = MaxTokens;
        this.DeadlineUtc = DeadlineUtc;
    }

    /// <summary>The steps the agent may take.</summary>
    public int MaxSteps { get; init; }

    /// <summary>The prompt and completion tokens the agent may consume.</summary>
    public int MaxTokens { get; init; }

    /// <summary>The absolute UTC instant by which the agent must be finished.</summary>
    public DateTimeOffset DeadlineUtc { get; init; }
}

/// <summary>
/// What an agent has spent so far. Persisted with the run, so restarting a role never resets its
/// budget (ADR-0030 section 6).
/// </summary>
public sealed record BudgetConsumption
{
    /// <summary>Creates a consumption record.</summary>
    /// <param name="Steps">Steps taken.</param>
    /// <param name="Tokens">Prompt and completion tokens consumed.</param>
    /// <exception cref="ArgumentOutOfRangeException">An amount is negative.</exception>
    public BudgetConsumption(int Steps, int Tokens)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(Steps);
        ArgumentOutOfRangeException.ThrowIfNegative(Tokens);

        this.Steps = Steps;
        this.Tokens = Tokens;
    }

    /// <summary>Nothing spent yet.</summary>
    public static BudgetConsumption Empty { get; } = new(0, 0);

    /// <summary>Steps taken.</summary>
    public int Steps { get; init; }

    /// <summary>Prompt and completion tokens consumed.</summary>
    public int Tokens { get; init; }
}

/// <summary>
/// What one agent of a delegated run may do (ADR-0030 section 3): an immutable value with exact,
/// non-wildcard members. An envelope only ever <em>reduces</em> authority — a child's is the
/// intersection of its parent's, its role profile's and the request's, dimension by dimension, and an
/// empty intersection in any dimension is a refusal, never a fallback (<see cref="EnvelopeReduction"/>).
/// The runtime enforces it in the single step-execution path before policy is evaluated, and it can
/// only deny: policy stays the sole source of <see cref="PolicyMode.Automatic"/> and
/// <see cref="PolicyMode.Approval"/>.
/// </summary>
/// <remarks>
/// An empty set means that nothing in that dimension is permitted; it never means "unrestricted".
/// This type states an agent's authority. It computes nothing: reduction and enforcement belong to the
/// runtime. Because a C# <c>with</c> expression does not run the constructor, code that builds a
/// narrowed copy must go through the runtime's reduction rather than editing a granted envelope.
/// </remarks>
public sealed record AuthorityEnvelope
{
    /// <summary>Creates an envelope.</summary>
    /// <param name="Originator">The operator on whose authority the run executes. Every envelope of a run has the same originator.</param>
    /// <param name="Depth">How many delegations deep the agent is. <c>0</c> is the objective's root envelope, <c>1</c> a role of the orchestrator.</param>
    /// <param name="AllowedSkills">Exact Skill ids the agent may use.</param>
    /// <param name="AllowedCapabilities">Exact Capability names the agent may prepare or execute.</param>
    /// <param name="AllowedTools">Exact tool names the agent may invoke through the runtime. A tool that is not listed is never callable by the agent, whatever its risk.</param>
    /// <param name="MaxRisk">The highest tool or capability risk the agent may reach. <see cref="RiskLevel.Critical"/> stays forbidden by policy whatever this says.</param>
    /// <param name="MaxBlastRadius">The largest blast radius the agent may reach.</param>
    /// <param name="AllowedTargets">Exact target labels the agent may act on.</param>
    /// <param name="AllowedEnvironments">Exact environment labels the agent may act in.</param>
    /// <param name="Budget">What the agent may spend.</param>
    /// <param name="Window">When the agent may act, or <c>null</c> for no time restriction beyond the budget's deadline.</param>
    /// <exception cref="ArgumentException">A set contains a null, blank, padded or wildcard member (<c>*</c> or <c>?</c>).</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="Depth"/> is negative or a ceiling is not a defined value.</exception>
    public AuthorityEnvelope(
        ActorIdentity Originator,
        int Depth,
        IReadOnlyList<string> AllowedSkills,
        IReadOnlyList<string> AllowedCapabilities,
        IReadOnlyList<string> AllowedTools,
        RiskLevel MaxRisk,
        BlastRadius MaxBlastRadius,
        IReadOnlyList<string> AllowedTargets,
        IReadOnlyList<string> AllowedEnvironments,
        DelegationBudget Budget,
        MaintenanceWindow? Window = null)
    {
        ArgumentNullException.ThrowIfNull(Originator);
        ArgumentNullException.ThrowIfNull(Budget);
        ArgumentOutOfRangeException.ThrowIfNegative(Depth);
        if (!Enum.IsDefined(MaxRisk))
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRisk), MaxRisk, "Unknown risk level.");
        }

        if (!Enum.IsDefined(MaxBlastRadius))
        {
            throw new ArgumentOutOfRangeException(nameof(MaxBlastRadius), MaxBlastRadius, "Unknown blast radius.");
        }

        this.Originator = Originator;
        this.Depth = Depth;
        this.AllowedSkills = EnvelopeMembers.ValidateExact(AllowedSkills, nameof(AllowedSkills));
        this.AllowedCapabilities = EnvelopeMembers.ValidateExact(AllowedCapabilities, nameof(AllowedCapabilities));
        this.AllowedTools = EnvelopeMembers.ValidateExact(AllowedTools, nameof(AllowedTools));
        this.MaxRisk = MaxRisk;
        this.MaxBlastRadius = MaxBlastRadius;
        this.AllowedTargets = EnvelopeMembers.ValidateExact(AllowedTargets, nameof(AllowedTargets));
        this.AllowedEnvironments = EnvelopeMembers.ValidateExact(AllowedEnvironments, nameof(AllowedEnvironments));
        this.Budget = Budget;
        this.Window = Window;
    }

    /// <summary>The operator on whose authority the run executes.</summary>
    public ActorIdentity Originator { get; init; }

    /// <summary>How many delegations deep the agent is.</summary>
    public int Depth { get; init; }

    /// <summary>Exact Skill ids the agent may use.</summary>
    public IReadOnlyList<string> AllowedSkills { get; init; }

    /// <summary>Exact Capability names the agent may prepare or execute.</summary>
    public IReadOnlyList<string> AllowedCapabilities { get; init; }

    /// <summary>Exact tool names the agent may invoke through the runtime.</summary>
    public IReadOnlyList<string> AllowedTools { get; init; }

    /// <summary>The highest tool or capability risk the agent may reach.</summary>
    public RiskLevel MaxRisk { get; init; }

    /// <summary>The largest blast radius the agent may reach.</summary>
    public BlastRadius MaxBlastRadius { get; init; }

    /// <summary>Exact target labels the agent may act on.</summary>
    public IReadOnlyList<string> AllowedTargets { get; init; }

    /// <summary>Exact environment labels the agent may act in.</summary>
    public IReadOnlyList<string> AllowedEnvironments { get; init; }

    /// <summary>What the agent may spend.</summary>
    public DelegationBudget Budget { get; init; }

    /// <summary>When the agent may act, or <c>null</c> for no time restriction beyond the budget's deadline.</summary>
    public MaintenanceWindow? Window { get; init; }
}

/// <summary>
/// The one definition of an acceptable envelope member, shared by the envelope, a role profile and an
/// authority request so the three cannot drift apart: an exact, unpadded, non-blank name (ADR-0025's
/// exact-match posture) with no wildcard. It returns a read-only snapshot, not the list it was given: these
/// contracts state authority and claim to be immutable values, so a list the caller keeps and edits after
/// construction must not be able to widen what was validated or slip in a wildcard.
/// </summary>
internal static class EnvelopeMembers
{
    internal static IReadOnlyList<string> ValidateExact(IReadOnlyList<string> members, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(members, parameterName);

        // Copy first, then validate the copy, so nothing can change between the check and the use.
        string[] snapshot = [.. members];
        foreach (var member in snapshot)
        {
            if (string.IsNullOrWhiteSpace(member) || member.Trim().Length != member.Length)
            {
                throw new ArgumentException("An envelope member must be a non-blank, unpadded name.", parameterName);
            }

            if (member.AsSpan().IndexOfAny('*', '?') >= 0)
            {
                throw new ArgumentException("An envelope member is an exact name; wildcards are not allowed.", parameterName);
            }
        }

        return Array.AsReadOnly(snapshot);
    }
}

/// <summary>The dimension in which delegation was refused, and why (ADR-0030 section 3).</summary>
public sealed record DelegationDenial
{
    /// <summary>Creates a denial.</summary>
    /// <param name="Dimension">The dimension whose intersection was empty or whose limit was exceeded.</param>
    /// <param name="Reason">Why, in words for the operator and the audit log. Never carries a secret.</param>
    /// <exception cref="ArgumentException"><paramref name="Reason"/> is blank.</exception>
    public DelegationDenial(EnvelopeDimension Dimension, string Reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Reason);

        this.Dimension = Dimension;
        this.Reason = Reason;
    }

    /// <summary>The dimension whose intersection was empty or whose limit was exceeded.</summary>
    public EnvelopeDimension Dimension { get; init; }

    /// <summary>Why delegation was refused.</summary>
    public string Reason { get; init; }
}

/// <summary>
/// The result of computing a child envelope from a parent, a role profile and a request (ADR-0030
/// section 3): either a granted <see cref="Envelope"/> or a <see cref="Denial"/>, never both and never
/// neither. Denial is the outcome of an empty intersection in any dimension; there is no fallback to a
/// default or to the parent's value.
/// </summary>
public sealed record EnvelopeReduction
{
    /// <summary>Creates a reduction result.</summary>
    /// <param name="Envelope">The granted envelope, or <c>null</c> when refused.</param>
    /// <param name="ReducedDimensions">The dimensions in which the granted envelope is narrower than its parent's. Empty when refused.</param>
    /// <param name="Denial">Why delegation was refused, or <c>null</c> when granted.</param>
    /// <exception cref="ArgumentException">Both or neither of <paramref name="Envelope"/> and <paramref name="Denial"/> are given, or a refusal lists reduced dimensions.</exception>
    public EnvelopeReduction(AuthorityEnvelope? Envelope, IReadOnlyList<EnvelopeDimension> ReducedDimensions, DelegationDenial? Denial)
    {
        ArgumentNullException.ThrowIfNull(ReducedDimensions);
        if ((Envelope is null) == (Denial is null))
        {
            throw new ArgumentException("A reduction is either a granted envelope or a denial, never both and never neither.", nameof(Envelope));
        }

        if (Denial is not null && ReducedDimensions.Count != 0)
        {
            throw new ArgumentException("A refused delegation grants nothing, so it reduces nothing.", nameof(ReducedDimensions));
        }

        this.Envelope = Envelope;
        this.ReducedDimensions = ReducedDimensions;
        this.Denial = Denial;
    }

    /// <summary>The granted envelope, or <c>null</c> when refused.</summary>
    public AuthorityEnvelope? Envelope { get; init; }

    /// <summary>The dimensions in which the granted envelope is narrower than its parent's.</summary>
    public IReadOnlyList<EnvelopeDimension> ReducedDimensions { get; init; }

    /// <summary>Why delegation was refused, or <c>null</c> when granted.</summary>
    public DelegationDenial? Denial { get; init; }

    /// <summary>True when delegation was refused.</summary>
    [JsonIgnore]
    public bool IsDenied => Denial is not null;

    /// <summary>A granted reduction.</summary>
    /// <param name="envelope">The granted envelope.</param>
    /// <param name="reducedDimensions">The dimensions narrower than the parent's, or <c>null</c> for none.</param>
    public static EnvelopeReduction Granted(AuthorityEnvelope envelope, IReadOnlyList<EnvelopeDimension>? reducedDimensions = null)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return new EnvelopeReduction(envelope, reducedDimensions ?? [], null);
    }

    /// <summary>A refused reduction.</summary>
    /// <param name="dimension">The dimension that had an empty intersection.</param>
    /// <param name="reason">Why.</param>
    public static EnvelopeReduction Denied(EnvelopeDimension dimension, string reason) =>
        new(null, [], new DelegationDenial(dimension, reason));
}

/// <summary>
/// Computes the hashes the delegation contracts refer to (ADR-0030 sections 7 and 8). Like
/// <see cref="ExecutionPlanHasher"/>, always computed from content and never cached on a record, so a
/// non-destructive copy cannot carry a stale value. Envelope contents are audited only as this hash.
/// </summary>
public static class DelegationHasher
{
    private static readonly JsonSerializerOptions SerializeOptions = new() { WriteIndented = false };

    /// <summary>
    /// Hashes an envelope over a canonical form: keys sorted by ordinal name, every set sorted and
    /// de-duplicated, times as UTC, enums by name, and only the originator's kind and id (a display
    /// name is cosmetic). Returns a lowercase hex SHA-256.
    /// </summary>
    /// <param name="envelope">The envelope to hash.</param>
    public static string ComputeEnvelopeHash(AuthorityEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var node = new JsonObject
        {
            ["originator"] = new JsonObject { ["kind"] = envelope.Originator.Kind, ["id"] = envelope.Originator.Id },
            ["depth"] = envelope.Depth,
            ["skills"] = CanonicalSet(envelope.AllowedSkills),
            ["capabilities"] = CanonicalSet(envelope.AllowedCapabilities),
            ["tools"] = CanonicalSet(envelope.AllowedTools),
            ["maxRisk"] = envelope.MaxRisk.ToString(),
            ["maxBlastRadius"] = envelope.MaxBlastRadius.ToString(),
            ["targets"] = CanonicalSet(envelope.AllowedTargets),
            ["environments"] = CanonicalSet(envelope.AllowedEnvironments),
            ["window"] = envelope.Window is null
                ? null
                : new JsonObject { ["startUtc"] = Utc(envelope.Window.StartUtc), ["endUtc"] = Utc(envelope.Window.EndUtc) },
            ["budget"] = new JsonObject
            {
                ["maxSteps"] = envelope.Budget.MaxSteps,
                ["maxTokens"] = envelope.Budget.MaxTokens,
                ["deadlineUtc"] = Utc(envelope.Budget.DeadlineUtc),
            },
        };

        return Hash(node);
    }

    /// <summary>
    /// Hashes a tool call's arguments over their canonical form (keys sorted by ordinal name), for the
    /// step journal's intent entry (ADR-0030 section 7). The hash is a fingerprint of what was about to
    /// run, so a resumed run can tell whether a step is the one it journaled; the arguments themselves
    /// never enter the journal. Returns a lowercase hex SHA-256.
    /// </summary>
    /// <param name="arguments">The arguments as they appear in the approved plan.</param>
    public static string ComputeArgumentsHash(ToolArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return Hash(arguments.ToJson());
    }

    private static JsonArray CanonicalSet(IReadOnlyList<string> members)
    {
        var array = new JsonArray();
        foreach (var member in members.Distinct(StringComparer.Ordinal).OrderBy(m => m, StringComparer.Ordinal))
        {
            array.Add(member);
        }

        return array;
    }

    // Not "O": that format writes a "+00:00" offset, which the default JSON encoder escapes, so the
    // canonical text would depend on an encoder setting. "Z" has nothing to escape.
    private static string Utc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static string Hash(JsonObject node)
    {
        // CanonicalJson.Sort always returns a non-null JsonObject for a non-null JsonObject input.
        var canonical = CanonicalJson.Sort(node)!;
        var bytes = Encoding.UTF8.GetBytes(canonical.ToJsonString(SerializeOptions));
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
