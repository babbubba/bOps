// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Packages.Web;

/// <summary>
/// Copies up to a fixed byte budget from a stream, shared by <see cref="WebFetchService"/> and
/// <see cref="SearxngClient"/>. The counter runs against whatever <paramref name="source"/> yields
/// — already-decompressed bytes, when the caller wraps a decompression stream — which is what
/// makes it a bound on materialized memory, not just wire bytes (ADR-0028).
/// </summary>
internal static class BoundedReader
{
    public static async Task<(byte[] Bytes, bool Truncated)> ReadAsync(Stream source, long maxBytes, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await source.ReadAsync(chunk, ct);
            if (read == 0)
            {
                return (buffer.ToArray(), false);
            }

            var remaining = maxBytes - buffer.Length;
            if (read >= remaining)
            {
                await buffer.WriteAsync(chunk.AsMemory(0, (int)remaining), ct);
                return (buffer.ToArray(), true);
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
        }
    }
}
