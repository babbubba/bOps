// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>
/// Checks whether a named capability (a Docker daemon responding, a systemd bus present, …) is
/// available, caching each result for a configured duration rather than resolving once at
/// startup (agentic/01-architecture-rules.md, rule B4) — a daemon that starts later must become
/// visible without restarting bOps.
/// </summary>
public sealed class CachingCapabilityProbe(TimeProvider timeProvider, TimeSpan cacheDuration) : ICapabilityProbe
{
    private readonly ConcurrentDictionary<string, Func<CancellationToken, Task<bool>>> _checks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (bool Available, DateTimeOffset ExpiresAtUtc)> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers how to check one capability. A package (or the host) calls this once per capability it can probe.</summary>
    public void RegisterCheck(string capability, Func<CancellationToken, Task<bool>> check) =>
        _checks[capability] = check;

    /// <inheritdoc />
    public async Task<bool> IsAvailableAsync(string capability, CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow();

        if (_cache.TryGetValue(capability, out var cached) && cached.ExpiresAtUtc > now)
        {
            return cached.Available;
        }

        if (!_checks.TryGetValue(capability, out var check))
        {
            return false;
        }

        var available = await check(ct);
        _cache[capability] = (available, now + cacheDuration);
        return available;
    }
}
