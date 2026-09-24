// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.PluginHost;

/// <summary>
/// The archive admission facade for the existing plugin authority. It performs no loading or
/// activation: a successfully admitted archive is passed to <see cref="PluginManager.Install"/>,
/// which remains the sole manifest, signature, trust, and installed-record authority.
/// </summary>
public sealed class PluginLifecycleService(PluginManager pluginManager, string pluginsRootDirectory, PluginArchiveLimits? archiveLimits = null)
{
    private readonly PluginArchiveStager _stager = new(
        Path.Combine(Path.GetFullPath(pluginsRootDirectory), ".lifecycle"),
        archiveLimits ?? new PluginArchiveLimits());

    /// <summary>
    /// Receives and validates a ZIP without executing its contents. Installation remains disabled;
    /// callers must use the explicit existing enable transition afterwards.
    /// </summary>
    public async Task<PluginRecord> InstallArchiveAsync(Stream archive, CancellationToken cancellationToken = default)
    {
        var stagedDirectory = await _stager.StageAsync(archive, cancellationToken);
        try
        {
            return pluginManager.Install(stagedDirectory);
        }
        finally
        {
            if (Directory.Exists(stagedDirectory))
            {
                Directory.Delete(stagedDirectory, recursive: true);
            }
        }
    }

    /// <summary>Reads the durable, monotonic lifecycle version for M6's future ETag projection.</summary>
    internal long GetLifecycleVersion(string pluginId) => pluginManager.LifecycleStore.GetLifecycle(pluginId).LifecycleVersion;

    /// <summary>
    /// Reconciles a durable incomplete marker. The marker, rather than directory enumeration,
    /// decides whether a candidate is uncommitted. Recovery is safe to repeat.
    /// </summary>
    internal void Recover(string pluginId)
    {
        var store = pluginManager.LifecycleStore;
        var journal = store.GetJournal(pluginId);
        if (journal is null) return;

        var lifecycle = store.GetLifecycle(pluginId);
        if (journal.Phase is PluginTransactionPhase.Committed or PluginTransactionPhase.MetadataCommitStarted)
        {
            store.SetLifecycle(pluginId, lifecycle with
            {
                TransactionRollbackGenerationId = null,
                CandidateGenerationId = null,
                TransactionPhase = PluginTransactionPhase.None,
            });
            return;
        }

        // A journal predating a committed metadata marker cannot make its candidate current.
        // Restore the explicit rollback role, never activation LKG, and require an explicit enable.
        if (journal.RollbackGenerationId is not null)
        {
            store.SetLifecycle(pluginId, lifecycle with
            {
                LifecycleVersion = checked(lifecycle.LifecycleVersion + 1),
                State = PluginLifecycleState.InstalledDisabled,
                CurrentGenerationId = journal.RollbackGenerationId,
                TransactionRollbackGenerationId = null,
                CandidateGenerationId = null,
                TransactionPhase = PluginTransactionPhase.None,
                SanitizedFailure = null,
            });
            return;
        }

        store.SetLifecycle(pluginId, lifecycle with
        {
            LifecycleVersion = checked(lifecycle.LifecycleVersion + 1),
            State = PluginLifecycleState.RecoveryRequired,
            CandidateGenerationId = null,
            TransactionPhase = PluginTransactionPhase.None,
            SanitizedFailure = "Recovery could not establish a committed generation.",
        });
    }

    /// <summary>Runs deterministic startup reconciliation before any persisted enabled intent is activated.</summary>
    public void RecoverAll()
    {
        foreach (var pluginId in pluginManager.LifecycleStore.GetJournalPluginIds()) Recover(pluginId);
    }
}
