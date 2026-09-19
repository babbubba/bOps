// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Runtime.Tests;

/// <summary>
/// SplitMix64. The property test must produce the same inputs on every machine and every .NET version
/// so a failure can be reproduced from its message alone; <see cref="Random"/> does not promise its
/// sequence across versions, and it is a security-analyzer finding besides.
/// </summary>
internal sealed class DeterministicRandom(ulong seed)
{
    private ulong _state = seed;

    public int Next(int maxExclusive) => Next(0, maxExclusive);

    public int Next(int minInclusive, int maxExclusive) =>
        minInclusive + (int)(NextUInt64() % (ulong)(maxExclusive - minInclusive));

    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    private ulong NextUInt64()
    {
        unchecked
        {
            _state += 0x9E3779B97F4A7C15UL;
            var z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}
