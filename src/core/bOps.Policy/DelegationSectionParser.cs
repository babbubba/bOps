// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using bOps.Abstractions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace bOps.Policy;

/// <summary>
/// Reads the optional <c>delegation</c> section of <c>policy.yaml</c> into one <see cref="RoleProfile"/> per
/// configured role (ADR-0030 section 1, ADR-0031 sections 2 and 5). It is deliberately stricter than the rest of
/// the loader, which ignores keys it does not know: here an unknown key is an error, because a misspelt
/// <c>window</c> would otherwise load as no restriction at all, and a value that only parses by accident (a
/// <c>"low, medium"</c> that .NET reads as a flag set) is refused. Every problem is reported with the line and the
/// path of the key, and nothing is guessed: a key that is absent grants nothing where "nothing" exists (a list is
/// empty, steps and tokens are zero, no window is no restriction beyond the deadline) and is an error where it does
/// not (a ceiling always permits its lowest value and a duration must be positive).
/// </summary>
/// <remarks>
/// What a role may not be given (Skills or Capabilities for a role that runs no Capability, tokens for a role that
/// makes no model call) is not restated here. The profile constructor refuses it, and this parser reports that
/// refusal against the key that caused it, so the table of ADR-0031 has one definition. A whole section is refused
/// when any part of it is, never partially applied, and the caller turns that refusal into <c>AllForbidden</c> for
/// the whole policy exactly as it does for any other malformed file (rule S3).
/// </remarks>
internal static class DelegationSectionParser
{
    private const string SectionKey = "delegation";
    private const string RolesKey = "roles";
    private const string RolesPath = SectionKey + "." + RolesKey;

    private const string SkillsKey = "skills";
    private const string CapabilitiesKey = "capabilities";
    private const string ToolsKey = "tools";
    private const string MaxRiskKey = "maxRisk";
    private const string MaxBlastRadiusKey = "maxBlastRadius";
    private const string TargetsKey = "targets";
    private const string EnvironmentsKey = "environments";
    private const string MaxStepsKey = "maxSteps";
    private const string MaxTokensKey = "maxTokens";
    private const string MaxDurationKey = "maxDuration";
    private const string WindowKey = "window";
    private const string WindowStartKey = "start";
    private const string WindowEndKey = "end";

    private static readonly string[] SectionKeys = [RolesKey];

    private static readonly string[] ProfileKeys =
    [
        SkillsKey, CapabilitiesKey, ToolsKey, MaxRiskKey, MaxBlastRadiusKey, TargetsKey, EnvironmentsKey, MaxStepsKey, MaxTokensKey, MaxDurationKey, WindowKey,
    ];

    private static readonly string[] WindowKeys = [WindowStartKey, WindowEndKey];

