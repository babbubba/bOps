// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Knowledge;
using bOps.Runtime;

namespace bOps.AgentFramework;

/// <summary>One pack version the operator grants to the Local Agent (ADR-0058). The tenant is never configurable: it is always the Local Agent.</summary>
public sealed class LocalKnowledgeGrant
{
    /// <summary>The pack id, as the signed manifest declares it.</summary>
    public string? PackId { get; init; }

    /// <summary>The exact pack version. There is no "latest".</summary>
    public string? Version { get; init; }

    /// <summary>The kind the signed manifest must declare. The pilot serves <c>Knowledge</c> only.</summary>
    public string? Kind { get; init; }
}

/// <summary>The pack every new task of the CLI is bound to (ADR-0058). One pack per task; this is the host policy, never data from a model or a pack.</summary>
public sealed class LocalKnowledgeBinding
{
    /// <summary>The pack id.</summary>
    public string? PackId { get; init; }

    /// <summary>The exact pack version.</summary>
    public string? Version { get; init; }
}

/// <summary>The read budget of one attempt, inside the ceilings of <see cref="LocalKnowledgeComposition"/>.</summary>
public sealed class LocalKnowledgeBudget
{
    /// <summary>How many reads one attempt may be served.</summary>
    public int MaxReads { get; init; } = DefaultMaxReads;

    /// <summary>How many refused reads end the task as a control-protocol failure.</summary>
    public int MaxRejections { get; init; } = DefaultMaxRejections;

    /// <summary>The most characters one read may return.</summary>
    public int MaxCharactersPerRead { get; init; } = DefaultMaxCharactersPerRead;

    /// <summary>The documented default of <see cref="MaxReads"/>.</summary>
    public const int DefaultMaxReads = 4;

    /// <summary>The documented default of <see cref="MaxRejections"/>.</summary>
    public const int DefaultMaxRejections = 4;

    /// <summary>The documented default of <see cref="MaxCharactersPerRead"/>.</summary>
    public const int DefaultMaxCharactersPerRead = 8000;
}

/// <summary>
/// The <c>Knowledge</c> section of the CLI (ADR-0058). Everything is opt-in: <see cref="Enabled"/> is <c>false</c> unless the operator sets it, and
/// when it is <c>true</c> an invalid value is an error, never a silent return to a run without knowledge. The section names no tenant, no
/// signing key and no source other than one directory the operator controls.
/// </summary>
public sealed class LocalKnowledgeOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Knowledge";

    /// <summary>Whether the CLI opens knowledge for its tasks. <c>false</c> by default.</summary>
    public bool Enabled { get; init; }

    /// <summary>The installed packs, laid out as <c>packs directory/pack id/version</c>. Installing a pack here authorises nothing.</summary>
    public string? PacksDirectory { get; init; }

    /// <summary>The tenant-private, content-addressed snapshots bOps writes of the packs it verified. Operator-only permissions; never under the packs directory.</summary>
    public string? SnapshotsDirectory { get; init; }

    /// <summary>The operator-owned JSON file with the public keys that may sign packs and the revoked publishers. It holds no private key.</summary>
    public string? TrustStorePath { get; init; }

    /// <summary>The pack versions the Local Agent may use. A pack that is installed and signed by a trusted key but not listed here is refused.</summary>
    public IReadOnlyList<LocalKnowledgeGrant> Grants { get; init; } = [];

    /// <summary>The pack new tasks are bound to.</summary>
    public LocalKnowledgeBinding? Binding { get; init; }

    /// <summary>The read budget; the documented defaults apply when omitted.</summary>
    public LocalKnowledgeBudget? Budget { get; init; }
}

/// <summary>The enabled <c>Knowledge</c> configuration is not usable. The message names the key and never echoes file content or key material.</summary>
public sealed class LocalKnowledgeConfigurationException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public LocalKnowledgeConfigurationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with the failure that caused it.</summary>
    public LocalKnowledgeConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception.</summary>
    public LocalKnowledgeConfigurationException()
    {
    }
}

/// <summary>
/// Builds the knowledge host of the CLI (ADR-0058): the pilot composition that the earlier ADRs kept out of every production host. It
/// wires only what already exists and adds no authority: the trust store, grants, one directory source registered for the Local Agent
/// tenant, the snapshot store and the KNOW-01B <see cref="KnowledgeTaskHost"/>. It has no model, no loop and no executor. The Local
/// Agent tenant is <see cref="TenantId.Local"/>, the one <see cref="KnowledgeTaskScope.ForLocalAgent"/> issues; there is no way to name another.
/// </summary>
public static class LocalKnowledgeComposition
{
    /// <summary>The ceiling of <see cref="LocalKnowledgeBudget.MaxReads"/>.</summary>
    public const int MaxReadsCeiling = 8;

