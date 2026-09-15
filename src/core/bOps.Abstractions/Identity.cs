using System.Text.Json;
using System.Text.Json.Serialization;

namespace bOps.Abstractions;

/// <summary>
/// Identifies the node (machine) a task, step, or audit event belongs to. Every one of them
/// carries a <see cref="NodeId"/> from V0.1, even though bOps runs on a single node until
/// remote agents exist — see agentic/01-architecture-rules.md, rule A3.
/// </summary>
[JsonConverter(typeof(NodeIdJsonConverter))]
public readonly record struct NodeId(string Value)
{
    /// <summary>The node id used while bOps executes only on the machine it administers.</summary>
    public static NodeId Local { get; } = new("local");

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// Identifies the package that contributed a tool or a model provider. Assigned by the
/// registry at registration time (agentic/01-architecture-rules.md, rule A11) — a package
/// declares an id in its manifest, but cannot stamp it onto its own contributions.
/// </summary>
[JsonConverter(typeof(PackageIdJsonConverter))]
public readonly record struct PackageId(string Value)
{
    /// <summary>Used on an audit event for a call that never resolved to any package — for example, a tool name the model invented.</summary>
    public static PackageId Unknown { get; } = new("unknown");

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// Identifies who caused an action: the operator who launched a task, or the operator who
/// approved a step. <paramref name="Kind"/> is <c>"os-user"</c> in Phase 1 and becomes
/// <c>"api-user"</c> or <c>"service"</c> once bOps.Api exists in Phase 2.
/// </summary>
public sealed record ActorIdentity(string Kind, string Id, string? DisplayName)
{
    /// <summary>The actor identity for automatic, unattended runtime decisions (never for a human approval).</summary>
    public static ActorIdentity RuntimeSystem { get; } = new("system", "bops-runtime", "bOps runtime");

    /// <summary>Builds the actor identity for the operating-system user running the current process.</summary>
    public static ActorIdentity FromOperatingSystemUser(string userName) => new("os-user", userName, null);
}

/// <summary>Converts <see cref="NodeId"/> to and from a bare JSON string. Public so source-generated <see cref="JsonSerializerContext"/> types in other assemblies can reference it.</summary>
public sealed class NodeIdJsonConverter : JsonConverter<NodeId>
{
    /// <inheritdoc />
    public override NodeId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? string.Empty);

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, NodeId value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.Value);
    }
}

/// <summary>Converts <see cref="PackageId"/> to and from a bare JSON string. Public so source-generated <see cref="JsonSerializerContext"/> types in other assemblies can reference it.</summary>
public sealed class PackageIdJsonConverter : JsonConverter<PackageId>
{
    /// <inheritdoc />
    public override PackageId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? string.Empty);

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, PackageId value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.Value);
    }
}
