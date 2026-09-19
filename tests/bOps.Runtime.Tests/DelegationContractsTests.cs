// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using bOps.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// ADR-0030 (V1.2-B): the delegation contracts in <c>bOps.Abstractions</c>. Rule A2 requires every
/// contract type to round-trip through <see cref="System.Text.Json"/> with source generation, so each
/// test runs the reflection engine and a source-generated context and requires them to agree on the
/// wire format. The remaining tests pin the invariants a later runtime relies on: exact non-wildcard
/// envelopes, a reduction result that cannot be both granted and denied, verification that cannot be
/// confirmed without its own evidence, and audit/evidence JSON that is byte-identical for callers that
/// never delegate.
/// </summary>
public sealed class DelegationContractsTests
{
    private static readonly JsonSerializerOptions Reflection = new() { WriteIndented = false };

    private static readonly ActorIdentity Operator = new("os-user", "alice", "Alice");
    private static readonly ActorIdentity Administrator = new("api-user", "root", "Root");
    private static readonly NodeId Node = new("node-1");
    private static readonly Guid DelegationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly AgentId DiscoveryAgent = new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"));
    private static readonly AgentId DiagnosticAgent = new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002"));
    private static readonly AgentId RemediationAgent = new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003"));
    private static readonly AgentId VerificationAgent = new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000004"));
    private static readonly DateTimeOffset T0 = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    private const string HashA = "3f786850e387550fdab836ed7e6dc881de23001b3f786850e387550fdab836ed";
    private const string HashB = "9a0364b9e99bb480dd25e1f0284c8555a3f5c1d1d3a9f0f6e1c7b8a2d4e6f801";

    private static AuthorityEnvelope SampleEnvelope() => new(
        Operator,
        Depth: 1,
        AllowedSkills: ["system.skill"],
        AllowedCapabilities: ["system.diagnose"],
        AllowedTools: ["system.cpu", "system.memory"],
        MaxRisk: RiskLevel.Low,
        MaxBlastRadius: BlastRadius.Single,
        AllowedTargets: ["node-1"],
        AllowedEnvironments: ["staging"],
        Budget: new DelegationBudget(10, 20_000, T0.AddHours(1)),
        Window: new MaintenanceWindow(T0, T0.AddHours(2)));

    private static Evidence SampleEvidence(string id, EvidenceKind kind = EvidenceKind.Fact) =>
        new(id, kind, "Observed.", "42%", "system.cpu", T0);

    private static ExecutionPlan SamplePlan() => new(
        "system.diagnose", "1.0.0", "Restore the service.",
        [new ExecutionPlanStep(0, "service.restart", ToolArguments.FromJson(new JsonObject { ["name"] = "svc" }), "Restart.")]);

    private static DelegationCorrelation SampleCorrelation() =>
        new(DelegationId, HashA, new AgentIdentity(DiagnosticAgent, AgentRoleKind.Diagnostic));

    private static T RoundTripBoth<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        var reflectionJson = JsonSerializer.Serialize(value, Reflection);
        var sourceJson = JsonSerializer.Serialize(value, typeInfo);
        Assert.Equal(reflectionJson, sourceJson);

        var viaReflection = JsonSerializer.Deserialize<T>(reflectionJson, Reflection)!;
        var viaSource = JsonSerializer.Deserialize(sourceJson, typeInfo)!;

