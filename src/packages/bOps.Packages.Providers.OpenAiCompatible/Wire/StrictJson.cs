// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;

namespace bOps.Packages.Providers.Wire;

/// <summary>
/// The strict JSON parsing every provider adapter uses to read a model's own tool-call payload (ADR-0038, HARDEN-1
/// review finding M-2). Owned by the OpenAI-compatible package and linked, as this one file, into the Anthropic
/// package, exactly like <see cref="ToolWireNames"/>.
/// </summary>
/// <remarks>
/// <see cref="JsonNode.Parse(string, JsonNodeOptions?, JsonDocumentOptions)"/> with the default
/// <see cref="JsonDocumentOptions"/> accepts a JSON object with a repeated property name — <c>{"a":1,"a":2}</c> is
/// syntactically valid UTF-8 — and defers the ambiguity: the parse itself succeeds, and only a later enumeration or
/// property access on the resulting <see cref="JsonObject"/> throws <see cref="ArgumentException"/>, which is not a
/// validation failure any caller here is prepared to catch, so it can escape as an unhandled exception instead of
/// becoming the ordinary, bounded "malformed arguments" outcome ADR-0038 already defines. Setting
/// <see cref="JsonDocumentOptions.AllowDuplicateProperties"/> to <c>false</c> turns the same input into an ordinary,
/// catchable <see cref="JsonException"/> at parse time instead, at every level of nesting — so a duplicate key deep
/// inside a tool's arguments is caught exactly where a syntax error already is.
/// </remarks>
internal static class StrictJson
{
    private static readonly JsonDocumentOptions DocumentOptions = new() { AllowDuplicateProperties = false };

    /// <summary>
    /// Parses <paramref name="json"/> like <see cref="JsonNode.Parse(string, JsonNodeOptions?, JsonDocumentOptions)"/>,
    /// except a JSON object with a repeated property name anywhere in the document throws <see cref="JsonException"/>
    /// at parse time instead of only on a later access.
    /// </summary>
    public static JsonNode? Parse(string json) => JsonNode.Parse(json, nodeOptions: null, documentOptions: DocumentOptions);
}
