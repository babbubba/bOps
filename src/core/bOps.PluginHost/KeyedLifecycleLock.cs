// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.PluginHost;

/// <summary>
/// Per-plugin-id asynchronous mutual exclusion (ADR-0037). Entries are reference counted and removed
/// as soon as no holder or waiter remains, so the table cannot grow without bound. Different keys
/// never contend.
/// </summary>
internal sealed class KeyedLifecycleLock
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>Number of keys currently held or awaited; zero when idle.</summary>
    public int ActiveKeyCount
    {
        get
        {
            lock (_entries)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Holders plus waiters for one key (0 when idle). Test-observable proof that a request is queued.</summary>
    public int ReferenceCount(string key)
    {
        lock (_entries)
        {
            return _entries.TryGetValue(key, out var entry) ? entry.References : 0;
        }
    }

    public async Task<IDisposable> AcquireAsync(string key, CancellationToken cancellationToken = default)
    {
        var entry = Enter(key);
        try
        {
            await entry.Gate.WaitAsync(cancellationToken);
        }
        catch
        {
            Exit(key, entry, release: false);
            throw;
        }

        return new Releaser(this, key, entry);
    }

    public IDisposable Acquire(string key)
    {
        var entry = Enter(key);
        entry.Gate.Wait();
        return new Releaser(this, key, entry);
    }

    private Entry Enter(string key)
    {
        lock (_entries)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new Entry();
                _entries[key] = entry;
            }

            entry.References++;
            return entry;
        }
    }

    private void Exit(string key, Entry entry, bool release)
    {
        if (release)
        {
            entry.Gate.Release();
        }

        lock (_entries)
        {
            if (--entry.References == 0)
            {
                _entries.Remove(key);
                entry.Gate.Dispose();
            }
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int References { get; set; }
    }

    private sealed class Releaser(KeyedLifecycleLock owner, string key, Entry entry) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Exit(key, entry, release: true);
            }
        }
    }
}
