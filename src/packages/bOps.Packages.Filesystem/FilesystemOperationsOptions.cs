// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Packages.Filesystem;

public sealed class FilesystemOperationsOptions
{
    public const long DefaultMaxCopyBytes = 1024L * 1024 * 1024;
    public const long HardMaxCopyBytes = 10L * 1024 * 1024 * 1024;

    public long MaxCopyBytes { get; init; } = DefaultMaxCopyBytes;

    public void Validate()
    {
        if (MaxCopyBytes <= 0 || MaxCopyBytes > HardMaxCopyBytes)
        {
            throw new InvalidOperationException(
                $"Filesystem:Operations:MaxCopyBytes must be between 1 and {HardMaxCopyBytes}.");
        }
    }
}
