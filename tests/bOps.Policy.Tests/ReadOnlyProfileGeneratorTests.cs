// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Policy.Tests;

/// <summary>
/// ADR-0044 section 10 and section 20 P6/P7 (review N-1, N-2): the read-only generator emits YAML safely for every tool name the
/// registry accepts, verifies it through the real <see cref="PolicyConfigLoader"/>, grants exactly the available Read tools to
/// Discovery and Diagnostic with the accepted budgets and nothing else, changes no decision outside delegation, fails rather than
/// alters or omits a name it cannot represent, and lets <c>--overwrite</c> replace only a generator-equivalent file.
/// </summary>
public sealed class ReadOnlyProfileGeneratorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static ToolManifest Manifest(string name, RiskLevel risk = RiskLevel.Read) => new()
    {
        Name = name,
        Description = "x",
        Risk = risk,
        Platforms = ["windows", "linux"],
        Requires = [],
        Parameters = [],
        Verification = risk == RiskLevel.Read ? null : new VerificationSpec("system.cpu", [], "x"),
    };

    private static GeneratedReadOnlyPolicy Generate(params string[] readTools) =>
        ReadOnlyProfileGenerator.Generate([.. readTools.Select(name => Manifest(name)), Manifest("service.restart", RiskLevel.High)], new FixedTime(T0));

    private static void AssertGeneratedAuthority(PolicyConfig config, IReadOnlyList<string> tools)
    {
        Assert.Equal([AgentRoleKind.Discovery, AgentRoleKind.Diagnostic], config.RoleProfiles.Select(p => p.Role).Order());
        foreach (var profile in config.RoleProfiles)
        {
            Assert.Equal(tools, profile.AllowedTools);
            Assert.Equal(RiskLevel.Read, profile.MaxRisk);
            Assert.Equal(BlastRadius.Single, profile.MaxBlastRadius);
            Assert.Empty(profile.AllowedSkills);
            Assert.Empty(profile.AllowedCapabilities);
            Assert.Equal(["local"], profile.AllowedTargets);
            Assert.Equal(["local"], profile.AllowedEnvironments);
            Assert.Equal(15, profile.MaxSteps);
            Assert.Equal(150_000, profile.MaxTokens);
            Assert.Equal(TimeSpan.FromMinutes(30), profile.MaxDuration);
            Assert.Null(profile.Window);
        }
    }

    // ---- what is generated ----

    [Fact]
    public void Generate_GrantsExactlyTheAvailableReadTools_ToDiscoveryAndDiagnostic_WithTheAcceptedBudgets()
    {
        var generated = Generate("system.memory", "system.cpu", "system.cpu");

        var config = PolicyConfigLoader.Load(generated.Document);

        Assert.Equal(["system.cpu", "system.memory"], generated.Tools);
        AssertGeneratedAuthority(config, ["system.cpu", "system.memory"]);
        Assert.DoesNotContain(config.RoleProfiles, p => p.Role is AgentRoleKind.Remediation or AgentRoleKind.Verification);
        Assert.DoesNotContain("service.restart", generated.Document, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_NeverWritesASkillCapabilityWindowRemediationOrVerificationKey()
    {
        var text = Generate("system.cpu").Document;

        foreach (var key in new[] { "skills:", "capabilities:", "window:", "remediation:", "verification:", "*" })
        {
            Assert.DoesNotContain(key, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Generate_StatesTheBuiltInDefaults_SoCreatingTheFileChangesNoDecisionOutsideDelegation()
    {
        var config = PolicyConfigLoader.Load(Generate("system.cpu").Document);

        Assert.Equal(PolicyConfig.SafeDefault.Defaults.OrderBy(e => e.Key), config.Defaults.OrderBy(e => e.Key));
        Assert.Empty(config.ToolOverrides);
        Assert.Empty(config.PackageCeilings);
        Assert.Empty(config.SkillRules);
    }

    [Fact]
    public void P7_EveryDecisionOfTheGeneratedPolicy_EqualsTheNoFileDefault()
    {
        var generated = new PolicyEngine(PolicyConfigLoader.Load(Generate("system.cpu", "system.memory").Document));
        var safe = new PolicyEngine(PolicyConfig.SafeDefault);
        var actor = ActorIdentity.FromOperatingSystemUser("operator");

        foreach (var risk in Enum.GetValues<RiskLevel>())
        {
            foreach (var trust in Enum.GetValues<PackageTrustLevel>())
            {
                foreach (var name in new[] { "system.cpu", "service.restart", "other.tool" })
                {
                    foreach (var skill in new string?[] { null, "sk" })
                    {
                        var context = new PolicyContext(NodeId.Local, new PackageId("p"), trust, Manifest(name, risk), ToolArguments.Empty, actor)
                        {
                            SkillId = skill, CapabilityName = skill is null ? null : "cap", Target = skill is null ? null : "local",
                            Environment = skill is null ? null : "local", BlastRadius = skill is null ? null : BlastRadius.Single,
                        };
                        Assert.Equal(safe.Evaluate(context).Mode, generated.Evaluate(context).Mode);
                    }
                }
            }
        }
    }

    [Fact]
    public void Generate_TheFragment_IsOnlyTheDelegationSection_AndLoadsToTheSameProfiles()
    {
        var generated = Generate("system.cpu");

        Assert.DoesNotContain("defaults", generated.DelegationFragment, StringComparison.Ordinal);
        Assert.StartsWith("delegation:", generated.DelegationFragment, StringComparison.Ordinal);
        AssertGeneratedAuthority(PolicyConfigLoader.Load(generated.DelegationFragment), ["system.cpu"]);
    }

    [Fact]
    public void Generate_TheHeaderTimestamp_ComesFromTheInjectedClock_AndOutputIsDeterministic()
    {
        var first = Generate("system.cpu", "system.disk");
        var second = Generate("system.disk", "system.cpu");
        var later = ReadOnlyProfileGenerator.Generate([Manifest("system.cpu"), Manifest("system.disk")], new FixedTime(T0.AddDays(1)));

        Assert.StartsWith("# Generated by `bops delegate profiles init --read-only` on 2026-10-05T12:00:00Z.\n", first.Document, StringComparison.Ordinal);
        Assert.Equal(first.Document, second.Document);
        Assert.Contains("2026-10-06T12:00:00Z", later.Document, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', first.Document);
    }

    [Fact]
    public void Generate_WithNoReadToolAvailable_Refuses()
    {
        var refused = Assert.Throws<ProfileGenerationException>(
            () => ReadOnlyProfileGenerator.Generate([Manifest("service.restart", RiskLevel.High)], new FixedTime(T0)));

        Assert.Contains("No Read tool", refused.Message, StringComparison.Ordinal);
    }

    // ---- P6: every name the registry accepts is either emitted exactly or refused, never altered ----

    public static TheoryData<string> HostileButRepresentableNames() =>
    [
        "plain.name", "with space", "colon: inside", "#hash", "a # comment", "-leading", "!tag", "&anchor", "*not-wildcard-start".Replace("*", "%", StringComparison.Ordinal),
        "yes", "no", "null", "~", "true", "Off", "123", "1e3", "0x1F", ".inf", "'single'", "\"double\"", "back\\slash", "[flow]", "{map}",
        "multi\nline", "tab\tinside", "uni-çø∂é", "emoji-😀", "rtl-‮evil", "zero​width", "nul-\u0001ctl", "---", "...", "|", ">", "@at", "`tick`",
        "lone-\ud800-surrogate", "very-long-" + new string('x', 300),
    ];

    [Theory]
    [MemberData(nameof(HostileButRepresentableNames))]
    public void P6_AnUnusualToolName_IsEmittedAsAnExactlyRoundTrippingGrant(string name)
    {
        var generated = Generate(name, "system.cpu");

        var config = PolicyConfigLoader.Load(generated.Document);

        var expected = new[] { name, "system.cpu" }.Order(StringComparer.Ordinal).ToList();
        AssertGeneratedAuthority(config, expected);
        Assert.Equal(expected, generated.Tools);
        Assert.Equal(2, config.RoleProfiles.Count);
    }

    public static TheoryData<string> UnrepresentableNames() =>
    [
        "wild*card", "what?", "?leading", " padded", "padded ", "\tlead", "trail\n",
    ];

    [Theory]
    [MemberData(nameof(UnrepresentableNames))]
    public void P6_ANameTheProfileContractOrYamlCannotRepresent_FailsTheWholeGeneration_NamingIt_NeverOmitted(string name)
    {
        var refused = Assert.Throws<ProfileGenerationException>(() => Generate(name, "system.cpu"));

        Assert.Contains("nothing was generated", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', refused.Message);
    }

    [Fact]
    public void P6_GeneratedRegistries_AlwaysLoadBack_AsExactlyTheIntendedGrant()
    {
        var alphabet = "ab.:-_ #?!&*|>'\"{}[],%@`~\\\t\né中";
        var seed = 0x4811_0601UL;
        for (var i = 0; i < 300; i++)
        {
            var names = Enumerable.Range(0, (int)(Next(ref seed) % 6) + 1)
                .Select(_ => new string([.. Enumerable.Range(0, (int)(Next(ref seed) % 12) + 1).Select(_ => alphabet[(int)(Next(ref seed) % (ulong)alphabet.Length)])]))
                .ToArray();

            GeneratedReadOnlyPolicy generated;
            try
            {
                generated = Generate(names);
            }
            catch (ProfileGenerationException)
            {
                // Refused is safe; it must be because some name is not representable as an exact member.
                Assert.Contains(names, name => string.IsNullOrWhiteSpace(name) || name.Trim() != name || name.Contains('*', StringComparison.Ordinal) || name.Contains('?', StringComparison.Ordinal));
                continue;
            }

            AssertGeneratedAuthority(PolicyConfigLoader.Load(generated.Document), [.. names.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
        }
    }

    private static ulong Next(ref ulong state)
    {
        unchecked
        {
            state += 0x9E3779B97F4A7C15UL;
            var z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    // ---- --overwrite conditions (S17, review N-2) ----

    [Fact]
    public void Overwrite_IsAllowed_ForAPreviouslyGeneratedFile_WhateverItsToolList()
    {
        Assert.Null(ReadOnlyProfileGenerator.CheckOverwritable(Generate("old.tool").Document));
        Assert.Null(ReadOnlyProfileGenerator.CheckOverwritable(Generate("system.cpu", "system.memory").Document));
    }

    public static TheoryData<string, string> RefusedOverwrites()
    {
        var generated = Generate("system.cpu").Document;
        return new TheoryData<string, string>
        {
            { "this: is: [not valid", "condition 1" },
            { generated.Replace("medium: approval", "medium: automatic", StringComparison.Ordinal), "condition 2" },
            { generated.Replace("  medium: approval\n", string.Empty, StringComparison.Ordinal), "condition 2" },
            { generated + "tools:\n  system.cpu: approval\n", "condition 2" },
            { generated + "packages:\n  some.package: read\n", "condition 2" },
            { generated.Replace("critical: forbidden", "critical: forbidden\n  low: approval", StringComparison.Ordinal).Replace("  low: automatic\n", string.Empty, StringComparison.Ordinal), "condition 2" },
            {
                generated + "skills:\n- skill: s\n  capability: c\n  target: local\n  environment: local\n  blastRadius: single\n  mode: approval\n", "condition 2"
            },
            {
                generated.Replace("  roles:\n", "  roles:\n    verification:\n      tools: [\"system.cpu\"]\n      maxRisk: read\n      maxBlastRadius: single\n      targets: [local]\n      environments: [local]\n      maxSteps: 1\n      maxDuration: 00:01:00\n", StringComparison.Ordinal),
                "condition 3"
            },
            { generated.Replace("maxSteps: 15", "maxSteps: 14", StringComparison.Ordinal), "condition 4" },
            { generated.Replace("maxTokens: 150000", "maxTokens: 1000", StringComparison.Ordinal), "condition 4" },
            { generated.Replace("maxDuration: 00:30:00", "maxDuration: 00:10:00", StringComparison.Ordinal), "condition 4" },
            { generated.Replace("targets: [local]", "targets: [local, node-1]", StringComparison.Ordinal), "condition 4" },
            { generated.Replace("environments: [local]", "environments: [staging]", StringComparison.Ordinal), "condition 4" },
            { generated.Replace("maxDuration: 00:30:00", "maxDuration: 00:30:00\n      window:\n        start: 2026-10-05T00:00:00Z\n        end: 2026-10-06T00:00:00Z", StringComparison.Ordinal), "condition 4" },
            { "defaults:\n  read: automatic\n  low: automatic\n  medium: approval\n  high: approval\n  critical: forbidden\n", "condition 4" },
        };
    }

    [Theory]
    [MemberData(nameof(RefusedOverwrites))]
    public void S17_Overwrite_IsRefused_NamingTheFirstFailedCondition_AndNeverQuotingTheFile(string existing, string condition)
    {
        var refusal = ReadOnlyProfileGenerator.CheckOverwritable(existing);

        Assert.NotNull(refusal);
        Assert.True(refusal.StartsWith(condition, StringComparison.Ordinal), $"{refusal} ||| {existing}");
        Assert.DoesNotContain("system.cpu", refusal, StringComparison.Ordinal);
        Assert.DoesNotContain("node-1", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void S17_AnOperatorsStricterDiagnostic_IsNotOverwritable()
    {
        var narrowed = Generate("system.cpu").Document.Replace("maxRisk: read\n      maxBlastRadius: single\n      targets: [local]", "maxRisk: read\n      maxBlastRadius: single\n      targets: [local]", StringComparison.Ordinal)
            .Replace("    diagnostic:\n", "    diagnostic:\n      skills: [sk.only]\n      capabilities: [cap.only]\n", StringComparison.Ordinal);

        Assert.StartsWith("condition 4", ReadOnlyProfileGenerator.CheckOverwritable(narrowed), StringComparison.Ordinal);
    }

    // ---- the whole policy file stays fail-closed (S24, review N-10) ----

    [Fact]
    public void S24_AMalformedProfileForARoleNoRequestUses_StillFailsTheWholePolicy()
    {
        var withBrokenVerification = Generate("system.cpu").Document.Replace(
            "  roles:\n", "  roles:\n    verification:\n      tools: [\"system.cpu\"]\n      maxRisk: read\n      maxBlastRadius: single\n      maxTokens: 5\n      maxDuration: 00:01:00\n", StringComparison.Ordinal);

        Assert.Throws<PolicyConfigurationException>(() => PolicyConfigLoader.Load(withBrokenVerification));
    }

    [Fact]
    public void AValidPolicyWithOnlyReadOnlyProfiles_LoadsWithoutRemediationOrVerification()
    {
        var config = PolicyConfigLoader.Load(Generate("system.cpu").Document);
        var source = new PolicyRoleProfileSource(config);

        Assert.NotNull(source.GetProfile(AgentRoleKind.Discovery));
        Assert.NotNull(source.GetProfile(AgentRoleKind.Diagnostic));
        Assert.Null(source.GetProfile(AgentRoleKind.Remediation));
        Assert.Null(source.GetProfile(AgentRoleKind.Verification));
    }
}
