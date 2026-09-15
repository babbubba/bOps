// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;
using bOps.Abstractions;

namespace bOps.PluginHost;

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(PluginManifest))]
internal sealed partial class PluginManifestJsonContext : JsonSerializerContext;
