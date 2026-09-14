using System.Text.Json.Serialization;
using bOps.Abstractions;

namespace bOps.Audit;

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(AuditEvent))]
[JsonSerializable(typeof(ToolCallAuditEvent))]
[JsonSerializable(typeof(ModelCallAuditEvent))]
[JsonSerializable(typeof(PolicyDecisionAuditEvent))]
internal sealed partial class AuditJsonContext : JsonSerializerContext;
