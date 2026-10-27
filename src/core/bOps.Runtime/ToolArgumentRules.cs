// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>
/// The one evaluator of a tool's declared <see cref="ToolManifest.ArgumentRules"/>: relations between parameters that the
/// per-parameter schema cannot express. It is generic: it reads parameter names, values and the declared message, and names no
/// tool. The tool stays the authority at execution; this is what lets a plan be refused before a step that no call could satisfy.
/// </summary>
internal static class ToolArgumentRules
{
    internal const int MaximumMessageCharacters = 240;

    /// <summary>
    /// The rules that can apply to <paramref name="manifest"/>: both parameters are declared and neither is sensitive. A manifest
    /// derived with fewer parameters keeps the rules of the original; one that names a parameter the manifest no longer has can
    /// never be broken, so it is ignored (and never shown to a planner).
    /// </summary>
    internal static IEnumerable<ToolArgumentRule> Applicable(ToolManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.ArgumentRules is not { Count: > 0 } rules)
        {
            yield break;
        }

        var visible = manifest.Parameters.Where(parameter => !parameter.Sensitive).Select(parameter => parameter.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            if (rule is not null && visible.Contains(rule.Parameter) && visible.Contains(rule.Other))
            {
                yield return rule;
            }
        }
    }

    /// <summary>
    /// The first rule that the <paramref name="supplied"/> values already break, or <c>null</c>. The values are a plan's expected
    /// arguments, a subset of what a call will carry, so only a violation that no completion of the subset can repair is reported:
    /// both parameters of an exclusive pair, or a forbidden parameter beside an explicit value that forbids it. A value the call
    /// might still add, or a tool default, is never assumed.
    /// </summary>
    internal static string? Violation(ToolManifest manifest, ToolArguments supplied)
    {
        ArgumentNullException.ThrowIfNull(supplied);
        var values = supplied.ToJson();
        foreach (var rule in Applicable(manifest))
        {
            if (!values.TryGetPropertyValue(rule.Parameter, out var parameterValue) || parameterValue is null
                || !values.TryGetPropertyValue(rule.Other, out var otherValue) || otherValue is null)
            {
                continue;
            }

            switch (rule.Kind)
            {
                case ToolArgumentRuleKind.MutuallyExclusive:
                    return rule.Message;
                case ToolArgumentRuleKind.ForbiddenWithValue
                    when otherValue is JsonValue text
                        && text.TryGetValue<string>(out var value)
                        && rule.OtherValues.Contains(value, StringComparer.Ordinal):
                    return rule.Message;
            }
        }

        return null;
    }

    /// <summary>The applicable rules' messages as one planner-facing sentence list, or <c>null</c> when none applies.</summary>
    internal static string? Describe(ToolManifest manifest)
    {
        var messages = Applicable(manifest).Select(rule => rule.Message.Trim()).ToArray();
        return messages.Length == 0 ? null : string.Join(' ', messages);
    }

    /// <summary>Why the declared rules are unusable (registration check), or <c>null</c> when every rule is well formed and bounded.</summary>
    internal static string? DescribeInvalid(ToolManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.ArgumentRules is not { } rules)
        {
            return null;
        }

        var declared = manifest.Parameters.ToDictionary(parameter => parameter.Name, StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            if (rule is null)
            {
                return "it declares a null argument rule.";
            }

            if (!Enum.IsDefined(rule.Kind))
            {
                return "it declares an argument rule of an undefined kind.";
            }

            if (string.IsNullOrWhiteSpace(rule.Message) || rule.Message.Length > MaximumMessageCharacters)
            {
                return $"an argument rule needs a message of 1 to {MaximumMessageCharacters} characters.";
            }

            if (string.IsNullOrWhiteSpace(rule.Parameter) || string.IsNullOrWhiteSpace(rule.Other))
            {
                return "an argument rule needs two parameter names.";
            }

            if (string.Equals(rule.Parameter, rule.Other, StringComparison.Ordinal))
            {
                return $"an argument rule relates parameter '{Bounded(rule.Parameter)}' to itself.";
            }

            if (rule.Kind == ToolArgumentRuleKind.ForbiddenWithValue
                && (rule.OtherValues is not { Count: > 0 }
                    || rule.OtherValues.Any(string.IsNullOrEmpty)
                    || (declared.TryGetValue(rule.Other, out var other) && other.Type is not (ToolParameterType.Enum or ToolParameterType.String))))
            {
                return $"a forbidden-with-value rule on '{Bounded(rule.Parameter)}' needs non-empty values of a String or Enum parameter '{Bounded(rule.Other)}'.";
            }
        }

        return null;
    }

    private static string Bounded(string name) => name.Length <= 128 ? name : name[..128];
}
