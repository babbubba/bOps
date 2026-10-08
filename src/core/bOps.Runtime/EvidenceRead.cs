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

internal enum ControlCallKind
{
    /// <summary>The response carries no <c>runtime.</c> control call.</summary>
    None,

    /// <summary>A supported control call with valid typed arguments.</summary>
    Valid,

    /// <summary>The supported control function, with arguments that are not exactly the closed typed request.</summary>
    Malformed,

    /// <summary>A <c>runtime.</c> name the runtime does not implement: a control protocol error, never a package tool.</summary>
    Unsupported,

    /// <summary>A <c>runtime.</c> call to a function this model turn was not offered (PRE-3B1): a control protocol error, nothing is read.</summary>
    NotOffered,
}

/// <summary>The first <c>runtime.</c> control call of a model response (PRE-3A); its directive is the one legacy text produces.</summary>
internal sealed record ControlCallRecognition(ControlCallKind Kind, ModelToolCall? Call, EvidenceReadDirective? Directive)
{
    internal static ControlCallRecognition None { get; } = new(ControlCallKind.None, null, null);
}

internal sealed record EvidenceReadResult(EvidenceReadResultCode Code, string? Fragment, int ReturnedLength);

/// <summary>Exact recognition, current-task resolution and UTF-16 range semantics for <c>EvidenceRead/v1</c>.</summary>
internal static class EvidenceRead
{
    internal const string Discriminator = "EvidenceRead/v1";
    internal const int ChunkCharacters = 4000;
    internal const int MaxAttempts = 4;

    /// <summary>
    /// The runtime-internal control function (PRE-3A, ADR-0046 amendment). It is not a package tool: never in the
    /// <see cref="ToolRegistry"/>, never authorized, approved or entitled; the registry reserves the whole
    /// <see cref="ControlNamespace"/> so no package can claim it.
    /// </summary>
    internal const string ControlFunctionName = "runtime.evidence_read";
    internal const string ControlNamespace = "runtime.";

    /// <summary>The offered function declaration. The task is never an argument: it always comes from the runtime context.</summary>
    internal static ToolManifest ControlManifest { get; } = new()
    {
        Name = ControlFunctionName,
        Description = "Runtime control function, not an operational tool: reads up to 4000 UTF-16 code units of already persisted "
            + "evidence of the current task, by step index. It runs nothing and does not complete the planned step; "
            + "a tool call in the same reply is not executed.",
        Risk = RiskLevel.Read,
        Platforms = [CurrentPlatform.Id],
        Requires = [],
        Parameters =
        [
            new ToolParameter("step", ToolParameterType.Integer, "Index of the earlier step whose evidence to read.") { Minimum = 0 },
            new ToolParameter("source", ToolParameterType.Enum, "Which persisted text to read.",
                AllowedValues: ["result", "observation"]),
            new ToolParameter("offset", ToolParameterType.Integer, "UTF-16 start offset.") { Minimum = 0 },
            new ToolParameter("length", ToolParameterType.Integer, "UTF-16 code units to return.") { Minimum = 1, Maximum = ChunkCharacters },
        ],
    };

    /// <summary>Recognizes the first <c>runtime.</c> native call, if any, as a typed request for the current task.</summary>
    internal static ControlCallRecognition RecognizeControl(Guid currentTaskId, ModelResponse response)
    {
        var call = response.ToolCalls.FirstOrDefault(candidate =>
            candidate.ToolName.StartsWith(ControlNamespace, StringComparison.Ordinal));
        if (call is null)
        {
            return ControlCallRecognition.None;
        }

        if (!string.Equals(call.ToolName, ControlFunctionName, StringComparison.Ordinal) || call.ToolNameError is not null)
        {
            return new ControlCallRecognition(ControlCallKind.Unsupported, call, null);
        }

        var json = call.Arguments.ToJson();
        if (call.ArgumentsError is not null || json.Count != 4
            || json.Any(property => property.Key is not ("step" or "source" or "offset" or "length"))
            || !TryInt(json["step"], out var step) || step < 0
            || !TryString(json["source"], out var source)
            || !TryInt(json["offset"], out var offset)
            || !TryInt(json["length"], out var length))
        {
            return new ControlCallRecognition(ControlCallKind.Malformed, call, null);
        }

        return new ControlCallRecognition(ControlCallKind.Valid, call,
            new EvidenceReadDirective(BoundedHistory.EvidenceId(currentTaskId, step), source, offset, length));
    }
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

/// <summary>
/// PRE-3B1: whether a candidate final response is itself a tool/control invocation envelope rather than prose for the user.
/// Deliberately closed and shallow: it matches the whole-response shape of the formats the runtime knows (Qwen-style
/// tool_call / function= markup and a serialized {name, arguments} call object). It is not an XML parser, and prose that
/// merely mentions a function name is never matched.
/// </summary>
internal static class TerminalProtocolArtifact
{
    private static readonly string[] EnvelopeStarts = ["<tool_call>", "<function="];

    internal static bool IsArtifact(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return false;
        }

        foreach (var start in EnvelopeStarts)
        {
            if (trimmed.StartsWith(start, StringComparison.Ordinal))
            {
                return true;
            }
        }

        if (trimmed[0] != '{')
        {
            return false;
        }

        try
        {
            if (JsonNode.Parse(trimmed) is JsonObject root)
            {
                var name = root["name"] ?? root["function"];
                var hasName = name is JsonValue value && value.TryGetValue<string>(out _);
                return hasName && (root.ContainsKey("arguments") || root.ContainsKey("parameters"));
            }
        }
        catch (JsonException)
        {
            // Not a JSON object: ordinary text that happens to start with a brace.
        }

        return false;
    }
}
