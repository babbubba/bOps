// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace Acme.SamplePlugin;

/// <summary>
/// A read-only prerequisite check a plugin contributes through <see cref="IPrerequisiteProvider"/> (ADR-0049): it observes whether
/// the marker directory the sample's marker tools write into can be used, and changes nothing. The host registers it under the
/// plugin's host-assigned package id and removes it when the plugin is disabled — the plugin never names its own identity.
/// </summary>
public sealed class SamplePrerequisiteCheck : IPrerequisiteCheck
{
    /// <summary>The prerequisite id, lowercase dotted.</summary>
    public const string Id = "acme.sample-marker-store";

    /// <summary>The directory whose contents steer this sample check: an <c>unavailable</c> file reports it Unavailable, and each run appends to <c>runs.log</c>.</summary>
    public static string ControlDirectory { get; } = Path.Combine(Path.GetTempPath(), "bops-sample-prerequisite");

    public PrerequisiteDescriptor Descriptor { get; } = new(
        Id, "Sample marker store", "Where the sample plugin's marker tools write their files.", PrerequisiteKind.Configuration)
    {
        Remediation = "Make the sample marker directory writable.",
    };

    public async Task<PrerequisiteCheckOutcome> CheckAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (Directory.Exists(ControlDirectory))
        {
            await File.AppendAllTextAsync(Path.Combine(ControlDirectory, "runs.log"), "run" + Environment.NewLine, ct);
        }

        return File.Exists(Path.Combine(ControlDirectory, "unavailable"))
            ? new PrerequisiteCheckOutcome(PrerequisiteState.Unavailable, "marker-store-unavailable", "The sample marker store is not usable.")
            : new PrerequisiteCheckOutcome(PrerequisiteState.Available, "available", "The sample marker store is usable.");
    }
}
