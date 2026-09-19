// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Policy.Tests;

/// <summary>
/// V1.2-C3 (ADR-0030 section 1, ADR-0031 sections 2 and 5): the optional <c>delegation</c> section of
/// <c>policy.yaml</c>. It is host configuration, so the loader must be as strict as the rest of policy (rule S3):
/// a section it cannot read exactly is an error and never a guess, because a guess in this section is either a
/// grant nobody made or a restriction that silently vanished. A section that is absent configures nothing, which
/// is delegation off.
/// </summary>
public sealed class DelegationSectionLoaderTests
{
    private static readonly bool[] Both = [false, true];

    // The four roles as an operator would write them, each with only the dimensions the role uses (ADR-0031
    // section 1: "operators write smaller profiles, listing only what a role uses").
    private static List<(string Key, string Value)> Base(AgentRoleKind role) => role switch
    {
        AgentRoleKind.Discovery =>
        [
            ("tools", "[host.info, disk.list]"),
            ("maxRisk", "read"),
            ("maxBlastRadius", "single"),
            ("targets", "[host-a]"),
            ("environments", "[staging]"),
            ("maxSteps", "12"),
            ("maxTokens", "20000"),
            ("maxDuration", "00:10:00"),
        ],
        AgentRoleKind.Diagnostic =>
        [
            ("skills", "[sample.skill]"),
            ("capabilities", "[sample.diagnose]"),
            ("tools", "[host.info, disk.list]"),
            ("maxRisk", "read"),
            ("maxBlastRadius", "single"),
            ("targets", "[host-a]"),
            ("environments", "[staging]"),
            ("maxSteps", "8"),
            ("maxTokens", "30000"),
            ("maxDuration", "00:15:00"),
        ],
        AgentRoleKind.Remediation =>
        [
            ("skills", "[sample.skill]"),
            ("capabilities", "[sample.remediate]"),
            ("tools", "[service.restart]"),
            ("maxRisk", "medium"),
            ("maxBlastRadius", "multiple"),
            ("targets", "[host-a, host-b]"),
            ("environments", "[staging]"),
            ("maxSteps", "5"),
            ("maxDuration", "00:30:00"),
        ],
        AgentRoleKind.Verification =>
        [
            ("tools", "[host.info]"),
            ("maxRisk", "read"),
            ("maxBlastRadius", "single"),
            ("targets", "[host-a]"),
            ("environments", "[staging]"),
            ("maxSteps", "6"),
            ("maxDuration", "00:05:00"),
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "A new role needs a base profile here."),
    };

    // An edit replaces a key or adds it; a null value removes it.
    private static List<(string Key, string Value)> Body(AgentRoleKind role, IEnumerable<(string Key, string? Value)> edits)
    {
        var body = Base(role);
        foreach (var (key, value) in edits)
        {
            body.RemoveAll(p => p.Key == key);
            if (value is not null)
            {
                body.Add((key, value));
            }
        }

        return body;
    }

    private static string Block(string name, IEnumerable<(string Key, string Value)> body) =>
        $"    {name}:\n" + string.Concat(body.Select(p => $"      {p.Key}: {p.Value}\n"));

    private static string Block(AgentRoleKind role, params (string Key, string? Value)[] edits) =>
        Block(RoleName(role), Body(role, edits));

    private static string Delegation(params string[] roleBlocks) => "delegation:\n  roles:\n" + string.Concat(roleBlocks);

    private static string RoleName(AgentRoleKind role) => role.ToString().ToLowerInvariant();

    private static string RolePath(AgentRoleKind role) => $"delegation.roles.{RoleName(role)}";

    private static PolicyConfig LoadRole(AgentRoleKind role, params (string Key, string? Value)[] edits) =>
        PolicyConfigLoader.Load(Delegation(Block(role, edits)));

    private static PolicyConfigurationException Rejected(string yaml) =>
        Assert.Throws<PolicyConfigurationException>(() => PolicyConfigLoader.Load(yaml));

    private static string AllFour() =>
        Delegation(
            Block(AgentRoleKind.Discovery),
            Block(AgentRoleKind.Diagnostic),
            Block(AgentRoleKind.Remediation),
            Block(AgentRoleKind.Verification));

    // ---- absent means off -------------------------------------------------------------------------------------

