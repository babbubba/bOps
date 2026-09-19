// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// ADR-0030 section 3 and ADR-0031 section 4 (V1.2-C2): what the authority envelope says about one step, on its
/// own, without a runner. The envelope can only deny, so every case is either "allowed, nothing to say" or
/// "refused, and in which dimension". The runner's use of the answer is in <see cref="DelegatedExecutionTests"/>.
/// </summary>
public sealed class EnvelopeEnforcementTests
{
    private static readonly ActorIdentity Operator = new("os-user", "alice", "Alice");
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    // ---- fixtures ----

    /// <summary>A Remediation-shaped role envelope (depth 1) that the step from <see cref="Step"/> fits inside.</summary>
    private static AuthorityEnvelope Envelope(
        IReadOnlyList<string>? skills = null,
        IReadOnlyList<string>? capabilities = null,
        IReadOnlyList<string>? tools = null,
        RiskLevel risk = RiskLevel.High,
        BlastRadius blast = BlastRadius.Multiple,
        IReadOnlyList<string>? targets = null,
        IReadOnlyList<string>? environments = null,
        MaintenanceWindow? window = null,
        int depth = 1,
        ActorIdentity? originator = null) =>
        new(
            originator ?? Operator,
            depth,
            skills ?? ["system.skill"],
            capabilities ?? ["system.remediate"],
            tools ?? ["service.restart", "system.read"],
            risk,
            blast,
            targets ?? ["node-1"],
            environments ?? ["staging"],
            new DelegationBudget(5, 0, Now.AddMinutes(30)),
            window);

    private static DelegatedExecutionScope ScopeOf(AuthorityEnvelope envelope, AgentRoleKind role = AgentRoleKind.Remediation) =>
        DelegatedExecutionScope.For(Guid.NewGuid(), new AgentIdentity(AgentId.New(), role), envelope);

    private static ToolManifest Tool(string name, RiskLevel risk) => new()
    {
        Name = name,
        Description = "A tool for envelope tests.",
        Risk = risk,
        Platforms = [],
        Requires = [],
        Parameters = [],
    };

    private static SkillExecutionScope Skill(
        string skillId = "system.skill",
        string capability = "system.remediate",
        string target = "node-1",
        string environment = "staging",
        BlastRadius blast = BlastRadius.Single) =>
        new(Guid.NewGuid(), skillId, capability, target, environment, blast, PlanHash: null);

    /// <summary>The step every test starts from: an approved-plan step, inside <see cref="Envelope"/> in every dimension.</summary>
    private static EnvelopeRefusal? CheckStep(
        DelegatedExecutionScope scope,
        ToolManifest? tool = null,
        SkillExecutionScope? skill = null,
        ActorIdentity? actor = null,
        DateTimeOffset? now = null,
        bool withoutSkill = false) =>
        EnvelopeEnforcer.CheckStep(
            scope, actor ?? Operator, tool ?? Tool("service.restart", RiskLevel.High), withoutSkill ? null : skill ?? Skill(), now ?? Now);

    private static EnvelopeRefusal Refused(EnvelopeRefusal? refusal)
    {
        Assert.NotNull(refusal);
        return refusal;
    }

    // ---- the baseline ----

    [Fact]
    public void AStepInsideEveryDimensionIsAllowed() =>
        Assert.Null(CheckStep(ScopeOf(Envelope())));

    [Fact]
    public void AReadCallWithoutASkillScopeIsAllowedWhenTheToolIsListed() =>
        Assert.Null(CheckStep(ScopeOf(Envelope()), Tool("system.read", RiskLevel.Read), withoutSkill: true));

    [Fact]
    public void ARefusalNamesTheRole()
    {
        var refusal = Refused(CheckStep(ScopeOf(Envelope(), AgentRoleKind.Remediation), Tool("service.stop", RiskLevel.High)));

        Assert.Contains("Remediation", refusal.Reason, StringComparison.Ordinal);
    }

    // ---- the scope itself must be usable: no envelope, no run ----

