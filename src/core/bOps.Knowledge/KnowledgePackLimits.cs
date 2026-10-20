// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Knowledge;

/// <summary>The size and count bounds a pack must stay inside. Sources stop reading at them and the verifier refuses what exceeds them.</summary>
/// <param name="MaxFiles">The most payload files.</param>
/// <param name="MaxFileBytes">The largest payload file.</param>
/// <param name="MaxTotalBytes">The largest sum of payload files.</param>
/// <param name="MaxManifestBytes">The largest manifest.</param>
/// <param name="MaxSignatureBytes">The largest signature file.</param>
/// <param name="MaxPathLength">The longest relative path.</param>
public sealed record KnowledgePackLimits(
    int MaxFiles = 128,
    long MaxFileBytes = 512 * 1024,
    long MaxTotalBytes = 4 * 1024 * 1024,
    long MaxManifestBytes = 128 * 1024,
    long MaxSignatureBytes = 8 * 1024,
    int MaxPathLength = 128)
{
    /// <summary>The defaults.</summary>
    public static KnowledgePackLimits Default { get; } = new();
}
