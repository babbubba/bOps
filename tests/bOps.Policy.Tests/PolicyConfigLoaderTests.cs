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
}
