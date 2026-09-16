// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace bOps.PluginHost;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(PluginSignatureEnvelope))]
[JsonSerializable(typeof(List<PluginPublisherTrust>))]
internal sealed partial class PluginSecurityJsonContext : JsonSerializerContext;
