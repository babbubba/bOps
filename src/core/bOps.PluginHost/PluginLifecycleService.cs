// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.PluginHost;

/// <summary>
/// The ADR-0037 lifecycle backend: bounded archive admission, transactional install/replace,
/// explicit enable/disable, deterministic recovery, ETag (lifecycle revision) enforcement, per-plugin
/// serialization and durable idempotency. It composes the existing <see cref="PluginManager"/> — the
/// sole loader, manifest, signature and trust authority — and adds no second one.
/// </summary>
/// <remarks>
/// Lock order (deadlock-free by construction): idempotency reservation (short, non-awaiting gate) →
/// archive intake and identity discovery (no lock) → per-plugin keyed lock → ETag validation →
/// lifecycle transaction. A follower never holds the plugin lock while waiting for an owner.
/// </remarks>
public sealed partial class PluginLifecycleService
{
    private static readonly TimeSpan IdempotencyRetention = TimeSpan.FromHours(24);
    private static readonly TimeSpan ActivationLkgRetention = TimeSpan.FromDays(30);
    private const int MaximumIdempotencyKeyLength = 128;
    private const int MaximumIdempotencyRecords = 512;
    private const int MaximumFailureLength = 240;

    private readonly PluginManager _manager;
    private readonly PluginStore _store;
    private readonly string _root;
    private readonly string _lifecycleRoot;
    private readonly PluginArchiveLimits _limits;
    private readonly PluginArchiveStager _stager;
    private readonly IAuditSink? _audit;
    private readonly KeyedLifecycleLock _locks = new();
    private int _inFlightMutations;

    /// <summary>Creates the lifecycle service over the host's single <see cref="PluginManager"/>.</summary>
    public PluginLifecycleService(
        PluginManager pluginManager,
        string pluginsRootDirectory,
        PluginArchiveLimits? archiveLimits = null,
        IAuditSink? auditSink = null)
    {
        ArgumentNullException.ThrowIfNull(pluginManager);
        _manager = pluginManager;
        _store = pluginManager.LifecycleStore;
        _root = Path.GetFullPath(pluginsRootDirectory);
        _lifecycleRoot = Path.Combine(_root, ".lifecycle");
        _limits = archiveLimits ?? new PluginArchiveLimits();
        _stager = new PluginArchiveStager(_lifecycleRoot, _limits);
        _audit = auditSink;
    }

    /// <summary>Internal deterministic test seam invoked at named transaction/reservation checkpoints. Never a public debug API.</summary>
    internal Func<string, Task>? Checkpoint { get; set; }

    /// <summary>Number of plugin ids currently holding or awaiting a lifecycle lock.</summary>
    internal int ActiveLockKeyCount => _locks.ActiveKeyCount;

    /// <summary>Holders plus waiters currently on one plugin id's lifecycle lock.</summary>
    internal int LockReferenceCount(string pluginId) => _locks.ReferenceCount(pluginId);

    /// <summary>Reads the durable lifecycle projection for M6 (state, revision, ETag); <c>null</c> for an unknown id.</summary>
    public PluginLifecycleResult? GetStatus(string pluginId)
    {
        var document = _store.Read();
        var record = document.Plugins.FirstOrDefault(item => string.Equals(item.Id, pluginId, StringComparison.Ordinal));
        if (record is null)
        {
            return null;
        }

        // One coherent snapshot: state, LKG role and journal all come from the same document read.
        var lifecycle = Lifecycle(document, record);
        var journalPending = document.Journals.ContainsKey(record.Id) || lifecycle.TransactionPhase != PluginTransactionPhase.None;
        var recoveryAvailable = lifecycle.State is PluginLifecycleState.ActivationFailed or PluginLifecycleState.RecoveryRequired &&
            lifecycle.ActivationLkgGenerationId is not null &&
            !journalPending;
        return ResultFor(PluginLifecycleResultCategory.Succeeded, record, lifecycle) with
        {
            LifecycleFailure = lifecycle.SanitizedFailure,
            RecoveryAvailable = recoveryAvailable,
        };
    }

    /// <summary>Reads the durable, monotonic lifecycle version used for M6's ETag projection.</summary>
    internal long GetLifecycleVersion(string pluginId) => _store.GetLifecycle(pluginId).LifecycleVersion;

