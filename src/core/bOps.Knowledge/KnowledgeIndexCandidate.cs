// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace bOps.Knowledge;

/// <summary>
/// One skill the caller declares already admitted and authorized for the task, as the selector and the renderer see it
/// (ADR-0057 task T4). The caller builds it from frontmatter and pin it has already verified; nothing here grants access, and a
/// candidate the caller did not pass can never appear in a result.
/// </summary>
/// <param name="RefIndex">The host-generated 1-based position of the skill in the pin (<c>K{RefIndex}</c>).</param>
/// <param name="Skill">The skill name (U7).</param>
/// <param name="Description">The publisher's description, already admitted under <see cref="KnowledgeTextPolicy"/> (U1-U6).</param>
/// <param name="BodyChars">The size of the skill body, from the signed manifest.</param>
/// <param name="Resources">The skill's resources: 1-based position in the pin (<c>R{Index}</c>), path and size from the signed manifest.</param>
public sealed record KnowledgeIndexCandidate(
    int RefIndex,
    string Skill,
    string Description,
    int BodyChars,
    IReadOnlyList<(int Index, string Path, int Chars)> Resources)
{
    /// <summary>The highest ref number a <c>K</c> or <c>R</c> reference can carry (three digits, <c>^K[1-9][0-9]{0,2}</c>).</summary>
    internal const int MaxRefNumber = 999;

    /// <summary>The longest resource path, in characters (U8).</summary>
    internal const int MaxPathLength = 128;

    /// <summary>
    /// Refuses a candidate list that breaks the contract the selector and renderer rely on. The input is the caller's, so a violation
    /// is a defect of the caller and throws; the message never carries any text of the candidate.
    /// </summary>
    internal static void EnsureValid(IReadOnlyList<KnowledgeIndexCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        HashSet<int> refs = [];
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (candidate is null)
            {
                throw new ArgumentException("knowledge index candidate is null", nameof(candidates));
            }

            var label = string.Create(CultureInfo.InvariantCulture, $"knowledge index candidate #{candidate.RefIndex}");
            if (candidate.RefIndex is < 1 or > MaxRefNumber || !refs.Add(candidate.RefIndex))
            {
                throw new ArgumentException($"{label}: ref index out of range or repeated", nameof(candidates));
            }

            if (!KnowledgeTextPolicy.IsValidSkillName(candidate.Skill) || !names.Add(candidate.Skill))
            {
                throw new ArgumentException($"{label}: skill name is invalid or repeated", nameof(candidates));
            }

            if (!KnowledgeTextPolicy.IsValidDescription(
                    candidate.Description, KnowledgeTextPolicy.MaxDescriptionLength, KnowledgeTextPolicy.MaxDescriptionUtf8Bytes))
            {
                throw new ArgumentException($"{label}: description does not conform to the text policy", nameof(candidates));
            }

            EnsureValidResources(candidate, label);
        }
    }

    private static void EnsureValidResources(KnowledgeIndexCandidate candidate, string label)
    {
        if (candidate.BodyChars < 0 || candidate.Resources is null)
        {
            throw new ArgumentException($"{label}: body size or resources are invalid", nameof(candidate));
        }

        HashSet<int> indexes = [];
        foreach (var (index, path, chars) in candidate.Resources)
        {
            if (index is < 1 or > MaxRefNumber || !indexes.Add(index) || chars < 0 || !IsDisplayablePath(path))
            {
                throw new ArgumentException($"{label}: a resource is out of range, repeated or not a plain relative path", nameof(candidate));
            }
        }
    }

    /// <summary>
    /// The renderer's precondition on a path it displays: plain ASCII segments, no empty segment, no dot segment. This is a narrower
    /// restatement of admission (U8), kept here only so the renderer never depends on an unchecked string.
    /// </summary>
    private static bool IsDisplayablePath(string? path)
    {
        if (path is null || path.Length is 0 or > MaxPathLength || path[0] == '/' || path[^1] == '/')
        {
            return false;
        }

        var segmentLength = 0;
        foreach (var character in path)
        {
            if (character == '/')
            {
                if (segmentLength == 0)
                {
                    return false;
                }

                segmentLength = 0;
                continue;
            }

            if (!(character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-'))
            {
                return false;
            }

            segmentLength++;
        }

        return !path.Contains("..", StringComparison.Ordinal);
    }
}
