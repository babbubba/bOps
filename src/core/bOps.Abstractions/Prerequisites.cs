// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace bOps.Abstractions;

/// <summary>The observed state of one prerequisite (ADR-0049).</summary>
public enum PrerequisiteState
{
    /// <summary>Never checked. Host-internal: a check that returns it is recorded as <see cref="Error"/>.</summary>
    Unknown = 0,

    /// <summary>The prerequisite is fully usable.</summary>
    Available = 1,

    /// <summary>The prerequisite is usable with reduced function. Components depending on it stay available.</summary>
    Degraded = 2,

    /// <summary>The prerequisite is absent or not usable. Components that require it are unavailable.</summary>
    Unavailable = 3,

    /// <summary>The check itself failed, timed out or returned an invalid result; availability is not known.</summary>
    Error = 4,
}

/// <summary>How a component depends on a prerequisite.</summary>
public enum PrerequisiteRequirement
{
    /// <summary>The component cannot run without it (<see cref="ToolManifest.Requires"/>).</summary>
    Required = 0,

    /// <summary>The component runs without it, degraded (<see cref="ToolManifest.OptionalRequires"/>).</summary>
    Optional = 1,
}

/// <summary>What kind of dependency a prerequisite describes. Informational: it never changes how a check is run.</summary>
public enum PrerequisiteKind
{
    /// <summary>Anything not covered by another kind.</summary>
    Other = 0,

    /// <summary>An external executable, such as a debugger or a command-line client.</summary>
    Executable = 1,

    /// <summary>A library or client runtime, such as a database driver.</summary>
    Library = 2,

    /// <summary>A local daemon or operating-system service.</summary>
    Service = 3,

    /// <summary>A network endpoint, such as a search instance or a database server.</summary>
    Endpoint = 4,

    /// <summary>Operator configuration that must be present.</summary>
    Configuration = 5,
}

/// <summary>Maps a <see cref="PrerequisiteState"/> to the boolean availability view of <see cref="ICapabilityProbe"/>.</summary>
public static class PrerequisiteStateExtensions
{
    /// <summary>
    /// Whether a component requiring a prerequisite in this state may run: <see cref="PrerequisiteState.Available"/> and
    /// <see cref="PrerequisiteState.Degraded"/> are satisfied; every other state, <see cref="PrerequisiteState.Unknown"/>
    /// included, is not.
    /// </summary>
    /// <param name="state">The observed state.</param>
    public static bool IsSatisfied(this PrerequisiteState state) =>
        state is PrerequisiteState.Available or PrerequisiteState.Degraded;
}

/// <summary>Format rules for the stable identifiers used by prerequisites and system messages (ADR-0049).</summary>
public static class OperationalIdentifier
{
    /// <summary>The maximum length of a prerequisite id.</summary>
    public const int MaxPrerequisiteIdLength = 128;

    /// <summary>The maximum length of a code.</summary>
    public const int MaxCodeLength = 64;

    /// <summary>The maximum length of a system message source.</summary>
    public const int MaxSourceLength = 160;

    /// <summary>
    /// Whether <paramref name="id"/> is a valid prerequisite id: lowercase ASCII letters, digits, <c>.</c>, <c>_</c> and
    /// <c>-</c>, starting with a letter or digit, at most <see cref="MaxPrerequisiteIdLength"/> characters.
    /// </summary>
    /// <param name="id">The candidate id.</param>
    public static bool IsValidPrerequisiteId(string? id) =>
        id is { Length: > 0 and <= MaxPrerequisiteIdLength }
        && IsLowerAlphanumeric(id[0])
        && id.All(c => IsLowerAlphanumeric(c) || c is '.' or '_' or '-');

