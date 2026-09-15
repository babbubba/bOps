using System.Text.Json.Serialization;
using bOps.Abstractions;

namespace bOps.Memory;

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(TaskState))]
internal sealed partial class MemoryJsonContext : JsonSerializerContext;
