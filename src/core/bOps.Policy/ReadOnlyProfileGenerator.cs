// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using bOps.Abstractions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace bOps.Policy;

/// <summary>
/// Thrown when <see cref="ReadOnlyProfileGenerator"/> cannot produce a configuration it can prove safe: no Read tool is available,
/// a tool name the profile contract or YAML cannot represent exactly, or a generated document that does not load back as exactly
/// what was intended. Nothing is printed as valid and nothing is written.
/// </summary>
public sealed class ProfileGenerationException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ProfileGenerationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with no message. CA1032 requires this constructor to exist, not that it be used.</summary>
    public ProfileGenerationException()
    {
    }

    /// <summary>Creates the exception wrapping the underlying failure.</summary>
    public ProfileGenerationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A generated read-only configuration, verified through the real loader before it is returned.</summary>
/// <param name="Document">The whole <c>policy.yaml</c>: a header comment, the built-in default policy restated, and the delegation section.</param>
/// <param name="DelegationFragment">Only the <c>delegation</c> section, for a manual merge into an existing file.</param>
/// <param name="Tools">The exact tool names granted to both roles, in ordinal order.</param>
public sealed record GeneratedReadOnlyPolicy(string Document, string DelegationFragment, IReadOnlyList<string> Tools);

/// <summary>
/// <c>bops delegate profiles init --read-only</c> (ADR-0044 section 10): an explicit, finite, read-only delegation configuration —
/// Discovery and Diagnostic only, each listing by exact name every available <see cref="RiskLevel.Read"/> tool at generation time,
/// with the operator-accepted budgets and no Skill, Capability, window, Remediation or Verification. YAML is emitted through the
/// YamlDotNet emitter with every tool name a double-quoted scalar, never interpolated into text (review N-1), and the result is
/// parsed back through <see cref="PolicyConfigLoader"/> and checked structurally before it is returned. A tool name that cannot be
/// represented exactly fails the generation, naming the tool; it is never silently altered or left out. Generated profiles are a
/// safe default, not the authority boundary: the envelope and the role risk cap are (ADR-0044 section 3).
/// </summary>
public static class ReadOnlyProfileGenerator
{
    /// <summary>Steps per role (operator decision C).</summary>
    public const int MaxSteps = 15;

    /// <summary>Model tokens per role (operator decision C).</summary>
    public const int MaxTokens = 150_000;

    /// <summary>Maximum duration per role (operator decision C).</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(30);

    /// <summary>The only target and environment label written (operator decision C).</summary>
    public const string LocalLabel = "local";

    private static readonly AgentRoleKind[] Roles = [AgentRoleKind.Discovery, AgentRoleKind.Diagnostic];

    private static readonly System.Text.UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly (string Risk, string Mode)[] SafeDefaults =
    [
        ("read", "automatic"), ("low", "automatic"), ("medium", "approval"), ("high", "approval"), ("critical", "forbidden"),
    ];

    /// <summary>Generates and verifies the read-only configuration for the given available tools.</summary>
    /// <param name="availableTools">The host's available tool manifests (after capability refresh, enabled plugins activated).</param>
    /// <param name="timeProvider">The clock of the generation instant written in the header.</param>
    /// <exception cref="ProfileGenerationException">No Read tool is available, a name cannot be represented, or verification failed.</exception>
    public static GeneratedReadOnlyPolicy Generate(IReadOnlyList<ToolManifest> availableTools, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(availableTools);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var tools = availableTools
            .Where(manifest => manifest.Risk == RiskLevel.Read)
            .Select(manifest => manifest.Name)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (tools.Count == 0)
        {
            throw new ProfileGenerationException("No Read tool is available on this host, so a read-only profile could never be ready; nothing was generated.");
        }

        foreach (var tool in tools)
        {
            RejectUnrepresentable(tool);
        }

        var fragment = Emit(tools, withDefaults: false);
        var document = Header(timeProvider.GetUtcNow()) + Emit(tools, withDefaults: true);

        Verify(document, tools, expectSafeDefaults: true);
        Verify(fragment, tools, expectSafeDefaults: false);
        return new GeneratedReadOnlyPolicy(document, fragment, tools);
    }

