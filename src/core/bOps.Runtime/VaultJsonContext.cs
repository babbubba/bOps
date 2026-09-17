// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace bOps.Runtime;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(VaultFile))]
internal sealed partial class VaultJsonContext : JsonSerializerContext;