        // Structural equality without hand-listing every member: a second serialization must be identical.
        Assert.Equal(reflectionJson, JsonSerializer.Serialize(viaReflection, Reflection));
        Assert.Equal(sourceJson, JsonSerializer.Serialize(viaSource, typeInfo));
        return viaSource;
    }

    // ---- identity ----

    [Fact]
    public void AgentId_RoundTrips_AsABareJsonString()
    {
        var value = DiscoveryAgent;

        var json = JsonSerializer.Serialize(value, Reflection);
        var result = RoundTripBoth(value, DelegationContractsJsonContext.Default.AgentId);

        Assert.Equal("\"aaaaaaaa-0000-0000-0000-000000000001\"", json);
        Assert.Equal(value, result);
        Assert.False(value.IsEmpty);
        Assert.True(default(AgentId).IsEmpty);
        Assert.NotEqual(AgentId.New(), AgentId.New());
    }

    [Fact]
    public void AgentIdentity_RoundTrips()
    {
        var value = new AgentIdentity(RemediationAgent, AgentRoleKind.Remediation, DiagnosticAgent);

        var result = RoundTripBoth(value, DelegationContractsJsonContext.Default.AgentIdentity);

        Assert.Equal(value.Id, result.Id);
        Assert.Equal(AgentRoleKind.Remediation, result.Role);
        Assert.Equal(DiagnosticAgent, result.ParentAgentId);
    }

    [Fact]
    public void AgentIdentity_RejectsAnEmptyIdAndItsOwnParent()
    {
        Assert.Throws<ArgumentException>(() => new AgentIdentity(default, AgentRoleKind.Discovery));
        Assert.Throws<ArgumentException>(() => new AgentIdentity(DiscoveryAgent, AgentRoleKind.Discovery, DiscoveryAgent));
        Assert.Throws<ArgumentException>(() => new AgentIdentity(DiscoveryAgent, (AgentRoleKind)99));
    }

    [Fact]
    public void DelegationCorrelation_RoundTrips_WithAndWithoutAnAgent()
    {
        var roleScoped = SampleCorrelation();
        var orchestratorScoped = new DelegationCorrelation(DelegationId, HashB, null);

        var first = RoundTripBoth(roleScoped, DelegationContractsJsonContext.Default.DelegationCorrelation);
        var second = RoundTripBoth(orchestratorScoped, DelegationContractsJsonContext.Default.DelegationCorrelation);

        Assert.Equal(DiagnosticAgent, first.Agent!.Id);
        Assert.Equal(AgentRoleKind.Diagnostic, first.Agent.Role);
        Assert.Equal(HashA, first.EnvelopeHash);
        Assert.Null(second.Agent);
    }

    // ---- envelope ----

    [Fact]
    public void AuthorityEnvelope_RoundTrips()
    {
        var value = SampleEnvelope();

        var result = RoundTripBoth(value, DelegationContractsJsonContext.Default.AuthorityEnvelope);

        Assert.Equal(value.Originator, result.Originator);
        Assert.Equal(1, result.Depth);
        Assert.Equal(value.AllowedSkills, result.AllowedSkills);
        Assert.Equal(value.AllowedCapabilities, result.AllowedCapabilities);
        Assert.Equal(value.AllowedTools, result.AllowedTools);
        Assert.Equal(RiskLevel.Low, result.MaxRisk);
        Assert.Equal(BlastRadius.Single, result.MaxBlastRadius);
        Assert.Equal(value.AllowedTargets, result.AllowedTargets);
        Assert.Equal(value.AllowedEnvironments, result.AllowedEnvironments);
        Assert.Equal(value.Window, result.Window);
        Assert.Equal(value.Budget, result.Budget);
    }

    [Fact]
    public void AuthorityEnvelope_RoundTrips_WithoutAMaintenanceWindow()
    {
        var value = SampleEnvelope() with { Window = null };

        var result = RoundTripBoth(value, DelegationContractsJsonContext.Default.AuthorityEnvelope);

        Assert.Null(result.Window);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("system.*")]
    [InlineData("svc-?")]
    public void AuthorityEnvelope_RejectsWildcardMembers_InEverySet(string member)
    {
        Assert.Throws<ArgumentException>(() => Envelope(skills: [member]));
        Assert.Throws<ArgumentException>(() => Envelope(capabilities: [member]));
        Assert.Throws<ArgumentException>(() => Envelope(tools: [member]));
        Assert.Throws<ArgumentException>(() => Envelope(targets: [member]));
        Assert.Throws<ArgumentException>(() => Envelope(environments: [member]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(" padded")]
    public void AuthorityEnvelope_RejectsBlankOrPaddedMembers(string member)
    {
        Assert.Throws<ArgumentException>(() => Envelope(tools: [member]));
        Assert.Throws<ArgumentException>(() => Envelope(targets: [member]));
    }

    [Fact]
    public void AuthorityEnvelope_AllowsEmptySets_WhichMeanNothingIsPermitted()
    {
        var value = Envelope(skills: [], capabilities: [], tools: [], targets: [], environments: []);

        Assert.Empty(value.AllowedTools);
        Assert.Empty(value.AllowedCapabilities);
    }

    [Fact]
    public void AuthorityEnvelope_RejectsANegativeDepthAndAnUndefinedCeiling()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Envelope(depth: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Envelope(risk: (RiskLevel)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => Envelope(blast: (BlastRadius)99));
    }

    [Fact]
    public void MaintenanceWindow_RequiresTheStartBeforeTheEnd()
    {
        Assert.Throws<ArgumentException>(() => new MaintenanceWindow(T0, T0));
        Assert.Throws<ArgumentException>(() => new MaintenanceWindow(T0.AddHours(1), T0));
        _ = new MaintenanceWindow(T0, T0.AddTicks(1));
    }

    [Fact]
    public void DelegationBudget_AndBudgetConsumption_RejectNegativeAmounts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DelegationBudget(-1, 0, T0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DelegationBudget(0, -1, T0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BudgetConsumption(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BudgetConsumption(0, -1));
        Assert.Equal(new BudgetConsumption(0, 0), BudgetConsumption.Empty);
    }

    [Fact]
    public void DelegationBudget_AndBudgetConsumption_RoundTrip()
    {
        var budget = RoundTripBoth(new DelegationBudget(5, 1_000, T0), DelegationContractsJsonContext.Default.DelegationBudget);
        var consumed = RoundTripBoth(new BudgetConsumption(3, 640), DelegationContractsJsonContext.Default.BudgetConsumption);

        Assert.Equal(new DelegationBudget(5, 1_000, T0), budget);
        Assert.Equal(new BudgetConsumption(3, 640), consumed);
    }

    // ---- reduction result ----

    [Fact]
    public void EnvelopeReduction_Granted_RoundTrips_WithItsReducedDimensions()
    {
        var value = EnvelopeReduction.Granted(SampleEnvelope(), [EnvelopeDimension.Tools, EnvelopeDimension.Risk]);

        var result = RoundTripBoth(value, DelegationContractsJsonContext.Default.EnvelopeReduction);

        Assert.False(result.IsDenied);
        Assert.NotNull(result.Envelope);
        Assert.Null(result.Denial);
        Assert.Equal([EnvelopeDimension.Tools, EnvelopeDimension.Risk], result.ReducedDimensions);
        Assert.DoesNotContain("isDenied", JsonSerializer.Serialize(value, Reflection), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnvelopeReduction_Denied_RoundTrips_WithTheDimensionAndReason()
    {
        var value = EnvelopeReduction.Denied(EnvelopeDimension.Targets, "No target is permitted by the parent, the role profile and the request.");

        var result = RoundTripBoth(value, DelegationContractsJsonContext.Default.EnvelopeReduction);

        Assert.True(result.IsDenied);
        Assert.Null(result.Envelope);
        Assert.Equal(EnvelopeDimension.Targets, result.Denial!.Dimension);
        Assert.Contains("No target", result.Denial.Reason, StringComparison.Ordinal);
        Assert.Empty(result.ReducedDimensions);
    }

    [Fact]
    public void EnvelopeReduction_CannotBeBothGrantedAndDenied_OrNeither()
    {
        var denial = new DelegationDenial(EnvelopeDimension.Steps, "No steps remain.");

        Assert.Throws<ArgumentException>(() => new EnvelopeReduction(SampleEnvelope(), [], denial));
        Assert.Throws<ArgumentException>(() => new EnvelopeReduction(null, [], null));
        Assert.Throws<ArgumentException>(() => new EnvelopeReduction(null, [EnvelopeDimension.Tools], denial));
        Assert.Throws<ArgumentException>(() => new DelegationDenial(EnvelopeDimension.Steps, " "));
    }

    // ---- hashing ----

    [Fact]
    public void EnvelopeHash_IsTheSha256OfTheDocumentedCanonicalForm()
    {
        const string canonical =
            "{\"budget\":{\"deadlineUtc\":\"2026-09-18T13:00:00.0000000Z\",\"maxSteps\":10,\"maxTokens\":20000}," +
            "\"capabilities\":[\"system.diagnose\"],\"depth\":1,\"environments\":[\"staging\"]," +
            "\"maxBlastRadius\":\"Single\",\"maxRisk\":\"Low\",\"originator\":{\"id\":\"alice\",\"kind\":\"os-user\"}," +
            "\"skills\":[\"system.skill\"],\"targets\":[\"node-1\"],\"tools\":[\"system.cpu\",\"system.memory\"]," +
            "\"window\":{\"endUtc\":\"2026-09-18T14:00:00.0000000Z\",\"startUtc\":\"2026-09-18T12:00:00.0000000Z\"}}";
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

        Assert.Equal(expected, DelegationHasher.ComputeEnvelopeHash(SampleEnvelope()));
    }

    [Fact]
    public void EnvelopeHash_IgnoresMemberOrderDuplicatesDisplayNameAndUtcOffset()
    {
        var reordered = Envelope(
            tools: ["system.memory", "system.cpu", "system.cpu"],
            originator: new ActorIdentity("os-user", "alice", "A different display name"),
            deadline: new DateTimeOffset(2026, 9, 18, 15, 0, 0, TimeSpan.FromHours(2)));

        Assert.Equal(DelegationHasher.ComputeEnvelopeHash(SampleEnvelope()), DelegationHasher.ComputeEnvelopeHash(reordered));
    }

    public static TheoryData<string, AuthorityEnvelope> EveryDimensionChanged() => new()
    {
        { "originator", Envelope(originator: new ActorIdentity("os-user", "mallory", "Alice")) },
        { "depth", Envelope(depth: 2) },
        { "skills", Envelope(skills: ["system.skill", "other.skill"]) },
        { "capabilities", Envelope(capabilities: []) },
        { "tools", Envelope(tools: ["system.cpu"]) },
        { "risk", Envelope(risk: RiskLevel.Medium) },
        { "blast", Envelope(blast: BlastRadius.Multiple) },
        { "targets", Envelope(targets: ["node-2"]) },
        { "environments", Envelope(environments: ["production"]) },
        { "window", Envelope(window: new MaintenanceWindow(T0, T0.AddHours(3))) },
        { "no window", Envelope(window: null, noWindow: true) },
        { "steps", Envelope(budget: new DelegationBudget(11, 20_000, T0.AddHours(1))) },
        { "tokens", Envelope(budget: new DelegationBudget(10, 20_001, T0.AddHours(1))) },
        { "deadline", Envelope(budget: new DelegationBudget(10, 20_000, T0.AddHours(2))) },
    };

    [Theory]
    [MemberData(nameof(EveryDimensionChanged))]
    public void EnvelopeHash_ChangesWhenAnyDimensionChanges(string dimension, AuthorityEnvelope changed)
    {
        Assert.NotEqual(DelegationHasher.ComputeEnvelopeHash(SampleEnvelope()), DelegationHasher.ComputeEnvelopeHash(changed));
        Assert.False(string.IsNullOrEmpty(dimension));
    }

    [Fact]
    public void ArgumentsHash_IgnoresPropertyOrder_AndSeparatesDifferentArguments()
    {
        var first = ToolArguments.FromJson(new JsonObject { ["a"] = 1, ["b"] = "x" });
        var reordered = ToolArguments.FromJson(new JsonObject { ["b"] = "x", ["a"] = 1 });
        var different = ToolArguments.FromJson(new JsonObject { ["a"] = 2, ["b"] = "x" });

        Assert.Equal(DelegationHasher.ComputeArgumentsHash(first), DelegationHasher.ComputeArgumentsHash(reordered));
        Assert.NotEqual(DelegationHasher.ComputeArgumentsHash(first), DelegationHasher.ComputeArgumentsHash(different));
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData("{}"u8)),
            DelegationHasher.ComputeArgumentsHash(ToolArguments.Empty));
    }

    // ---- evidence provenance ----

    [Fact]
    public void Evidence_RoundTrips_WithProvenance()
    {
        var value = SampleEvidence("e-1") with { Provenance = new EvidenceProvenance(DelegationId, DiscoveryAgent, AgentRoleKind.Discovery) };

        var result = RoundTripBoth(value, DelegationContractsJsonContext.Default.Evidence);

        Assert.Equal(new EvidenceProvenance(DelegationId, DiscoveryAgent, AgentRoleKind.Discovery), result.Provenance);
    }

    [Fact]
    public void Evidence_WithoutProvenance_SerializesExactlyAsInVersion11_AndOldJsonStillLoads()
    {
        var value = SampleEvidence("e-1");

        var json = JsonSerializer.Serialize(value, Reflection);
        var old = JsonSerializer.Deserialize<Evidence>(
            "{\"Id\":\"e-1\",\"Kind\":0,\"Description\":\"Observed.\",\"Data\":\"42%\",\"SourceTool\":\"system.cpu\",\"ObservedAtUtc\":\"2026-09-18T12:00:00+00:00\"}",
            Reflection);

        Assert.DoesNotContain("rovenance", json, StringComparison.Ordinal);
        Assert.Null(old!.Provenance);
        Assert.Equal(value, old);
    }

    [Fact]
    public void EvidenceProvenance_CannotBeSetByAPackage()
    {
        // Rule A11 applied to agents (ADR-0030 section 1): only the runtime, an InternalsVisibleTo
        // friend, may stamp who observed a piece of evidence. A public setter would let a Capability
        // claim to be another agent.
        var setter = typeof(Evidence).GetProperty(nameof(Evidence.Provenance))!.SetMethod!;

        Assert.False(setter.IsPublic);
    }

    // ---- audit ----

    [Fact]
    public void NonDelegatedAuditEvents_SerializeExactlyAsInVersion11()
    {
        AuditEvent value = SampleToolCallEvent();

        var json = JsonSerializer.Serialize(value, Reflection);
        var old = JsonSerializer.Deserialize<AuditEvent>(json, Reflection);

        Assert.DoesNotContain("elegation", json, StringComparison.Ordinal);
        Assert.Null(old!.Delegation);
    }

    [Fact]
    public void AnyAuditEvent_CanCarryTheDelegationCorrelationBlock()
    {
        AuditEvent toolCall = SampleToolCallEvent() with { Delegation = SampleCorrelation() };
        AuditEvent modelCall = new ModelCallAuditEvent
        {
            TimestampUtc = T0,
            Node = Node,
            TaskId = DelegationId,
            StepIndex = 0,
            Actor = Operator,
            Provider = "provider",
            Model = "model",
            Outcome = ModelCallOutcome.Success,
            Delegation = SampleCorrelation(),
        };

        foreach (var value in new[] { toolCall, modelCall })
        {
            var result = RoundTripBoth(value, DelegationContractsJsonContext.Default.AuditEvent);

            Assert.Equal(DelegationId, result.Delegation!.DelegationId);
            Assert.Equal(DiagnosticAgent, result.Delegation.Agent!.Id);
            Assert.Equal(value.GetType(), result.GetType());
        }
    }

    [Fact]
    public void DelegationLifecycleAuditEvent_RoundTrips_AsItsBaseType()
    {
        AuditEvent value = new DelegationLifecycleAuditEvent
        {
            TimestampUtc = T0,
            Node = Node,
            TaskId = DelegationId,
            StepIndex = -1,
            Actor = Operator,
            Delegation = SampleCorrelation(),
            Stage = DelegationStage.RoleCompleted,
            Status = DelegationStatus.Running,
            RoleStatus = DelegationRoleStatus.Completed,
            Consumed = new BudgetConsumption(4, 900),
        };

        var result = Assert.IsType<DelegationLifecycleAuditEvent>(RoundTripBoth(value, DelegationContractsJsonContext.Default.AuditEvent));

        Assert.Equal(DelegationStage.RoleCompleted, result.Stage);
        Assert.Equal(DelegationRoleStatus.Completed, result.RoleStatus);
        Assert.Equal(new BudgetConsumption(4, 900), result.Consumed);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void DelegationLifecycleAuditEvent_RoundTrips_AHumansDecisionOnAPlanHash()
    {
        AuditEvent value = new DelegationLifecycleAuditEvent
        {
            TimestampUtc = T0,
            Node = Node,
            TaskId = DelegationId,
            StepIndex = -1,
            Actor = Administrator,
            Delegation = SampleCorrelation(),
            Stage = DelegationStage.PlanDecided,
            Status = DelegationStatus.Rejected,
            PlanHash = HashA,
        };

        var result = Assert.IsType<DelegationLifecycleAuditEvent>(RoundTripBoth(value, DelegationContractsJsonContext.Default.AuditEvent));

        Assert.Equal(DelegationStage.PlanDecided, result.Stage);
        Assert.Equal(HashA, result.PlanHash);
        Assert.Equal(Administrator, result.Actor);
        Assert.Equal(5, (int)DelegationStage.PlanDecided);
    }

    [Fact]
    public void DelegationEnvelopeAuditEvent_RoundTrips_ForAReductionAndForADenial()
    {
        AuditEvent reduced = new DelegationEnvelopeAuditEvent
        {
            TimestampUtc = T0,
            Node = Node,
            TaskId = DelegationId,
            StepIndex = -1,
            Actor = Operator,
            Delegation = SampleCorrelation(),
            Role = AgentRoleKind.Diagnostic,
            ParentEnvelopeHash = HashB,
            ReducedDimensions = [EnvelopeDimension.Tools, EnvelopeDimension.Deadline],
        };
        AuditEvent denied = new DelegationEnvelopeAuditEvent
        {
            TimestampUtc = T0,
            Node = Node,
            TaskId = DelegationId,
            StepIndex = -1,
            Actor = Operator,
            Delegation = new DelegationCorrelation(DelegationId, HashB, null),
            Role = AgentRoleKind.Remediation,
            ParentEnvelopeHash = HashB,
            ReducedDimensions = [],
            Denial = new DelegationDenial(EnvelopeDimension.Environments, "Empty intersection."),
        };

        var first = Assert.IsType<DelegationEnvelopeAuditEvent>(RoundTripBoth(reduced, DelegationContractsJsonContext.Default.AuditEvent));
        var second = Assert.IsType<DelegationEnvelopeAuditEvent>(RoundTripBoth(denied, DelegationContractsJsonContext.Default.AuditEvent));

        Assert.Equal([EnvelopeDimension.Tools, EnvelopeDimension.Deadline], first.ReducedDimensions);
        Assert.Null(first.Denial);
        Assert.Equal(EnvelopeDimension.Environments, second.Denial!.Dimension);
        Assert.Equal(AgentRoleKind.Remediation, second.Role);
    }

    [Fact]
    public void DelegationJournalAuditEvent_RoundTrips_ForIntentAndOutcome()
    {
        AuditEvent intent = new DelegationJournalAuditEvent
        {
            TimestampUtc = T0,
            Node = Node,
            TaskId = DelegationId,
            StepIndex = 2,
            Actor = Operator,
            Delegation = SampleCorrelation(),
            Phase = JournalPhase.Intent,
            Tool = "service.restart",
            ArgumentsHash = HashA,
        };
        AuditEvent outcome = new DelegationJournalAuditEvent
        {
            TimestampUtc = T0,
            Node = Node,
            TaskId = DelegationId,
            StepIndex = 2,
            Actor = Operator,
            Delegation = SampleCorrelation(),
            Phase = JournalPhase.Outcome,
            Tool = "service.restart",
            ArgumentsHash = HashA,
            Outcome = StepOutcomeKind.Timeout,
            Verification = VerificationStatus.Inconclusive,
        };

        var first = Assert.IsType<DelegationJournalAuditEvent>(RoundTripBoth(intent, DelegationContractsJsonContext.Default.AuditEvent));
        var second = Assert.IsType<DelegationJournalAuditEvent>(RoundTripBoth(outcome, DelegationContractsJsonContext.Default.AuditEvent));

        Assert.Equal(JournalPhase.Intent, first.Phase);
        Assert.Null(first.Outcome);
        Assert.Equal(StepOutcomeKind.Timeout, second.Outcome);
        Assert.Equal(VerificationStatus.Inconclusive, second.Verification);
        Assert.Equal(HashA, second.ArgumentsHash);
    }

    [Fact]
    public void DelegationReconciliationAuditEvent_RoundTrips_WithWhoResolvedIt()
    {
        AuditEvent value = new DelegationReconciliationAuditEvent
        {
            TimestampUtc = T0,
            Node = Node,
            TaskId = DelegationId,
            StepIndex = 2,
            Actor = Operator,
            Delegation = new DelegationCorrelation(DelegationId, HashB, null),
            Action = ReconciliationAction.OperatorAcceptedDone,
            Verification = VerificationStatus.Inconclusive,
            ResolvedBy = Administrator,
            Note = "Checked the service by hand.",
        };

        var result = Assert.IsType<DelegationReconciliationAuditEvent>(RoundTripBoth(value, DelegationContractsJsonContext.Default.AuditEvent));

        Assert.Equal(ReconciliationAction.OperatorAcceptedDone, result.Action);
        Assert.Equal(Administrator, result.ResolvedBy);
        Assert.Equal(VerificationStatus.Inconclusive, result.Verification);
        Assert.Equal("Checked the service by hand.", result.Note);
    }

    // ---- verification report ----

    [Fact]
    public void VerificationReport_RoundTrips()
    {
        var evidence = SampleEvidence("v-1", EvidenceKind.Verification) with
        {
            Provenance = new EvidenceProvenance(DelegationId, VerificationAgent, AgentRoleKind.Verification),
        };
        var value = new VerificationReport(HashA, VerificationStatus.Confirmed, [evidence], "Service is running.");

        var result = RoundTripBoth(value, DelegationContractsJsonContext.Default.VerificationReport);

        Assert.Equal(HashA, result.PlanHash);
        Assert.Equal(VerificationStatus.Confirmed, result.Status);
        Assert.Equal(AgentRoleKind.Verification, Assert.Single(result.Evidence).Provenance!.Role);
    }

    [Fact]
    public void VerificationReport_CannotBeConfirmedWithoutItsOwnEvidence()
    {
        // ADR-0030 section 5: Verification reads the system itself. A verdict of Confirmed with no
        // evidence recorded by the verifier would be a verdict taken on someone else's word.
        Assert.Throws<ArgumentException>(() => new VerificationReport(HashA, VerificationStatus.Confirmed, [], null));
        _ = new VerificationReport(HashA, VerificationStatus.Inconclusive, [], "Could not read the service.");
        _ = new VerificationReport(HashA, VerificationStatus.Refuted, [SampleEvidence("v-1", EvidenceKind.Verification)], null);
    }

    // ---- run aggregate ----

    [Fact]
    public void DelegationRun_RoundTrips_WithEveryPartPopulated()
    {
        var value = SampleRun();

        var result = RoundTripBoth(value, DelegationContractsJsonContext.Default.DelegationRun);

        Assert.Equal(DelegationId, result.Id);
        Assert.Equal("start-42", result.IdempotencyKey);
        Assert.Equal(DelegationStatus.RequiresReconciliation, result.Status);
        Assert.Equal(4, result.Roles.Count);
        Assert.Equal(
            [AgentRoleKind.Discovery, AgentRoleKind.Diagnostic, AgentRoleKind.Remediation, AgentRoleKind.Verification],
            result.Roles.Select(r => r.Agent.Role));
        Assert.Equal(4, result.Roles.Select(r => r.Agent.Id).Distinct().Count());
        Assert.NotNull(result.Roles[1].Report!.Plan);
        Assert.Equal(ExecutionPlanHasher.ComputeHash(result.Roles[1].Report!.Plan!), result.PlanHash);
        Assert.Equal(result.PlanHash, result.Approval!.PlanHash);
        Assert.Equal(Administrator, result.Approval.Approver);
        Assert.Equal(2, result.Journal.Count);
        Assert.Equal(1, result.ResumeCount);
    }

    [Fact]
    public void StepJournalEntry_DistinguishesIntentOnlyFromCompleted_AndReconciled()
    {
        var intentOnly = SampleRun().Journal[1];
        var reconciled = SampleRun().Journal[0];

        var first = RoundTripBoth(intentOnly, DelegationContractsJsonContext.Default.StepJournalEntry);
        var second = RoundTripBoth(reconciled, DelegationContractsJsonContext.Default.StepJournalEntry);

        Assert.Null(first.Outcome);
        Assert.Null(first.Reconciliation);
        Assert.Equal(StepOutcomeKind.Timeout, second.Outcome!.Kind);
        Assert.Equal(ReconciliationAction.VerifiedDone, second.Reconciliation!.Action);
        Assert.Equal(VerificationStatus.Confirmed, second.Reconciliation.Verification);
    }

    [Fact]
    public void DelegationRun_RoundTrips_AsADeniedRunWithNoRoles()
    {
        var value = SampleRun() with
        {
            Status = DelegationStatus.Denied,
            Roles = [],
            Journal = [],
            PlanHash = null,
            Approval = null,
            Denial = new DelegationDenial(EnvelopeDimension.Capabilities, "No capability is permitted."),
        };

        var result = RoundTripBoth(value, DelegationContractsJsonContext.Default.DelegationRun);

        Assert.Equal(DelegationStatus.Denied, result.Status);
        Assert.Equal(EnvelopeDimension.Capabilities, result.Denial!.Dimension);
        Assert.Empty(result.Roles);
    }

    [Fact]
    public void DelegationStartResult_RoundTrips()
    {
        var value = new DelegationStartResult(SampleRun(), Created: false);

        var result = RoundTripBoth(value, DelegationContractsJsonContext.Default.DelegationStartResult);

        Assert.False(result.Created);
        Assert.Equal(DelegationId, result.Run.Id);
    }

    [Fact]
    public void DurableEnums_KeepTheirWireValues()
    {
        // These values are written to the delegation store and to the audit log. Inserting or
        // reordering a member would silently change the meaning of data already on disk.
        Assert.Equal(
            [("Discovery", 0), ("Diagnostic", 1), ("Remediation", 2), ("Verification", 3)],
            Values<AgentRoleKind>());
        Assert.Equal(
            [("Running", 0), ("AwaitingApproval", 1), ("RequiresReconciliation", 2), ("Completed", 3), ("DiagnosisCompleted", 4),
             ("Rejected", 5), ("VerificationFailed", 6), ("Denied", 7), ("PolicyBlocked", 8), ("BudgetExceeded", 9),
             ("DeadlineExceeded", 10), ("Cancelled", 11), ("Abandoned", 12), ("Failed", 13)],
            Values<DelegationStatus>());
        Assert.Equal(
            [("Pending", 0), ("Running", 1), ("Completed", 2), ("Failed", 3), ("Cancelled", 4)],
            Values<DelegationRoleStatus>());
        Assert.Equal(
            [("Succeeded", 0), ("Failed", 1), ("Timeout", 2), ("Cancelled", 3)],
            Values<StepOutcomeKind>());
        Assert.Equal(
            [("VerifiedDone", 0), ("OperatorAcceptedDone", 1), ("EscalatedToOperator", 2), ("OperatorAbandoned", 3)],
            Values<ReconciliationAction>());
        Assert.Equal(
            [("Originator", 0), ("Depth", 1), ("Skills", 2), ("Capabilities", 3), ("Tools", 4), ("Risk", 5), ("BlastRadius", 6),
             ("Targets", 7), ("Environments", 8), ("MaintenanceWindow", 9), ("Steps", 10), ("Tokens", 11), ("Deadline", 12),
             ("Profile", 13)],
            Values<EnvelopeDimension>());
    }

    [Fact]
    public void EnvelopeProfileAndRequest_TakeASnapshotOfTheSetsTheyAreBuiltFrom()
    {
        // These contracts state authority and claim to be immutable values. A list the caller keeps and edits
        // afterwards must not be able to widen what was validated, or slip in a wildcard.
        var tools = new List<string> { "system.cpu" };
        var envelope = new AuthorityEnvelope(
            Operator, 1, [], [], tools, RiskLevel.Read, BlastRadius.Single, ["n"], ["e"], new DelegationBudget(1, 1, T0.AddHours(1)));
        var profile = new RoleProfile(
            AgentRoleKind.Verification, [], [], tools, RiskLevel.Read, BlastRadius.Single, ["n"], ["e"], 1, 0, TimeSpan.FromMinutes(1));
        var request = new DelegationAuthorityRequest(AllowedTools: tools);

        tools.Add("*");
        tools[0] = "evil.tool";

        Assert.Equal(["system.cpu"], envelope.AllowedTools);
        Assert.Equal(["system.cpu"], profile.AllowedTools);
        Assert.Equal(["system.cpu"], request.AllowedTools);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)envelope.AllowedTools).Add("evil.tool"));
    }

    // ---- role profile, authority request and profile source (ADR-0031 section 5) ----

    private static RoleProfile SampleRemediationProfile() => new(
        AgentRoleKind.Remediation,
        AllowedSkills: ["system.skill"],
        AllowedCapabilities: ["system.diagnose"],
        AllowedTools: ["service.restart"],
        MaxRisk: RiskLevel.High,
        MaxBlastRadius: BlastRadius.Multiple,
        AllowedTargets: ["node-1"],
        AllowedEnvironments: ["staging"],
        MaxSteps: 5,
        MaxTokens: 0,
        MaxDuration: TimeSpan.FromMinutes(5),
        Window: new MaintenanceWindow(T0, T0.AddHours(1)));

    private static RoleProfile ProfileWith(
        AgentRoleKind role,
        IReadOnlyList<string>? skills = null,
        IReadOnlyList<string>? capabilities = null,
        IReadOnlyList<string>? tools = null,
        int maxSteps = 1,
        int maxTokens = 0,
        TimeSpan? duration = null) =>
        new(role, skills ?? [], capabilities ?? [], tools ?? ["system.cpu"], RiskLevel.Read, BlastRadius.Single, ["node-1"], ["staging"], maxSteps, maxTokens, duration ?? TimeSpan.FromMinutes(1));

    [Fact]
    public void RoleProfile_RoundTrips()
    {
        var value = SampleRemediationProfile();

        var result = RoundTripBoth(value, DelegationContractsJsonContext.Default.RoleProfile);

        Assert.Equal(AgentRoleKind.Remediation, result.Role);
        Assert.Equal(value.AllowedSkills, result.AllowedSkills);
        Assert.Equal(value.AllowedCapabilities, result.AllowedCapabilities);
        Assert.Equal(value.AllowedTools, result.AllowedTools);
        Assert.Equal(RiskLevel.High, result.MaxRisk);
        Assert.Equal(BlastRadius.Multiple, result.MaxBlastRadius);
        Assert.Equal(value.AllowedTargets, result.AllowedTargets);
        Assert.Equal(value.AllowedEnvironments, result.AllowedEnvironments);
        Assert.Equal(5, result.MaxSteps);
        Assert.Equal(0, result.MaxTokens);
        Assert.Equal(TimeSpan.FromMinutes(5), result.MaxDuration);
        Assert.Equal(value.Window, result.Window);
    }

    [Fact]
    public void RoleProfile_ReadFromJson_IsHeldToTheSameRulesAsOneBuiltInCode()
    {
        // A profile that arrives from configuration or the wire cannot get around the constructor: a
        // Discovery profile that grants Skills is malformed however it was produced.
        const string json = """
            {"Role":0,"AllowedSkills":["system.skill"],"AllowedCapabilities":[],"AllowedTools":["system.cpu"],
             "MaxRisk":0,"MaxBlastRadius":0,"AllowedTargets":["node-1"],"AllowedEnvironments":["staging"],
             "MaxSteps":5,"MaxTokens":100,"MaxDuration":"00:05:00"}
            """;

        Assert.ThrowsAny<Exception>(() => JsonSerializer.Deserialize(json, DelegationContractsJsonContext.Default.RoleProfile));
        Assert.ThrowsAny<Exception>(() => JsonSerializer.Deserialize<RoleProfile>(json, Reflection));
    }

    public static TheoryData<AgentRoleKind, string> NotApplicableGrants => new()
    {
        { AgentRoleKind.Discovery, "skills" },
        { AgentRoleKind.Discovery, "capabilities" },
        { AgentRoleKind.Verification, "skills" },
        { AgentRoleKind.Verification, "capabilities" },
        { AgentRoleKind.Remediation, "tokens" },
        { AgentRoleKind.Verification, "tokens" },
    };

    [Theory]
    [MemberData(nameof(NotApplicableGrants))]
    public void RoleProfile_RefusesToGrantADimensionThatIsNotApplicableToItsRole(AgentRoleKind role, string dimension)
    {
        var exception = Assert.Throws<ArgumentException>(() => ProfileWith(
            role,
            skills: dimension == "skills" ? ["system.skill"] : null,
            capabilities: dimension == "capabilities" ? ["system.diagnose"] : null,
            maxTokens: dimension == "tokens" ? 1 : 0));

        Assert.Contains("not applicable", exception.Message, StringComparison.Ordinal);
        Assert.Contains(role.ToString(), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RoleProfile_AcceptsWhatIsApplicableToItsRole()
    {
        Assert.NotNull(ProfileWith(AgentRoleKind.Diagnostic, skills: ["system.skill"], capabilities: ["system.diagnose"], maxTokens: 30_000));
        Assert.NotNull(ProfileWith(AgentRoleKind.Remediation, skills: ["system.skill"], capabilities: ["system.diagnose"]));
        Assert.NotNull(ProfileWith(AgentRoleKind.Discovery, maxTokens: 20_000));
        Assert.NotNull(ProfileWith(AgentRoleKind.Verification));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("system.*")]
    [InlineData("tool?")]
    [InlineData(" padded")]
    [InlineData("padded ")]
    [InlineData(" ")]
    [InlineData("")]
    public void RoleProfileAndRequest_RejectWildcardsBlanksAndPadding(string member)
    {
        Assert.Throws<ArgumentException>(() => ProfileWith(AgentRoleKind.Verification, tools: [member]));
        Assert.Throws<ArgumentException>(() => new DelegationAuthorityRequest(AllowedSkills: [member]));
        Assert.Throws<ArgumentException>(() => new DelegationAuthorityRequest(AllowedCapabilities: [member]));
        Assert.Throws<ArgumentException>(() => new DelegationAuthorityRequest(AllowedTools: [member]));
        Assert.Throws<ArgumentException>(() => new DelegationAuthorityRequest(AllowedTargets: [member]));
        Assert.Throws<ArgumentException>(() => new DelegationAuthorityRequest(AllowedEnvironments: [member]));
    }

    [Fact]
    public void RoleProfile_RejectsAmountsAndValuesThatMeanNothing()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ProfileWith(AgentRoleKind.Discovery, maxSteps: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProfileWith(AgentRoleKind.Discovery, maxTokens: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProfileWith(AgentRoleKind.Discovery, duration: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProfileWith(AgentRoleKind.Discovery, duration: TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProfileWith((AgentRoleKind)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RoleProfile(
            AgentRoleKind.Discovery, [], [], ["system.cpu"], (RiskLevel)99, BlastRadius.Single, ["n"], ["e"], 1, 1, TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RoleProfile(
            AgentRoleKind.Discovery, [], [], ["system.cpu"], RiskLevel.Read, (BlastRadius)99, ["n"], ["e"], 1, 1, TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentNullException>(() => new RoleProfile(
            AgentRoleKind.Discovery, null!, [], ["system.cpu"], RiskLevel.Read, BlastRadius.Single, ["n"], ["e"], 1, 1, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void DelegationAuthorityRequest_OmitsWhatItDoesNotNarrow_AndDistinguishesEmptyFromAbsent()
    {
        var nothing = new DelegationAuthorityRequest();

        Assert.Equal("{}", JsonSerializer.Serialize(nothing, Reflection));
        Assert.Equal("{}", JsonSerializer.Serialize(nothing, DelegationContractsJsonContext.Default.DelegationAuthorityRequest));

        var value = new DelegationAuthorityRequest(
            AllowedSkills: [],
            AllowedTools: ["system.cpu"],
            MaxRisk: RiskLevel.Low,
            Window: new MaintenanceWindow(T0, T0.AddHours(1)),
            MaxSteps: 0,
            DeadlineUtc: T0.AddHours(2));

        var result = RoundTripBoth(value, DelegationContractsJsonContext.Default.DelegationAuthorityRequest);

        // An explicit empty set narrows to nothing; an absent one does not narrow at all.
        Assert.NotNull(result.AllowedSkills);
        Assert.Empty(result.AllowedSkills);
        Assert.Null(result.AllowedCapabilities);
        Assert.Equal(["system.cpu"], result.AllowedTools);
        Assert.Equal(RiskLevel.Low, result.MaxRisk);
        Assert.Null(result.MaxBlastRadius);
        Assert.Null(result.AllowedTargets);
        Assert.Null(result.AllowedEnvironments);
        Assert.Equal(value.Window, result.Window);
        Assert.Equal(0, result.MaxSteps);
        Assert.Null(result.MaxTokens);
        Assert.Equal(T0.AddHours(2), result.DeadlineUtc);
    }

    [Fact]
    public void DelegationAuthorityRequest_RejectsAmountsAndValuesThatMeanNothing()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DelegationAuthorityRequest(MaxSteps: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DelegationAuthorityRequest(MaxTokens: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DelegationAuthorityRequest(MaxRisk: (RiskLevel)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DelegationAuthorityRequest(MaxBlastRadius: (BlastRadius)99));
    }

    [Fact]
    public void RoleProfileSource_CanBeImplementedByAHostWithoutAnyPolicyAssembly()
    {
        IRoleProfileSource source = new SingleProfileSource(SampleRemediationProfile());

        Assert.Equal(AgentRoleKind.Remediation, source.GetProfile(AgentRoleKind.Remediation)!.Role);
        Assert.Null(source.GetProfile(AgentRoleKind.Discovery));
    }

    private sealed class SingleProfileSource(RoleProfile profile) : IRoleProfileSource
    {
        public RoleProfile? GetProfile(AgentRoleKind role) => role == profile.Role ? profile : null;
    }

    // ---- plan approval (V1.2-D) ----

    private static PlanApprovalRequest SamplePlanApproval(ExecutionPlan? plan = null)
    {
        var realPlan = plan ?? SamplePlan();
        return new PlanApprovalRequest(
            DelegationId, ExecutionPlanHasher.ComputeHash(realPlan), realPlan, "system.skill", "system.remediate",
            "node-1", "staging", BlastRadius.Single, [new Finding("f1", "The service is down.", ["e1"], RiskLevel.High)]);
    }

    [Fact]
    public void PlanApprovalRequest_RoundTrips()
    {
        var value = SamplePlanApproval();

        var result = RoundTripBoth(value, DelegationContractsJsonContext.Default.PlanApprovalRequest);

        Assert.Equal(value.PlanHash, result.PlanHash);
        Assert.Equal(value.DelegationId, result.DelegationId);
        Assert.Equal("system.remediate", result.CapabilityName);
        Assert.Equal("f1", Assert.Single(result.Findings).Id);
        Assert.Equal(ExecutionPlanHasher.ComputeHash(result.Plan), result.PlanHash);
    }

    [Fact]
    public void PlanApprovalRequest_RefusesAHashThatIsNotThePlansOwn()
    {
        // The approval binds to the hash. A request whose hash is another plan's would ask the human to approve one
        // plan and authorize another.
        var other = new ExecutionPlan(
            "system.diagnose", "1.0.0", "Something else.",
            [new ExecutionPlanStep(0, "service.stop", ToolArguments.Empty, null)]);

        var ex = Assert.Throws<ArgumentException>(() => new PlanApprovalRequest(
            DelegationId, ExecutionPlanHasher.ComputeHash(other), SamplePlan(), "system.skill", "system.remediate",
            "node-1", "staging", BlastRadius.Single, []));

        Assert.Equal("PlanHash", ex.ParamName);
    }

    [Fact]
    public void PlanApprovalRequest_RefusesAnEmptyDelegationBlankNamesAndMissingParts()
    {
        var plan = SamplePlan();
        var hash = ExecutionPlanHasher.ComputeHash(plan);

        Assert.Throws<ArgumentException>(() => new PlanApprovalRequest(Guid.Empty, hash, plan, "s", "c", "t", "e", BlastRadius.Single, []));
        Assert.Throws<ArgumentException>(() => new PlanApprovalRequest(DelegationId, " ", plan, "s", "c", "t", "e", BlastRadius.Single, []));
        Assert.Throws<ArgumentException>(() => new PlanApprovalRequest(DelegationId, hash, plan, "", "c", "t", "e", BlastRadius.Single, []));
        Assert.Throws<ArgumentException>(() => new PlanApprovalRequest(DelegationId, hash, plan, "s", " ", "t", "e", BlastRadius.Single, []));
        Assert.Throws<ArgumentException>(() => new PlanApprovalRequest(DelegationId, hash, plan, "s", "c", "", "e", BlastRadius.Single, []));
        Assert.Throws<ArgumentException>(() => new PlanApprovalRequest(DelegationId, hash, plan, "s", "c", "t", "", BlastRadius.Single, []));
        Assert.Throws<ArgumentNullException>(() => new PlanApprovalRequest(DelegationId, hash, null!, "s", "c", "t", "e", BlastRadius.Single, []));
        Assert.Throws<ArgumentNullException>(() => new PlanApprovalRequest(DelegationId, hash, plan, "s", "c", "t", "e", BlastRadius.Single, null!));
    }

    [Fact]
    public async Task PlanApprovalProvider_CanBeImplementedByAHostAndReturnsTheOrdinaryApprovalDecision()
    {
        IPlanApprovalProvider provider = new ApprovingPlanProvider();

        var decision = await provider.RequestPlanApprovalAsync(SamplePlanApproval());

        Assert.True(decision.Approved);
        Assert.Equal(Operator, decision.Actor);
    }

    private sealed class ApprovingPlanProvider : IPlanApprovalProvider
    {
        public Task<ApprovalDecision> RequestPlanApprovalAsync(PlanApprovalRequest request, CancellationToken ct = default) =>
            Task.FromResult(new ApprovalDecision(true, Operator, null));
    }

    // ---- fixtures ----

    private static (string Name, int Value)[] Values<TEnum>()
        where TEnum : struct, Enum =>
        [.. Enum.GetValues<TEnum>().Select(v => (v.ToString(), Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture)))];

    private static AuthorityEnvelope Envelope(
        ActorIdentity? originator = null,
        int depth = 1,
        IReadOnlyList<string>? skills = null,
        IReadOnlyList<string>? capabilities = null,
        IReadOnlyList<string>? tools = null,
        RiskLevel risk = RiskLevel.Low,
        BlastRadius blast = BlastRadius.Single,
        IReadOnlyList<string>? targets = null,
        IReadOnlyList<string>? environments = null,
        DelegationBudget? budget = null,
        MaintenanceWindow? window = null,
        DateTimeOffset? deadline = null,
        bool noWindow = false) =>
        new(
            originator ?? Operator,
            depth,
            skills ?? ["system.skill"],
            capabilities ?? ["system.diagnose"],
            tools ?? ["system.cpu", "system.memory"],
            risk,
            blast,
            targets ?? ["node-1"],
            environments ?? ["staging"],
            budget ?? new DelegationBudget(10, 20_000, deadline ?? T0.AddHours(1)),
            noWindow ? null : window ?? new MaintenanceWindow(T0, T0.AddHours(2)));

    private static ToolCallAuditEvent SampleToolCallEvent() => new()
    {
        TimestampUtc = T0,
        Node = Node,
        TaskId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        StepIndex = 0,
        Actor = Operator,
        Package = new PackageId("bops.packages.sample"),
        Tool = "system.cpu",
        Arguments = new JsonObject(),
        Risk = RiskLevel.Read,
        Authorization = AuthorizationKind.Automatic,
        Outcome = ToolOutcome.Success,
        Duration = TimeSpan.FromMilliseconds(3),
    };

    private static DelegationRun SampleRun()
    {
        var plan = SamplePlan();
        var planHash = ExecutionPlanHasher.ComputeHash(plan);
        var envelope = SampleEnvelope();

        DelegationRoleRun Role(AgentId id, AgentRoleKind kind, AgentId? parent, DelegationRoleStatus status, SkillReport? report = null, VerificationReport? verification = null) => new()
        {
            Agent = new AgentIdentity(id, kind, parent),
            Envelope = envelope,
            Status = status,
            Consumed = new BudgetConsumption(2, 300),
            StartedAtUtc = T0,
            CompletedAtUtc = status == DelegationRoleStatus.Completed ? T0.AddMinutes(1) : null,
            Report = report,
            Verification = verification,
        };

        var discoveryEvidence = SampleEvidence("e-1") with { Provenance = new EvidenceProvenance(DelegationId, DiscoveryAgent, AgentRoleKind.Discovery) };

        return new DelegationRun
        {
            Id = DelegationId,
            Node = Node,
            Actor = Operator,
            Objective = "Restore the service and prove it is running.",
            IdempotencyKey = "start-42",
            Status = DelegationStatus.RequiresReconciliation,
            RootEnvelope = envelope with { Depth = 0 },
            Roles =
            [
                Role(DiscoveryAgent, AgentRoleKind.Discovery, null, DelegationRoleStatus.Completed, new SkillReport([discoveryEvidence], [], null)),
                Role(DiagnosticAgent, AgentRoleKind.Diagnostic, null, DelegationRoleStatus.Completed, new SkillReport([discoveryEvidence], [new Finding("f-1", "The service is stopped.", ["e-1"], RiskLevel.Low)], plan)),
                Role(RemediationAgent, AgentRoleKind.Remediation, null, DelegationRoleStatus.Running),
                Role(VerificationAgent, AgentRoleKind.Verification, null, DelegationRoleStatus.Pending),
            ],
            PlanHash = planHash,
            Approval = new DelegationApproval(planHash, Administrator, T0.AddMinutes(2)),
            Journal =
            [
                new StepJournalEntry
                {
                    StepIndex = 0,
                    ToolName = "service.restart",
                    ArgumentsHash = HashA,
                    IntentAtUtc = T0.AddMinutes(3),
                    Outcome = new StepOutcome(StepOutcomeKind.Timeout, T0.AddMinutes(4), VerificationStatus.Inconclusive),
                    Reconciliation = new StepReconciliation(ReconciliationAction.VerifiedDone, VerificationStatus.Confirmed, ActorIdentity.RuntimeSystem, T0.AddMinutes(5)),
                },
                new StepJournalEntry
                {
                    StepIndex = 1,
                    ToolName = "service.start",
                    ArgumentsHash = HashB,
                    IntentAtUtc = T0.AddMinutes(6),
                },
            ],
            ResumeCount = 1,
            CreatedAtUtc = T0,
            UpdatedAtUtc = T0.AddMinutes(6),
        };
    }
}

/// <summary>Source-generated serialization for every ADR-0030 contract (rule A2), used only by the tests above.</summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(AgentId))]
[JsonSerializable(typeof(AgentIdentity))]
[JsonSerializable(typeof(DelegationCorrelation))]
[JsonSerializable(typeof(AuthorityEnvelope))]
[JsonSerializable(typeof(DelegationBudget))]
[JsonSerializable(typeof(BudgetConsumption))]
[JsonSerializable(typeof(RoleProfile))]
[JsonSerializable(typeof(DelegationAuthorityRequest))]
[JsonSerializable(typeof(EnvelopeReduction))]
[JsonSerializable(typeof(Evidence))]
[JsonSerializable(typeof(AuditEvent))]
[JsonSerializable(typeof(VerificationReport))]
[JsonSerializable(typeof(DelegationRun))]
[JsonSerializable(typeof(StepJournalEntry))]
[JsonSerializable(typeof(DelegationStartResult))]
[JsonSerializable(typeof(PlanApprovalRequest))]
internal sealed partial class DelegationContractsJsonContext : JsonSerializerContext;
