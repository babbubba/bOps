// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Policy;

namespace bOps.Policy.Tests;

/// <summary>
/// The operator guide (<c>docs/agents/delegation-policy.md</c>) shows a working <c>delegation</c> section. It is read from the
/// document itself and loaded here, so the example an operator copies cannot drift from what the loader accepts.
/// </summary>
public sealed class DelegationPolicyGuideTests
{
    private static string GuidePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "bOps.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "docs", "agents", "delegation-policy.md");
    }

    private static string FirstYamlExample()
    {
        var lines = File.ReadAllLines(GuidePath());
        var start = Array.FindIndex(lines, line => line.Trim() == "```yaml");
        Assert.True(start >= 0, "The guide has no yaml example.");
        var end = Array.FindIndex(lines, start + 1, line => line.Trim() == "```");
        Assert.True(end > start, "The yaml example is not closed.");
        return string.Join('\n', lines[(start + 1)..end]);
    }

    [Fact]
    public void TheExampleInTheGuide_Loads_AndGivesEveryRoleItsProfile()
    {
        var config = PolicyConfigLoader.Load(FirstYamlExample());

        Assert.Equal(4, config.RoleProfiles.Count);
        Assert.Equivalent(Enum.GetValues<AgentRoleKind>(), config.RoleProfiles.Select(p => p.Role));
        var source = new PolicyRoleProfileSource(config);
        foreach (var role in Enum.GetValues<AgentRoleKind>())
        {
            Assert.NotNull(source.GetProfile(role));
        }

        var remediation = source.GetProfile(AgentRoleKind.Remediation)!;
        Assert.Equal(RiskLevel.High, remediation.MaxRisk);
        Assert.Equal(TimeSpan.FromMinutes(5), remediation.MaxDuration);
        Assert.NotNull(remediation.Window);
        Assert.Equal(0, remediation.MaxTokens);
    }

    [Fact]
    public void TheRulesTheGuideStates_AreTheOnesTheLoaderEnforces()
    {
        var example = FirstYamlExample();

        // A required key left out, an unknown key, a bare duration, a window instant without a zone, a role given what it cannot use.
        Assert.Throws<PolicyConfigurationException>(() => PolicyConfigLoader.Load(example.Replace("      maxDuration: 00:10:00\n", string.Empty, StringComparison.Ordinal)));
        Assert.Throws<PolicyConfigurationException>(() => PolicyConfigLoader.Load(example.Replace("maxSteps: 12", "maxStep: 12", StringComparison.Ordinal)));
        Assert.Throws<PolicyConfigurationException>(() => PolicyConfigLoader.Load(example.Replace("maxDuration: 00:05:00", "maxDuration: 10", StringComparison.Ordinal)));
        Assert.Throws<PolicyConfigurationException>(() => PolicyConfigLoader.Load(example.Replace("2026-09-20T02:00:00Z", "2026-09-20T02:00:00", StringComparison.Ordinal)));
        Assert.Throws<PolicyConfigurationException>(() => PolicyConfigLoader.Load(example.Replace("maxRisk: read", "maxRisk: 0", StringComparison.Ordinal)));
        Assert.Throws<PolicyConfigurationException>(() => PolicyConfigLoader.Load(
            example.Replace("      tools: [service.status]\n      maxRisk: read", "      skills: [service.skill]\n      tools: [service.status]\n      maxRisk: read", StringComparison.Ordinal)));
    }
}
