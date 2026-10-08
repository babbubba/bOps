// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using Microsoft.Extensions.Logging;

namespace bOps.Runtime;

/// <summary>What one readiness refresh did.</summary>
/// <param name="Checked">How many prerequisites were checked.</param>
/// <param name="Messages">How many transition system messages were written.</param>
/// <param name="RecordFailures">How many observations could not be durably recorded; each one was treated as <c>Error</c> (fail-closed), never as the observed state.</param>
public sealed record PrerequisiteRefreshReport(int Checked, int Messages, int RecordFailures)
{
    /// <summary>How many prerequisites a component declares but no package registered a check for (each is recorded as <c>Error</c>/<c>not-registered</c>).</summary>
    public int NotRegistered { get; init; }
}

/// <summary>
/// The one global readiness cycle (ADR-0049): run every registered prerequisite check (bounded concurrency), atomically record each
/// state transition and its system message, then re-snapshot Tool and Skill Capability availability — in that order, so a component is
/// never offered on the strength of a state that was not recorded: an observation that cannot be durably recorded is replaced, in the
/// host-owned registry, by <c>Error</c>/<c>state-record-failed</c> before any snapshot is taken. A prerequisite a component declares but
/// nobody registered a check for is recorded the same way as <c>Error</c>/<c>not-registered</c> (one message, deduplicated by the
/// fingerprint). Cycles are serialised: overlapping callers queue, never interleave.
/// The boot refresh, the periodic coordinator and the CLI's one-shot refresh all use this class; none of them keeps its own copy of the
/// state, which lives in <see cref="PrerequisiteRegistry"/> and, durably, in the prerequisite state store.
/// </summary>
public sealed class PrerequisiteReadinessService(
    PrerequisiteRegistry registry,
    PrerequisiteTransitionRecorder recorder,
    ToolRegistry tools,
    SkillRegistry skills,
    ILogger<PrerequisiteReadinessService> logger) : IDisposable
{
    private readonly SemaphoreSlim _cycle = new(1, 1);

    /// <summary>Runs one full cycle. Caller cancellation propagates; a check or store failure never aborts the cycle.</summary>
    /// <param name="ct">Cancels the cycle.</param>
    public async Task<PrerequisiteRefreshReport> RefreshAsync(CancellationToken ct = default)
    {
        await _cycle.WaitAsync(ct);
        try
        {
            // Declarations only (no I/O): which components name which prerequisite, deciding severity and the affected list.
            var components = tools.GetReadiness().Concat(skills.GetReadiness()).ToArray();
            var descriptors = registry.GetRegistrations().ToDictionary(r => r.Descriptor.Id, r => r.Descriptor, StringComparer.OrdinalIgnoreCase);

            var results = await registry.RefreshAsync(ct);

            var messages = 0;
            var failures = 0;
            var observations = new List<PrerequisiteCheckResult>();

            async Task RecordAsync(PrerequisiteCheckResult result)
            {
                try
                {
                    var message = await recorder.RecordAsync(
                        result,
                        PrerequisiteUsage.For(result.Id, components),
                        descriptors.GetValueOrDefault(result.Id),
                        ct);
                    if (message is not null)
                    {
                        messages++;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Fail closed: an observation that could not be durably recorded must not make anything more permissive. The raw
                    // exception stays in the server log; the state and any operator-facing text carry only the stable code.
                    failures++;
                    logger.LogWarning(ex, "Prerequisite '{PrerequisiteId}' observation could not be recorded; treating it as unavailable.", result.Id);
                    var closed = RecordFailed(result);
                    registry.AddHostObservation(closed);
                    observations.Add(closed);
                }
            }

            foreach (var result in results)
            {
                await RecordAsync(result);
            }

            // Declared minus registered: a structural readiness failure, observed by the host and recorded like any other transition.
            var checkedIds = results.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unregistered = 0;
            foreach (var id in DeclaredIds(components).Where(id => !checkedIds.Contains(id) && !descriptors.ContainsKey(id)))
            {
                unregistered++;
                var structural = NotRegistered(id);
                registry.AddHostObservation(structural);
                observations.Add(structural);
                try
                {
                    var message = await recorder.RecordAsync(structural, PrerequisiteUsage.For(id, components), null, ct);
                    if (message is not null)
                    {
                        messages++;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures++;
                    logger.LogWarning(ex, "Prerequisite '{PrerequisiteId}' has no registered check and the observation could not be recorded.", id);
                }
            }

            registry.SetHostObservations(observations);

            await tools.RefreshCapabilitiesAsync(ct);
            await skills.RefreshPrerequisitesAsync(ct);
            return new PrerequisiteRefreshReport(results.Count, messages, failures) { NotRegistered = unregistered };
        }
        finally
        {
            _cycle.Release();
        }
    }

    private SortedSet<string> DeclaredIds(IEnumerable<ComponentReadiness> components)
    {
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var declared in components.SelectMany(c => c.Requires.Concat(c.OptionalRequires)))
        {
            // Legacy `Requires` entries are not newly validated; an id the state store could never hold is logged, not recorded
            // (the component stays unavailable: an unregistered id is never satisfied).
            var id = declared.ToLowerInvariant();
            if (OperationalIdentifier.IsValidPrerequisiteId(id))
            {
                ids.Add(id);
            }
            else
            {
                logger.LogWarning("A component declares prerequisite '{PrerequisiteId}', which is not a valid prerequisite id.", declared);
            }
        }

        return ids;
    }

    private PrerequisiteCheckResult NotRegistered(string id) => new()
    {
        Id = id,
        State = PrerequisiteState.Error,
        Code = PrerequisiteCodes.NotRegistered,
        Message = $"No package registered a check for prerequisite '{id}'. Components depending on it cannot determine readiness.",
        CheckedAtUtc = registry.Now,
    };

    private static PrerequisiteCheckResult RecordFailed(PrerequisiteCheckResult observed) => new()
    {
        Id = observed.Id,
        State = PrerequisiteState.Error,
        Code = PrerequisiteCodes.StateRecordFailed,
        Message = "The prerequisite observation could not be recorded, so the prerequisite is treated as unavailable until it can be.",
        CheckedAtUtc = observed.CheckedAtUtc,
    };

    /// <inheritdoc />
    public void Dispose() => _cycle.Dispose();
}
