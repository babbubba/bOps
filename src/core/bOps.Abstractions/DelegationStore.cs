// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Abstractions;

/// <summary>The result of <see cref="IDelegationStore.StartAsync"/>: the run now stored for the request, and whether this call created it.</summary>
/// <param name="Run">The stored run: the one just created, or the earlier run the idempotency key already identified.</param>
/// <param name="Created"><c>true</c> when this call stored <paramref name="Run"/>; <c>false</c> when an earlier run was returned and nothing was written.</param>
public sealed record DelegationStartResult(DelegationRun Run, bool Created);

/// <summary>
/// Persists <see cref="DelegationRun"/> so a delegated objective survives a crash or a restart and can be
/// resumed without repeating a side effect (ADR-0030 section 7). Node-local like <see cref="IAuditSink"/>
/// and <see cref="ITaskStore"/> (rule A6): a run never spans nodes. The whole aggregate is written
/// atomically, so a reader sees a run as of one complete write, never half of one. <see cref="TaskState"/>
/// and <see cref="ITaskStore"/> are unchanged by delegation.
/// </summary>
/// <remarks>
/// The run's step journal is only useful if it is durable before the step it describes runs: an
/// implementation must have committed a <see cref="SaveAsync"/> before returning from it. The store is
/// protected by file permissions only; unlike the audit chain it is not tamper-evident (ADR-0030,
/// Consequences).
/// </remarks>
public interface IDelegationStore
{
    /// <summary>
    /// Stores a new run, unless a run already exists for the same <see cref="DelegationRun.Actor"/> and
    /// <see cref="DelegationRun.IdempotencyKey"/>, in which case that run is returned and nothing is
    /// written. Atomic: two concurrent starts with the same key create exactly one run. A run with no
    /// key is always created.
    /// </summary>
    /// <param name="run">The run to store.</param>
    /// <param name="ct">Cancelled if the write should be abandoned.</param>
    Task<DelegationStartResult> StartAsync(DelegationRun run, CancellationToken ct = default);

    /// <summary>
    /// Replaces the stored run with the same <see cref="DelegationRun.Id"/>. Durable when it returns.
    /// </summary>
    /// <param name="run">The run to store.</param>
    /// <param name="ct">Cancelled if the write should be abandoned.</param>
    Task SaveAsync(DelegationRun run, CancellationToken ct = default);

    /// <summary>Loads a run by id, or <c>null</c> if none is stored.</summary>
    /// <param name="delegationId">The run's id.</param>
    /// <param name="ct">Cancelled if the read should be abandoned.</param>
    Task<DelegationRun?> LoadAsync(Guid delegationId, CancellationToken ct = default);

    /// <summary>
    /// Lists every stored run currently at <paramref name="status"/> — in practice, how the runtime finds
    /// what is resumable after a restart (<see cref="DelegationStatus.Running"/>) and what waits for an
    /// operator (<see cref="DelegationStatus.AwaitingApproval"/>, <see cref="DelegationStatus.RequiresReconciliation"/>).
    /// </summary>
    /// <param name="status">The status to filter by.</param>
    /// <param name="ct">Cancelled if the read should be abandoned.</param>
    Task<IReadOnlyList<DelegationRun>> ListByStatusAsync(DelegationStatus status, CancellationToken ct = default);

    /// <summary>Lists the most recently written runs, newest first.</summary>
    /// <param name="limit">The maximum number of runs to return.</param>
    /// <param name="ct">Cancelled if the read should be abandoned.</param>
    Task<IReadOnlyList<DelegationRun>> ListRecentAsync(int limit, CancellationToken ct = default);
}