    /// <summary>The ceiling of <see cref="LocalKnowledgeBudget.MaxRejections"/>.</summary>
    public const int MaxRejectionsCeiling = 8;

    /// <summary>The ceiling of <see cref="LocalKnowledgeBudget.MaxCharactersPerRead"/>.</summary>
    public const int MaxCharactersCeiling = 16000;

    private const string ProviderLabel = "local-packs";

    /// <summary>
    /// The host for <paramref name="options"/>, or <c>null</c> when knowledge is not enabled, in which case the CLI behaves exactly as it
    /// did before knowledge existed.
    /// </summary>
    /// <exception cref="LocalKnowledgeConfigurationException">Knowledge is enabled and the configuration is not usable.</exception>
    public static IKnowledgeTaskHost? Create(LocalKnowledgeOptions options, IAuditSink audit, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(audit);
        if (!options.Enabled)
        {
            return null;
        }

        var packs = RequiredDirectory(options.PacksDirectory, "PacksDirectory", mustExist: true);
        var snapshots = RequiredDirectory(options.SnapshotsDirectory, "SnapshotsDirectory", mustExist: false);
        if (Overlaps(packs, snapshots))
        {
            throw Invalid("'Knowledge:SnapshotsDirectory' and 'Knowledge:PacksDirectory' must be separate directories, neither inside the other.");
        }

        var trust = LoadTrustStore(options.TrustStorePath);
        var grants = ParseGrants(options.Grants);
        var binding = ParseBinding(options.Binding);
        var budget = ParseBudget(options.Budget);

        var registry = new KnowledgeProviderRegistry();
        var source = new DirectoryKnowledgePackSource(packs);
        registry.Register(source, ProviderLabel, KnowledgeProviderTrust.Trusted, [PackKind.Knowledge], [TenantId.Local]);

        var service = new KnowledgeAdmissionService(
            registry, grants, new KnowledgePackVerifier(trust, EngineVersion()), new KnowledgeSnapshotStore(snapshots), timeProvider);
        return new KnowledgeTaskHost(
            service, new LocalAgentTenantAuthority(), new LocalKnowledgePolicy(source, binding), audit, timeProvider, budget);
    }

    /// <summary>The engine version packs are checked against: the product version of <c>bOps.Abstractions</c>, without a pre-release suffix.</summary>
    internal static Version EngineVersion()
    {
        var version = typeof(IAuditSink).Assembly.GetName().Version ?? new Version(1, 0, 0);
        return new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
    }

