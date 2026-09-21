// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Packages.Docker;

/// <summary>
/// An <see cref="IProgress{T}"/> that runs its callback on the reporting thread, in order. <see cref="Progress{T}"/> posts to the
/// thread pool, so its callbacks can still be running (or reordered) when the daemon call returns; a tool that reads what the
/// daemon said once the call is over needs every message to have been seen.
/// </summary>
internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
