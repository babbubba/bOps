// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;
using bOps.Abstractions;

namespace bOps.Runtime.Tests;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TaskState))]
[JsonSerializable(typeof(DiagnosticAssuranceResult))]
internal sealed partial class DiagnosticAssuranceTestJsonContext : JsonSerializerContext;
