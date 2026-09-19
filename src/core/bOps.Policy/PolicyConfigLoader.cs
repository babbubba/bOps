// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace bOps.Policy;

/// <summary>
/// Thrown when a <c>policy.yaml</c> document cannot become a valid <see cref="PolicyConfig"/> —
/// either it is not well-formed YAML, or it violates the one invariant this loader enforces
/// loudly rather than failing closed silently: <see cref="RiskLevel.Critical"/> can never be
/// configured as anything but forbidden (agentic/03-security-rules.md, rule S3 — "the policy
/// loader rejects a configuration that assigns any other mode to Critical, with a clear error").
/// </summary>
public sealed class PolicyConfigurationException : Exception
{
    /// <summary>Creates a policy configuration exception.</summary>
    public PolicyConfigurationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a policy configuration exception with no message. CA1032 requires this constructor to exist, not that it be used.</summary>
    public PolicyConfigurationException()
    {
    }

    /// <summary>Creates a policy configuration exception wrapping the underlying parse failure.</summary>
    public PolicyConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Parses <c>policy.yaml</c> into a <see cref="PolicyConfig"/>. Pure and side-effect-free — it
/// takes YAML text in and returns a config or a clear failure, never touching the filesystem
/// itself, so it is testable with plain strings. Reading the file and deciding what to do when
/// it is absent or broken is the host's job (see <c>bOps.Cli</c>'s composition root), because
/// that decision — fall back to <see cref="PolicyConfig.SafeDefault"/> versus
/// <see cref="PolicyConfig.AllForbidden"/> — depends on *why* there is no valid config, which
/// only the caller with filesystem access can distinguish (rule S3).
/// </summary>
public static class PolicyConfigLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Parses <paramref name="yaml"/> into a <see cref="PolicyConfig"/>.
    /// </summary>
    /// <exception cref="PolicyConfigurationException">
    /// The text is not well-formed YAML in the expected shape, or it assigns
    /// <see cref="RiskLevel.Critical"/> a mode other than <see cref="PolicyMode.Forbidden"/>.
    /// </exception>
    public static PolicyConfig Load(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        PolicyDocument? document;
        try
        {
            document = Deserializer.Deserialize<PolicyDocument?>(yaml);
        }
        catch (YamlException ex)
        {
            throw new PolicyConfigurationException($"policy.yaml is not valid YAML: {ex.Message}", ex);
        }

        document ??= new PolicyDocument();

        var defaults = new Dictionary<RiskLevel, PolicyMode>();
        if (document.Defaults is not null)
        {
            foreach (var (riskText, modeText) in document.Defaults)
            {
                var risk = ParseEnum<RiskLevel>(riskText, "defaults", riskText);
                var mode = ParseEnum<PolicyMode>(modeText, "defaults", riskText);

                if (risk == RiskLevel.Critical && mode != PolicyMode.Forbidden)
                {
                    throw new PolicyConfigurationException(
                        $"policy.yaml sets defaults.critical to '{modeText}'. Critical-risk actions are always " +
                        "forbidden and cannot be configured otherwise (agentic/03-security-rules.md, rule S3) — " +
                        "either remove this entry or set it to 'forbidden'.");
                }

                defaults[risk] = mode;
            }
        }

        var toolOverrides = new Dictionary<string, PolicyMode>(StringComparer.Ordinal);
        if (document.Tools is not null)
        {
            foreach (var (toolName, modeText) in document.Tools)
            {
                toolOverrides[toolName] = ParseEnum<PolicyMode>(modeText, "tools", toolName);
            }
        }

        var packageCeilings = new Dictionary<string, RiskLevel>(StringComparer.Ordinal);
        if (document.Packages is not null)
        {
            foreach (var (packageId, riskText) in document.Packages)
            {
                packageCeilings[packageId] = ParseEnum<RiskLevel>(riskText, "packages", packageId);
            }
        }

        var skillRules = new List<SkillPolicyRule>();
        var skillRuleKeys = new HashSet<string>(StringComparer.Ordinal);
        if (document.Skills is not null)
        {
            foreach (var rule in document.Skills)
            {
                var skill = rule.GetValueOrDefault("skill");
                var capability = rule.GetValueOrDefault("capability");
                var target = rule.GetValueOrDefault("target");
                var environment = rule.GetValueOrDefault("environment");
                var blastRadiusText = rule.GetValueOrDefault("blastRadius");
                var modeText = rule.GetValueOrDefault("mode");
                if (string.IsNullOrWhiteSpace(skill)
                    || string.IsNullOrWhiteSpace(capability)
                    || string.IsNullOrWhiteSpace(target)
                    || string.IsNullOrWhiteSpace(environment)
                    || string.IsNullOrWhiteSpace(blastRadiusText)
                    || string.IsNullOrWhiteSpace(modeText))
                {
                    throw new PolicyConfigurationException(
                        "Every skills entry requires skill, capability, target, environment, blastRadius and mode.");
                }

                var blastRadius = ParseEnum<BlastRadius>(blastRadiusText, "skills", skill);
                var mode = ParseEnum<PolicyMode>(modeText, "skills", skill);
                var key = string.Join("\u001f", skill, capability, target, environment, blastRadius);
                if (!skillRuleKeys.Add(key))
                {
                    throw new PolicyConfigurationException(
                        $"policy.yaml contains more than one exact rule for Skill '{skill}' Capability '{capability}'.");
                }

                skillRules.Add(new SkillPolicyRule(
                    skill, capability, target, environment, blastRadius, mode));
            }
        }

        // Read last, so a problem in an older section is reported exactly as before. Strict where the sections above
        // are lenient: see DelegationSectionParser.
        var roleProfiles = DelegationSectionParser.Parse(yaml);

        return new PolicyConfig(defaults, toolOverrides, packageCeilings, skillRules, roleProfiles);
    }

    private static T ParseEnum<T>(string value, string section, string key) where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: true, out var parsed)
            ? parsed
            : throw new PolicyConfigurationException(
                $"policy.yaml: '{value}' under {section}.{key} is not a valid {typeof(T).Name}.");

    private sealed class PolicyDocument
    {
        public Dictionary<string, string>? Defaults { get; set; }
        public Dictionary<string, string>? Tools { get; set; }
        public Dictionary<string, string>? Packages { get; set; }
        public List<Dictionary<string, string>>? Skills { get; set; }
    }
}
