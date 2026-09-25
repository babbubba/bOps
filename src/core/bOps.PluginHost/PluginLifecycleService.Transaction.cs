// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.PluginHost;

/// <summary>
/// Archive install/replace transaction and deterministic recovery (ADR-0037). Filesystem promotion and
/// metadata persistence are separate physical operations, so this never pretends they are one atomic
/// action: each non-idempotent rename is preceded by an individually atomic journal write, and lifecycle
/// metadata alone decides which generation holds which role.
/// </summary>
/// <remarks>
/// Physical layout (all under the plugin root, same volume): the committed generation of plugin
/// <c>id</c> is <c>&lt;id&gt;</c>; a candidate lives in <c>.lifecycle/staging/&lt;generationId&gt;</c>; a
/// rollback or retained activation-LKG generation lives in <c>.lifecycle/lkg/&lt;generationId&gt;</c>.
/// </remarks>
public sealed partial class PluginLifecycleService
{
    private string FinalPath(string pluginId) => Path.Combine(_root, pluginId);
    private string StagingPath(string generationId) => Path.Combine(_lifecycleRoot, "staging", generationId);
    private string RetainedPath(string generationId) => Path.Combine(_lifecycleRoot, "lkg", generationId);
    private string Relative(string path) => Path.GetRelativePath(_root, path).Replace('\\', '/');