    /// <summary>
    /// Whether an existing policy file may be replaced by <c>--overwrite</c> (ADR-0044 section 10.1, review N-2): it must load, its
    /// non-delegation configuration must be the built-in default with no tool, package or Skill entry, it must configure no
    /// Remediation and no Verification profile, and its Discovery and Diagnostic profiles must both exist and equal the
    /// generator's own in every authority dimension but the tool list. Returns the first failed condition, or <c>null</c>. The
    /// text never quotes the file.
    /// </summary>
    /// <param name="existingYaml">The existing file's content, as just read.</param>
    public static string? CheckOverwritable(string existingYaml)
    {
        ArgumentNullException.ThrowIfNull(existingYaml);

        PolicyConfig config;
        try
        {
            config = PolicyConfigLoader.Load(existingYaml);
        }
        catch (PolicyConfigurationException)
        {
            return "condition 1: the existing file does not load as a policy";
        }

        if (!IsSafeDefaultBaseline(config))
        {
            return "condition 2: the existing file's policy outside delegation is not the built-in default (defaults differ, or it has tools, packages or skills entries)";
        }

        if (config.RoleProfiles.Any(profile => profile.Role is AgentRoleKind.Remediation or AgentRoleKind.Verification))
        {
            return "condition 3: the existing file configures a Remediation or Verification profile";
        }

        foreach (var role in Roles)
        {
            if (config.RoleProfiles.FirstOrDefault(profile => profile.Role == role) is not { } profile || !EqualsGeneratedExceptTools(profile))
            {
                return $"condition 4: the existing {role} profile is absent or differs from the generated read-only defaults in a dimension other than its tools";
            }
        }

        return null;
    }

    // ---- emission --------------------------------------------------------------------------------------------------