    [Fact]
    public void AScopeThatNamesAnAgentButCarriesNoEnvelopeRefusesEveryStep()
    {
        var agent = new AgentIdentity(AgentId.New(), AgentRoleKind.Remediation);
        var scope = new DelegatedExecutionScope(new DelegationCorrelation(Guid.NewGuid(), "0123abcd", agent), envelope: null);

        var refusal = Refused(CheckStep(scope));

        Assert.Null(refusal.Dimension);
        Assert.Contains("envelope", refusal.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AScopeWithoutAnEnvelopeRefusesEvenAReadCall()
    {
        var agent = new AgentIdentity(AgentId.New(), AgentRoleKind.Discovery);
        var scope = new DelegatedExecutionScope(new DelegationCorrelation(Guid.NewGuid(), "0123abcd", agent), envelope: null);

        Assert.Null(Refused(CheckStep(scope, Tool("system.read", RiskLevel.Read), withoutSkill: true)).Dimension);
    }

    [Fact]
    public void AnEnvelopeThatIsNotTheOneTheCorrelationRecordsIsRefused()
    {
        var recorded = Envelope(tools: ["system.read"]);
        var supplied = Envelope();
        var agent = new AgentIdentity(AgentId.New(), AgentRoleKind.Remediation);
        var scope = new DelegatedExecutionScope(
            new DelegationCorrelation(Guid.NewGuid(), DelegationHasher.ComputeEnvelopeHash(recorded), agent), supplied);

        var refusal = Refused(CheckStep(scope));

        Assert.Null(refusal.Dimension);
        Assert.Contains("does not match", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AScopeWithNoAgentRefusesEveryStep()
    {
        var envelope = Envelope();
        var scope = new DelegatedExecutionScope(
            new DelegationCorrelation(Guid.NewGuid(), DelegationHasher.ComputeEnvelopeHash(envelope), Agent: null), envelope);

        var refusal = Refused(CheckStep(scope));

        Assert.Null(refusal.Dimension);
        Assert.Contains("agent", refusal.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(7)]
    public void AnEnvelopeThatIsNotAtTheFixedRoleDepthIsRefused(int depth)
    {
        var refusal = Refused(CheckStep(ScopeOf(Envelope(depth: depth))));

        Assert.Equal(EnvelopeDimension.Depth, refusal.Dimension);
    }

    [Theory]
    [InlineData("os-user", "mallory")]
    [InlineData("service", "alice")]
    public void AnEnvelopeGrantedToAnotherOperatorIsRefused(string kind, string id)
    {
        var refusal = Refused(CheckStep(ScopeOf(Envelope()), actor: new ActorIdentity(kind, id, "Alice")));

        Assert.Equal(EnvelopeDimension.Originator, refusal.Dimension);
    }

    [Fact]
    public void TheOperatorsDisplayNameIsCosmeticAndDoesNotMatter() =>
        Assert.Null(CheckStep(ScopeOf(Envelope()), actor: new ActorIdentity("os-user", "alice", "A different display name")));

    // ---- tools ----

    [Theory]
    [InlineData("service.stop")]
    [InlineData("Service.Restart")]
    [InlineData("service.restart ")]
    [InlineData("")]
    public void AToolThatIsNotListedIsRefused(string toolName)
    {
        var refusal = Refused(CheckStep(ScopeOf(Envelope()), Tool(toolName, RiskLevel.High)));

        Assert.Equal(EnvelopeDimension.Tools, refusal.Dimension);
    }

    [Fact]
    public void AnEmptyToolSetPermitsNothing_NotEvenARead()
    {
        var scope = ScopeOf(Envelope(tools: []));

        Assert.Equal(EnvelopeDimension.Tools, Refused(CheckStep(scope, Tool("system.read", RiskLevel.Read), withoutSkill: true)).Dimension);
    }

    // ---- risk ----

    public static TheoryData<RiskLevel, RiskLevel, bool> CeilingAndToolRisk()
    {
        var data = new TheoryData<RiskLevel, RiskLevel, bool>();
        foreach (var ceiling in Enum.GetValues<RiskLevel>())
        {
            foreach (var risk in Enum.GetValues<RiskLevel>())
            {
                // Critical never executes, whatever the ceiling says (S3).
                data.Add(ceiling, risk, risk <= ceiling && risk != RiskLevel.Critical);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CeilingAndToolRisk))]
    public void ARiskAboveTheCeilingIsRefusedAndCriticalIsNeverAllowed(RiskLevel ceiling, RiskLevel risk, bool allowed)
    {
        var scope = ScopeOf(Envelope(risk: ceiling, tools: ["a.tool"]));
        var step = risk == RiskLevel.Read
            ? CheckStep(scope, Tool("a.tool", risk), withoutSkill: true)
            : CheckStep(scope, Tool("a.tool", risk));

        if (allowed)
        {
            Assert.Null(step);
        }
        else
        {
            Assert.Equal(EnvelopeDimension.Risk, Refused(step).Dimension);
        }
    }

    [Theory]
    [InlineData(AgentRoleKind.Discovery)]
    [InlineData(AgentRoleKind.Diagnostic)]
    [InlineData(AgentRoleKind.Verification)]
    public void AReadOnlyRoleNeverExceedsReadEvenWithAnEnvelopeThatSaysOtherwise(AgentRoleKind role)
    {
        // The reduction never builds such an envelope (RoleRequirements.RiskCap), but a value handed in by
        // mistake must not turn a read-only role into one that acts.
        var scope = ScopeOf(Envelope(risk: RiskLevel.High), role);

        Assert.Equal(EnvelopeDimension.Risk, Refused(CheckStep(scope, Tool("service.restart", RiskLevel.Low))).Dimension);
        Assert.Null(CheckStep(scope, Tool("system.read", RiskLevel.Read), withoutSkill: true));
    }

    // ---- a step above Read is only ever a step of an approved plan ----

    [Theory]
    [InlineData(RiskLevel.Low)]
    [InlineData(RiskLevel.Medium)]
    [InlineData(RiskLevel.High)]
    public void ANonReadStepWithoutASkillScopeIsRefused(RiskLevel risk)
    {
        var scope = ScopeOf(Envelope(tools: ["service.restart"]));

        var refusal = Refused(CheckStep(scope, Tool("service.restart", risk), withoutSkill: true));

        Assert.Equal(EnvelopeDimension.Skills, refusal.Dimension);
        Assert.Contains("Skill scope", refusal.Reason, StringComparison.Ordinal);
    }

    // ---- the maintenance window, at execution ----

    [Fact]
    public void NoWindowMeansNoTimeRestrictionBeyondTheBudget() =>
        Assert.Null(CheckStep(ScopeOf(Envelope(window: null)), now: Now.AddYears(5)));

    public static TheoryData<int, bool> MinutesFromNowAndAllowed() => new()
    {
        { -61, false },
        { -60, true },   // the start is inclusive
        { 0, true },
        { 59, true },
        { 60, false },   // the end is exclusive
        { 61, false },
    };

    [Theory]
    [MemberData(nameof(MinutesFromNowAndAllowed))]
    public void TheWindowIsCheckedAtExecution_StartInclusiveEndExclusive(int minutesFromNow, bool allowed)
    {
        var window = new MaintenanceWindow(Now.AddMinutes(-60), Now.AddMinutes(60));
        var refusal = CheckStep(ScopeOf(Envelope(window: window)), now: Now.AddMinutes(minutesFromNow));

        if (allowed)
        {
            Assert.Null(refusal);
        }
        else
        {
            Assert.Equal(EnvelopeDimension.MaintenanceWindow, Refused(refusal).Dimension);
        }
    }

    [Fact]
    public void TheWindowAlsoBindsAReadCallWithoutASkillScope()
    {
        var window = new MaintenanceWindow(Now.AddMinutes(10), Now.AddMinutes(20));

        var refusal = CheckStep(ScopeOf(Envelope(window: window)), Tool("system.read", RiskLevel.Read), withoutSkill: true);

        Assert.Equal(EnvelopeDimension.MaintenanceWindow, Refused(refusal).Dimension);
    }

    [Fact]
    public void TheWindowIsComparedInUtcWhateverOffsetTheInstantsCarry()
    {
        var window = new MaintenanceWindow(new DateTimeOffset(2026, 9, 19, 14, 0, 0, TimeSpan.FromHours(2)), new DateTimeOffset(2026, 9, 19, 15, 0, 0, TimeSpan.FromHours(2)));

        // 12:30 UTC is 14:30 at +02:00, inside the window.
        Assert.Null(CheckStep(ScopeOf(Envelope(window: window)), now: new DateTimeOffset(2026, 9, 19, 12, 30, 0, TimeSpan.Zero)));
        // 14:30 UTC is 16:30 at +02:00, after it.
        Assert.Equal(
            EnvelopeDimension.MaintenanceWindow,
            Refused(CheckStep(ScopeOf(Envelope(window: window)), now: new DateTimeOffset(2026, 9, 19, 14, 30, 0, TimeSpan.Zero))).Dimension);
    }

    // ---- the Skill scope: what a step of a Skill run declares ----

    public static TheoryData<string, string, string, string, BlastRadius, EnvelopeDimension> SkillScopeViolations() => new()
    {
        { "other.skill", "system.remediate", "node-1", "staging", BlastRadius.Single, EnvelopeDimension.Skills },
        { "System.Skill", "system.remediate", "node-1", "staging", BlastRadius.Single, EnvelopeDimension.Skills },
        { "system.skill", "other.capability", "node-1", "staging", BlastRadius.Single, EnvelopeDimension.Capabilities },
        { "system.skill", "System.Remediate", "node-1", "staging", BlastRadius.Single, EnvelopeDimension.Capabilities },
        { "system.skill", "system.remediate", "node-1", "staging", BlastRadius.Fleet, EnvelopeDimension.BlastRadius },
        { "system.skill", "system.remediate", "node-2", "staging", BlastRadius.Single, EnvelopeDimension.Targets },
        { "system.skill", "system.remediate", "Node-1", "staging", BlastRadius.Single, EnvelopeDimension.Targets },
        { "system.skill", "system.remediate", "node-1", "production", BlastRadius.Single, EnvelopeDimension.Environments },
        { "system.skill", "system.remediate", "node-1", "Staging", BlastRadius.Single, EnvelopeDimension.Environments },
    };

    [Theory]
    [MemberData(nameof(SkillScopeViolations))]
    public void AStepWhoseSkillScopeIsOutsideTheEnvelopeIsRefusedInThatDimension(
        string skillId, string capability, string target, string environment, BlastRadius blast, EnvelopeDimension expected)
    {
        var refusal = Refused(CheckStep(
            ScopeOf(Envelope()), skill: Skill(skillId, capability, target, environment, blast)));

        Assert.Equal(expected, refusal.Dimension);
    }

    [Theory]
    [InlineData(BlastRadius.Single)]
    [InlineData(BlastRadius.Multiple)]
    public void ABlastRadiusUpToTheCeilingIsAllowed(BlastRadius blast) =>
        Assert.Null(CheckStep(ScopeOf(Envelope(blast: BlastRadius.Multiple)), skill: Skill(blast: blast)));

    [Theory]
    [InlineData(EnvelopeDimension.Skills)]
    [InlineData(EnvelopeDimension.Capabilities)]
    [InlineData(EnvelopeDimension.Targets)]
    [InlineData(EnvelopeDimension.Environments)]
    public void AnEmptySetPermitsNothing_ItIsNeverUnrestricted(EnvelopeDimension emptied)
    {
        var envelope = Envelope(
            skills: emptied == EnvelopeDimension.Skills ? [] : null,
            capabilities: emptied == EnvelopeDimension.Capabilities ? [] : null,
            targets: emptied == EnvelopeDimension.Targets ? [] : null,
            environments: emptied == EnvelopeDimension.Environments ? [] : null);

        Assert.Equal(emptied, Refused(CheckStep(ScopeOf(envelope))).Dimension);
    }

    [Theory]
    [InlineData(AgentRoleKind.Discovery)]
    [InlineData(AgentRoleKind.Verification)]
    public void ARoleForWhichSkillsAreNotApplicableCannotRunASkillScopedStep(AgentRoleKind role)
    {
        // What the reduction hands these roles: no Skills and no Capabilities at all (ADR-0031 section 1).
        var envelope = Envelope(skills: [], capabilities: [], risk: RiskLevel.Read, blast: BlastRadius.Single);

        var refusal = Refused(CheckStep(ScopeOf(envelope, role), Tool("system.read", RiskLevel.Read)));

        Assert.Equal(EnvelopeDimension.Skills, refusal.Dimension);
    }

    // ---- a step is refused for the first dimension that fails, in one fixed order ----

    [Fact]
    public void WhenSeveralDimensionsFailTheFirstInTheFixedOrderIsReported()
    {
        var window = new MaintenanceWindow(Now.AddHours(1), Now.AddHours(2));
        var scope = ScopeOf(Envelope(window: window, blast: BlastRadius.Single));
        var everythingWrong = Skill("other.skill", "other.capability", "node-9", "production", BlastRadius.Fleet);

        // Order: the scope, the step's shape, Tools, Risk, the window, then Skill, Capability, blast radius, target, environment.
        Assert.Equal(EnvelopeDimension.Depth, Refused(CheckStep(ScopeOf(Envelope(window: window, depth: 3)), Tool("service.stop", RiskLevel.Critical), everythingWrong)).Dimension);
        Assert.Equal(EnvelopeDimension.Skills, Refused(CheckStep(scope, Tool("service.stop", RiskLevel.Critical), withoutSkill: true)).Dimension);
        Assert.Equal(EnvelopeDimension.Tools, Refused(CheckStep(scope, Tool("service.stop", RiskLevel.Critical), everythingWrong)).Dimension);
        Assert.Equal(EnvelopeDimension.Risk, Refused(CheckStep(scope, Tool("service.restart", RiskLevel.Critical), everythingWrong)).Dimension);
        Assert.Equal(EnvelopeDimension.MaintenanceWindow, Refused(CheckStep(scope, Tool("service.restart", RiskLevel.Low), everythingWrong)).Dimension);

        var open = ScopeOf(Envelope(blast: BlastRadius.Single));
        Assert.Equal(EnvelopeDimension.Skills, Refused(CheckStep(open, skill: everythingWrong)).Dimension);
        Assert.Equal(EnvelopeDimension.Capabilities, Refused(CheckStep(open, skill: Skill(capability: "other.capability", target: "node-9", environment: "production", blast: BlastRadius.Fleet))).Dimension);
        Assert.Equal(EnvelopeDimension.BlastRadius, Refused(CheckStep(open, skill: Skill(target: "node-9", environment: "production", blast: BlastRadius.Fleet))).Dimension);
        Assert.Equal(EnvelopeDimension.Targets, Refused(CheckStep(open, skill: Skill(target: "node-9", environment: "production"))).Dimension);
        Assert.Equal(EnvelopeDimension.Environments, Refused(CheckStep(open, skill: Skill(environment: "production"))).Dimension);
    }

    // ---- what a refusal says ----

    [Fact]
    public void ARefusalNeverListsWhatTheEnvelopeDoesAllow()
    {
        // The reason reaches the model as a tool observation and the audit log. It may name what the step asked
        // for, which the model already knows, and never what the envelope grants (the "9F3" marker).
        var envelope = Envelope(
            skills: ["granted.skill-9F3"], capabilities: ["granted.capability-9F3"], tools: ["granted.tool-9F3", "asked.action"],
            targets: ["granted.target-9F3"], environments: ["granted.environment-9F3"]);
        var scope = ScopeOf(envelope);
        var closed = ScopeOf(Envelope(
            skills: ["granted.skill-9F3"], capabilities: ["granted.capability-9F3"], tools: ["granted.tool-9F3"],
            targets: ["granted.target-9F3"], environments: ["granted.environment-9F3"],
            window: new MaintenanceWindow(Now.AddHours(1), Now.AddHours(2))));
        var grantedSkill = Skill("granted.skill-9F3", "granted.capability-9F3", "granted.target-9F3", "granted.environment-9F3");

        var reasons = new[]
        {
            Refused(CheckStep(scope, Tool("asked.tool", RiskLevel.High))).Reason,
            Refused(CheckStep(scope, Tool("asked.action", RiskLevel.Critical))).Reason,
            Refused(CheckStep(scope, Tool("granted.tool-9F3", RiskLevel.Read), Skill("asked.skill", "asked.capability", "asked.target", "asked.env"))).Reason,
            Refused(CheckStep(scope, Tool("granted.tool-9F3", RiskLevel.Read), Skill("granted.skill-9F3", "asked.capability", "asked.target", "asked.env"))).Reason,
            Refused(CheckStep(scope, Tool("granted.tool-9F3", RiskLevel.Read), Skill("granted.skill-9F3", "granted.capability-9F3", "asked.target", "asked.env"))).Reason,
            Refused(CheckStep(scope, Tool("granted.tool-9F3", RiskLevel.Read), Skill("granted.skill-9F3", "granted.capability-9F3", "granted.target-9F3", "asked.env"))).Reason,
            Refused(CheckStep(scope, Tool("asked.action", RiskLevel.High), withoutSkill: true)).Reason,
            Refused(CheckStep(closed, Tool("granted.tool-9F3", RiskLevel.High), grantedSkill)).Reason,
        };

        foreach (var reason in reasons)
        {
            Assert.DoesNotContain("9F3", reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryRefusalCarriesAReason()
    {
        var scope = ScopeOf(Envelope());
        var refusals = new[]
        {
            CheckStep(scope, Tool("service.stop", RiskLevel.High)),
            CheckStep(scope, Tool("service.restart", RiskLevel.Critical)),
            CheckStep(scope, withoutSkill: true),
            CheckStep(scope, skill: Skill("other.skill")),
            CheckStep(scope, actor: new ActorIdentity("os-user", "mallory", null)),
        };

        Assert.All(refusals, refusal => Assert.False(string.IsNullOrWhiteSpace(Refused(refusal).Reason)));
    }

    // ---- preparing a Capability ----

    private static EnvelopeRefusal? CheckPreparation(
        DelegatedExecutionScope scope, SkillExecutionScope? skill = null, ActorIdentity? actor = null, DateTimeOffset? now = null) =>
        EnvelopeEnforcer.CheckPreparation(scope, actor ?? Operator, skill ?? Skill(), now ?? Now);

    [Fact]
    public void PreparingACapabilityInsideTheEnvelopeIsAllowedWithoutNamingATool() =>
        Assert.Null(CheckPreparation(ScopeOf(Envelope(tools: []))));

    [Theory]
    [MemberData(nameof(SkillScopeViolations))]
    public void PreparingACapabilityOutsideTheEnvelopeIsRefusedInThatDimension(
        string skillId, string capability, string target, string environment, BlastRadius blast, EnvelopeDimension expected)
    {
        var refusal = Refused(CheckPreparation(ScopeOf(Envelope()), Skill(skillId, capability, target, environment, blast)));

        Assert.Equal(expected, refusal.Dimension);
    }

    [Fact]
    public void PreparingIsRefusedOutsideTheWindow()
    {
        var window = new MaintenanceWindow(Now.AddMinutes(10), Now.AddMinutes(20));

        Assert.Equal(EnvelopeDimension.MaintenanceWindow, Refused(CheckPreparation(ScopeOf(Envelope(window: window)))).Dimension);
    }

    [Fact]
    public void PreparingIsRefusedForAnUnusableScope()
    {
        var agent = new AgentIdentity(AgentId.New(), AgentRoleKind.Diagnostic);
        var noEnvelope = new DelegatedExecutionScope(new DelegationCorrelation(Guid.NewGuid(), "0123abcd", agent), envelope: null);

        Assert.Null(Refused(CheckPreparation(noEnvelope)).Dimension);
        Assert.Equal(EnvelopeDimension.Depth, Refused(CheckPreparation(ScopeOf(Envelope(depth: 0)))).Dimension);
        Assert.Equal(EnvelopeDimension.Originator, Refused(CheckPreparation(ScopeOf(Envelope()), actor: new ActorIdentity("os-user", "mallory", null))).Dimension);
    }

    // ---- the API itself ----

    [Fact]
    public void ArgumentsAreValidated()
    {
        var scope = ScopeOf(Envelope());

        Assert.Throws<ArgumentNullException>(() => EnvelopeEnforcer.CheckStep(null!, Operator, Tool("a", RiskLevel.Read), null, Now));
        Assert.Throws<ArgumentNullException>(() => EnvelopeEnforcer.CheckStep(scope, null!, Tool("a", RiskLevel.Read), null, Now));
        Assert.Throws<ArgumentNullException>(() => EnvelopeEnforcer.CheckStep(scope, Operator, null!, null, Now));
        Assert.Throws<ArgumentNullException>(() => EnvelopeEnforcer.CheckPreparation(null!, Operator, Skill(), Now));
        Assert.Throws<ArgumentNullException>(() => EnvelopeEnforcer.CheckPreparation(scope, null!, Skill(), Now));
        Assert.Throws<ArgumentNullException>(() => EnvelopeEnforcer.CheckPreparation(scope, Operator, null!, Now));
    }

    [Fact]
    public void AScopeNeedsACorrelation() =>
        Assert.Throws<ArgumentNullException>(() => new DelegatedExecutionScope(null!, null));

    [Fact]
    public void AScopeBuiltFromAnEnvelopeCarriesItsHash()
    {
        var envelope = Envelope();
        var agent = new AgentIdentity(AgentId.New(), AgentRoleKind.Remediation);
        var id = Guid.NewGuid();

        var scope = DelegatedExecutionScope.For(id, agent, envelope);

        Assert.Same(envelope, scope.Envelope);
        Assert.Equal(id, scope.Correlation.DelegationId);
        Assert.Equal(agent, scope.Correlation.Agent);
        Assert.Equal(DelegationHasher.ComputeEnvelopeHash(envelope), scope.Correlation.EnvelopeHash);
    }

    // ---- a property test: allowed exactly when every dimension is inside ----

    private static readonly string[] SkillUniverse = ["s1", "s2", "s3"];
    private static readonly string[] CapabilityUniverse = ["c1", "c2", "c3"];
    private static readonly string[] ToolUniverse = ["t1", "t2", "t3", "t4"];
    private static readonly string[] TargetUniverse = ["n1", "n2", "n3"];
    private static readonly string[] EnvironmentUniverse = ["e1", "e2", "e3"];

    private static string[] Subset(DeterministicRandom rng, string[] universe) =>
        [.. universe.Where(_ => rng.Next(4) != 0)];

    // Mostly a member of what the envelope grants, sometimes anything at all: a step drawn only from the universe
    // would almost never be inside every dimension at once, and the property would say nothing about "allowed".
    private static string Pick(DeterministicRandom rng, IReadOnlyList<string> granted, string[] universe) =>
        granted.Count > 0 && rng.Next(4) != 0 ? granted[rng.Next(granted.Count)] : universe[rng.Next(universe.Length)];

    [Fact]
    public void AStepIsAllowedExactlyWhenEveryDimensionIsInside()
    {
        var rng = new DeterministicRandom(0x5EEDC2);
        var roles = Enum.GetValues<AgentRoleKind>();
        var allowed = 0;
        var refused = 0;

        for (var iteration = 0; iteration < 1500; iteration++)
        {
            var role = roles[rng.Next(roles.Length)];
            var risks = Enum.GetValues<RiskLevel>();
            var blasts = Enum.GetValues<BlastRadius>();
            MaintenanceWindow? window = rng.Next(3) == 0
                ? null
                : new MaintenanceWindow(Now.AddMinutes(rng.Next(-90, 0)), Now.AddMinutes(rng.Next(1, 90)));
            var envelope = Envelope(
                Subset(rng, SkillUniverse), Subset(rng, CapabilityUniverse), Subset(rng, ToolUniverse),
                risks[rng.Next(risks.Length)], blasts[rng.Next(blasts.Length)],
                Subset(rng, TargetUniverse), Subset(rng, EnvironmentUniverse), window);
            var tool = Tool(Pick(rng, envelope.AllowedTools, ToolUniverse), risks[rng.Next(risks.Length)]);
            var skill = rng.Next(4) == 0
                ? null
                : Skill(
                    Pick(rng, envelope.AllowedSkills, SkillUniverse), Pick(rng, envelope.AllowedCapabilities, CapabilityUniverse),
                    Pick(rng, envelope.AllowedTargets, TargetUniverse), Pick(rng, envelope.AllowedEnvironments, EnvironmentUniverse),
                    blasts[rng.Next(blasts.Length)]);
            var now = Now.AddMinutes(rng.Next(-100, 100));

            var ceiling = role == AgentRoleKind.Remediation ? envelope.MaxRisk : RiskLevel.Read;
            ceiling = ceiling < envelope.MaxRisk ? ceiling : envelope.MaxRisk;
            var inside =
                envelope.AllowedTools.Contains(tool.Name)
                && tool.Risk <= ceiling
                && tool.Risk != RiskLevel.Critical
                && (tool.Risk == RiskLevel.Read || skill is not null)
                && (window is null || (now >= window.StartUtc && now < window.EndUtc))
                && (skill is null
                    || (envelope.AllowedSkills.Contains(skill.SkillId)
                        && envelope.AllowedCapabilities.Contains(skill.CapabilityName)
                        && skill.BlastRadius <= envelope.MaxBlastRadius
                        && envelope.AllowedTargets.Contains(skill.Target)
                        && envelope.AllowedEnvironments.Contains(skill.Environment)));

            var refusal = EnvelopeEnforcer.CheckStep(ScopeOf(envelope, role), Operator, tool, skill, now);

            Assert.True(
                inside == (refusal is null),
                $"iteration {iteration}: role {role}, tool {tool.Name}/{tool.Risk}, skill {skill}, envelope risk {envelope.MaxRisk}, "
                + $"expected {(inside ? "allowed" : "refused")} but got {refusal?.Reason ?? "allowed"}");
            if (inside)
            {
                allowed++;
            }
            else
            {
                refused++;
            }
        }

        // The generator has to reach both outcomes often, or the property proves nothing.
        Assert.InRange(allowed, 100, 1400);
        Assert.InRange(refused, 100, 1400);
    }
}
