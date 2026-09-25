// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using bOps.Abstractions;

namespace bOps.Packages.Providers.Wire;

/// <summary>
/// The closed, per-request mapping between canonical bOps tool names (lowercase, dotted) and the names a strict
/// provider accepts on the wire, <c>^[A-Za-z0-9_-]{1,64}$</c> (ADR-0038). Owned by the OpenAI-compatible package and
/// linked, as this one file, into the Anthropic package, so every adapter uses exactly one implementation.
/// Canonical names are never changed anywhere else; an alias exists only inside one adapter for one request.
/// </summary>
/// <remarks>
/// The map is a pure function of the set of canonical names: no randomness, no hash codes, no process state.
/// A name that is already wire-safe is its own alias. Any other name gets a readable alias (every character outside
/// the safe set becomes <c>_</c>); when that would collide with another name in the set it gets the reversible
/// injective alias instead (every character outside <c>[A-Za-z0-9-]</c> becomes <c>_</c> plus the hex of its UTF-8
/// bytes). The result is verified; anything still ambiguous or out of bounds throws before a request is sent.
/// </remarks>
internal sealed class ToolWireNames
{
    /// <summary>The longest function name providers accept.</summary>
    public const int MaxLength = 64;

    private readonly Dictionary<string, string> _toWire;
    private readonly Dictionary<string, string> _toCanonical;

    private ToolWireNames(Dictionary<string, string> toWire, Dictionary<string, string> toCanonical)
    {
        _toWire = toWire;
        _toCanonical = toCanonical;
    }

    /// <summary>Builds the map for one request over every canonical name it will mention.</summary>
    /// <exception cref="ModelProtocolException">A name is empty, not valid text, too long once aliased, or would share an alias with another.</exception>
    public static ToolWireNames Create(IEnumerable<string> canonicalNames)
    {
        ArgumentNullException.ThrowIfNull(canonicalNames);

        var names = canonicalNames.Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToList();
        var readable = new Dictionary<string, string>(StringComparer.Ordinal);
        var candidateCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var name in names)
        {
            var candidate = ReadableAlias(name);
            readable[name] = candidate;
            candidateCounts[candidate] = candidateCounts.GetValueOrDefault(candidate) + 1;
        }

        var toWire = new Dictionary<string, string>(StringComparer.Ordinal);
        var toCanonical = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var name in names)
        {
            var alias = IsWireSafe(name) || candidateCounts[readable[name]] == 1
                ? readable[name]
                : InjectiveAlias(name);

            if (!IsWireSafe(alias))
            {
                throw new ModelProtocolException(
                    $"Tool name '{name}' cannot be sent to a provider: its wire name must be 1 to {MaxLength} characters of A-Z, a-z, 0-9, '_' or '-'.");
            }

            if (!toCanonical.TryAdd(alias, name))
            {
                throw new ModelProtocolException(
                    $"Tool names '{toCanonical[alias]}' and '{name}' would share the wire name '{alias}', so neither is sent.");
            }

            toWire[name] = alias;
        }

        return new ToolWireNames(toWire, toCanonical);
    }

    /// <summary>
    /// Builds the map for a model request: the tool names mentioned by the history's assistant turns, plus, when the
    /// request sends tool definitions, the offered tools. A name that cannot be mapped fails the request before
    /// anything is sent.
    /// </summary>
    /// <exception cref="ModelProtocolException">The names cannot be mapped to unambiguous provider-valid names.</exception>
    public static ToolWireNames ForRequest(ModelRequest request, bool includeOfferedTools)
    {
        var names = request.History
            .Where(turn => turn.ToolCalls is not null)
            .SelectMany(turn => turn.ToolCalls!)
            .Select(call => call.ToolName);
        if (includeOfferedTools)
        {
            names = names.Concat(request.AvailableTools.Select(manifest => manifest.Name));
        }

        return Create(names);
    }

    /// <summary>The bounded, fixed-wording reason recorded when a provider names a tool that was not offered in the request.</summary>
    public static string DescribeUnknown(string providerName)
    {
        const int limit = 80;
        var shown = providerName;
        if (shown.Length > limit)
        {
            var keep = char.IsHighSurrogate(shown[limit - 1]) ? limit - 1 : limit;
            shown = $"{shown[..keep]}…";
        }

        return $"'{shown}' is not a tool that was offered in this request.";
    }

    /// <summary>Whether <paramref name="name"/> is already a valid provider function name.</summary>
    public static bool IsWireSafe(string name)
    {
        if (name.Length is 0 or > MaxLength)
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!IsSafeChar(c, allowUnderscore: true))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The wire name of a canonical name that was given to <see cref="Create"/>.</summary>
    public string ToWire(string canonicalName) =>
        _toWire.TryGetValue(canonicalName, out var alias)
            ? alias
            : throw new InvalidOperationException($"Tool name '{canonicalName}' was not part of this request's wire name map.");

    /// <summary>Looks a provider-returned name up exactly. A name that is not an alias in this request is never resolved, guessed or repaired.</summary>
    public bool TryGetCanonical(string wireName, out string canonicalName)
    {
        if (_toCanonical.TryGetValue(wireName, out var found))
        {
            canonicalName = found;
            return true;
        }

        canonicalName = string.Empty;
        return false;
    }

    private static bool IsSafeChar(char c, bool allowUnderscore) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' || (allowUnderscore && c == '_');

    private static string ReadableAlias(string name)
    {
        if (name.Length == 0)
        {
            throw new ModelProtocolException("A tool with an empty name cannot be sent to a provider.");
        }

        var builder = new StringBuilder(name.Length);
        foreach (var rune in RunesOf(name))
        {
            builder.Append(rune.IsAscii && IsSafeChar((char)rune.Value, allowUnderscore: true) ? (char)rune.Value : '_');
        }

        return builder.ToString();
    }

    private static string InjectiveAlias(string name)
    {
        var builder = new StringBuilder(name.Length * 2);
        Span<byte> bytes = stackalloc byte[4];
        foreach (var rune in RunesOf(name))
        {
            if (rune.IsAscii && IsSafeChar((char)rune.Value, allowUnderscore: false))
            {
                builder.Append((char)rune.Value);
                continue;
            }

            var length = rune.EncodeToUtf8(bytes);
            for (var i = 0; i < length; i++)
            {
                builder.Append('_').Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    private static IEnumerable<Rune> RunesOf(string name)
    {
        for (var i = 0; i < name.Length;)
        {
            if (!Rune.TryGetRuneAt(name, i, out var rune))
            {
                throw new ModelProtocolException("A tool name that is not valid text cannot be sent to a provider.");
            }

            yield return rune;
            i += rune.Utf16SequenceLength;
        }
    }
}