    private static string Header(DateTimeOffset now) =>
        "# Generated by `bops delegate profiles init --read-only` on "
        + now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) + ".\n"
        + "# Read-only delegation for diagnosis. Grants no Remediation or Verification profile, no Skill,\n"
        + "# no Capability and nothing above Read. Tools are listed by exact name; a tool installed later is\n"
        + "# NOT included until this file is regenerated (`profiles check` reports it). `defaults` repeats\n"
        + "# the built-in policy that applies when no policy.yaml exists, so creating this file changes no\n"
        + "# decision outside delegation.\n";

    private static string Emit(IReadOnlyList<string> tools, bool withDefaults)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var emitter = new Emitter(writer);
        emitter.Emit(new StreamStart());
        emitter.Emit(new DocumentStart());
        emitter.Emit(new MappingStart(AnchorName.Empty, TagName.Empty, true, MappingStyle.Block));

        if (withDefaults)
        {
            Plain(emitter, "defaults");
            emitter.Emit(new MappingStart(AnchorName.Empty, TagName.Empty, true, MappingStyle.Block));
            foreach (var (risk, mode) in SafeDefaults)
            {
                Plain(emitter, risk);
                Plain(emitter, mode);
            }

            emitter.Emit(new MappingEnd());
        }

        Plain(emitter, "delegation");
        emitter.Emit(new MappingStart(AnchorName.Empty, TagName.Empty, true, MappingStyle.Block));
        Plain(emitter, "roles");
        emitter.Emit(new MappingStart(AnchorName.Empty, TagName.Empty, true, MappingStyle.Block));
        foreach (var role in Roles)
        {
            Plain(emitter, role == AgentRoleKind.Discovery ? "discovery" : "diagnostic");
            emitter.Emit(new MappingStart(AnchorName.Empty, TagName.Empty, true, MappingStyle.Block));

            Plain(emitter, "tools");
            emitter.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, true, SequenceStyle.Block));
            foreach (var tool in tools)
            {
                Quoted(emitter, tool);
            }

            emitter.Emit(new SequenceEnd());

            Plain(emitter, "maxRisk");
            Plain(emitter, "read");
            Plain(emitter, "maxBlastRadius");
            Plain(emitter, "single");
            foreach (var key in new[] { "targets", "environments" })
            {
                Plain(emitter, key);
                emitter.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, true, SequenceStyle.Flow));
                Plain(emitter, LocalLabel);
                emitter.Emit(new SequenceEnd());
            }

            Plain(emitter, "maxSteps");
            Plain(emitter, MaxSteps.ToString(CultureInfo.InvariantCulture));
            Plain(emitter, "maxTokens");
            Plain(emitter, MaxTokens.ToString(CultureInfo.InvariantCulture));
            Plain(emitter, "maxDuration");
            Plain(emitter, MaxDuration.ToString("c", CultureInfo.InvariantCulture));
            emitter.Emit(new MappingEnd());
        }

        emitter.Emit(new MappingEnd());
        emitter.Emit(new MappingEnd());
        emitter.Emit(new MappingEnd());
        emitter.Emit(new DocumentEnd(true));
        emitter.Emit(new StreamEnd());
        return writer.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    // Only fixed generator constants are written plain; a tool name is never one of them.
    private static void Plain(Emitter emitter, string constant) =>
        emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty, constant, ScalarStyle.Plain, true, false));

    private static void Quoted(Emitter emitter, string value) =>
        emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty, value, ScalarStyle.DoubleQuoted, false, true));

    // ---- verification -----------------------------------------------------------------------------------------------

    /// <summary>
    /// A name the profile contract would refuse, or that does not survive a double-quoted YAML round trip exactly, fails the whole
    /// generation (ADR-0044 section 10.2): granting it altered or leaving it out would present an incomplete profile as complete.
    /// </summary>
    private static void RejectUnrepresentable(string tool)
    {
        if (string.IsNullOrWhiteSpace(tool) || tool.Trim().Length != tool.Length || tool.AsSpan().IndexOfAny('*', '?') >= 0)
        {
            throw new ProfileGenerationException(
                $"The available Read tool {Describe(tool)} cannot be granted by an exact profile name (blank, padded or containing a wildcard character); nothing was generated.");
        }

        string? back;
        try
        {
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            var emitter = new Emitter(writer);
            emitter.Emit(new StreamStart());
            emitter.Emit(new DocumentStart());
            emitter.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, true, SequenceStyle.Block));
            Quoted(emitter, tool);
            emitter.Emit(new SequenceEnd());
            emitter.Emit(new DocumentEnd(true));
            emitter.Emit(new StreamEnd());

            // The file is UTF-8: what cannot be encoded strictly (a lone surrogate written raw) would not reach the disk as verified.
            var stream = new YamlDotNet.RepresentationModel.YamlStream();
            using var reader = new StringReader(StrictUtf8.GetString(StrictUtf8.GetBytes(writer.ToString())));
            stream.Load(reader);
            back = ((YamlDotNet.RepresentationModel.YamlSequenceNode)stream.Documents[0].RootNode).Children[0] is YamlDotNet.RepresentationModel.YamlScalarNode scalar
                ? scalar.Value
                : null;
        }
        catch (Exception ex) when (ex is YamlException or InvalidCastException or ArgumentException or InvalidOperationException or System.Text.EncoderFallbackException)
        {
            back = null;
        }

        if (!string.Equals(back, tool, StringComparison.Ordinal))
        {
            throw new ProfileGenerationException(
                $"The available Read tool {Describe(tool)} does not round-trip exactly through YAML; nothing was generated.");
        }
    }

    private static void Verify(string yaml, IReadOnlyList<string> tools, bool expectSafeDefaults)
    {
        PolicyConfig config;
        try
        {
            // Verified as the bytes a UTF-8 file will hold, so what was checked is what is written.
            config = PolicyConfigLoader.Load(StrictUtf8.GetString(StrictUtf8.GetBytes(yaml)));
        }
        catch (Exception ex) when (ex is PolicyConfigurationException or System.Text.EncoderFallbackException or System.Text.DecoderFallbackException)
        {
            throw new ProfileGenerationException("The generated configuration does not load through the policy loader; nothing was generated.", ex);
        }

        Check(config.RoleProfiles.Count == 2 && Roles.All(role => config.RoleProfiles.Any(p => p.Role == role)), "profiles are exactly Discovery and Diagnostic");
        foreach (var profile in config.RoleProfiles)
        {
            Check(profile.AllowedTools.SequenceEqual(tools, StringComparer.Ordinal), $"{profile.Role} tools equal the intended available Read tools exactly");
            Check(EqualsGeneratedExceptTools(profile), $"{profile.Role} has exactly the generated read-only authority");
        }

        if (expectSafeDefaults)
        {
            Check(IsSafeDefaultBaseline(config), "the policy outside delegation equals the built-in default");
        }
    }

    private static bool EqualsGeneratedExceptTools(RoleProfile profile) =>
        profile.AllowedSkills.Count == 0
        && profile.AllowedCapabilities.Count == 0
        && profile.MaxRisk == RiskLevel.Read
        && profile.MaxBlastRadius == BlastRadius.Single
        && profile.AllowedTargets.SequenceEqual([LocalLabel], StringComparer.Ordinal)
        && profile.AllowedEnvironments.SequenceEqual([LocalLabel], StringComparer.Ordinal)
        && profile.MaxSteps == MaxSteps
        && profile.MaxTokens == MaxTokens
        && profile.MaxDuration == MaxDuration
        && profile.Window is null;

    private static bool IsSafeDefaultBaseline(PolicyConfig config) =>
        config.ToolOverrides.Count == 0
        && config.PackageCeilings.Count == 0
        && config.SkillRules.Count == 0
        && config.Defaults.Count == PolicyConfig.SafeDefault.Defaults.Count
        && PolicyConfig.SafeDefault.Defaults.All(entry => config.Defaults.TryGetValue(entry.Key, out var mode) && mode == entry.Value);

    private static void Check(bool holds, string assertion)
    {
        if (!holds)
        {
            throw new ProfileGenerationException($"The generated configuration failed its own check ({assertion}); nothing was generated.");
        }
    }

    // A tool name is shown for the operator to find it: bounded, quoted, every character outside printable ASCII as \uXXXX so
    // control characters, invisible characters and lone surrogates stay visible and the line cannot be split.
    private static string Describe(string tool)
    {
        var builder = new System.Text.StringBuilder("\"");
        foreach (var character in tool.Length > 128 ? tool[..128] : tool)
        {
            if (character is >= ' ' and <= '~' and not '"' and not '\\')
            {
                builder.Append(character);
            }
            else
            {
                builder.Append(CultureInfo.InvariantCulture, $"\\u{(int)character:x4}");
            }
        }

        return builder.Append(tool.Length > 128 ? "\"…" : "\"").ToString();
    }
}