    [Fact]
    public void ThereAreNoProfilesWithoutTheSection()
    {
        Assert.Empty(PolicyConfigLoader.Load(string.Empty).RoleProfiles);
        Assert.Empty(PolicyConfigLoader.Load("defaults:\n  read: automatic\n").RoleProfiles);
        Assert.Empty(PolicyConfig.SafeDefault.RoleProfiles);
        Assert.Empty(PolicyConfig.AllForbidden.RoleProfiles);
    }

    [Theory]
    [InlineData("delegation: {}\n")]
    [InlineData("delegation:\n  roles: {}\n")]
    public void AnEmptySectionConfiguresNoProfile(string yaml) =>
        Assert.Empty(PolicyConfigLoader.Load(yaml).RoleProfiles);

    [Fact]
    public void AMisspelledSectionNameConfiguresNothing()
    {
        // The top level has always ignored what it does not know, so the section is off rather than half read.
        var yaml = AllFour().Replace("delegation:", "delegaton:", StringComparison.Ordinal);

        Assert.Empty(PolicyConfigLoader.Load(yaml).RoleProfiles);
    }

    [Fact]
    public void TheRestOfThePolicyIsLoadedAlongsideTheSection()
    {
        var yaml = "defaults:\n  read: automatic\ntools:\n  service.restart: forbidden\n" + AllFour();

        var config = PolicyConfigLoader.Load(yaml);

        Assert.Equal(PolicyMode.Automatic, config.Defaults[RiskLevel.Read]);
        Assert.Equal(PolicyMode.Forbidden, config.ToolOverrides["service.restart"]);
        Assert.Equal(4, config.RoleProfiles.Count);
    }

    // ---- reading a profile ------------------------------------------------------------------------------------