    /// <summary>
    /// Whether <paramref name="code"/> is a valid stable code: lowercase alphanumeric segments separated by single
    /// <c>.</c> or <c>-</c> (<c>prerequisite.check-failed</c>), at most <see cref="MaxCodeLength"/> characters.
    /// </summary>
    /// <param name="code">The candidate code.</param>
    public static bool IsValidCode(string? code)
    {
        if (string.IsNullOrEmpty(code)
            || code.Length > MaxCodeLength
            || !IsLowerAlphanumeric(code[0])
            || !IsLowerAlphanumeric(code[^1]))
        {
            return false;
        }

        for (var i = 0; i < code.Length; i++)
        {
            var c = code[i];
            if (c is '.' or '-')
            {
                if (code[i - 1] is '.' or '-')
                {
                    return false;
                }
            }
            else if (!IsLowerAlphanumeric(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether <paramref name="source"/> is a valid system message source: <c>&lt;kind&gt;/&lt;name&gt;</c>, where the kind
    /// is lowercase alphanumeric with <c>-</c> and the name follows <see cref="IsValidPrerequisiteId"/>
    /// (<c>prerequisite/docker</c>, <c>runtime/agent</c>, <c>plugin/acme.postgres</c>).
    /// </summary>
    /// <param name="source">The candidate source.</param>
    public static bool IsValidSource(string? source)
    {
        if (string.IsNullOrEmpty(source) || source.Length > MaxSourceLength)
        {
            return false;
        }

        var slash = source.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0)
        {
            return false;
        }

        var kind = source[..slash];
        return IsLowerAlphanumeric(kind[0])
            && kind.All(c => IsLowerAlphanumeric(c) || c == '-')
            && IsValidPrerequisiteId(source[(slash + 1)..]);
    }

    private static bool IsLowerAlphanumeric(char c) => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c);
}

/// <summary>
/// Describes one prerequisite a package can check: its stable id, operator-facing text and how long a check may take.
/// The id is what <see cref="ToolManifest.Requires"/>, <see cref="ToolManifest.OptionalRequires"/>,
/// <see cref="CapabilityManifest.Requires"/> and <see cref="CapabilityManifest.OptionalRequires"/> name.
/// </summary>
public sealed record PrerequisiteDescriptor
{
    /// <summary>The check timeout used when none is declared.</summary>
    public static readonly TimeSpan DefaultCheckTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The shortest check timeout the host honours.</summary>
    public static readonly TimeSpan MinCheckTimeout = TimeSpan.FromSeconds(1);

    /// <summary>The longest check timeout the host honours.</summary>
    public static readonly TimeSpan MaxCheckTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The maximum length of <see cref="DisplayName"/>.</summary>
    public const int MaxDisplayNameLength = 128;

    /// <summary>The maximum length of <see cref="Description"/> and <see cref="Remediation"/>.</summary>
    public const int MaxTextLength = 1024;

    /// <summary>Creates a prerequisite descriptor.</summary>
    /// <param name="Id">The stable id; see <see cref="OperationalIdentifier.IsValidPrerequisiteId"/>.</param>
    /// <param name="DisplayName">A short operator-facing name, e.g. <c>Debugging Tools for Windows</c>.</param>
    /// <param name="Description">What the prerequisite is and why components need it.</param>
    /// <param name="Kind">What kind of dependency it is.</param>
    public PrerequisiteDescriptor(string Id, string DisplayName, string Description, PrerequisiteKind Kind = PrerequisiteKind.Other)
    {
        if (!OperationalIdentifier.IsValidPrerequisiteId(Id))
        {
            throw new ArgumentException($"'{Id}' is not a valid prerequisite id.", nameof(Id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(DisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(Description);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(DisplayName.Length, MaxDisplayNameLength, nameof(DisplayName));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Description.Length, MaxTextLength, nameof(Description));

        this.Id = Id;
        this.DisplayName = DisplayName;
        this.Description = Description;
        this.Kind = Kind;
    }

    /// <summary>The stable id.</summary>
    public string Id { get; init; }

    /// <summary>A short operator-facing name.</summary>
    public string DisplayName { get; init; }

    /// <summary>What the prerequisite is and why components need it.</summary>
    public string Description { get; init; }

    /// <summary>What kind of dependency it is.</summary>
    public PrerequisiteKind Kind { get; init; }

    /// <summary>Operator-facing guidance on installing or configuring the prerequisite, if any.</summary>
    public string? Remediation { get; init; }

    /// <summary>How long one check may run. The host clamps it to <see cref="MinCheckTimeout"/>–<see cref="MaxCheckTimeout"/>.</summary>
    public TimeSpan CheckTimeout { get; init; } = DefaultCheckTimeout;

    /// <summary>Static, non-secret facts about the prerequisite, e.g. <c>requiredExecutable</c> or <c>installComponent</c>.</summary>
    public OperationalMetadata Metadata { get; init; } = OperationalMetadata.Empty;
}

/// <summary>
/// What one prerequisite check observed. The host adds the prerequisite id and the time when it records the
/// <see cref="PrerequisiteCheckResult"/>; a check can never claim another prerequisite's identity.
/// </summary>
/// <param name="State">The observed state. <see cref="PrerequisiteState.Unknown"/> is not a valid observation.</param>
/// <param name="Code">A stable machine-readable reason, e.g. <c>executable-not-found</c>; see <see cref="OperationalIdentifier.IsValidCode"/>.</param>
/// <param name="Message">A short operator-facing explanation, at most <see cref="MaxMessageLength"/> characters. Never a secret.</param>
public sealed record PrerequisiteCheckOutcome(PrerequisiteState State, string Code, string Message)
{
    /// <summary>The maximum length of <see cref="Message"/>.</summary>
    public const int MaxMessageLength = 512;

    /// <summary>Bounded, non-secret detail about the observation.</summary>
    public OperationalMetadata Metadata { get; init; } = OperationalMetadata.Empty;
}

/// <summary>A host-recorded prerequisite observation: the check's outcome stamped with the prerequisite id and the check time.</summary>
public sealed record PrerequisiteCheckResult
{
    /// <summary>The prerequisite id, taken from the registered descriptor.</summary>
    public required string Id { get; init; }

    /// <summary>The observed state.</summary>
    public required PrerequisiteState State { get; init; }

    /// <summary>The stable reason code.</summary>
    public required string Code { get; init; }

    /// <summary>The operator-facing explanation.</summary>
    public required string Message { get; init; }

    /// <summary>When the host finished the check.</summary>
    public required DateTimeOffset CheckedAtUtc { get; init; }

    /// <summary>Bounded, non-secret detail.</summary>
    public OperationalMetadata Metadata { get; init; } = OperationalMetadata.Empty;

    /// <summary>Whether a component requiring this prerequisite may run; see <see cref="PrerequisiteStateExtensions.IsSatisfied"/>.</summary>
    [JsonIgnore]
    public bool IsSatisfied => State.IsSatisfied();
}

/// <summary>
/// One package-owned prerequisite check. A check is <b>read-only by contract</b>: it observes (a file exists, a daemon
/// answers, a setting is configured) and never installs, starts, configures or changes anything. It must honour
/// <c>ct</c>; the host also cancels it after <see cref="PrerequisiteDescriptor.CheckTimeout"/>. A check reports expected
/// conditions through its outcome and never throws for them; a thrown exception is recorded as
/// <see cref="PrerequisiteState.Error"/> without its message.
/// </summary>
public interface IPrerequisiteCheck
{
    /// <summary>What this check is about.</summary>
    PrerequisiteDescriptor Descriptor { get; }

    /// <summary>Observes the prerequisite once.</summary>
    /// <param name="ct">Cancelled by the caller or when the check timeout elapses.</param>
    Task<PrerequisiteCheckOutcome> CheckAsync(CancellationToken ct = default);
}

/// <summary>
/// Implemented, optionally, by a package entry point (<see cref="IToolProvider"/> or <see cref="ISkillProvider"/>) to
/// contribute prerequisite checks. First-party packages and enabled plugins use this same mechanism; the host registers
/// each check under the package's host-assigned <see cref="PackageId"/> and removes them with the package.
/// </summary>
public interface IPrerequisiteProvider
{
    /// <summary>Returns this package's checks. The host snapshots and validates the result.</summary>
    IReadOnlyList<IPrerequisiteCheck> GetPrerequisiteChecks();
}

/// <summary>
/// The host-only route by which a package's <see cref="IPrerequisiteProvider"/> contributions are registered under the
/// host-assigned <see cref="PackageId"/> and removed with the package (ADR-0049 section 4). It is a mutation surface:
/// the host composition and the plugin loader hold it; it is <b>never</b> resolvable by package code, and the
/// <see cref="ICapabilityProbe"/> a package receives does not implement it.
/// </summary>
public interface IPrerequisiteRegistrar
{
    /// <summary>
    /// Registers every check <paramref name="provider"/> contributes under <paramref name="package"/>, atomically: all of them
    /// or none.
    /// </summary>
    /// <param name="package">The host-assigned package identity. A package never names its own.</param>
    /// <param name="provider">The package's provider.</param>
    void Register(PackageId package, IPrerequisiteProvider provider);

    /// <summary>Removes every check contributed by <paramref name="package"/>, and their last results.</summary>
    /// <param name="package">The host-assigned package identity.</param>
    void Unregister(PackageId package);
}

/// <summary>The persisted last observation of one prerequisite on one node, used to detect transitions (ADR-0049 section 7).</summary>
public sealed record PrerequisiteStateRecord
{
    /// <summary>The node the prerequisite was checked on.</summary>
    public required NodeId Node { get; init; }

    /// <summary>The prerequisite id.</summary>
    public required string PrerequisiteId { get; init; }

    /// <summary>The last observed state.</summary>
    public required PrerequisiteState State { get; init; }

    /// <summary>The last observed reason code.</summary>
    public required string Code { get; init; }

    /// <summary>The last observed operator-facing explanation.</summary>
    public required string Message { get; init; }

    /// <summary>When the last check finished.</summary>
    public required DateTimeOffset CheckedAtUtc { get; init; }

    /// <summary>When <see cref="Fingerprint"/> last changed.</summary>
    public required DateTimeOffset ChangedAtUtc { get; init; }

    /// <summary>The last observed detail.</summary>
    public OperationalMetadata Metadata { get; init; } = OperationalMetadata.Empty;

    /// <summary>The value whose change is a transition: state and code, never message text or metadata.</summary>
    [JsonIgnore]
    public string Fingerprint => ComputeFingerprint(State, Code);

    /// <summary>Computes the transition fingerprint of a state and code.</summary>
    /// <param name="state">The state.</param>
    /// <param name="code">The reason code.</param>
    public static string ComputeFingerprint(PrerequisiteState state, string code) => $"{state}|{code}";
}

/// <summary>Node-local persistence of the last observation of each prerequisite (ADR-0049 section 8).</summary>
public interface IPrerequisiteStateStore
{
    /// <summary>Loads the last observation of one prerequisite, or <c>null</c> if it was never recorded.</summary>
    Task<PrerequisiteStateRecord?> LoadStateAsync(NodeId node, string prerequisiteId, CancellationToken ct = default);

    /// <summary>Lists every recorded prerequisite of a node, ordered by id.</summary>
    Task<IReadOnlyList<PrerequisiteStateRecord>> ListStatesAsync(NodeId node, CancellationToken ct = default);

    /// <summary>
    /// Atomically replaces the observation of <paramref name="state"/>'s prerequisite and, when given, appends its
    /// transition message — but only if the stored fingerprint still equals <paramref name="expectedFingerprint"/>
    /// (<c>null</c>: no row yet). Returns <c>false</c> and writes nothing when another writer got there first.
    /// </summary>
    Task<bool> SaveStateAsync(
        PrerequisiteStateRecord state,
        string? expectedFingerprint,
        SystemMessage? transitionMessage,
        CancellationToken ct = default);
}
