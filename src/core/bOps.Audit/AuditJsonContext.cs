// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;
using bOps.Abstractions;

namespace bOps.Audit;

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(AuditEvent))]
[JsonSerializable(typeof(ToolCallAuditEvent))]
[JsonSerializable(typeof(ModelCallAuditEvent))]
[JsonSerializable(typeof(EvidenceReadAuditEvent))]
[JsonSerializable(typeof(KnowledgeReadAuditEvent))]
[JsonSerializable(typeof(KnowledgePackAdmissionAuditEvent))]
[JsonSerializable(typeof(KnowledgeIndexAuditEvent))]
[JsonSerializable(typeof(PolicyDecisionAuditEvent))]
[JsonSerializable(typeof(ApprovalAuditEvent))]
[JsonSerializable(typeof(SkillRunAuditEvent))]
[JsonSerializable(typeof(AuditChainEnvelope))]
[JsonSerializable(typeof(DiagnosticAssuranceResult))]
internal sealed partial class AuditJsonContext : JsonSerializerContext;
