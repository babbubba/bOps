// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;
using bOps.Abstractions;

namespace bOps.Audit;

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(AuditEvent))]
[JsonSerializable(typeof(ToolCallAuditEvent))]
[JsonSerializable(typeof(ModelCallAuditEvent))]
[JsonSerializable(typeof(PolicyDecisionAuditEvent))]
[JsonSerializable(typeof(ApprovalAuditEvent))]
[JsonSerializable(typeof(AuditChainEnvelope))]
internal sealed partial class AuditJsonContext : JsonSerializerContext;
