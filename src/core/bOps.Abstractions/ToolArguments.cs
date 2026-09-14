using System.Text.Json;
using System.Text.Json.Nodes;

namespace bOps.Abstractions;

/// <summary>
/// The arguments passed to a tool call. Backed by a JSON object so that a call round-trips
/// through serialization, the audit log, and — once it exists — a remote transport, none of
/// which a <c>IReadOnlyDictionary&lt;string, object?&gt;</c> can do reliably: <c>object?</c>
/// comes back as a <see cref="JsonElement"/> after a round trip, which silently breaks replay
/// and audit. See agentic/07-plan-corrections.md.
/// </summary>
public sealed class ToolArguments
{
    private readonly JsonObject _values;

    /// <summary>Creates an empty argument set.</summary>
    public ToolArguments() => _values = new JsonObject();

    /// <summary>Wraps an existing JSON object. The object is not cloned; do not mutate it afterwards.</summary>
    public ToolArguments(JsonObject values) => _values = values;

    /// <summary>The empty argument set, for tools that take none.</summary>
    public static ToolArguments Empty { get; } = new();

    /// <summary>Builds a <see cref="ToolArguments"/> from a JSON object, typically parsed from a model's tool call.</summary>
    public static ToolArguments FromJson(JsonObject json) => new(json);

    /// <summary>True if an argument with this name was supplied, regardless of its value.</summary>
    public bool ContainsKey(string name) => _values.ContainsKey(name);

    /// <summary>
    /// Attempts to read and convert an argument. Returns <c>false</c> — never throws — when the
    /// argument is absent or cannot convert to <typeparamref name="T"/>. This is the routine way
    /// a tool reads its own arguments (agentic/03-security-rules.md, rule S2): arguments already
    /// passed manifest validation by the time a tool runs, but an unchecked cast on model-produced
    /// input is exactly the line that survives a refactor and fails in production.
    /// </summary>
    public bool TryGet<T>(string name, out T? value)
    {
        if (_values.TryGetPropertyValue(name, out var node) && node is not null)
        {
            try
            {
                value = node.Deserialize<T>();
                return value is not null;
            }
            catch (JsonException)
            {
                value = default;
                return false;
            }
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Reads a required argument. Throws <see cref="ToolArgumentException"/> if it is missing or
    /// malformed — which signals a validation bug upstream, not a condition the tool should
    /// handle itself (agentic/02-coding-standards.md — exceptions are for broken invariants).
    /// </summary>
    public T GetRequired<T>(string name)
    {
        if (TryGet<T>(name, out var value) && value is not null)
        {
            return value;
        }

        throw new ToolArgumentException(name, $"Required argument '{name}' of type {typeof(T).Name} is missing or invalid.");
    }

    /// <summary>Returns a deep-cloned copy of the underlying JSON object, safe for a caller to keep or mutate.</summary>
    public JsonObject ToJson() => (JsonObject)_values.DeepClone();

    /// <summary>
    /// Returns a JSON object with the named properties replaced by a redaction marker, for
    /// writing to the audit log. Redaction happens at this boundary — never inside a sink — so
    /// that a new sink cannot leak what an existing one already masked
    /// (agentic/03-security-rules.md, rule S6).
    /// </summary>
    public JsonObject Redact(IEnumerable<string> parameterNames)
    {
        var clone = ToJson();
        foreach (var name in parameterNames)
        {
            if (clone.ContainsKey(name))
            {
                clone[name] = "***redacted***";
            }
        }

        return clone;
    }
}

/// <summary>
/// Thrown when a required tool argument is missing or fails to convert. This is a broken
/// invariant — argument validation against the <see cref="ToolManifest"/> should have already
/// rejected the call before a tool ever ran — not a condition a tool is expected to recover from.
/// </summary>
public sealed class ToolArgumentException(string argumentName, string message) : Exception(message)
{
    /// <summary>The name of the argument that was missing or malformed.</summary>
    public string ArgumentName { get; } = argumentName;
}
