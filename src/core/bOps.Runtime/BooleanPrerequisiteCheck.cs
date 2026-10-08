// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>
/// Adapts an existing boolean capability check (the shape <see cref="CachingCapabilityProbe.RegisterCheck"/> takes) to an
/// <see cref="IPrerequisiteCheck"/>, so a host can move a check to <see cref="PrerequisiteRegistry"/> without changing what it
/// observes: <c>true</c> is <see cref="PrerequisiteState.Available"/>, <c>false</c> is <see cref="PrerequisiteState.Unavailable"/>.
/// </summary>
public sealed class BooleanPrerequisiteCheck(PrerequisiteDescriptor descriptor, Func<CancellationToken, Task<bool>> check) : IPrerequisiteCheck
{
    /// <inheritdoc />
    public PrerequisiteDescriptor Descriptor { get; } = descriptor ?? throw new ArgumentNullException(nameof(descriptor));

    /// <inheritdoc />
    public async Task<PrerequisiteCheckOutcome> CheckAsync(CancellationToken ct = default) =>
        await check(ct)
            ? new PrerequisiteCheckOutcome(PrerequisiteState.Available, PrerequisiteCodes.Available, $"{Descriptor.DisplayName} is available.")
            : new PrerequisiteCheckOutcome(PrerequisiteState.Unavailable, PrerequisiteCodes.Unavailable, $"{Descriptor.DisplayName} is not available.");
}
