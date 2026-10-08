// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace bOps.Abstractions;

/// <summary>
/// Bounded, JSON-native diagnostic metadata attached to a prerequisite result or a system message (ADR-0049 section 6).
/// Values are JSON strings, numbers, booleans, null, or arrays of strings; nested objects are refused. Metadata is shown to
/// administrators and persisted, so it must <b>never</b> carry a secret: keys that name a secret and string values that
/// embed URL credentials or a bearer token are refused at construction. That check is defence in depth, not permission to
/// pass sensitive data and rely on it.
/// </summary>
[JsonConverter(typeof(OperationalMetadataJsonConverter))]
public sealed class OperationalMetadata : IEquatable<OperationalMetadata>
{
    /// <summary>The maximum number of entries.</summary>
    public const int MaxEntries = 32;

    /// <summary>The maximum length of a key.</summary>
    public const int MaxKeyLength = 64;

    /// <summary>The maximum length of one string value, including each string inside an array.</summary>
    public const int MaxStringLength = 512;

    /// <summary>The maximum number of items in an array value.</summary>
    public const int MaxArrayItems = 32;

    /// <summary>The maximum size of the compact JSON serialization, in UTF-8 bytes.</summary>
    public const int MaxSerializedBytes = 4096;

    private static readonly string[] SecretKeyFragments =
    [
        "password", "passwd", "secret", "apikey", "accesskey", "privatekey", "credential", "connectionstring",
        "authorization", "cookie", "bearer",
    ];

    private readonly JsonObject _values;

    private OperationalMetadata(JsonObject values) => _values = values;

    /// <summary>Metadata with no entries.</summary>
    public static OperationalMetadata Empty { get; } = new(new JsonObject());

    /// <summary>The number of entries.</summary>
    public int Count => _values.Count;

    /// <summary>The entry keys, in insertion order.</summary>
    public IEnumerable<string> Keys => _values.Select(pair => pair.Key).ToArray();

    /// <summary>Validates and copies <paramref name="values"/>. The argument is not retained.</summary>
    /// <param name="values">The entries.</param>
    /// <exception cref="ArgumentException">The entries exceed a bound, use an unsupported value shape, or look like a secret.</exception>
    public static OperationalMetadata From(JsonObject values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return TryFrom(values, out var metadata, out var problem)
            ? metadata
            : throw new ArgumentException($"Invalid operational metadata: {problem}", nameof(values));
    }

    /// <summary>Validates and copies <paramref name="values"/> without throwing.</summary>
    /// <param name="values">The entries.</param>
    /// <param name="metadata">The validated copy, when valid.</param>
    /// <param name="problem">Why the entries were refused, when invalid. Never contains a value.</param>
    public static bool TryFrom(
        JsonObject? values,
        [NotNullWhen(true)] out OperationalMetadata? metadata,
        [NotNullWhen(false)] out string? problem)
    {
        metadata = null;
        if (values is null)
        {
            problem = "metadata is null.";
            return false;
        }

        problem = DescribeViolation(values);
        if (problem is not null)
        {
            return false;
        }

        metadata = values.Count == 0 ? Empty : new OperationalMetadata(values.DeepClone().AsObject());
        return true;
    }

    /// <summary>Returns whether an entry with this key exists.</summary>
    /// <param name="key">The entry key.</param>
    public bool ContainsKey(string key) => _values.ContainsKey(key);

    /// <summary>Reads a string entry.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="value">The string value, when present and a string.</param>
    public bool TryGetString(string key, [NotNullWhen(true)] out string? value)
    {
        value = null;
        if (_values.TryGetPropertyValue(key, out var node)
            && node is JsonValue scalar
            && scalar.GetValueKind() == JsonValueKind.String)
        {
            value = scalar.GetValue<string>();
            return true;
        }

        return false;
    }

    /// <summary>Returns a copy of the entries as a JSON object. Mutating it does not change this instance.</summary>
    public JsonObject ToJson() => _values.DeepClone().AsObject();