    [Fact]
    public void EveryFieldOfAProfileIsRead()
    {
        var yaml = Delegation(
            Block(
                AgentRoleKind.Remediation,
                ("window", "\n        start: 2026-09-20T02:00:00Z\n        end: 2026-09-20T04:00:00Z")));

        var profile = Assert.Single(PolicyConfigLoader.Load(yaml).RoleProfiles);

        Assert.Equal(AgentRoleKind.Remediation, profile.Role);
        Assert.Equal(["sample.skill"], profile.AllowedSkills);
        Assert.Equal(["sample.remediate"], profile.AllowedCapabilities);
        Assert.Equal(["service.restart"], profile.AllowedTools);
        Assert.Equal(RiskLevel.Medium, profile.MaxRisk);
        Assert.Equal(BlastRadius.Multiple, profile.MaxBlastRadius);
        Assert.Equal(["host-a", "host-b"], profile.AllowedTargets);
        Assert.Equal(["staging"], profile.AllowedEnvironments);
        Assert.Equal(5, profile.MaxSteps);
        Assert.Equal(0, profile.MaxTokens);
        Assert.Equal(TimeSpan.FromMinutes(30), profile.MaxDuration);
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 2, 0, 0, TimeSpan.Zero), profile.Window!.StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 4, 0, 0, TimeSpan.Zero), profile.Window.EndUtc);
    }

    [Fact]
    public void AllFourRolesAreLoadedEachUnderItsOwnRole()
    {
        var profiles = PolicyConfigLoader.Load(AllFour()).RoleProfiles;

        Assert.Equal(
            [AgentRoleKind.Discovery, AgentRoleKind.Diagnostic, AgentRoleKind.Remediation, AgentRoleKind.Verification],
            profiles.Select(p => p.Role).Order());
    }

    [Fact]
    public void RoleNamesAndEnumValuesAreCaseInsensitive()
    {
        var yaml = "delegation:\n  roles:\n    DISCOVERY:\n" + string.Concat(
            Body(AgentRoleKind.Discovery, [("maxRisk", "READ"), ("maxBlastRadius", "Single")]).Select(p => $"      {p.Key}: {p.Value}\n"));

        var profile = Assert.Single(PolicyConfigLoader.Load(yaml).RoleProfiles);

        Assert.Equal(AgentRoleKind.Discovery, profile.Role);
        Assert.Equal(RiskLevel.Read, profile.MaxRisk);
        Assert.Equal(BlastRadius.Single, profile.MaxBlastRadius);
    }

    [Fact]
    public void ADimensionAProfileDoesNotMentionGrantsNothing()
    {
        // Discovery without targets, environments or Skills: nothing is permitted there, which is the B contract
        // for an empty set. Steps and tokens absent are zero; no window is no restriction beyond the deadline.
        var profile = Assert.Single(LoadRole(
            AgentRoleKind.Discovery,
            ("targets", null),
            ("environments", null),
            ("maxSteps", null),
            ("maxTokens", null)).RoleProfiles);

        Assert.Empty(profile.AllowedSkills);
        Assert.Empty(profile.AllowedCapabilities);
        Assert.Empty(profile.AllowedTargets);
        Assert.Empty(profile.AllowedEnvironments);
        Assert.Equal(0, profile.MaxSteps);
        Assert.Equal(0, profile.MaxTokens);
        Assert.Null(profile.Window);
    }

    [Fact]
    public void AListMayBeWrittenEitherWay()
    {
        var flow = Assert.Single(LoadRole(AgentRoleKind.Discovery, ("tools", "[a.one, a.two]")).RoleProfiles);
        var block = Assert.Single(LoadRole(AgentRoleKind.Discovery, ("tools", "\n        - a.one\n        - a.two")).RoleProfiles);

        Assert.Equal(["a.one", "a.two"], flow.AllowedTools);
        Assert.Equal(flow.AllowedTools, block.AllowedTools);
    }

    [Theory]
    [InlineData("2026-09-20T04:00:00Z", 4, 0, 0)]
    [InlineData("2026-09-20T06:00:00+02:00", 4, 0, 0)]
    [InlineData("2026-09-19T23:30:00-05:00", 4, 30, 0)]
    [InlineData("2026-09-20T04:00:00.5Z", 4, 0, 500)]
    public void AWindowIsAnInstantInUtcWhateverOffsetItIsWrittenIn(string end, int utcHour, int utcMinute, int milliseconds)
    {
        var yaml = Delegation(Block(
            AgentRoleKind.Discovery, ("window", $"\n        start: 2026-09-20T01:00:00Z\n        end: {end}")));

        var window = Assert.Single(PolicyConfigLoader.Load(yaml).RoleProfiles).Window!;

        Assert.Equal(TimeSpan.Zero, window.EndUtc.Offset);
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 1, 0, 0, TimeSpan.Zero), window.StartUtc);
        Assert.Equal(
            new DateTimeOffset(2026, 9, 20, utcHour, utcMinute, 0, TimeSpan.Zero).AddMilliseconds(milliseconds),
            window.EndUtc);
    }

    [Theory]
    [InlineData("00:10:00", 10 * 60)]
    [InlineData("\"00:10:00\"", 10 * 60)]
    [InlineData("01:00:00", 3600)]
    [InlineData("1.02:00:00", 26 * 3600)]
    [InlineData("00:00:30", 30)]
    public void ADurationIsWrittenInTheInvariantConstantFormat(string text, int seconds)
    {
        var profile = Assert.Single(LoadRole(AgentRoleKind.Discovery, ("maxDuration", text)).RoleProfiles);

        Assert.Equal(TimeSpan.FromSeconds(seconds), profile.MaxDuration);
    }

    // ---- ADR-0031 section 2: not applicable is malformed, exhaustively --------------------------------------

    // The ADR-0031 table, restated here on purpose so the loader is checked against the document and not only
    // against the code that implements it: Skills and Capabilities apply to Diagnostic (optionally) and
    // Remediation; tokens apply to Discovery and Diagnostic.
    private static bool SkillsApply(AgentRoleKind role) => role is AgentRoleKind.Diagnostic or AgentRoleKind.Remediation;

    private static bool TokensApply(AgentRoleKind role) => role is AgentRoleKind.Discovery or AgentRoleKind.Diagnostic;

    public static TheoryData<AgentRoleKind, bool, bool, bool> GrantCombinations()
    {
        var data = new TheoryData<AgentRoleKind, bool, bool, bool>();
        foreach (var role in Enum.GetValues<AgentRoleKind>())
        {
            foreach (var skills in Both)
            {
                foreach (var capabilities in Both)
                {
                    foreach (var tokens in Both)
                    {
                        data.Add(role, skills, capabilities, tokens);
                    }
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(GrantCombinations))]
    public void ANotApplicableGrantIsRefusedAndAnApplicableOneIsLoaded(AgentRoleKind role, bool skills, bool capabilities, bool tokens)
    {
        (string Key, string? Value)[] edits =
        [
            ("skills", skills ? "[sample.skill]" : "[]"),
            ("capabilities", capabilities ? "[sample.capability]" : "[]"),
            ("maxTokens", tokens ? "1000" : "0"),
        ];

        // The first violation in the profile's own order is the one reported.
        string? firstViolation = (skills && !SkillsApply(role)) ? "skills"
            : (capabilities && !SkillsApply(role)) ? "capabilities"
            : (tokens && !TokensApply(role)) ? "maxTokens"
            : null;

        if (firstViolation is null)
        {
            var profile = Assert.Single(LoadRole(role, edits).RoleProfiles);
            Assert.Equal(skills, profile.AllowedSkills.Count != 0);
            Assert.Equal(capabilities, profile.AllowedCapabilities.Count != 0);
            Assert.Equal(tokens, profile.MaxTokens != 0);
            return;
        }

        var ex = Rejected(Delegation(Block(role, edits)));
        Assert.Contains($"{RolePath(role)}.{firstViolation}", ex.Message, StringComparison.Ordinal);
        Assert.Contains("not applicable", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(GrantCombinations))]
    public void TheLoaderRefusesExactlyWhatTheProfileConstructorRefuses(AgentRoleKind role, bool skills, bool capabilities, bool tokens)
    {
        var constructorAccepts = true;
        try
        {
            _ = new RoleProfile(
                role,
                skills ? ["sample.skill"] : [],
                capabilities ? ["sample.capability"] : [],
                ["sample.tool"],
                RiskLevel.Read,
                BlastRadius.Single,
                ["host-a"],
                ["staging"],
                MaxSteps: 1,
                MaxTokens: tokens ? 1 : 0,
                MaxDuration: TimeSpan.FromMinutes(1));
        }
        catch (ArgumentException)
        {
            constructorAccepts = false;
        }

        var loaderAccepts = true;
        try
        {
            _ = LoadRole(
                role,
                ("skills", skills ? "[sample.skill]" : "[]"),
                ("capabilities", capabilities ? "[sample.capability]" : "[]"),
                ("maxTokens", tokens ? "1" : "0"));
        }
        catch (PolicyConfigurationException)
        {
            loaderAccepts = false;
        }

        Assert.Equal(constructorAccepts, loaderAccepts);
    }

    [Theory]
    [InlineData(AgentRoleKind.Discovery)]
    [InlineData(AgentRoleKind.Verification)]
    public void AnExplicitEmptyGrantInANotApplicableDimensionIsFine(AgentRoleKind role)
    {
        var profile = Assert.Single(LoadRole(role, ("skills", "[]"), ("capabilities", "[]")).RoleProfiles);

        Assert.Empty(profile.AllowedSkills);
        Assert.Empty(profile.AllowedCapabilities);
    }

    [Fact]
    public void AProfileThatCanNeverBeUsedIsStillWellFormed()
    {
        // ADR-0031 section 3: a required budget granted as zero is a denial when delegation starts (C1's
        // reduction), not a malformed file. The loader only refuses what cannot be built.
        var profile = Assert.Single(LoadRole(AgentRoleKind.Discovery, ("maxSteps", "0"), ("maxTokens", "0"), ("tools", "[]")).RoleProfiles);

        Assert.Equal(0, profile.MaxSteps);
        Assert.Empty(profile.AllowedTools);
    }

    // ---- malformed values in a profile ---------------------------------------------------------------------

    [Theory]
    [InlineData("maxRisk", "reed")]
    [InlineData("maxRisk", "3")]
    [InlineData("maxRisk", "read, low")]
    [InlineData("maxRisk", "\"\"")]
    [InlineData("maxRisk", "")]
    [InlineData("maxRisk", null)]
    [InlineData("maxBlastRadius", "huge")]
    [InlineData("maxBlastRadius", "single, fleet")]
    [InlineData("maxBlastRadius", "2")]
    [InlineData("maxBlastRadius", null)]
    [InlineData("maxDuration", null)]
    [InlineData("maxDuration", "10")]
    [InlineData("maxDuration", "90")]
    [InlineData("maxDuration", "10m")]
    [InlineData("maxDuration", "abc")]
    [InlineData("maxDuration", "00:00:00")]
    [InlineData("maxDuration", "-00:10:00")]
    [InlineData("maxDuration", "")]
    [InlineData("maxSteps", "-1")]
    [InlineData("maxSteps", "+5")]
    [InlineData("maxSteps", "many")]
    [InlineData("maxSteps", "1.5")]
    [InlineData("maxSteps", "1 000")]
    [InlineData("maxSteps", "99999999999")]
    [InlineData("maxSteps", "")]
    [InlineData("maxTokens", "-5")]
    [InlineData("maxTokens", "lots")]
    [InlineData("maxTokens", "99999999999")]
    [InlineData("tools", "host.info")]
    [InlineData("tools", "")]
    [InlineData("tools", "{a: b}")]
    [InlineData("tools", "[[a]]")]
    [InlineData("tools", "[{a: b}]")]
    [InlineData("tools", "[~]")]
    [InlineData("tools", "[null]")]
    [InlineData("tools", "[\"*\"]")]
    [InlineData("tools", "[\"host.*\"]")]
    [InlineData("tools", "[\"host.?\"]")]
    [InlineData("tools", "[\"\"]")]
    [InlineData("tools", "[\"   \"]")]
    [InlineData("tools", "[\" host.info\"]")]
    [InlineData("tools", "[\"host.info \"]")]
    [InlineData("targets", "[\"*\"]")]
    [InlineData("targets", "host-a")]
    [InlineData("environments", "[\"*\"]")]
    [InlineData("environments", "staging")]
    [InlineData("skills", "sample.skill")]
    [InlineData("capabilities", "[\"*\"]")]
    [InlineData("window", "later")]
    [InlineData("window", "")]
    [InlineData("window", "[a, b]")]
    [InlineData("window", "\n        start: 2026-09-20T02:00:00Z")]
    [InlineData("window", "\n        end: 2026-09-20T04:00:00Z")]
    [InlineData("window", "\n        start: 2026-09-20T04:00:00Z\n        end: 2026-09-20T02:00:00Z")]
    [InlineData("window", "\n        start: 2026-09-20T02:00:00Z\n        end: 2026-09-20T02:00:00Z")]
    [InlineData("window", "\n        start: 2026-09-20T02:00:00\n        end: 2026-09-20T04:00:00")]
    [InlineData("window", "\n        start: tomorrow\n        end: 2026-09-20T04:00:00Z")]
    [InlineData("window", "\n        start: 2026-09-20\n        end: 2026-09-21")]
    [InlineData("window", "\n        start: 2026-09-20T02:00:00Z\n        end: 2026-09-20T04:00:00Z\n        extra: 1")]
    [InlineData("windw", "\n        start: 2026-09-20T02:00:00Z\n        end: 2026-09-20T04:00:00Z")]
    [InlineData("allowedTools", "[a]")]
    [InlineData("Tools", "[a]")]
    [InlineData("role", "discovery")]
    public void AMalformedValueIsRefusedNamingTheRoleAndTheKey(string key, string? value)
    {
        var ex = Rejected(Delegation(Block(AgentRoleKind.Discovery, (key, value))));

        Assert.Contains(RolePath(AgentRoleKind.Discovery), ex.Message, StringComparison.Ordinal);
        Assert.Contains(key, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownKeyIsNotSilentlyIgnored()
    {
        // The reason for strictness here: a misspelt `window` would otherwise load as no restriction at all.
        var ex = Rejected(Delegation(Block(AgentRoleKind.Remediation, ("windw", "{}"))));

        Assert.Contains("unknown", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("windw", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusalKeepsTheConstructorsReasonAsInnerException()
    {
        var ex = Rejected(Delegation(Block(AgentRoleKind.Discovery, ("tools", "[\"host.*\"]"))));

        Assert.IsAssignableFrom<ArgumentException>(ex.InnerException);
        Assert.Contains("tools", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusalSaysWhereInTheFileTheProblemIs()
    {
        var yaml = "defaults:\n  read: automatic\n" + Delegation(Block(AgentRoleKind.Discovery, ("maxRisk", "reed")));

        var ex = Rejected(yaml);

        // Any 1-based line will do here; which one depends on the layout of the base profile.
        Assert.Matches(@"line \d+", ex.Message);
    }

    // ---- malformed structure -------------------------------------------------------------------------------

    [Theory]
    [InlineData("delegation: 5\n")]
    [InlineData("delegation:\n")]
    [InlineData("delegation: ~\n")]
    [InlineData("delegation: [discovery]\n")]
    [InlineData("delegation:\n  rolez: {}\n")]
    [InlineData("delegation:\n  roles:\n  extra: 1\n")]
    [InlineData("delegation:\n  roles: 5\n")]
    [InlineData("delegation:\n  roles:\n")]
    [InlineData("delegation:\n  roles: [discovery]\n")]
    [InlineData("delegation:\n  roles:\n    discovery: 5\n")]
    [InlineData("delegation:\n  roles:\n    discovery:\n")]
    [InlineData("delegation:\n  roles:\n    discovery: [a]\n")]
    [InlineData("delegation:\n  roles:\n    orchestrator:\n      tools: [a]\n")]
    [InlineData("delegation:\n  roles:\n    discoverer:\n      tools: [a]\n")]
    [InlineData("delegation:\n  roles:\n    \"0\":\n      tools: [a]\n")]
    [InlineData("delegation:\n  roles:\n    \"discovery, diagnostic\":\n      tools: [a]\n")]
    [InlineData("delegation:\n  roles:\n    ? [a]\n    : {}\n")]
    public void AMalformedShapeIsRefusedWholeNamingTheSection(string yaml)
    {
        var ex = Rejected(yaml);

        Assert.Contains("delegation", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameRoleTwiceIsRefusedWhateverItsCase()
    {
        var yaml = Delegation(Block("discovery", Body(AgentRoleKind.Discovery, [])), Block("Discovery", Body(AgentRoleKind.Discovery, [])));

        var ex = Rejected(yaml);

        Assert.Contains("discovery", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheSameRoleWrittenTwiceWithTheSameNameIsRefused()
    {
        var yaml = Delegation(Block(AgentRoleKind.Discovery), Block(AgentRoleKind.Discovery));

        Rejected(yaml);
    }

    [Fact]
    public void TheSameKeyTwiceInAProfileIsRefused()
    {
        var yaml = Delegation(Block(AgentRoleKind.Discovery)) + "      maxRisk: low\n";

        Rejected(yaml);
    }

    [Fact]
    public void TheSectionTwiceIsRefused()
    {
        Rejected(AllFour() + AllFour());
    }

    [Fact]
    public void OneBadRoleRefusesTheWholeSectionNotJustThatRole()
    {
        // ADR-0031 section 2: loading a malformed profile denies delegation as a whole. There is no partial
        // section in which the three good roles load and the bad one is skipped.
        var yaml = Delegation(
            Block(AgentRoleKind.Discovery),
            Block(AgentRoleKind.Diagnostic),
            Block(AgentRoleKind.Remediation),
            Block(AgentRoleKind.Verification, ("skills", "[sample.skill]")));

        Rejected(yaml);
    }

    [Fact]
    public void AMalformedSectionIsRefusedEvenWhenTheRestOfThePolicyIsFine()
    {
        var yaml = "defaults:\n  read: automatic\n" + Delegation(Block(AgentRoleKind.Discovery, ("maxRisk", "reed")));

        Rejected(yaml);
    }

    [Fact]
    public void TheDelegationSectionCannotSmuggleInACriticalGrantThroughTheRiskName()
    {
        // `critical` is a valid ceiling and changes nothing (ADR-0031 section 1: Critical stays Forbidden), so it
        // loads; what must never load is a value that only parses because .NET reads "low, medium" as a flag set.
        var profile = Assert.Single(LoadRole(AgentRoleKind.Remediation, ("maxRisk", "critical")).RoleProfiles);
        Assert.Equal(RiskLevel.Critical, profile.MaxRisk);

        Rejected(Delegation(Block(AgentRoleKind.Remediation, ("maxRisk", "low, medium"))));
    }

    [Fact]
    public void AMalformedYamlDocumentStillFailsAsBefore()
    {
        Assert.Throws<PolicyConfigurationException>(() => PolicyConfigLoader.Load("delegation: [this is not a mapping"));
    }
}