    // An instant must say which zone it is in: without an offset the same text means a different moment on every
    // machine, and a maintenance window is a security boundary.
    private static readonly string[] TimestampFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
        "yyyy-MM-dd'T'HH:mm:sszzz",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
    ];

    // The profile constructor names the parameter it refused; the operator wrote a key.
    private static readonly Dictionary<string, string> KeyOfParameter = new(StringComparer.Ordinal)
    {
        [nameof(RoleProfile.AllowedSkills)] = SkillsKey,
        [nameof(RoleProfile.AllowedCapabilities)] = CapabilitiesKey,
        [nameof(RoleProfile.AllowedTools)] = ToolsKey,
        [nameof(RoleProfile.MaxRisk)] = MaxRiskKey,
        [nameof(RoleProfile.MaxBlastRadius)] = MaxBlastRadiusKey,
        [nameof(RoleProfile.AllowedTargets)] = TargetsKey,
        [nameof(RoleProfile.AllowedEnvironments)] = EnvironmentsKey,
        [nameof(RoleProfile.MaxSteps)] = MaxStepsKey,
        [nameof(RoleProfile.MaxTokens)] = MaxTokensKey,
        [nameof(RoleProfile.MaxDuration)] = MaxDurationKey,
    };

    /// <summary>Reads the <c>delegation</c> section of <paramref name="yaml"/>.</summary>
    /// <returns>One profile per configured role, or none when the section is absent.</returns>
    /// <exception cref="PolicyConfigurationException">The section is malformed in any way.</exception>
    internal static IReadOnlyList<RoleProfile> Parse(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        var stream = new YamlStream();
        try
        {
            using var reader = new StringReader(yaml);
            stream.Load(reader);
        }
        catch (YamlException ex)
        {
            throw new PolicyConfigurationException($"policy.yaml is not valid YAML: {ex.Message}", ex);
        }

        // Like the rest of the loader, only the first document is read.
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            return [];
        }

        if (Find(root, SectionKey) is not { } section)
        {
            return [];
        }

        var sectionMap = Mapping(section, SectionKey);
        RejectUnknownKeys(sectionMap, SectionKey, SectionKeys);
        if (Find(sectionMap, RolesKey) is not { } rolesNode)
        {
            return [];
        }

        var profiles = new List<RoleProfile>();
        var seen = new HashSet<AgentRoleKind>();
        foreach (var (nameNode, profileNode) in Mapping(rolesNode, RolesPath).Children)
        {
            var name = Text(nameNode, RolesPath);
            var role = Name<AgentRoleKind>(nameNode, name, RolesPath, "role");
            if (!seen.Add(role))
            {
                throw Fail(nameNode, RolesPath, $"the {role} role is configured more than once.");
            }

            profiles.Add(ParseProfile(role, profileNode, $"{RolesPath}.{name}"));
        }

        return profiles;
    }

    private static RoleProfile ParseProfile(AgentRoleKind role, YamlNode node, string path)
    {
        var profile = Mapping(node, path);
        RejectUnknownKeys(profile, path, ProfileKeys);

        var skills = List(profile, SkillsKey, path);
        var capabilities = List(profile, CapabilitiesKey, path);
        var tools = List(profile, ToolsKey, path);
        var maxRisk = Name<RiskLevel>(Require(profile, MaxRiskKey, path), Text(profile, MaxRiskKey, path), $"{path}.{MaxRiskKey}", "risk level");
        var maxBlastRadius = Name<BlastRadius>(Require(profile, MaxBlastRadiusKey, path), Text(profile, MaxBlastRadiusKey, path), $"{path}.{MaxBlastRadiusKey}", "blast radius");
        var targets = List(profile, TargetsKey, path);
        var environments = List(profile, EnvironmentsKey, path);
        var maxSteps = Count(profile, MaxStepsKey, path);
        var maxTokens = Count(profile, MaxTokensKey, path);
        var maxDuration = Duration(profile, path);
        var window = Window(profile, path);

        try
        {
            return new RoleProfile(
                role, skills, capabilities, tools, maxRisk, maxBlastRadius, targets, environments, maxSteps, maxTokens, maxDuration, window);
        }
        catch (ArgumentException ex)
        {
            // Includes ArgumentOutOfRangeException. The constructor is the one definition of what a profile may hold.
            var where = ex.ParamName is { } parameter && KeyOfParameter.TryGetValue(parameter, out var key) ? $"{path}.{key}" : path;
            throw Fail(node, where, FirstLine(ex.Message), ex);
        }
    }

    // ---- reading a key ----------------------------------------------------------------------------------------

    // Absent grants nothing; a value that is not a list of plain names is an error, and so is a blank one, which
    // could as easily be "none" as "forgot to fill in": `[]` says none.
    private static string[] List(YamlMappingNode profile, string key, string path)
    {
        if (Find(profile, key) is not { } node)
        {
            return [];
        }

        var where = $"{path}.{key}";
        if (node is not YamlSequenceNode sequence)
        {
            throw Fail(node, where, "must be a list of names; write [] to grant nothing.");
        }

        var members = new List<string>();
        foreach (var item in sequence.Children)
        {
            members.Add(Text(item, where));
        }

        return [.. members];
    }

    private static int Count(YamlMappingNode profile, string key, string path)
    {
        if (Find(profile, key) is not { } node)
        {
            return 0;
        }

        var where = $"{path}.{key}";
        var text = Text(node, where);
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            ? count
            : throw Fail(node, where, $"'{text}' is not a whole number of zero or more.");
    }

    private static TimeSpan Duration(YamlMappingNode profile, string path)
    {
        var where = $"{path}.{MaxDurationKey}";
        var node = Require(profile, MaxDurationKey, path);
        var text = Text(node, where);

        // The constant format reads a bare "10" as ten days. A duration is a security limit, and "10" is far more
        // likely to mean ten minutes than ten days, so the time of day must be written.
        return text.Contains(':', StringComparison.Ordinal) && TimeSpan.TryParseExact(text, "c", CultureInfo.InvariantCulture, out var duration)
            ? duration
            : throw Fail(node, where, $"'{text}' is not a duration; write hh:mm:ss, or d.hh:mm:ss for days (for example 00:10:00).");
    }

    private static MaintenanceWindow? Window(YamlMappingNode profile, string path)
    {
        if (Find(profile, WindowKey) is not { } node)
        {
            return null;
        }

        var where = $"{path}.{WindowKey}";
        var window = Mapping(node, where);
        RejectUnknownKeys(window, where, WindowKeys);
        var start = Instant(window, WindowStartKey, where);
        var end = Instant(window, WindowEndKey, where);
        try
        {
            return new MaintenanceWindow(start, end);
        }
        catch (ArgumentException ex)
        {
            throw Fail(node, where, FirstLine(ex.Message), ex);
        }
    }

    private static DateTimeOffset Instant(YamlMappingNode window, string key, string path)
    {
        var where = $"{path}.{key}";
        var node = Require(window, key, path);
        var text = Text(node, where);
        return DateTimeOffset.TryParseExact(
            text, TimestampFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant)
            ? instant
            : throw Fail(node, where, $"'{text}' is not an instant with a zone; write it as 2026-09-20T02:00:00Z or with an offset such as +02:00.");
    }

    private static T Name<T>(YamlNode node, string text, string path, string what)
        where T : struct, Enum
    {
        // Names only. Enum.TryParse would also read "3" and "low, medium", which is not what was meant.
        foreach (var name in Enum.GetNames<T>())
        {
            if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase))
            {
                return Enum.Parse<T>(name);
            }
        }

        throw Fail(node, path, $"'{text}' is not a {what}; expected one of: {string.Join(", ", Enum.GetNames<T>())}.");
    }

    // ---- reading the tree -------------------------------------------------------------------------------------

    private static YamlNode? Find(YamlMappingNode mapping, string key)
    {
        foreach (var (name, value) in mapping.Children)
        {
            if (name is YamlScalarNode { Value: { } text } && string.Equals(text, key, StringComparison.Ordinal))
            {
                return value;
            }
        }

        return null;
    }

    private static YamlNode Require(YamlMappingNode mapping, string key, string path) =>
        Find(mapping, key) ?? throw Fail(mapping, $"{path}.{key}", "is required; there is no default for it.");

    private static string Text(YamlMappingNode mapping, string key, string path) =>
        Text(Require(mapping, key, path), $"{path}.{key}");

    private static string Text(YamlNode node, string path)
    {
        // A blank value or a YAML null is not a name and not a number, and it is most likely a value left unfilled.
        if (node is not YamlScalarNode { Value: { } value } scalar
            || (scalar.Style == ScalarStyle.Plain && value is "" or "~" or "null" or "Null" or "NULL"))
        {
            throw Fail(node, path, "must be a single value, and it is empty or not a plain value.");
        }

        return value;
    }

    private static YamlMappingNode Mapping(YamlNode node, string path) =>
        node as YamlMappingNode ?? throw Fail(node, path, "must be a mapping.");

    private static void RejectUnknownKeys(YamlMappingNode mapping, string path, string[] known)
    {
        foreach (var name in mapping.Children.Keys)
        {
            if (name is not YamlScalarNode { Value: { } text } || !known.Contains(text, StringComparer.Ordinal))
            {
                var shown = name is YamlScalarNode { Value: { } given } ? given : "(not a plain name)";
                throw Fail(name, path, $"unknown key '{shown}'; expected one of: {string.Join(", ", known)}.");
            }
        }
    }

    private static string FirstLine(string message)
    {
        var end = message.AsSpan().IndexOfAny('\r', '\n');
        return end < 0 ? message : message[..end];
    }

    private static PolicyConfigurationException Fail(YamlNode at, string path, string problem, Exception? inner = null)
    {
        var message = $"policy.yaml (line {at.Start.Line}): {path}: {problem}";
        return inner is null ? new PolicyConfigurationException(message) : new PolicyConfigurationException(message, inner);
    }
}
