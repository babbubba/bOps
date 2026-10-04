// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Runtime;

internal enum RuntimeDirectiveRecognitionKind
{
    None,
    Valid,
    Malformed,
}

internal sealed record EvidenceReadDirective(string EvidenceId, string Source, int Offset, int Length);

internal sealed record RuntimeDirectiveRecognition(RuntimeDirectiveRecognitionKind Kind, EvidenceReadDirective? Directive)
{
    internal static RuntimeDirectiveRecognition None { get; } = new(RuntimeDirectiveRecognitionKind.None, null);
    internal static RuntimeDirectiveRecognition Malformed { get; } = new(RuntimeDirectiveRecognitionKind.Malformed, null);
}

internal sealed record EvidenceReadResult(EvidenceReadResultCode Code, string? Fragment, int ReturnedLength);

/// <summary>Exact recognition, current-task resolution and UTF-16 range semantics for <c>EvidenceRead/v1</c>.</summary>
internal static class EvidenceRead
{
    internal const string Discriminator = "EvidenceRead/v1";
    internal const int ChunkCharacters = 4000;
    internal const int MaxAttempts = 4;
    private static readonly JsonDocumentOptions StrictJson = new() { AllowDuplicateProperties = false };

    internal static RuntimeDirectiveRecognition Recognize(ModelResponse response)
    {
        var text = TrimJsonWhitespace(response.TextResponse ?? string.Empty);
        var hasToolCalls = response.ToolCalls.Count > 0;
        JsonObject? root = null;
        try
        {
            root = JsonNode.Parse(text, nodeOptions: null, documentOptions: StrictJson) as JsonObject;
        }
        catch (JsonException)
        {
            // Claimed malformed directives are classified below without fuzzy parsing.
        }

        var claim = root?.ContainsKey("runtime") == true || StartsRuntimeObject(text);
        if (!claim)
        {
            return RuntimeDirectiveRecognition.None;
        }

        if (hasToolCalls || root is null || root.Count != 5
            || !root.TryGetPropertyValue("runtime", out var runtimeNode)
            || !TryString(runtimeNode, out var runtime) || runtime != Discriminator
            || !root.TryGetPropertyValue("evidenceId", out var idNode) || !TryString(idNode, out var evidenceId)
            || !root.TryGetPropertyValue("source", out var sourceNode) || !TryString(sourceNode, out var source)
            || !root.TryGetPropertyValue("offset", out var offsetNode) || !TryInt(offsetNode, out var offset)
            || !root.TryGetPropertyValue("length", out var lengthNode) || !TryInt(lengthNode, out var length)
            || root.Any(property => property.Key is not ("runtime" or "evidenceId" or "source" or "offset" or "length")))
        {
            return RuntimeDirectiveRecognition.Malformed;
        }

        return new RuntimeDirectiveRecognition(RuntimeDirectiveRecognitionKind.Valid,
            new EvidenceReadDirective(evidenceId, source, offset, length));
    }

    internal static EvidenceReadResult Read(Guid currentTaskId, IReadOnlyList<PlanStep> steps, EvidenceReadDirective directive)
    {
        var idParts = directive.EvidenceId.Split(':');
        if (idParts.Length != 3 || idParts[0] != "ev1"
            || idParts[1].Length != 32 || idParts[1].Any(character => !char.IsAsciiHexDigit(character) || char.IsAsciiLetterUpper(character))
            || !Guid.TryParseExact(idParts[1], "N", out var claimedTask)
            || !int.TryParse(idParts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            || index < 0 || idParts[2] != index.ToString(CultureInfo.InvariantCulture))
        {
            return new EvidenceReadResult(EvidenceReadResultCode.InvalidId, null, 0);
        }

        if (claimedTask != currentTaskId)
        {
            return new EvidenceReadResult(EvidenceReadResultCode.CrossTaskRejected, null, 0);
        }

        var matches = steps.Where(step => step.Index == index).ToList();
        if (matches.Count > 1)
        {
            return new EvidenceReadResult(EvidenceReadResultCode.InvalidId, null, 0);
        }

        if (matches.Count == 0 || matches[0].ToolCall is null || matches[0].Result is null)
        {
            return new EvidenceReadResult(EvidenceReadResultCode.MissingStep, null, 0);
        }

        if (directive.Source is not ("result" or "observation"))
        {
            return new EvidenceReadResult(EvidenceReadResultCode.UnavailableSource, null, 0);
        }

        var step = matches[0];
        var source = directive.Source == "result" ? step.Result?.Output : step.Observation;
        if (source is null)
        {
            return new EvidenceReadResult(EvidenceReadResultCode.UnavailableSource, null, 0);
        }

        if (directive.Offset < 0 || directive.Length is <= 0 or > ChunkCharacters || directive.Offset > source.Length
            || SplitsSurrogate(source, directive.Offset))
        {
            return new EvidenceReadResult(EvidenceReadResultCode.OutOfRange, null, 0);
        }

        if (directive.Offset == source.Length)
        {
            return new EvidenceReadResult(EvidenceReadResultCode.EndOfEvidence, string.Empty, 0);
        }

        var returned = Math.Min(directive.Length, source.Length - directive.Offset);
        var end = directive.Offset + returned;
        if (SplitsSurrogate(source, end))
        {
            return new EvidenceReadResult(EvidenceReadResultCode.OutOfRange, null, 0);
        }

        return new EvidenceReadResult(EvidenceReadResultCode.Success, source.Substring(directive.Offset, returned), returned);
    }

    internal static string Reply(EvidenceReadDirective directive, EvidenceReadResult result) => result.Code switch
    {
        EvidenceReadResultCode.Success =>
            $"EvidenceRead/v1 Success; offset={directive.Offset}; returned={result.ReturnedLength}.\n{AgentRunner.WrapToolOutput(result.Fragment!)}",
        EvidenceReadResultCode.EndOfEvidence => "EvidenceRead/v1 EndOfEvidence; returned=0.",
        _ => $"EvidenceRead/v1 rejected: {result.Code}.",
    };

    private static bool SplitsSurrogate(string value, int boundary) =>
        boundary > 0 && boundary < value.Length && char.IsHighSurrogate(value[boundary - 1]) && char.IsLowSurrogate(value[boundary]);

    private static bool TryString(JsonNode? node, out string value)
    {
        try
        {
            value = node?.GetValue<string>() ?? string.Empty;
            return node is not null;
        }
        catch (InvalidOperationException)
        {
            value = string.Empty;
            return false;
        }
    }

    private static bool TryInt(JsonNode? node, out int value)
    {
        try
        {
            value = node?.GetValue<int>() ?? 0;
            return node is not null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException)
        {
            value = 0;
            return false;
        }
    }

    private static string TrimJsonWhitespace(string value) => value.Trim(' ', '\t', '\r', '\n');

    private static bool StartsRuntimeObject(string text)
    {
        const string key = "\"runtime\"";
        var objectStart = text.IndexOf('{');
        while (objectStart >= 0)
        {
            var position = objectStart + 1;
            while (position < text.Length && text[position] is ' ' or '\t' or '\r' or '\n')
            {
                position++;
            }

            if (text.AsSpan(position).StartsWith(key, StringComparison.Ordinal))
            {
                position += key.Length;
                while (position < text.Length && text[position] is ' ' or '\t' or '\r' or '\n')
                {
                    position++;
                }

                return position == text.Length || text[position] == ':';
            }

            objectStart = text.IndexOf('{', objectStart + 1);
        }

        return false;
    }
}