    /// <summary>
    /// Receives an archive and installs it (first install) or replaces the target (same id). The result is
    /// always <see cref="PluginLifecycleState.InstalledDisabled"/>: installation never enables, loads, or
    /// registers anything. First install requires <paramref name="expectedLifecycleVersion"/> to be
    /// <c>null</c> (create precondition); replacement requires the current revision.
    /// </summary>
    public async Task<PluginLifecycleResult> InstallArchiveAsync(
        PluginLifecycleRequestContext context,
        Stream archive,
        long? expectedLifecycleVersion = null,
        string? routePluginId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(archive);
        var intent = new Intent("install", routePluginId, expectedLifecycleVersion, null, null);
        return await ExecuteAsync(context, intent, archive, (flight, token) => InstallCoreAsync(context, archive, intent, flight, token), cancellationToken);
    }

    /// <summary>
    /// Enables the committed generation. <paramref name="confirmedVersion"/> must name the manifest
    /// version that will execute in-process with host privileges; without it nothing is loaded.
    /// </summary>
    public Task<PluginLifecycleResult> EnableAsync(
        PluginLifecycleRequestContext context,
        string pluginId,
        long? expectedLifecycleVersion,
        string? confirmedVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        var intent = new Intent("enable", pluginId, expectedLifecycleVersion, confirmedVersion, null);
        return ExecuteAsync(context, intent, null, (_, token) => EnableCoreAsync(context, pluginId, expectedLifecycleVersion, confirmedVersion, token), cancellationToken);
    }

    /// <summary>Disables a plugin through the existing unregistration path. Idempotent; the installed generation is preserved.</summary>
    public Task<PluginLifecycleResult> DisableAsync(
        PluginLifecycleRequestContext context,
        string pluginId,
        long? expectedLifecycleVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        var intent = new Intent("disable", pluginId, expectedLifecycleVersion, null, null);
        return ExecuteAsync(context, intent, null, (_, token) => DisableCoreAsync(context, pluginId, expectedLifecycleVersion, token), cancellationToken);
    }

    /// <summary>
    /// Explicit administrator recovery: restores the activation-LKG generation as the committed
    /// <see cref="PluginLifecycleState.InstalledDisabled"/> generation. Valid only from
    /// <see cref="PluginLifecycleState.ActivationFailed"/> or <see cref="PluginLifecycleState.RecoveryRequired"/>.
    /// Nothing executes and nothing is re-enabled: a separate explicit enable is required.
    /// </summary>
    public Task<PluginLifecycleResult> RecoverAsync(
        PluginLifecycleRequestContext context,
        string pluginId,
        long? expectedLifecycleVersion,
        bool confirmed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        var intent = new Intent("recover", pluginId, expectedLifecycleVersion, confirmed ? "confirmed" : null, null);
        return ExecuteAsync(context, intent, null, (_, token) => RecoverLkgCoreAsync(context, pluginId, expectedLifecycleVersion, confirmed, token), cancellationToken);
    }

    /// <summary>Runs deterministic startup reconciliation before any persisted enabled intent is activated.</summary>
    public void RecoverAll() => RecoverAllAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Canonical host-startup sequence (ADR-0037): reconcile every journal first, then activate each plugin whose
    /// lifecycle state is <see cref="PluginLifecycleState.Enabled"/> through the existing loader, and persist
    /// <see cref="PluginLifecycleState.ActivationFailed"/> (sanitized) for every plugin that did not activate. The
    /// failure never stays only in memory, is never retried silently by the next start, and never touches the
    /// generation roles: recovery to the activation LKG remains an explicit administrator action.
    /// </summary>
    /// <returns>Plugin id to a sanitized failure reason (the same projection as <see cref="PluginManager.StartupLoadErrors"/>).</returns>
    public async Task<IReadOnlyDictionary<string, string>> ActivateEnabledAsync(CancellationToken cancellationToken = default)
    {
        await RecoverAllAsync(cancellationToken);
        var before = _store.Read();
        var carried = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var record in before.Plugins)
        {
            var current = Lifecycle(before, record);
            if (record.Enabled && current.State == PluginLifecycleState.ActivationFailed && current.SanitizedFailure is not null)
            {
                carried[record.Id] = current.SanitizedFailure;
            }
        }