    /// <summary>
    /// Creates the lifecycle-owned work area under the plugin root (so the work and install roots share a
    /// volume and promotion is a rename), restrictive to the host owner where the platform supports it.
    /// </summary>
    private void EnsureWorkArea()
    {
        Directory.CreateDirectory(_root);
        foreach (var name in new[] { string.Empty, "uploads", "staging", "quarantine", "lkg" })
        {
            var path = Path.Combine(_lifecycleRoot, name);
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(path);
            }
            else
            {
                Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    private async Task<PluginLifecycleResult> InstallCoreAsync(
        PluginLifecycleRequestContext context, Stream archive, Intent intent, InFlight? flight, CancellationToken ct)
    {
        string? stagedDirectory = null;
        var crashed = false;
        await AuditAsync(context, "install", "received", "Attempted", intent.PluginId, null, null, null, 0, null, replay: false);
        try
        {
            EnsureWorkArea();
            PluginArchiveStager.StagedArchive staged;
            try
            {
                staged = await _stager.StageWithDigestAsync(archive, async digest =>
                {
                    await BindDigestAsync(context, flight, digest);
                    await Hook("DigestBound");
                }, ct);
            }
            catch (PluginArchiveValidationException ex)
            {
                return await Finish(context, "install", "archive", new PluginLifecycleResult(ex.Category, null, null, null, 0, "archive"), null);
            }

            stagedDirectory = staged.Directory;

            PluginManifest manifest;
            try
            {
                manifest = PluginManager.ReadCandidateManifest(stagedDirectory);
            }
            catch (Exception ex) when (ex is PluginValidationException or PluginOperationException)
            {
                return await Finish(context, "install", "manifest", new PluginLifecycleResult(PluginLifecycleResultCategory.ManifestInvalid, null, null, null, 0, "manifest"), null);
            }

            if (intent.PluginId is not null && !string.Equals(intent.PluginId, manifest.Id, StringComparison.Ordinal))
            {
                return await Finish(context, "install", "identity", new PluginLifecycleResult(PluginLifecycleResultCategory.IdentityConflict, manifest.Id, manifest.Version, null, 0, "identity"), null);
            }

            BindIdentity(context, flight, manifest.Id, manifest.Version);
            await Hook("IdentityKnown");

            PluginProvenance provenance;
            try
            {
                provenance = _manager.VerifyCandidate(stagedDirectory, manifest);
            }
            catch (Exception ex) when (ex is PluginValidationException or PluginOperationException)
            {
                return await Finish(context, "install", "signature", new PluginLifecycleResult(PluginLifecycleResultCategory.SignatureInvalid, manifest.Id, manifest.Version, null, 0, "signature"), null);
            }

            var trustName = provenance.Trust.ToString();
            if (!provenance.SignaturePresent)
            {
                return await Finish(context, "install", "signature", new PluginLifecycleResult(PluginLifecycleResultCategory.SignatureInvalid, manifest.Id, manifest.Version, null, 0, "signature"), null, trustName);
            }

            if (!provenance.Verified || provenance.Trust == PackageTrustLevel.Unverified)
            {
                return await Finish(context, "install", "trust", new PluginLifecycleResult(PluginLifecycleResultCategory.PublisherUntrusted, manifest.Id, manifest.Version, null, 0, "trust"), null, trustName);
            }

            await AuditAsync(context, "install", "trust", "Succeeded", manifest.Id, manifest.Version, null, null, 0, trustName, replay: false);

            if (PluginCandidateInspector.Inspect(stagedDirectory, manifest) is not null)
            {
                return await Finish(context, "install", "compatibility", new PluginLifecycleResult(PluginLifecycleResultCategory.CompatibilityRejected, manifest.Id, manifest.Version, null, 0, "compatibility"), null, trustName);
            }

            await Hook("BeforeLockWait");
            using var lockHandle = await _locks.AcquireAsync(manifest.Id, ct);
            await Hook("LockAcquired");
            await RecoverCoreAsync(manifest.Id, context);
            if (HasPendingTransaction(manifest.Id))
            {
                // A blocked cleanup of an uncommitted candidate left its bytes in place: never install over them.
                var pending = _store.Read();
                var pendingRecord = pending.Plugins.FirstOrDefault(item => string.Equals(item.Id, manifest.Id, StringComparison.Ordinal));
                var refused = pendingRecord is null
                    ? new PluginLifecycleResult(PluginLifecycleResultCategory.RecoveryRequired, manifest.Id, manifest.Version, null, 0, "recovery")
                    : ResultFor(PluginLifecycleResultCategory.RecoveryRequired, pendingRecord, Lifecycle(pending, pendingRecord), "recovery");
                return await Finish(context, "install", "recovery", refused, refused.State, trustName);
            }

            var document = _store.Read();
            var existing = document.Plugins.FirstOrDefault(item => string.Equals(item.Id, manifest.Id, StringComparison.Ordinal));
            var priorState = existing is null ? (PluginLifecycleState?)null : Lifecycle(document, existing).State;

            // Step 14: ETag / creation precondition, identity continuity, allowed transition — all inside the lock.
            if (existing is null)
            {
                if (intent.Expected is not null)
                {
                    return await Finish(context, "install", "precondition", new PluginLifecycleResult(PluginLifecycleResultCategory.StaleVersion, manifest.Id, manifest.Version, null, 0, "precondition"), null, trustName);
                }

                if (Directory.Exists(FinalPath(manifest.Id)))
                {
                    return await Finish(context, "install", "collision", new PluginLifecycleResult(PluginLifecycleResultCategory.IdentityConflict, manifest.Id, manifest.Version, null, 0, "collision"), null, trustName);
                }
            }
            else
            {
                var lifecycle = Lifecycle(document, existing);
                if (intent.Expected != lifecycle.LifecycleVersion)
                {
                    return await Finish(context, "install", "precondition", ResultFor(PluginLifecycleResultCategory.StaleVersion, existing, lifecycle, "precondition"), lifecycle.State, trustName);
                }

                if (lifecycle.State == PluginLifecycleState.Enabled)
                {
                    return await Finish(context, "install", "state", ResultFor(PluginLifecycleResultCategory.StateConflict, existing, lifecycle, "state"), lifecycle.State, trustName);
                }

                if (!string.Equals(existing.Manifest.Publisher, manifest.Publisher, StringComparison.Ordinal) ||
                    !string.Equals(existing.Provenance?.KeyId, provenance.KeyId, StringComparison.Ordinal))
                {
                    return await Finish(context, "install", "continuity", ResultFor(PluginLifecycleResultCategory.IdentityConflict, existing, lifecycle, "continuity"), lifecycle.State, trustName);
                }

                if (string.Equals(existing.Manifest.Version, manifest.Version, StringComparison.Ordinal))
                {
                    return await Finish(context, "install", "version", ResultFor(PluginLifecycleResultCategory.VersionConflict, existing, lifecycle, "version"), lifecycle.State, trustName);
                }
            }

            // Step 15: commit-eligible. Any failure from here is reconciled by the journal, never left ambiguous.
            try
            {
                await CommitAsync(manifest, provenance, stagedDirectory, existing, Path.GetFileName(stagedDirectory));
            }
            catch (PluginLifecycleInterruptedException)
            {
                crashed = true;
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                await RecoverCoreAsync(manifest.Id, context);
                var rolledBack = _store.Read();
                var current = rolledBack.Plugins.FirstOrDefault(item => string.Equals(item.Id, manifest.Id, StringComparison.Ordinal));
                var failure = current is null
                    ? new PluginLifecycleResult(PluginLifecycleResultCategory.InternalFailure, manifest.Id, manifest.Version, null, 0, "commit")
                    : ResultFor(PluginLifecycleResultCategory.InternalFailure, current, Lifecycle(rolledBack, current), "commit");
                return await Finish(context, "install", "commit", failure, priorState, trustName);
            }

            await SweepAsync(manifest.Id, context);
            var committed = _store.Read();
            var committedRecord = committed.Plugins.First(item => string.Equals(item.Id, manifest.Id, StringComparison.Ordinal));
            return await Finish(context, "install", "commit", ResultFor(PluginLifecycleResultCategory.Succeeded, committedRecord, Lifecycle(committed, committedRecord)), priorState, trustName);
        }
        catch (PluginLifecycleInterruptedException)
        {
            crashed = true;
            throw;
        }
        finally
        {
            if (!crashed && stagedDirectory is not null)
            {
                DeleteDirectory(stagedDirectory);
            }
        }
    }

    /// <summary>
    /// Journaled promotion. Phases (each written atomically BEFORE the rename it guards, or after the one it
    /// proves): CandidatePrepared → RollbackCaptured → CandidatePromoted → Committed → None.
    /// </summary>
    private async Task CommitAsync(PluginManifest manifest, PluginProvenance provenance, string stagedDirectory, PluginRecord? existing, string candidateId)
    {
        var id = manifest.Id;
        var final = FinalPath(id);
        var now = _manager.Clock.GetUtcNow();
        var candidate = new PluginGeneration(candidateId, Relative(stagedDirectory), provenance.PackageDigestSha256, now);
        var operationId = Guid.NewGuid().ToString("N");
        var record = new PluginRecord(id, final, manifest, Enabled: false, now, provenance);

        if (existing is null)
        {
            var journal = new PluginLifecycleJournal(operationId, id, PluginTransactionPhase.CandidatePrepared, null, null, null, candidateId);
            _store.Mutate(d => d.Journals[id] = journal);
            await Hook("BeforePromotion");
            Directory.Move(stagedDirectory, final);
            await Hook("AfterPromotion");
            _store.Mutate(d => d.Journals[id] = journal with { Phase = PluginTransactionPhase.CandidatePromoted });
            await Hook("BeforeMetadataCommit");
            _store.Mutate(d =>
            {
                d.Plugins.Add(record);
                d.Lifecycles[id] = new PluginLifecycleMetadata(
                    1, PluginLifecycleState.InstalledDisabled, candidateId, null, null, candidateId, PluginTransactionPhase.Committed,
                    [candidate with { RelativePath = id }]);
                d.Journals[id] = journal with { Phase = PluginTransactionPhase.Committed };
            });
            await Hook("AfterMetadataCommit");
            FinalizeCommitted(id);
            return;
        }

        var before = _store.Read().Lifecycles[id];
        // A RecoveryRequired target whose material is already missing has nothing to capture as rollback.
        var hasFinal = Directory.Exists(final);
        var rollbackId = hasFinal ? before.CurrentGenerationId : null;
        var retained = RetainedPath(before.CurrentGenerationId);
        var replacement = new PluginLifecycleJournal(operationId, id, PluginTransactionPhase.CandidatePrepared, rollbackId, before.ActivationLkgGenerationId, rollbackId, candidateId);
        _store.Mutate(d =>
        {
            var current = d.Lifecycles[id];
            d.Lifecycles[id] = current with
            {
                TransactionRollbackGenerationId = rollbackId,
                CandidateGenerationId = candidateId,
                TransactionPhase = PluginTransactionPhase.CandidatePrepared,
                Generations = [.. current.Generations, candidate],
            };
            d.Journals[id] = replacement;
        });
        if (rollbackId is not null)
        {
            await Hook("BeforeRollbackCapture");
            Directory.Move(final, retained);
            await Hook("AfterRollbackCapture");
            SetPhase(id, replacement, PluginTransactionPhase.RollbackCaptured, rollbackId, Relative(retained));
        }

        await Hook("BeforePromotion");
        Directory.Move(stagedDirectory, final);
        await Hook("AfterPromotion");
        SetPhase(id, replacement, PluginTransactionPhase.CandidatePromoted, null, null);
        await Hook("BeforeMetadataCommit");
        _store.Mutate(d =>
        {
            var current = d.Lifecycles[id];
            var index = d.Plugins.FindIndex(item => string.Equals(item.Id, id, StringComparison.Ordinal));
            d.Plugins[index] = record;
            d.Lifecycles[id] = current with
            {
                LifecycleVersion = checked(current.LifecycleVersion + 1),
                State = PluginLifecycleState.InstalledDisabled,
                CurrentGenerationId = candidateId,
                TransactionPhase = PluginTransactionPhase.Committed,
                SanitizedFailure = null,
                Generations = current.Generations.Select(g => g.GenerationId == candidateId ? g with { RelativePath = id } : g).ToList(),
            };
            d.Journals[id] = replacement with { Phase = PluginTransactionPhase.Committed };
        });
        await Hook("AfterMetadataCommit");
        FinalizeCommitted(id);
    }

    private void SetPhase(string id, PluginLifecycleJournal journal, PluginTransactionPhase phase, string? relocatedGenerationId, string? relocatedPath) =>
        _store.Mutate(d =>
        {
            var current = d.Lifecycles[id];
            d.Lifecycles[id] = current with
            {
                TransactionPhase = phase,
                Generations = relocatedGenerationId is null
                    ? current.Generations
                    : current.Generations.Select(g => g.GenerationId == relocatedGenerationId ? g with { RelativePath = relocatedPath! } : g).ToList(),
            };
            d.Journals[id] = journal with { Phase = phase };
        });

    /// <summary>
    /// Transaction complete: the metadata already names the new Current, so the rollback role is only
    /// cleanup-eligible. Retention keeps the rollback generation solely when it is also the activation LKG.
    /// Repeatable: every step tolerates already-done work.
    /// </summary>
    private void FinalizeCommitted(string id)
    {
        var document = _store.Read();
        var lifecycle = document.Lifecycles[id];
        var rollback = lifecycle.TransactionRollbackGenerationId;
        var now = _manager.Clock.GetUtcNow();
        _store.Mutate(d =>
        {
            var current = d.Lifecycles[id];
            var generations = current.Generations.ToList();
            if (rollback is not null && rollback != current.CurrentGenerationId && rollback != current.ActivationLkgGenerationId)
            {
                generations = generations.Select(g => g.GenerationId == rollback ? g with { RetiredAtUtc = now } : g).ToList();
            }

            d.Lifecycles[id] = current with
            {
                TransactionRollbackGenerationId = null,
                CandidateGenerationId = null,
                TransactionPhase = PluginTransactionPhase.None,
                Generations = generations,
            };
            d.Journals.Remove(id);
        });
    }

    /// <summary>
    /// Explicit administrator recovery transaction: swaps the activation-LKG generation (retained material)
    /// in as the committed Current, as InstalledDisabled. It reuses the journaled promotion phases; the
    /// candidate lives in retained storage, so recovery un-promotes it rather than deleting it. The current
    /// generation becomes the transaction rollback and is cleanup-eligible only after commit.
    /// </summary>
    private async Task RestoreLkgAsync(string id, PluginRecord existing, PluginLifecycleMetadata before, PluginManifest manifest, PluginProvenance provenance)
    {
        var final = FinalPath(id);
        var lkgId = before.ActivationLkgGenerationId!;
        var lkgPath = RetainedPath(lkgId);
        var currentRetained = RetainedPath(before.CurrentGenerationId);
        var hasFinal = Directory.Exists(final);
        var rollbackId = hasFinal ? before.CurrentGenerationId : null;
        var record = new PluginRecord(id, final, manifest, Enabled: false, existing.InstalledAtUtc, provenance);
        var journal = new PluginLifecycleJournal(
            Guid.NewGuid().ToString("N"), id, PluginTransactionPhase.CandidatePrepared, rollbackId, lkgId, rollbackId, lkgId, CandidateFromRetained: true);

        _store.Mutate(d =>
        {
            d.Lifecycles[id] = d.Lifecycles[id] with
            {
                TransactionRollbackGenerationId = rollbackId,
                CandidateGenerationId = lkgId,
                TransactionPhase = PluginTransactionPhase.CandidatePrepared,
            };
            d.Journals[id] = journal;
        });
        if (rollbackId is not null)
        {
            await Hook("BeforeRollbackCapture");
            Directory.Move(final, currentRetained);
            await Hook("AfterRollbackCapture");
            SetPhase(id, journal, PluginTransactionPhase.RollbackCaptured, rollbackId, Relative(currentRetained));
        }

        await Hook("BeforePromotion");
        Directory.Move(lkgPath, final);
        await Hook("AfterPromotion");
        SetPhase(id, journal, PluginTransactionPhase.CandidatePromoted, null, null);
        await Hook("BeforeMetadataCommit");
        _store.Mutate(d =>
        {
            var current = d.Lifecycles[id];
            var index = d.Plugins.FindIndex(item => string.Equals(item.Id, id, StringComparison.Ordinal));
            d.Plugins[index] = record;
            d.Lifecycles[id] = current with
            {
                LifecycleVersion = checked(current.LifecycleVersion + 1),
                State = PluginLifecycleState.InstalledDisabled,
                CurrentGenerationId = lkgId,
                TransactionPhase = PluginTransactionPhase.Committed,
                SanitizedFailure = null,
                Generations = current.Generations.Select(g => g.GenerationId == lkgId ? g with { RelativePath = id, RetiredAtUtc = null } : g).ToList(),
            };
            d.Journals[id] = journal with { Phase = PluginTransactionPhase.Committed };
        });
        await Hook("AfterMetadataCommit");
        FinalizeCommitted(id);
    }

    // ---- Recovery --------------------------------------------------------------------------------------

    /// <summary>
    /// Deterministic, idempotent per-plugin reconciliation. It runs under the plugin lock, decides only from
    /// the journal and lifecycle metadata (never timestamps, versions or enumeration order), and never
    /// activates or registers anything.
    /// </summary>
    private async Task RecoverCoreAsync(string id, PluginLifecycleRequestContext? context)
    {
        var document = _store.Read();
        var record = document.Plugins.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
        if (!document.Journals.TryGetValue(id, out var journal))
        {
            if (record is not null && !Directory.Exists(record.InstallPath) && Lifecycle(document, record).State != PluginLifecycleState.RecoveryRequired)
            {
                MarkRecoveryRequired(id, "The committed generation material is missing.");
                var after = _store.Read();
                await AuditAsync(context, "recover", "recovery", nameof(PluginLifecycleState.RecoveryRequired), id, record.Manifest.Version, Lifecycle(document, record).State, PluginLifecycleState.RecoveryRequired, Lifecycle(after, record).LifecycleVersion, null, replay: false);
            }

            return;
        }

        var lifecycle = record is null ? null : Lifecycle(document, record);
        var committed = lifecycle is not null && journal.CandidateGenerationId is not null && lifecycle.CurrentGenerationId == journal.CandidateGenerationId;
        string outcome;
        if (committed)
        {
            if (Directory.Exists(FinalPath(id)))
            {
                FinalizeCommitted(id);
                outcome = "CommittedFinalized";
            }
            else
            {
                MarkRecoveryRequired(id, "The committed generation material is missing.");
                outcome = nameof(PluginLifecycleState.RecoveryRequired);
            }
        }
        else if (record is null)
        {
            // First install never committed: the unreferenced candidate is deleted, never loaded.
            var stage = StagingPath(journal.CandidateGenerationId!);
            if (Directory.Exists(stage))
            {
                DeleteDirectory(stage);
            }
            else if (Directory.Exists(FinalPath(id)))
            {
                DeleteDirectory(FinalPath(id));
            }

            _store.Mutate(d => d.Journals.Remove(id));
            outcome = "CandidateDiscarded";
        }
        else if (journal.RollbackGenerationId is null)
        {
            var home = CandidateHome(journal);
            if (journal.CandidateFromRetained)
            {
                if (Directory.Exists(FinalPath(id)) && !Directory.Exists(home))
                {
                    Directory.Move(FinalPath(id), home);
                }

                ClearTransaction(id, journal.CandidateGenerationId!, PluginLifecycleState.RecoveryRequired, removeCandidate: false);
                outcome = "CandidateDiscarded";
            }
            else if (TryDiscardUncommittedCandidate(home, FinalPath(id)))
            {
                // No pre-transaction material was captured, so whatever sits at the final path is the promoted,
                // never-committed candidate. It is deleted, never adopted as Current or the activation LKG.
                ClearTransaction(id, journal.CandidateGenerationId!, PluginLifecycleState.RecoveryRequired, removeCandidate: true);
                outcome = "CandidateDiscarded";
            }
            else
            {
                // Fail closed: the journal, the candidate role and RecoveryRequired stay until the deletion can be retried,
                // so the uncommitted bytes can neither be mistaken for a committed generation nor be replaced over.
                KeepRecoveryRequiredPending(id, "An uncommitted candidate could not be removed; recovery will retry.");
                await AuditCleanupFailureAsync(context, id, "uncommitted-candidate");
                outcome = "CleanupRetryRequired";
            }
        }
        else if (TryRestoreRollback(journal, id))
        {
            // An archive replacement restores as InstalledDisabled (ADR-0037); an LKG restore changed nothing durable, so its prior state stands.
            ClearTransaction(id, journal.CandidateGenerationId!, journal.CandidateFromRetained ? null : PluginLifecycleState.InstalledDisabled, removeCandidate: !journal.CandidateFromRetained);
            outcome = "RollbackRestored";
        }
        else
        {
            MarkRecoveryRequired(id, "Recovery could not prove a single safe current generation.");
            outcome = nameof(PluginLifecycleState.RecoveryRequired);
        }

        var final = _store.Read();
        var finalRecord = final.Plugins.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
        await AuditAsync(
            context, "recover", "recovery", outcome, id, finalRecord?.Manifest.Version, lifecycle?.State,
            finalRecord is null ? null : Lifecycle(final, finalRecord).State, finalRecord is null ? 0 : Lifecycle(final, finalRecord).LifecycleVersion, null, replay: false);
    }

    /// <summary>
    /// Restores the pre-transaction Current generation from the transaction-rollback role. When the rollback
    /// generation's retained copy exists, whatever sits at the final path is the (uncommitted) candidate.
    /// Once restoration has been journaled, an absent retained copy with a present final path means it already
    /// completed. Returns false when a single safe current generation cannot be proved.
    /// </summary>
    private bool TryRestoreRollback(PluginLifecycleJournal journal, string id)
    {
        var final = FinalPath(id);
        var retained = RetainedPath(journal.RollbackGenerationId!);
        var stage = StagingPath(journal.CandidateGenerationId!);
        var candidateHome = CandidateHome(journal);
        var retainedExists = Directory.Exists(retained);
        var finalExists = Directory.Exists(final);

        if (retainedExists)
        {
            if (finalExists && journal.Phase == PluginTransactionPhase.CandidatePrepared)
            {
                return false;
            }

            _store.Mutate(d => d.Journals[id] = journal with { Phase = PluginTransactionPhase.Restoring });
            if (finalExists)
            {
                // A candidate that came from retained material (an LKG restore) is un-promoted, never deleted.
                if (journal.CandidateFromRetained)
                {
                    Directory.Move(final, candidateHome);
                }
                else
                {
                    DeleteDirectory(final);
                }
            }

            Directory.Move(retained, final);
        }
        else if (!(finalExists && journal.Phase is PluginTransactionPhase.CandidatePrepared or PluginTransactionPhase.Restoring))
        {
            return false;
        }

        if (!journal.CandidateFromRetained)
        {
            DeleteDirectory(stage);
        }

        return true;
    }

    /// <summary>Where a not-yet-promoted candidate lives: staging for an archive install, retained storage for an LKG restore.</summary>
    private string CandidateHome(PluginLifecycleJournal journal) =>
        journal.CandidateFromRetained ? RetainedPath(journal.CandidateGenerationId!) : StagingPath(journal.CandidateGenerationId!);

    private void ClearTransaction(string id, string candidateId, PluginLifecycleState? requestedState, bool removeCandidate = true) =>
        _store.Mutate(d =>
        {
            var index = d.Plugins.FindIndex(item => string.Equals(item.Id, id, StringComparison.Ordinal));
            var current = d.Lifecycles[id];
            var state = requestedState ?? current.State;
            var changed = current.State != state;
            d.Plugins[index] = d.Plugins[index] with { Enabled = false };
            d.Lifecycles[id] = current with
            {
                LifecycleVersion = changed ? checked(current.LifecycleVersion + 1) : current.LifecycleVersion,
                State = state,
                TransactionRollbackGenerationId = null,
                CandidateGenerationId = null,
                TransactionPhase = PluginTransactionPhase.None,
                SanitizedFailure = requestedState is null
                    ? current.SanitizedFailure
                    : state == PluginLifecycleState.RecoveryRequired ? "Recovery could not establish a committed generation." : null,
                Generations = removeCandidate ? current.Generations.Where(g => g.GenerationId != candidateId).ToList() : current.Generations,
            };
            d.Journals.Remove(id);
        });

    /// <summary>Deletes the staged and the promoted copies of an uncommitted candidate. Returns false (never throws) when deletion is blocked.</summary>
    private static bool TryDiscardUncommittedCandidate(string home, string finalPath)
    {
        try
        {
            DeleteDirectory(home);
            DeleteDirectory(finalPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Ensures the plugin is RecoveryRequired without closing its journal or candidate role, so recovery retries the cleanup.</summary>
    private void KeepRecoveryRequiredPending(string id, string reason) =>
        _store.Mutate(d =>
        {
            var index = d.Plugins.FindIndex(item => string.Equals(item.Id, id, StringComparison.Ordinal));
            if (index < 0 || !d.Lifecycles.TryGetValue(id, out var current) || current.State == PluginLifecycleState.RecoveryRequired)
            {
                return;
            }

            d.Plugins[index] = d.Plugins[index] with { Enabled = false };
            d.Lifecycles[id] = current with
            {
                LifecycleVersion = checked(current.LifecycleVersion + 1),
                State = PluginLifecycleState.RecoveryRequired,
                SanitizedFailure = reason,
            };
        });

    /// <summary>True while a journal survived recovery (a cleanup that must be retried): no new transaction may start over its material.</summary>
    private bool HasPendingTransaction(string id) => _store.Read().Journals.ContainsKey(id);

    /// <summary>
    /// Registers neither generation. The unproven rollback/candidate material is preserved as bounded
    /// evidence under the activation-LKG retention clock and the transaction marker is closed.
    /// </summary>
    private void MarkRecoveryRequired(string id, string reason)
    {
        var now = _manager.Clock.GetUtcNow();
        _store.Mutate(d =>
        {
            var index = d.Plugins.FindIndex(item => string.Equals(item.Id, id, StringComparison.Ordinal));
            if (index < 0)
            {
                d.Journals.Remove(id);
                return;
            }

            var current = d.Lifecycles[id];
            var evidence = new HashSet<string>(StringComparer.Ordinal);
            if (current.TransactionRollbackGenerationId is not null) evidence.Add(current.TransactionRollbackGenerationId);
            if (current.CandidateGenerationId is not null) evidence.Add(current.CandidateGenerationId);
            d.Plugins[index] = d.Plugins[index] with { Enabled = false };
            d.Lifecycles[id] = current with
            {
                LifecycleVersion = checked(current.LifecycleVersion + 1),
                State = PluginLifecycleState.RecoveryRequired,
                TransactionRollbackGenerationId = null,
                CandidateGenerationId = null,
                TransactionPhase = PluginTransactionPhase.None,
                SanitizedFailure = reason,
                Generations = current.Generations
                    .Select(g => evidence.Contains(g.GenerationId) && g.GenerationId != current.CurrentGenerationId
                        ? g with { WasActivationLkg = true, RetiredAtUtc = now }
                        : g)
                    .ToList(),
            };
            d.Journals.Remove(id);
        });
    }

    // ---- Cleanup ---------------------------------------------------------------------------------------

    /// <summary>
    /// Bounded retention for one plugin: keeps Current, the activation LKG, the transaction roles, and any
    /// former activation LKG until its 30-day retention has elapsed. A failed deletion never changes state and
    /// is retried by the next sweep.
    /// </summary>
    private async Task SweepAsync(string id, PluginLifecycleRequestContext? context)
    {
        var document = _store.Read();
        if (!document.Lifecycles.TryGetValue(id, out var lifecycle))
        {
            return;
        }

        var now = _manager.Clock.GetUtcNow();
        var kept = new List<PluginGeneration>();
        var failedCategories = new List<string>();
        var changed = false;
        foreach (var generation in lifecycle.Generations)
        {
            var isRole = generation.GenerationId == lifecycle.CurrentGenerationId ||
                         generation.GenerationId == lifecycle.ActivationLkgGenerationId ||
                         generation.GenerationId == lifecycle.TransactionRollbackGenerationId ||
                         generation.GenerationId == lifecycle.CandidateGenerationId;
            if (isRole)
            {
                kept.Add(generation);
                continue;
            }

            var eligible = !generation.WasActivationLkg ||
                           (generation.RetiredAtUtc is { } retired && now >= retired + ActivationLkgRetention);
            if (eligible && TryDeleteGenerationMaterial(generation.GenerationId))
            {
                changed = true;
                continue;
            }

            if (eligible)
            {
                failedCategories.Add(generation.WasActivationLkg ? "retired-activation-lkg" : "superseded-generation");
            }

            kept.Add(generation.WasActivationLkg && generation.RetiredAtUtc is null ? generation with { RetiredAtUtc = now } : generation);
            changed |= generation.WasActivationLkg && generation.RetiredAtUtc is null;
        }

        if (changed)
        {
            _store.Mutate(d =>
            {
                if (d.Lifecycles.TryGetValue(id, out var current))
                {
                    d.Lifecycles[id] = current with { Generations = kept };
                }
            });
        }

        foreach (var category in failedCategories)
        {
            await AuditCleanupFailureAsync(context, id, category);
        }
    }

    private bool TryDeleteGenerationMaterial(string generationId)
    {
        try
        {
            DeleteDirectory(RetainedPath(generationId));
            DeleteDirectory(StagingPath(generationId));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Deletes lifecycle-owned material that no journal or generation references. Startup only, never during a mutation.</summary>
    private async Task SweepOrphansAsync()
    {
        var document = _store.Read();
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var lifecycle in document.Lifecycles.Values)
        {
            foreach (var generation in lifecycle.Generations) referenced.Add(generation.GenerationId);
        }

        foreach (var journal in document.Journals.Values)
        {
            if (journal.CandidateGenerationId is not null) referenced.Add(journal.CandidateGenerationId);
            if (journal.RollbackGenerationId is not null) referenced.Add(journal.RollbackGenerationId);
        }

        var failures = 0;
        failures += DeleteAllIn(Path.Combine(_lifecycleRoot, "uploads"), _ => false);
        failures += DeleteAllIn(Path.Combine(_lifecycleRoot, "staging"), name => referenced.Contains(name));
        failures += DeleteAllIn(Path.Combine(_lifecycleRoot, "lkg"), name => referenced.Contains(name));
        if (Directory.Exists(_root))
        {
            foreach (var legacy in Directory.EnumerateDirectories(_root, ".staging-*"))
            {
                failures += TryDelete(legacy) ? 0 : 1;
            }
        }

        for (var index = 0; index < failures; index++)
        {
            await AuditCleanupFailureAsync(null, null, "orphan-material");
        }
    }

    /// <summary>Deletes every unreferenced entry; returns how many could not be deleted (retried by the next startup sweep).</summary>
    private static int DeleteAllIn(string directory, Func<string, bool> keep)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var failures = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if (!keep(Path.GetFileName(entry)) && !TryDelete(entry))
            {
                failures++;
            }
        }

        return failures;
    }

    private static bool TryDelete(string path)
    {
        try
        {
            DeleteDirectory(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleanup failure never changes committed state; the caller audits it and the next sweep retries.
            return false;
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}

/// <summary>Test-seam signal emulating process death at an internal checkpoint (the checkpoint hook is internal): no in-process rollback, cleanup or idempotency release runs.</summary>
public sealed class PluginLifecycleInterruptedException : Exception
{
    public PluginLifecycleInterruptedException() : this("Simulated process interruption.") { }
    public PluginLifecycleInterruptedException(string message) : base(message) { }
    public PluginLifecycleInterruptedException(string message, Exception innerException) : base(message, innerException) { }
}
