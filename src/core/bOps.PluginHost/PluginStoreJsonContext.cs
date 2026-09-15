// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace bOps.PluginHost;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<PluginRecord>))]
internal sealed partial class PluginStoreJsonContext : JsonSerializerContext;
