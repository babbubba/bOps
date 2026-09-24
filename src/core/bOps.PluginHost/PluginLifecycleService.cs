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
        return record is null ? null : ResultFor(PluginLifecycleResultCategory.Succeeded, record, Lifecycle(document, record));
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

    /// <summary>Runs deterministic startup reconciliation before any persisted enabled intent is activated.</summary>
    public void RecoverAll() => RecoverAllAsync().GetAwaiter().GetResult();

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
            Sweep(id);
        }

        DropInterruptedIdempotencyReservations();
        if (Volatile.Read(ref _inFlightMutations) == 0)
        {
            SweepOrphans();
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
            var failure = SanitizeFailure(ex, record.InstallPath);
            _store.Mutate(d =>
            {
                var index = d.Plugins.FindIndex(item => string.Equals(item.Id, pluginId, StringComparison.Ordinal));
                var current = d.Lifecycles[pluginId];
                d.Plugins[index] = d.Plugins[index] with { Enabled = false };
                d.Lifecycles[pluginId] = current with
                {
                    LifecycleVersion = checked(current.LifecycleVersion + 1),
                    State = PluginLifecycleState.ActivationFailed,
                    SanitizedFailure = failure,
                };
            });
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

        Sweep(pluginId);
        var committed = _store.Read();
        var committedRecord = committed.Plugins.First(item => string.Equals(item.Id, pluginId, StringComparison.Ordinal));
        return await Finish(context, "enable", "activation", ResultFor(PluginLifecycleResultCategory.Succeeded, committedRecord, Lifecycle(committed, committedRecord)), lifecycle.State);
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