    private static string RequiredDirectory(string? value, string key, bool mustExist)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Invalid($"'Knowledge:{key}' is required when knowledge is enabled.");
        }

        string full;
        try
        {
            full = Path.GetFullPath(value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new LocalKnowledgeConfigurationException($"'Knowledge:{key}' is not a valid path.", ex);
        }

        if (mustExist && !Directory.Exists(full))
        {
            throw Invalid($"'Knowledge:{key}' ('{full}') does not exist or is not a directory.");
        }

        if (File.Exists(full))
        {
            throw Invalid($"'Knowledge:{key}' ('{full}') is a file, not a directory.");
        }

        return full;
    }

    private static bool Overlaps(string first, string second)
    {
        static string WithSeparator(string path) => Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return WithSeparator(first).StartsWith(WithSeparator(second), comparison) || WithSeparator(second).StartsWith(WithSeparator(first), comparison);
    }

    private static KnowledgeTrustStore LoadTrustStore(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw Invalid("'Knowledge:TrustStorePath' is required when knowledge is enabled: without a trust store no pack can be verified.");
        }

        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            throw Invalid($"'Knowledge:TrustStorePath' ('{full}') does not exist.");
        }

        KnowledgeTrustStore store;
        try
        {
            store = KnowledgeTrustStore.FromFile(full);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            throw new LocalKnowledgeConfigurationException(
                $"'Knowledge:TrustStorePath' ('{full}') is not a valid trust store: {ex.Message}", ex);
        }

        return store.KeyCount > 0
            ? store
            : throw Invalid($"'Knowledge:TrustStorePath' ('{full}') lists no publisher key, so no pack could ever be admitted.");
    }

    private static KnowledgeGrants ParseGrants(IReadOnlyList<LocalKnowledgeGrant> configured)
    {
        if (configured is not { Count: > 0 })
        {
            throw Invalid("'Knowledge:Grants' must list at least one pack version: an installed pack is never granted implicitly.");
        }

        List<PackGrant> grants = [];
        for (var index = 0; index < configured.Count; index++)
        {
            var grant = configured[index];
            var where = $"Knowledge:Grants:{index}";
            var reference = Reference(grant?.PackId, grant?.Version, where);
            if (!Enum.TryParse<PackKind>(grant?.Kind, ignoreCase: true, out var kind) || !Enum.IsDefined(kind))
            {
                throw Invalid($"'{where}:Kind' must be one of Knowledge, Experience, Skill.");
            }

            if (kind != PackKind.Knowledge)
            {
                throw Invalid($"'{where}:Kind' is '{kind}': the CLI pilot serves Knowledge packs only (ADR-0058).");
            }

            grants.Add(new PackGrant(TenantId.Local, reference, kind));
        }

        try
        {
            return new KnowledgeGrants(grants);
        }
        catch (ArgumentException ex)
        {
            throw new LocalKnowledgeConfigurationException("'Knowledge:Grants' lists the same pack version twice.", ex);
        }
    }

    private static PackReference ParseBinding(LocalKnowledgeBinding? binding)
    {
        if (binding is null)
        {
            throw Invalid("'Knowledge:Binding' is required when knowledge is enabled: nothing is bound by default.");
        }

        return Reference(binding.PackId, binding.Version, "Knowledge:Binding");
    }

    private static PackReference Reference(string? packId, string? version, string where)
    {
        try
        {
            return new PackReference(packId ?? string.Empty, version ?? string.Empty);
        }
        catch (ArgumentException ex)
        {
            throw new LocalKnowledgeConfigurationException(
                $"'{where}' needs a lower-case 'PackId' and an exact 'Version' (major.minor.patch).", ex);
        }
    }

    private static KnowledgeReadBudget ParseBudget(LocalKnowledgeBudget? budget)
    {
        budget ??= new LocalKnowledgeBudget();
        Within(budget.MaxReads, 1, MaxReadsCeiling, "MaxReads");
        Within(budget.MaxRejections, 1, MaxRejectionsCeiling, "MaxRejections");
        Within(budget.MaxCharactersPerRead, 500, MaxCharactersCeiling, "MaxCharactersPerRead");
        return new KnowledgeReadBudget(budget.MaxReads, budget.MaxRejections, budget.MaxCharactersPerRead);
    }

    private static void Within(int value, int minimum, int maximum, string key)
    {
        if (value < minimum || value > maximum)
        {
            throw Invalid($"'Knowledge:Budget:{key}' must be between {minimum} and {maximum} (it is {value}).");
        }
    }

    private static LocalKnowledgeConfigurationException Invalid(string message) => new(message);
}

/// <summary>
/// The tenant authority of the CLI (ADR-0058): the operating-system user running the process acts for the Local Agent, and nothing else
/// acts for any tenant. An identity of another kind has no tenant, so it gets no scope and no knowledge, and a pin made under another
/// tenant is refused on resume with <see cref="KnowledgeAdmissionCode.TenantNotAllowed"/>.
/// </summary>
internal sealed class LocalAgentTenantAuthority : ITenantAuthority
{
    /// <inheritdoc />
    public TenantId? Resolve(ActorIdentity actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return actor with { DisplayName = null } == ActorIdentity.FromOperatingSystemUser(actor.Id) ? TenantId.Local : null;
    }
}

/// <summary>The CLI host policy (ADR-0058): every new task of the Local Agent is bound to the configured pack; a pinned pack is served by the same directory.</summary>
internal sealed class LocalKnowledgePolicy(IKnowledgePackSource source, PackReference binding) : IKnowledgeTaskPolicy
{
    /// <inheritdoc />
    public KnowledgeBindingChoice? ChooseFor(ActorIdentity actor, KnowledgeTaskScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return scope.Tenant == TenantId.Local ? new KnowledgeBindingChoice(source, binding) : null;
    }

    /// <inheritdoc />
    public IKnowledgePackSource? SourceFor(KnowledgeTaskScope scope, PackReference reference)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return scope.Tenant == TenantId.Local ? source : null;
    }
}
