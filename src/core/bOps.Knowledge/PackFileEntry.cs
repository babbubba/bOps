// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Knowledge;

/// <summary>One payload file of a pack as the signed manifest lists it.</summary>
/// <param name="Path">The relative path with forward slashes, below the pack root.</param>
/// <param name="Sha256">The lower-case hexadecimal SHA-256 of the file bytes.</param>
/// <param name="Size">The size of the file in bytes.</param>
public sealed record PackFileEntry(string Path, string Sha256, long Size);