        // Only an authoritatively Enabled generation is activated; an already persisted ActivationFailed is reported, never retried.
        var errors = _manager.LoadEnabled(record => Lifecycle(before, record).State == PluginLifecycleState.Enabled, carried);
        foreach (var (pluginId, reason) in errors)
        {
            if (carried.ContainsKey(pluginId))
            {
                continue;
            }

            using var _ = await _locks.AcquireAsync(pluginId, cancellationToken);
            var document = _store.Read();
            var record = document.Plugins.FirstOrDefault(item => string.Equals(item.Id, pluginId, StringComparison.Ordinal));
            if (record is null || Lifecycle(document, record).State != PluginLifecycleState.Enabled || _manager.IsActivated(pluginId))
            {
                continue;
            }

            TryDeactivate(pluginId);
            var prior = Lifecycle(document, record).State;
            PersistActivationFailed(pluginId, BoundFailure(reason), keepEnabledIntent: true);
            var after = _store.Read();
            var failed = after.Plugins.First(item => string.Equals(item.Id, pluginId, StringComparison.Ordinal));
            await AuditAsync(null, "startup", "activation", nameof(PluginLifecycleResultCategory.ActivationFailed), pluginId, failed.Manifest.Version, prior, PluginLifecycleState.ActivationFailed, Lifecycle(after, failed).LifecycleVersion, null, replay: false);
        }

