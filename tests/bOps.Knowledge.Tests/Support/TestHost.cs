// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using bOps.Abstractions;

namespace bOps.Knowledge.Tests;

/// <summary>A scratch directory removed with the test; snapshot files are read-only, so removal clears that first.</summary>
internal sealed class TempDirectory : IDisposable
{
    internal TempDirectory()
    {
        // Short on purpose: snapshot paths are deep and the Windows path limit is real.
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bk" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Path);
    }

    internal string Path { get; }

    internal string Sub(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A test scratch directory that cannot be removed is not a test failure.
        }
    }
}

/// <summary>An audit sink safe for the concurrent tests.</summary>
internal sealed class ConcurrentAuditSink : IAuditSink
{
    private readonly ConcurrentQueue<AuditEvent> events = new();

    internal IReadOnlyList<AuditEvent> Events => [.. events];

    public Task WriteAsync(AuditEvent evt, CancellationToken ct = default)
    {
        events.Enqueue(evt);
        return Task.CompletedTask;
    }
}

/// <summary>A source that serves prepared packs per tenant and pack reference and counts how often it was asked.</summary>
internal sealed class InMemoryPackSource : IKnowledgePackSource
{
    private readonly Dictionary<(string Tenant, string Id, string Version), Func<RawKnowledgePack?>> packs = [];
    private int opens;

    internal int Opens => Volatile.Read(ref opens);

    internal Exception? Throws { get; set; }

    /// <summary>What the source claims about itself. Nothing may ever read it to decide trust.</summary>
    internal string ClaimedName { get; set; } = "trusted-official-provider";

    internal bool ClaimsTrusted { get; set; } = true;

    internal InMemoryPackSource Add(TenantId tenant, string id, string version, Func<RawKnowledgePack?> pack)
    {
        packs[(tenant.Value, id, version)] = pack;
        return this;
    }

    public Task<RawKnowledgePack?> OpenAsync(PackReference reference, TenantId tenant, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Interlocked.Increment(ref opens);
        if (Throws is { } failure)
        {
            throw failure;
        }

        return Task.FromResult(packs.TryGetValue((tenant.Value, reference.PackId, reference.Version), out var factory) ? factory() : null);
    }
}

/// <summary>A host tenant authority for tests: the actor's id is the tenant.</summary>
internal sealed class ActorIdTenantAuthority : ITenantAuthority
{
    public TenantId? Resolve(ActorIdentity actor) =>
        actor.Id.StartsWith("tenant-", StringComparison.Ordinal) ? new TenantId(actor.Id) : null;
}
