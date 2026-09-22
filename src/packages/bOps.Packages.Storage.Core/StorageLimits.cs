// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Packages.Storage.Core;

public static class StorageLimits
{
    public const int DefaultRows = 200;
    public const int MaximumRows = 2_000;
    public const int DefaultOutputBytes = 65_536;
    public const int MinimumOutputBytes = 4_096;
    public const int MaximumOutputBytes = 262_144;
    public const int DefaultSampleMilliseconds = 1_000;
    public const int MinimumSampleMilliseconds = 500;
    public const int MaximumSampleMilliseconds = 5_000;
    public const int TextCharacters = 256;
}
