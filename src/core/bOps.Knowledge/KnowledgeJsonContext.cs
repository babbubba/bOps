// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;

namespace bOps.Knowledge;

/// <summary>
/// Strict source-generated serialization for every document of the format: unknown members are refused, so a manifest cannot carry
/// a field a reader of another version would silently ignore.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowTrailingCommas = false,
    ReadCommentHandling = JsonCommentHandling.Disallow,
    RespectNullableAnnotations = true)]
[JsonSerializable(typeof(PackManifest))]
[JsonSerializable(typeof(PackSignatureEnvelope))]
[JsonSerializable(typeof(KnowledgePin))]
[JsonSerializable(typeof(KnowledgeTrustStoreFile))]
internal sealed partial class KnowledgeJsonContext : JsonSerializerContext;
