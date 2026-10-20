// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using bOps.Abstractions;

namespace bOps.Knowledge;

/// <summary>
/// The shape every payload path of a pack must have: <c>skill-name/…/file</c>, plain ASCII segments, a closed list of text
/// extensions, nothing that names a script, a device or a second skill entry. The same rule is applied to the manifest and to what a
/// source actually returned, so a path is judged by one definition on both sides.
/// </summary>
internal static partial class PackPathPolicy
{
    /// <summary>A skill directory, a resource folder and a file below the pack root: at most four segments.</summary>
    private const int MaxSegments = 4;

    internal static KnowledgeAdmissionCode? Check(string path, KnowledgePackLimits limits)
    {
        if (path.Length == 0)
        {
            return KnowledgeAdmissionCode.UnsafePath;
        }

        if (path.Length > limits.MaxPathLength)
        {
            return KnowledgeAdmissionCode.LimitExceeded;
        }

        var segments = path.Split('/');
        if (segments.Length is < 2 or > MaxSegments)
        {
            return KnowledgeAdmissionCode.UnsafePath;
        }

        foreach (var segment in segments)
        {
            if (!SegmentShape().IsMatch(segment) || IsReservedDeviceName(segment))
            {
                return KnowledgeAdmissionCode.UnsafePath;
            }
        }

        if (!SkillDirectoryShape().IsMatch(segments[0]))
        {
            return KnowledgeAdmissionCode.UnsafePath;
        }

        if (segments.Any(segment => string.Equals(segment, "scripts", StringComparison.OrdinalIgnoreCase)))
        {
            return KnowledgeAdmissionCode.ForbiddenContent;
        }

        var file = segments[^1];
        var extension = Path.GetExtension(file);
        if (!KnowledgePackFormat.AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return KnowledgeAdmissionCode.ForbiddenContent;
        }

        // A skill entry document anywhere but directly below the skill directory would be discovered as a second, unpinned skill.
        if (string.Equals(file, KnowledgePackFormat.SkillFileName, StringComparison.OrdinalIgnoreCase)
            && (segments.Length != 2 || !string.Equals(file, KnowledgePackFormat.SkillFileName, StringComparison.Ordinal)))
        {
            return KnowledgeAdmissionCode.ForbiddenContent;
        }

        return null;
    }

    private static bool IsReservedDeviceName(string segment)
    {
        var dot = segment.IndexOf('.', StringComparison.Ordinal);
        var stem = dot < 0 ? segment : segment[..dot];
        return ReservedDevice().IsMatch(stem);
    }

    [GeneratedRegex("^[A-Za-z0-9]([A-Za-z0-9._-]*[A-Za-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex SegmentShape();

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,62}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex SkillDirectoryShape();

    [GeneratedRegex("^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ReservedDevice();
}