        return errors;
    }

    /// <summary>
    /// Reconciles every journal, verifies committed material, drops interrupted idempotency
    /// reservations, deletes orphan upload/staging material, and applies retention. Safe to repeat.
    /// </summary>
    public async Task RecoverAllAsync(CancellationToken cancellationToken = default)
    {
        var document = _store.Read();
        var ids = document.Journals.Keys.Concat(document.Plugins.Select(record => record.Id)).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var id in ids)
        {
            using var _ = await _locks.AcquireAsync(id, cancellationToken);
            await RecoverCoreAsync(id, null);
            await SweepAsync(id, null);
        }

        DropInterruptedIdempotencyReservations();
        if (Volatile.Read(ref _inFlightMutations) == 0)
        {
            await SweepOrphansAsync();
        }
    }

    // ---- Enable / Disable ----------------------------------------------------------------------------

    private async Task<PluginLifecycleResult> EnableCoreAsync(
        PluginLifecycleRequestContext context, string pluginId, long? expected, string? confirmedVersion, CancellationToken ct)
    {
        await Hook("BeforeLockWait");
        using var _ = await _locks.AcquireAsync(pluginId, ct);
        await Hook("LockAcquired");
        await RecoverCoreAsync(pluginId, context);

        var document = _store.Read();
        var record = document.Plugins.FirstOrDefault(item => string.Equals(item.Id, pluginId, StringComparison.Ordinal));
        if (record is null)
        {
            return await Finish(context, "enable", "lookup", new PluginLifecycleResult(PluginLifecycleResultCategory.NotFound, pluginId, null, null, 0, "lookup"), null);
        }

        var lifecycle = Lifecycle(document, record);
        if (expected != lifecycle.LifecycleVersion)
        {
            return await Finish(context, "enable", "precondition", ResultFor(PluginLifecycleResultCategory.StaleVersion, record, lifecycle, "precondition"), lifecycle.State);
        }

        if (lifecycle.State == PluginLifecycleState.Enabled && _manager.IsActivated(pluginId))
        {
            // Idempotent success for the same generation: no reload, no revision change.
            return await Finish(context, "enable", "activation", ResultFor(PluginLifecycleResultCategory.Succeeded, record, lifecycle), lifecycle.State);
        }

        if (lifecycle.State is not (PluginLifecycleState.InstalledDisabled or PluginLifecycleState.Enabled))
        {
            return await Finish(context, "enable", "state", ResultFor(PluginLifecycleResultCategory.StateConflict, record, lifecycle, "state"), lifecycle.State);
        }

        if (!string.Equals(confirmedVersion, record.Manifest.Version, StringComparison.Ordinal))
        {
            // Rejected before any plugin code is loaded.
            return await Finish(context, "enable", "confirmation", ResultFor(PluginLifecycleResultCategory.ActivationConfirmationRequired, record, lifecycle, "confirmation"), lifecycle.State);
        }

        try
        {
            if (record.Provenance is not { Verified: true } || record.Provenance.Trust == PackageTrustLevel.Unverified)
            {
                throw new PluginOperationException("The installed bytes do not have verified trusted provenance.");
            }

            _manager.Activate(record);
        }
        catch (Exception ex) when (ex is not PluginLifecycleInterruptedException)
        {
            // Activation already unregistered everything it added; repeat defensively for a failure raised
            // before its own cleanup ran (idempotent, plugin-owned registrations only).
            TryDeactivate(pluginId);
            PersistActivationFailed(pluginId, SanitizeFailure(ex, record.InstallPath));
            var failed = _store.Read();
            var failedRecord = failed.Plugins.First(item => string.Equals(item.Id, pluginId, StringComparison.Ordinal));
            return await Finish(context, "enable", "activation", ResultFor(PluginLifecycleResultCategory.ActivationFailed, failedRecord, Lifecycle(failed, failedRecord), "activation"), lifecycle.State);
        }

        await Hook("AfterActivation");
        try
        {
            var now = _manager.Clock.GetUtcNow();
            _store.Mutate(d =>
            {
                var index = d.Plugins.FindIndex(item => string.Equals(item.Id, pluginId, StringComparison.Ordinal));
                var current = d.Lifecycles[pluginId];
                d.Plugins[index] = d.Plugins[index] with { Enabled = true };
                d.Lifecycles[pluginId] = PluginLifecycleTransitions.WithActivationLkg(
                    current with
                    {
                        LifecycleVersion = checked(current.LifecycleVersion + 1),
                        State = PluginLifecycleState.Enabled,
                        SanitizedFailure = null,
                    },
                    now);
            });
        }
        catch (Exception ex) when (ex is not PluginLifecycleInterruptedException)
        {
            // Persistence failed: never report (or leave) the plugin running with no durable record.
            TryDeactivate(pluginId);
            throw;
        }

        await SweepAsync(pluginId, context);
        var committed = _store.Read();
        var committedRecord = committed.Plugins.First(item => string.Equals(item.Id, pluginId, StringComparison.Ordinal));
        return await Finish(context, "enable", "activation", ResultFor(PluginLifecycleResultCategory.Succeeded, committedRecord, Lifecycle(committed, committedRecord)), lifecycle.State);
    }

    private async Task<PluginLifecycleResult> RecoverLkgCoreAsync(
        PluginLifecycleRequestContext context, string pluginId, long? expected, bool confirmed, CancellationToken ct)
    {
        await Hook("BeforeLockWait");
        using var _ = await _locks.AcquireAsync(pluginId, ct);
        await Hook("LockAcquired");
        await RecoverCoreAsync(pluginId, context);

        var document = _store.Read();
        var record = document.Plugins.FirstOrDefault(item => string.Equals(item.Id, pluginId, StringComparison.Ordinal));
        if (record is null)
        {
            return await Finish(context, "recover", "lookup", new PluginLifecycleResult(PluginLifecycleResultCategory.NotFound, pluginId, null, null, 0, "lookup"), null);
        }

        var lifecycle = Lifecycle(document, record);
        if (document.Journals.ContainsKey(pluginId))
        {
            // A cleanup that must still be retried leaves material that is not a committed generation: never recover over it.
            return await Finish(context, "recover", "recovery", ResultFor(PluginLifecycleResultCategory.RecoveryRequired, record, lifecycle, "recovery"), lifecycle.State);
        }

        if (expected != lifecycle.LifecycleVersion)
        {
            return await Finish(context, "recover", "precondition", ResultFor(PluginLifecycleResultCategory.StaleVersion, record, lifecycle, "precondition"), lifecycle.State);
        }

        if (lifecycle.State is not (PluginLifecycleState.ActivationFailed or PluginLifecycleState.RecoveryRequired))
        {
            return await Finish(context, "recover", "state", ResultFor(PluginLifecycleResultCategory.StateConflict, record, lifecycle, "state"), lifecycle.State);
        }

        if (!confirmed)
        {
            return await Finish(context, "recover", "confirmation", ResultFor(PluginLifecycleResultCategory.ActivationConfirmationRequired, record, lifecycle, "confirmation"), lifecycle.State);
        }

        var lkgId = lifecycle.ActivationLkgGenerationId;
        if (lkgId is null)
        {
            return await Finish(context, "recover", "lkg", ResultFor(PluginLifecycleResultCategory.StateConflict, record, lifecycle, "lkg"), lifecycle.State);
        }

        // The LKG must still be a provable, verifiably signed generation of this plugin before it becomes Current.
        var lkgIsCurrent = lkgId == lifecycle.CurrentGenerationId;
        var lkgDirectory = lkgIsCurrent ? FinalPath(pluginId) : RetainedPath(lkgId);
        PluginManifest manifest;
        PluginProvenance provenance;
        try
        {
            manifest = PluginManager.ReadCandidateManifest(lkgDirectory);
            provenance = _manager.VerifyCandidate(lkgDirectory, manifest);
        }
        catch (Exception ex) when (ex is PluginValidationException or PluginOperationException or IOException)
        {
            return await Finish(context, "recover", "lkg", ResultFor(PluginLifecycleResultCategory.StateConflict, record, lifecycle, "lkg"), lifecycle.State);
        }

        if (!string.Equals(manifest.Id, pluginId, StringComparison.Ordinal) || !provenance.Verified || provenance.Trust == PackageTrustLevel.Unverified)
        {
            return await Finish(context, "recover", "lkg", ResultFor(PluginLifecycleResultCategory.StateConflict, record, lifecycle, "lkg"), lifecycle.State);
        }

        // Trust proves who signed the bytes; only the generation digest proves they ARE the recorded LKG generation.
        var recorded = lifecycle.Generations.FirstOrDefault(g => g.GenerationId == lkgId);
        if (recorded is null || !string.Equals(recorded.PackageDigestSha256, provenance.PackageDigestSha256, StringComparison.OrdinalIgnoreCase))
        {
            return await Finish(context, "recover", "integrity", ResultFor(PluginLifecycleResultCategory.StateConflict, record, lifecycle, "integrity"), lifecycle.State);
        }

        try
        {
            if (lkgIsCurrent)
            {
                _store.Mutate(d =>
                {
                    var index = d.Plugins.FindIndex(item => string.Equals(item.Id, pluginId, StringComparison.Ordinal));
                    var current = d.Lifecycles[pluginId];
                    d.Plugins[index] = d.Plugins[index] with { Enabled = false, Manifest = manifest, Provenance = provenance };
                    d.Lifecycles[pluginId] = current with
                    {
                        LifecycleVersion = checked(current.LifecycleVersion + 1),
                        State = PluginLifecycleState.InstalledDisabled,
                        SanitizedFailure = null,
                    };
                });
            }
            else
            {
                await RestoreLkgAsync(pluginId, record, lifecycle, manifest, provenance);
            }
        }
        catch (PluginLifecycleInterruptedException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await RecoverCoreAsync(pluginId, context);
            var rolledBack = _store.Read();
            var current = rolledBack.Plugins.First(item => string.Equals(item.Id, pluginId, StringComparison.Ordinal));
            return await Finish(context, "recover", "commit", ResultFor(PluginLifecycleResultCategory.InternalFailure, current, Lifecycle(rolledBack, current), "commit"), lifecycle.State);
        }

        await SweepAsync(pluginId, context);
        var committed = _store.Read();
        var committedRecord = committed.Plugins.First(item => string.Equals(item.Id, pluginId, StringComparison.Ordinal));
        return await Finish(context, "recover", "commit", ResultFor(PluginLifecycleResultCategory.Succeeded, committedRecord, Lifecycle(committed, committedRecord)), lifecycle.State);
    }

    private async Task<PluginLifecycleResult> DisableCoreAsync(
        PluginLifecycleRequestContext context, string pluginId, long? expected, CancellationToken ct)
    {
        await Hook("BeforeLockWait");
        using var _ = await _locks.AcquireAsync(pluginId, ct);
        await Hook("LockAcquired");
        await RecoverCoreAsync(pluginId, context);

        var document = _store.Read();
        var record = document.Plugins.FirstOrDefault(item => string.Equals(item.Id, pluginId, StringComparison.Ordinal));
        if (record is null)
        {
            return await Finish(context, "disable", "lookup", new PluginLifecycleResult(PluginLifecycleResultCategory.NotFound, pluginId, null, null, 0, "lookup"), null);
        }

        var lifecycle = Lifecycle(document, record);
        if (expected != lifecycle.LifecycleVersion)
        {
            return await Finish(context, "disable", "precondition", ResultFor(PluginLifecycleResultCategory.StaleVersion, record, lifecycle, "precondition"), lifecycle.State);
        }

        if (lifecycle.State != PluginLifecycleState.Enabled && !_manager.IsActivated(pluginId))
        {
            if (record.Enabled)
            {
                // A startup activation failure kept the enabled intent; an explicit disable withdraws it (a durable change).
                _store.Mutate(d =>
                {
                    var index = d.Plugins.FindIndex(item => string.Equals(item.Id, pluginId, StringComparison.Ordinal));
                    d.Plugins[index] = d.Plugins[index] with { Enabled = false };
                    d.Lifecycles[pluginId] = d.Lifecycles[pluginId] with { LifecycleVersion = checked(d.Lifecycles[pluginId].LifecycleVersion + 1) };
                });
                var withdrawn = _store.Read();
                var withdrawnRecord = withdrawn.Plugins.First(item => string.Equals(item.Id, pluginId, StringComparison.Ordinal));
                return await Finish(context, "disable", "deactivation", ResultFor(PluginLifecycleResultCategory.Succeeded, withdrawnRecord, Lifecycle(withdrawn, withdrawnRecord)), lifecycle.State);
            }

            // Already disabled (or never running): idempotent, no revision change.
            return await Finish(context, "disable", "deactivation", ResultFor(PluginLifecycleResultCategory.Succeeded, record, lifecycle), lifecycle.State);
        }

        try
        {
            _manager.Deactivate(pluginId);
        }
        catch (PluginOperationException)
        {
            // A registered model provider cannot be retracted: refuse and keep it enabled rather than lie.
            return await Finish(context, "disable", "deactivation", ResultFor(PluginLifecycleResultCategory.DisableRefused, record, lifecycle, "deactivation"), lifecycle.State);
        }

        _store.Mutate(d =>
        {
            var index = d.Plugins.FindIndex(item => string.Equals(item.Id, pluginId, StringComparison.Ordinal));
            var current = d.Lifecycles[pluginId];
            d.Plugins[index] = d.Plugins[index] with { Enabled = false };
            d.Lifecycles[pluginId] = current with
            {
                LifecycleVersion = checked(current.LifecycleVersion + 1),
                State = PluginLifecycleState.InstalledDisabled,
                SanitizedFailure = null,
            };
        });
        var committed = _store.Read();
        var committedRecord = committed.Plugins.First(item => string.Equals(item.Id, pluginId, StringComparison.Ordinal));
        return await Finish(context, "disable", "deactivation", ResultFor(PluginLifecycleResultCategory.Succeeded, committedRecord, Lifecycle(committed, committedRecord)), lifecycle.State);
    }

    // ---- Shared helpers -------------------------------------------------------------------------------

    private static PluginLifecycleMetadata Lifecycle(PluginLifecycleDocument document, PluginRecord record) =>
        document.Lifecycles[record.Id];

    private static PluginLifecycleResult ResultFor(
        PluginLifecycleResultCategory category, PluginRecord record, PluginLifecycleMetadata lifecycle, string? stage = null) =>
        new(category, record.Id, record.Manifest.Version, lifecycle.State, lifecycle.LifecycleVersion, stage);

    private Task Hook(string name) => Checkpoint?.Invoke(name) ?? Task.CompletedTask;

    private void TryDeactivate(string pluginId)
    {
        try
        {
            _manager.Deactivate(pluginId);
        }
        catch (PluginOperationException)
        {
            // A provider that cannot be retracted was never registered by a failed activation of it.
        }
    }

    /// <summary>
    /// Records an activation failure as the authoritative lifecycle state; generation roles are untouched. A startup
    /// failure keeps the operator's persisted enabled intent (<see cref="PluginRecord.Enabled"/>), which the catalog reports
    /// separately from the lifecycle state; an explicit enable that failed never established that intent.
    /// </summary>
    private void PersistActivationFailed(string pluginId, string failure, bool keepEnabledIntent = false) =>
        _store.Mutate(d =>
        {
            var index = d.Plugins.FindIndex(item => string.Equals(item.Id, pluginId, StringComparison.Ordinal));
            var current = d.Lifecycles[pluginId];
            d.Plugins[index] = d.Plugins[index] with { Enabled = keepEnabledIntent && d.Plugins[index].Enabled };
            d.Lifecycles[pluginId] = current with
            {
                LifecycleVersion = checked(current.LifecycleVersion + 1),
                State = PluginLifecycleState.ActivationFailed,
                SanitizedFailure = failure,
            };
        });

    private string BoundFailure(string reason)
    {
        var text = reason.Replace(_root, "<plugins-root>", StringComparison.Ordinal);
        return text.Length > MaximumFailureLength ? text[..MaximumFailureLength] : text;
    }

    /// <summary>A bounded reason that never carries a local path or a raw exception: only host-authored message types pass through.</summary>
    private string SanitizeFailure(Exception exception, string installPath)
    {
        var text = exception is PluginOperationException or PluginValidationException or ToolRegistrationException
            ? PluginManager.Sanitize(PluginManager.Sanitize(exception.Message, installPath), _root)
            : "An unexpected error occurred while activating the plugin.";
        text = text.Replace(_root, "<plugins-root>", StringComparison.Ordinal);
        return text.Length > MaximumFailureLength ? text[..MaximumFailureLength] : text;
    }

    /// <summary>Audits and returns the operation outcome.</summary>
    private async Task<PluginLifecycleResult> Finish(
        PluginLifecycleRequestContext context, string operation, string stage, PluginLifecycleResult result, PluginLifecycleState? priorState, string? trust = null)
    {
        await AuditAsync(context, operation, stage, result.Category.ToString(), result.PluginId, result.PluginVersion, priorState, result.State, result.LifecycleVersion, trust, replay: false);
        return result;
    }

    /// <summary>
    /// Makes a failed, retryable cleanup observable. The stage is a neutral material category; no path, exception
    /// text or stack is ever recorded, and the committed lifecycle state is left exactly as it was.
    /// </summary>
    private async Task AuditCleanupFailureAsync(PluginLifecycleRequestContext? context, string? pluginId, string materialCategory)
    {
        PluginLifecycleState? state = null;
        var version = 0L;
        string? pluginVersion = null;
        if (pluginId is not null)
        {
            var document = _store.Read();
            var record = document.Plugins.FirstOrDefault(item => string.Equals(item.Id, pluginId, StringComparison.Ordinal));
            if (record is not null)
            {
                var lifecycle = Lifecycle(document, record);
                state = lifecycle.State;
                version = lifecycle.LifecycleVersion;
                pluginVersion = record.Manifest.Version;
            }
        }

        await AuditAsync(context, "cleanup", materialCategory, "RetryRequired", pluginId, pluginVersion, state, state, version, null, replay: false);
    }

    private async Task AuditAsync(
        PluginLifecycleRequestContext? context, string operation, string stage, string outcome, string? pluginId, string? version,
        PluginLifecycleState? prior, PluginLifecycleState? next, long lifecycleVersion, string? trust, bool replay)
    {
        if (_audit is null)
        {
            return;
        }

        try
        {
            await _audit.WriteAsync(new PluginLifecycleAuditEvent
            {
                TimestampUtc = _manager.Clock.GetUtcNow(),
                Node = context?.Node ?? NodeId.Local,
                TaskId = Guid.Empty,
                StepIndex = -1,
                Actor = context?.Actor ?? ActorIdentity.RuntimeSystem,
                Operation = operation,
                Stage = stage,
                Outcome = outcome,
                PluginId = pluginId,
                PluginVersion = version,
                PriorState = prior?.ToString(),
                NewState = next?.ToString(),
                LifecycleVersion = lifecycleVersion,
                PublisherTrust = trust,
                CorrelationId = context?.CorrelationId,
                IdempotencyKeyPresent = !string.IsNullOrWhiteSpace(context?.IdempotencyKey),
                IdempotentReplay = replay,
            });
        }
#pragma warning disable CA1031 // An audit-sink failure is an operational concern and must not undo or mask a committed lifecycle result.
        catch (Exception ex) when (ex is not PluginLifecycleInterruptedException)
#pragma warning restore CA1031
        {
        }
    }
}
