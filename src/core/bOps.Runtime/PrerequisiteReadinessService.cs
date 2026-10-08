// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using Microsoft.Extensions.Logging;

namespace bOps.Runtime;

/// <summary>What one readiness refresh did.</summary>
/// <param name="Checked">How many prerequisites were checked.</param>
/// <param name="Messages">How many transition system messages were written.</param>
/// <param name="RecordFailures">How many observations could not be recorded (the readiness snapshots were refreshed regardless).</param>
public sealed record PrerequisiteRefreshReport(int Checked, int Messages, int RecordFailures);

/// <summary>
/// The one global readiness cycle (ADR-0049): run every registered prerequisite check (bounded concurrency), atomically record each
/// state transition and its system message, then re-snapshot Tool and Skill Capability availability — in that order, so a component is
/// never offered on the strength of a state that was not recorded. Cycles are serialised: overlapping callers queue, never interleave.
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
            foreach (var result in results)
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
                    failures++;
                    logger.LogWarning(ex, "Prerequisite '{PrerequisiteId}' observation could not be recorded.", result.Id);
                }
            }

            await tools.RefreshCapabilitiesAsync(ct);
            await skills.RefreshPrerequisitesAsync(ct);
            return new PrerequisiteRefreshReport(results.Count, messages, failures);
        }
        finally
        {
            _cycle.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _cycle.Dispose();
}
