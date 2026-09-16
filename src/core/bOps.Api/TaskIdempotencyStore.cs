// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;

namespace bOps.Api;

internal sealed class TaskIdempotencyStore
{
    private readonly ConcurrentDictionary<string, Lazy<Task<Guid?>>> _starts = new(StringComparer.Ordinal);

    public async Task<Guid?> GetOrStartAsync(string actorId, string? key, Func<Task<Guid?>> start)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(start);

        if (string.IsNullOrWhiteSpace(key))
        {
            return await start();
        }

        if (key.Length > 128)
        {
            throw new ArgumentException("Idempotency-Key must not exceed 128 characters.", nameof(key));
        }

        var scope = $"{actorId}\n{key}";
        var lazy = _starts.GetOrAdd(scope, _ => new Lazy<Task<Guid?>>(start, LazyThreadSafetyMode.ExecutionAndPublication));
        var result = await lazy.Value;
        if (result is null)
        {
            _starts.TryRemove(scope, out _);
        }

        return result;
    }
}