    /// <inheritdoc />
    public bool Equals(OperationalMetadata? other) =>
        other is not null && (ReferenceEquals(this, other) || JsonNode.DeepEquals(_values, other._values));

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as OperationalMetadata);

    /// <inheritdoc />
    public override int GetHashCode() => _values.Count;

    /// <summary>The compact JSON form of the entries.</summary>
    public override string ToString() => _values.ToJsonString();

    private static string? DescribeViolation(JsonObject values)
    {
        if (values.Count > MaxEntries)
        {
            return $"at most {MaxEntries} entries are allowed.";
        }

        foreach (var (key, node) in values)
        {
            if (!IsValidKey(key))
            {
                return $"key '{Truncate(key)}' must start with a letter, use only letters, digits, '.', '_' or '-', and be at most {MaxKeyLength} characters.";
            }

            if (LooksLikeSecretKey(key))
            {
                return $"key '{key}' names a secret; metadata must never carry secrets.";
            }

            if (DescribeValueViolation(node) is { } valueProblem)
            {
                return $"entry '{key}' {valueProblem}";
            }
        }

        return Encoding.UTF8.GetByteCount(values.ToJsonString()) > MaxSerializedBytes
            ? $"the serialized metadata exceeds {MaxSerializedBytes} bytes."
            : null;
    }

    private static string? DescribeValueViolation(JsonNode? node) => node switch
    {
        null => null,
        JsonArray array => DescribeArrayViolation(array),
        JsonObject => "is a nested object; only strings, numbers, booleans, null and arrays of strings are allowed.",
        JsonValue scalar => scalar.GetValueKind() switch
        {
            JsonValueKind.String => DescribeStringViolation(scalar.GetValue<string>()),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => null,
            _ => "has an unsupported value kind.",
        },
        _ => "has an unsupported value kind.",
    };

    private static string? DescribeArrayViolation(JsonArray array)
    {
        if (array.Count > MaxArrayItems)
        {
            return $"has more than {MaxArrayItems} items.";
        }

        foreach (var item in array)
        {
            if (item is not JsonValue scalar || scalar.GetValueKind() != JsonValueKind.String)
            {
                return "is an array whose items are not all strings.";
            }

            if (DescribeStringViolation(scalar.GetValue<string>()) is { } problem)
            {
                return problem;
            }
        }

        return null;
    }

    private static string? DescribeStringViolation(string value)
    {
        if (value.Length > MaxStringLength)
        {
            return $"is longer than {MaxStringLength} characters.";
        }

        if (value.TrimStart().StartsWith("bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return "looks like a bearer token; metadata must never carry secrets.";
        }

        return ContainsUrlUserInfo(value)
            ? "contains a URL with embedded credentials; metadata must never carry secrets."
            : null;
    }

    private static bool ContainsUrlUserInfo(string value)
    {
        var start = 0;
        while ((start = value.IndexOf("://", start, StringComparison.Ordinal)) >= 0)
        {
            start += 3;
            var end = value.IndexOfAny(['/', '?', '#', ' ', '\t', '\r', '\n'], start);
            var authority = end < 0 ? value[start..] : value[start..end];
            if (authority.Contains('@', StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsValidKey(string key)
    {
        if (key.Length is 0 or > MaxKeyLength || !char.IsAsciiLetter(key[0]))
        {
            return false;
        }

        foreach (var c in key)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool LooksLikeSecretKey(string key)
    {
        var normalized = new string(key.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        // "token" alone or as a suffix (accessToken, refreshToken) is a secret; a token *count* (promptTokens) is not.
        return normalized.EndsWith("token", StringComparison.Ordinal)
            || SecretKeyFragments.Any(fragment => normalized.Contains(fragment, StringComparison.Ordinal));
    }

    private static string Truncate(string key) => key.Length <= MaxKeyLength ? key : key[..MaxKeyLength] + "…";
}

/// <summary>
/// Converts <see cref="OperationalMetadata"/> to and from a plain JSON object, re-validating every bound on read. Public so
/// source-generated <see cref="JsonSerializerContext"/> types in other assemblies can reference it.
/// </summary>
public sealed class OperationalMetadataJsonConverter : JsonConverter<OperationalMetadata>
{
    /// <inheritdoc />
    public override OperationalMetadata Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var node = JsonNode.Parse(ref reader);
        if (node is null)
        {
            return OperationalMetadata.Empty;
        }

        if (node is not JsonObject json)
        {
            throw new JsonException("Operational metadata must be a JSON object.");
        }

        return OperationalMetadata.TryFrom(json, out var metadata, out var problem)
            ? metadata
            : throw new JsonException($"Invalid operational metadata: {problem}");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, OperationalMetadata value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        value.ToJson().WriteTo(writer);
    }
}
