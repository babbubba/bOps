// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Policy.Tests;

/// <summary>
/// agentic/03-security-rules.md, rule S3: the loader parses defaults/tools/packages, and rejects
/// — loudly, not by silently coercing — a configuration that assigns Critical anything but
/// Forbidden.
/// </summary>
public sealed class PolicyConfigLoaderTests
{
    [Fact]
    public void Load_ParsesDefaultsToolsAndPackages()
    {
        var yaml = """
            defaults:
              read: automatic
              medium: approval
            tools:
              service.restart: forbidden
            packages:
              bops.packages.system.windows: high
            """;

        var config = PolicyConfigLoader.Load(yaml);

        Assert.Equal(PolicyMode.Automatic, config.Defaults[RiskLevel.Read]);
        Assert.Equal(PolicyMode.Approval, config.Defaults[RiskLevel.Medium]);
        Assert.Equal(PolicyMode.Forbidden, config.ToolOverrides["service.restart"]);
        Assert.Equal(RiskLevel.High, config.PackageCeilings["bops.packages.system.windows"]);
    }

    [Fact]
    public void Load_IsCaseInsensitiveForEnumValues()
    {
        var yaml = """
            defaults:
              Read: Automatic
            """;

        var config = PolicyConfigLoader.Load(yaml);

        Assert.Equal(PolicyMode.Automatic, config.Defaults[RiskLevel.Read]);
    }

    [Fact]
    public void Load_ReturnsEmptyConfig_ForEmptyYaml()
    {
        var config = PolicyConfigLoader.Load(string.Empty);

        Assert.Empty(config.Defaults);
        Assert.Empty(config.ToolOverrides);
        Assert.Empty(config.PackageCeilings);
    }

    [Fact]
    public void Load_AllowsCriticalExplicitlySetToForbidden()
    {
        var yaml = """
            defaults:
              critical: forbidden
            """;

        var config = PolicyConfigLoader.Load(yaml);

        Assert.Equal(PolicyMode.Forbidden, config.Defaults[RiskLevel.Critical]);
    }

    [Theory]
    [InlineData("automatic")]
    [InlineData("approval")]
    public void Load_RejectsCriticalConfiguredAsAnythingOtherThanForbidden(string mode)
    {
        var yaml = $"""
            defaults:
              critical: {mode}
            """;

        var ex = Assert.Throws<PolicyConfigurationException>(() => PolicyConfigLoader.Load(yaml));
        Assert.Contains("Critical", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_ThrowsOnMalformedYaml()
    {
        var yaml = "defaults: [this is not a mapping";

        Assert.Throws<PolicyConfigurationException>(() => PolicyConfigLoader.Load(yaml));
    }

    [Fact]
    public void Load_ThrowsOnUnrecognizedRiskLevel()
    {
        var yaml = """
            defaults:
              nonexistent: automatic
            """;

        Assert.Throws<PolicyConfigurationException>(() => PolicyConfigLoader.Load(yaml));
    }

    [Fact]
    public void Load_ThrowsOnUnrecognizedPolicyMode()
    {
        var yaml = """
            defaults:
              read: sometimes
            """;

        Assert.Throws<PolicyConfigurationException>(() => PolicyConfigLoader.Load(yaml));
    }

    // Rule S3: a value that is not the name of a member is refused. Enum.TryParse also reads numbers and comma
    // lists, which produced members that do not exist (PolicyMode 3) or a different one than was written.
    [Theory]
    [InlineData("approval, forbidden")]
    [InlineData("automatic,approval")]
    [InlineData("3")]
    [InlineData("1")]
    [InlineData("99")]
    [InlineData("-1")]
    [InlineData(" ")]
    [InlineData("\"\"")]
    public void Load_RefusesAModeThatIsNotAName(string mode)
    {
        var defaults = $"defaults:\n  read: {mode}\n";
        var tools = $"tools:\n  service.restart: {mode}\n";
        var skills = $"skills:\n  - skill: s\n    capability: c\n    target: t\n    environment: e\n    blastRadius: single\n    mode: {mode}\n";

        foreach (var yaml in new[] { defaults, tools, skills })
        {
            Assert.Throws<PolicyConfigurationException>(() => PolicyConfigLoader.Load(yaml));
        }
    }

    [Theory]
    [InlineData("low, medium")]
    [InlineData("3")]
    [InlineData("99")]
    [InlineData(" ")]
    public void Load_RefusesARiskLevelThatIsNotAName(string risk)
    {
        Assert.Throws<PolicyConfigurationException>(() => PolicyConfigLoader.Load($"defaults:\n  \"{risk}\": automatic\n"));
        Assert.Throws<PolicyConfigurationException>(() => PolicyConfigLoader.Load($"packages:\n  some.package: \"{risk}\"\n"));
    }

    [Theory]
    [InlineData("single, fleet")]
    [InlineData("0")]
    [InlineData("7")]
    public void Load_RefusesABlastRadiusThatIsNotAName(string radius)
    {
        var yaml = $"skills:\n  - skill: s\n    capability: c\n    target: t\n    environment: e\n    blastRadius: \"{radius}\"\n    mode: approval\n";

        Assert.Throws<PolicyConfigurationException>(() => PolicyConfigLoader.Load(yaml));
    }

    [Fact]
    public void Load_NamesTheSectionAndKeyOfARefusedValue()
    {
        var ex = Assert.Throws<PolicyConfigurationException>(() => PolicyConfigLoader.Load("defaults:\n  read: \"approval, forbidden\"\n"));

        Assert.Contains("defaults.read", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_ParsesExactSkillRules()
    {
        var yaml = """
            skills:
              - skill: sample.skill
                capability: sample.remediate
                target: local
                environment: test
                blastRadius: single
                mode: approval
            """;

        var config = PolicyConfigLoader.Load(yaml);

        var rule = Assert.Single(config.SkillRules);
        Assert.Equal("sample.skill", rule.SkillId);
        Assert.Equal("sample.remediate", rule.CapabilityName);
        Assert.Equal("local", rule.Target);
        Assert.Equal("test", rule.Environment);
        Assert.Equal(BlastRadius.Single, rule.BlastRadius);
        Assert.Equal(PolicyMode.Approval, rule.Mode);
    }

    [Fact]
    public void Load_RejectsIncompleteOrDuplicateSkillRules()
    {
        var incomplete = """
            skills:
              - skill: sample.skill
                capability: sample.remediate
                mode: automatic
            """;
        var duplicate = """
            skills:
              - skill: sample.skill
                capability: sample.remediate
                target: local
                environment: test
                blastRadius: single
                mode: automatic
              - skill: sample.skill
                capability: sample.remediate
                target: local
                environment: test
                blastRadius: single
                mode: approval
            """;

        Assert.Throws<PolicyConfigurationException>(() => PolicyConfigLoader.Load(incomplete));
        Assert.Throws<PolicyConfigurationException>(() => PolicyConfigLoader.Load(duplicate));
    }
}
