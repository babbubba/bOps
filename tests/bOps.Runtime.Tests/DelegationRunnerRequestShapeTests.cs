// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// ADR-0044 (HARDEN-11) through the real orchestrator: a diagnosis-only request needs only Discovery and Diagnostic and never
/// looks up, reserves for or begins the others; a remediation still needs all four; the diagnosis-only roles never execute a
/// tool above Read (P1 on the direct/fake-model path, with a policy spy and an execution spy); a named change is checked against
/// the activated catalog and its input schema before any model call; typed limitations and the Diagnostic reply outcome are
/// persisted with each model role, before any plan approval; the <c>BeginRoleAsync</c> guard holds.
/// </summary>
public sealed partial class DelegationRunnerTests
{
    private static Dictionary<AgentRoleKind, RoleProfile> ReadOnlyProfiles(IReadOnlyList<string>? tools = null) => new()
    {
        [AgentRoleKind.Discovery] = Profile(AgentRoleKind.Discovery, tools: tools),
        [AgentRoleKind.Diagnostic] = Profile(AgentRoleKind.Diagnostic, tools: tools) with { AllowedSkills = [], AllowedCapabilities = [] },
    };

    private static DelegationRequest Diagnosis() => new("Find out why the service stopped.", null, null);

    private static readonly string[] ReadToolNames = ["host.info", "test.read"];

    // Discovery is a prose role: with a limitation listed, its final answer carries the ADR-0042 disclosure heading, so no re-ask is made.
    private static ModelResponse Disclosed() => Final("Done.\n\nEvidence limitations\n- one read was not complete.");

    /// <summary>Records which roles were asked for, so a test can prove a role was never looked up.</summary>
    private sealed class SpyProfiles(Dictionary<AgentRoleKind, RoleProfile> profiles) : IRoleProfileSource
    {
        public List<AgentRoleKind> LookedUp { get; } = [];

        public RoleProfile? GetProfile(AgentRoleKind role)
        {
            LookedUp.Add(role);
            return profiles.GetValueOrDefault(role);
        }
    }

    /// <summary>Records every manifest policy is asked about; Read automatic, anything else forbidden.</summary>
    private sealed class SpyPolicy : IPolicyEngine
    {
        public List<string> Evaluated { get; } = [];

        public PolicyDecision Evaluate(PolicyContext context)
        {
            Evaluated.Add(context.Manifest.Name);
            return context.Manifest.Risk == RiskLevel.Read ? new PolicyDecision(PolicyMode.Automatic, "read") : new PolicyDecision(PolicyMode.Forbidden, "spy");
        }
    }

    private sealed class ResultReadTool(string name, ToolCallResult result) : ITool
    {
        public int ExecutionCount { get; private set; }

        public ToolManifest Manifest { get; } = new()
        {
            Name = name,
            Description = "Returns a fixed result.",
            Risk = RiskLevel.Read,
            Platforms = [CurrentPlatform.Id],
            Requires = [],
            Parameters = [],
        };

        public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
        {
            ExecutionCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class MemoryDelegationStore : IDelegationStore
    {
        private readonly Dictionary<Guid, DelegationRun> _runs = [];

        public Task<DelegationStartResult> StartAsync(DelegationRun run, CancellationToken ct = default)
        {
            _runs[run.Id] = run;
            return Task.FromResult(new DelegationStartResult(run, Created: true));
        }

        public Task SaveAsync(DelegationRun run, CancellationToken ct = default)
        {
            _runs[run.Id] = run;
            return Task.CompletedTask;
        }

        public Task<DelegationRun?> LoadAsync(Guid delegationId, CancellationToken ct = default) => Task.FromResult(_runs.GetValueOrDefault(delegationId));

        public Task<IReadOnlyList<DelegationRun>> ListByStatusAsync(DelegationStatus status, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DelegationRun>>([.. _runs.Values.Where(r => r.Status == status)]);

        public Task<IReadOnlyList<DelegationRun>> ListRecentAsync(int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DelegationRun>>([.. _runs.Values.Take(limit)]);
    }

    /// <summary>At the moment a plan is put to a human, reads what the store holds about the run.</summary>
    private sealed class StoreReadingApproval(IDelegationStore store) : IPlanApprovalProvider
    {
        public DelegationRun? SeenAtApproval { get; private set; }

        public async Task<ApprovalDecision> RequestPlanApprovalAsync(PlanApprovalRequest request, CancellationToken ct = default)
        {
            SeenAtApproval = await store.LoadAsync(request.DelegationId, ct);
            return new ApprovalDecision(false, Approver, "seen");
        }
    }

    private static DelegationRunner WithProfiles(Harness h, IRoleProfileSource profiles, IDelegationStore? store = null, IPlanApprovalProvider? approval = null) =>
        new(h.Agent, profiles, approval ?? h.Approval, new RecordingAuditSink(), h.Clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<DelegationRunner>.Instance, store);

    // ---- diagnosis-only needs only Discovery and Diagnostic (S1, S2, S8, S18, S25) ----

    [Fact]
    public async Task S1_ADiagnosis_WithOnlyReadOnlyProfiles_CompletesAsADiagnosis_AndNeverLooksUpOrBeginsTheOtherRoles()
    {
        var h = Create(profiles: ReadOnlyProfiles());
        var spy = new SpyProfiles(ReadOnlyProfiles());

        var run = await WithProfiles(h, spy).StartAsync(Diagnosis(), Operator);

        Assert.Equal(DelegationStatus.DiagnosisCompleted, run.Status);
        Assert.Equal([AgentRoleKind.Discovery, AgentRoleKind.Diagnostic], run.Roles.Select(r => r.Agent.Role));
        Assert.DoesNotContain(AgentRoleKind.Remediation, spy.LookedUp);
        Assert.DoesNotContain(AgentRoleKind.Verification, spy.LookedUp);
        Assert.Empty(run.RootEnvelope.AllowedSkills);
        Assert.Empty(run.RootEnvelope.AllowedCapabilities);
        Assert.Equal(RiskLevel.Read, run.RootEnvelope.MaxRisk);
        Assert.All(run.Roles, role => Assert.Equal(RiskLevel.Read, role.Envelope.MaxRisk));
        Assert.Equal(0, h.Restart.ExecutionCount);
    }

    [Fact]
    public async Task S2_ADiagnosis_WithNoProfiles_IsDeniedOnDiscovery_BeforeAnyModelCall()
    {
        var h = Create(profiles: []);

        var run = await h.Runner.StartAsync(Diagnosis(), Operator);

        Assert.Equal(DelegationStatus.Denied, run.Status);
        Assert.Equal(EnvelopeDimension.Profile, run.Denial!.Dimension);
        Assert.Equal("Discovery role: no usable profile is configured.", run.Denial.Reason);
        Assert.Empty(h.Model.Requests);
        Assert.Empty(run.Roles);
    }

    [Fact]
    public async Task S18_S25_BroadValidRemediationAndVerificationProfiles_LeaveTheDiagnosisRootUnchanged()
    {
        var readOnly = Create(profiles: ReadOnlyProfiles());
        var broad = ReadOnlyProfiles();
        broad[AgentRoleKind.Remediation] = Profile(AgentRoleKind.Remediation, tools: ["service.restart", "service.stop", "test.read"], targets: ["local", "fleet"], risk: RiskLevel.Critical);
        broad[AgentRoleKind.Verification] = Profile(AgentRoleKind.Verification, tools: ["test.read", "host.info"], targets: ["local", "fleet"]);
        var withBroad = Create(profiles: broad);

        var first = await readOnly.Runner.StartAsync(Diagnosis(), Operator);
        var second = await withBroad.Runner.StartAsync(Diagnosis(), Operator);

        Assert.Equal(DelegationStatus.DiagnosisCompleted, second.Status);
        Assert.Equal(DelegationHasher.ComputeEnvelopeHash(first.RootEnvelope), DelegationHasher.ComputeEnvelopeHash(second.RootEnvelope));
        Assert.Equal([AgentRoleKind.Discovery, AgentRoleKind.Diagnostic], second.Roles.Select(r => r.Agent.Role));
    }

    // ---- a remediation still needs all four (S5, dry run) ----

    [Fact]
    public async Task S5_ARemediation_WithOnlyReadOnlyProfiles_IsDeniedNamingRemediation_BeforeAnyModelCall()
    {
        var h = Create(profiles: ReadOnlyProfiles());

        var run = await h.Runner.StartAsync(Request(), Operator);

        Assert.Equal(DelegationStatus.Denied, run.Status);
        Assert.Equal(EnvelopeDimension.Profile, run.Denial!.Dimension);
        Assert.Equal("Remediation role: no usable profile is configured.", run.Denial.Reason);
        Assert.Empty(h.Model.Requests);
        Assert.Equal(0, h.Restart.ExecutionCount);
    }

    [Fact]
    public async Task ADryRunRemediation_StillRequiresAllFourRoles()
    {
        var h = Create(profiles: ReadOnlyProfiles());
        var dryRun = Remediation with { Request = CapabilityInput with { DryRun = true } };

        var run = await h.Runner.StartAsync(Request(dryRun), Operator);

        Assert.Equal(DelegationStatus.Denied, run.Status);
        Assert.Equal("Remediation role: no usable profile is configured.", run.Denial!.Reason);
        Assert.Empty(h.Model.Requests);
    }

    [Fact]
    public async Task ARemediationWithAllFour_IsUnchanged_AndItsRootStillGrantsTheChange()
    {
        var h = Create();

        var run = await h.Runner.StartAsync(Request(), Operator);

        Assert.Equal(DelegationStatus.Completed, run.Status);
        Assert.Equal(["sample.skill"], run.RootEnvelope.AllowedSkills);
        Assert.Equal(["sample.remediate"], run.RootEnvelope.AllowedCapabilities);
        Assert.Equal(RiskLevel.High, run.RootEnvelope.MaxRisk);
    }

    // ---- P1, direct/fake-model path (S19, S4) ----

    [Fact]
    public async Task S19_ADiagnosisModelProposingANonReadTool_IsRefusedByTheEnvelope_BeforePolicy_AndNeverExecuted()
    {
        var policy = new SpyPolicy();
        ModelResponse[] script =
        [
            PlanningTestSupport.PlanResponse(), Call("service.restart"), PlanningTestSupport.PlanResponse(), Call("host.info"), Disclosed(),
            PlanningTestSupport.PlanResponse(), Final(FindingsJson("discovery-1")),
        ];

        // S4: the mutation tool's name is even in both read-only profiles; the role risk cap still refuses it.
        var h = Create(script, profiles: ReadOnlyProfiles(tools: ["host.info", "service.restart"]), policy: policy);

        var run = await h.Runner.StartAsync(Diagnosis(), Operator);

        Assert.Equal(DelegationStatus.DiagnosisCompleted, run.Status);
        Assert.Equal(0, h.Restart.ExecutionCount);
        Assert.DoesNotContain("service.restart", policy.Evaluated);
        Assert.Contains("host.info", policy.Evaluated);
        var refusal = Assert.Single(h.Audit.Events.OfType<PolicyDecisionAuditEvent>(), e => e.Tool == "service.restart");
        Assert.Equal(PolicyMode.Forbidden, refusal.Mode);
        Assert.StartsWith("Authority envelope:", refusal.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(h.Audit.Events.OfType<ToolCallAuditEvent>(), e => e.Tool == "service.restart" && e.Outcome == ToolOutcome.Success);

        // The refused step is a typed limitation with no Evidence id: nothing was manufactured.
        var discovery = Role(run, AgentRoleKind.Discovery);
        var limitation = Assert.Single(discovery.EvidenceLimitations!, l => l.ToolName == "service.restart");
        Assert.Null(limitation.EvidenceId);
        Assert.Equal(ToolOutcome.Failure, limitation.Outcome);
    }

    [Fact]
    public async Task S19_EveryToolThatExecutedInADiagnosis_IsRead()
    {
        var h = Create(profiles: ReadOnlyProfiles(tools: ["host.info", "service.restart", "service.stop", "test.read"]));

        await h.Runner.StartAsync(Diagnosis(), Operator);

        var executed = h.Audit.Events.OfType<ToolCallAuditEvent>().Where(e => e.Outcome == ToolOutcome.Success).Select(e => e.Tool).ToList();
        Assert.NotEmpty(executed);
        Assert.All(executed, tool => Assert.Contains(tool, ReadToolNames));
    }

    // ---- typed limitations (S20, S21, S22) and the Diagnostic reply (S14, S29) ----

    [Fact]
    public async Task S20_AFailedRead_IsATypedLimitationWithNoEvidenceId_AndOtherFindingsAreUnaffected()
    {
        var failing = new ResultReadTool("disk.read", ToolCallResult.Failure("device not ready") with { FailureKind = ToolFailureKind.Environment });
        ModelResponse[] script =
        [
            PlanningTestSupport.PlanResponse(), Call("disk.read"), Call("host.info"), Disclosed(),
            PlanningTestSupport.PlanResponse(), Final(FindingsJson("discovery-1")),
        ];
        var h = Create(script, profiles: ReadOnlyProfiles(tools: ["host.info", "disk.read"]), extraRead: failing);

        var run = await h.Runner.StartAsync(Diagnosis(), Operator);

        var discovery = Role(run, AgentRoleKind.Discovery);
        var limitation = Assert.Single(discovery.EvidenceLimitations!);
        Assert.Equal(0, limitation.StepIndex);
        Assert.Equal("disk.read", limitation.ToolName);
        Assert.Equal(ToolOutcome.Failure, limitation.Outcome);
        Assert.Equal(ToolFailureKind.Environment, limitation.FailureKind);
        Assert.Null(limitation.EvidenceId);
        Assert.Equal(0, discovery.EvidenceLimitationsOmitted);
        Assert.DoesNotContain(discovery.Report!.Evidence, e => e.Id == "discovery-0");

        var finding = Assert.Single(Role(run, AgentRoleKind.Diagnostic).Report!.Findings);
        Assert.Equal(["discovery-1"], finding.EvidenceIds);
    }

    [Fact]
    public async Task S21_ARefusedRead_IsATypedLimitation_NothingExecutes_AndNoEvidenceIdIsMade()
    {
        var outside = new ResultReadTool("disk.read", ToolCallResult.Success("never"));
        ModelResponse[] script =
        [
            PlanningTestSupport.PlanResponse(), Call("disk.read"), PlanningTestSupport.PlanResponse(), Disclosed(),
            PlanningTestSupport.PlanResponse(), Final("{\"findings\":[]}"),
        ];
        var h = Create(script, profiles: ReadOnlyProfiles(tools: ["host.info"]), extraRead: outside);

        var run = await h.Runner.StartAsync(Diagnosis(), Operator);

        Assert.Equal(0, outside.ExecutionCount);
        var limitation = Assert.Single(Role(run, AgentRoleKind.Discovery).EvidenceLimitations!);
        Assert.Equal(ToolFailureKind.Authorization, limitation.FailureKind);
        Assert.Null(limitation.EvidenceId);
        Assert.Equal(new DiagnosticReplyOutcome { Status = DiagnosticReplyStatus.Valid, Problem = DiagnosticReplyProblem.None }, Role(run, AgentRoleKind.Diagnostic).FindingsReply);
    }

    [Fact]
    public async Task S22_PartialEvidence_IsATypedLimitationCarryingItsEvidenceId_WhichAFindingCanCite()
    {
        var partial = new ResultReadTool("events.read", ToolCallResult.Success("some events") with { Completeness = ToolResultCompleteness.Partial });
        ModelResponse[] script =
        [
            PlanningTestSupport.PlanResponse(), Call("events.read"), Disclosed(),
            PlanningTestSupport.PlanResponse(), Final(FindingsJson("discovery-0")),
        ];
        var h = Create(script, profiles: ReadOnlyProfiles(tools: ["host.info", "events.read"]), extraRead: partial);

        var run = await h.Runner.StartAsync(Diagnosis(), Operator);

        var limitation = Assert.Single(Role(run, AgentRoleKind.Discovery).EvidenceLimitations!);
        Assert.Equal(ToolResultCompleteness.Partial, limitation.Completeness);
        Assert.Equal("discovery-0", limitation.EvidenceId);
        Assert.Contains("discovery-0", Assert.Single(Role(run, AgentRoleKind.Diagnostic).Report!.Findings).EvidenceIds);
    }

    [Fact]
    public async Task AModelRoleWithNoLimitations_RecordsAnEmptyList_NotNull()
    {
        var h = Create(profiles: ReadOnlyProfiles());

        var run = await h.Runner.StartAsync(Diagnosis(), Operator);

        Assert.All(run.Roles, role => Assert.NotNull(role.EvidenceLimitations));
        Assert.Empty(Role(run, AgentRoleKind.Discovery).EvidenceLimitations!);
        Assert.Null(Role(run, AgentRoleKind.Discovery).FindingsReply);
        Assert.Equal(DiagnosticReplyStatus.Valid, Role(run, AgentRoleKind.Diagnostic).FindingsReply!.Status);
    }

    [Theory]
    [InlineData("{\"findings\":[],\"findings\":[{\"summary\":\"x\",\"evidenceIds\":[\"discovery-0\"]}]}")]
    [InlineData("{\"findings\":[{\"summary\":\"a\",\"summary\":\"b\",\"evidenceIds\":[\"discovery-0\"]}]}")]
    [InlineData("{\"findings\":[{\"summary\":\"a\",\"evidenceIds\":[\"discovery-0\"],\"evidenceIds\":[]}]}")]
    [InlineData("{\"findings\":[{\"summary\":\"a\",\"evidenceIds\":[\"discovery-0\"],\"severity\":\"high\",\"severity\":\"low\"}]}")]
    public async Task S14_S29_ADuplicatePropertyInTheDiagnosticReply_IsAMalformedReplyWithZeroFindings_AndTheRunStillCompletes(string reply)
    {
        ModelResponse[] script =
        [
            PlanningTestSupport.PlanResponse(), Call("host.info"), Final(),
            PlanningTestSupport.PlanResponse(), Final(reply),
        ];
        var h = Create(script, profiles: ReadOnlyProfiles());

        var run = await h.Runner.StartAsync(Diagnosis(), Operator);

        Assert.Equal(DelegationStatus.DiagnosisCompleted, run.Status);
        var diagnostic = Role(run, AgentRoleKind.Diagnostic);
        Assert.Empty(diagnostic.Report!.Findings);
        Assert.Equal(DiagnosticReplyStatus.Malformed, diagnostic.FindingsReply!.Status);
        Assert.Equal(DiagnosticReplyProblem.DuplicateProperty, diagnostic.FindingsReply.Problem);
    }

    [Fact]
    public async Task Limitations_ArePersistedBeforeThePlanIsPutToAHuman()
    {
        var store = new MemoryDelegationStore();
        var approval = new StoreReadingApproval(store);
        var h = Create(store: store);

        await WithProfiles(h, new FixedProfiles(AllProfiles()), store, approval).StartAsync(Request(), Operator);

        var seen = approval.SeenAtApproval!;
        var diagnostic = Assert.Single(seen.Roles, r => r.Agent.Role == AgentRoleKind.Diagnostic && r.Status == DelegationRoleStatus.Completed);
        Assert.NotNull(diagnostic.EvidenceLimitations);
        Assert.NotNull(diagnostic.FindingsReply);
        Assert.NotNull(Assert.Single(seen.Roles, r => r.Agent.Role == AgentRoleKind.Discovery).EvidenceLimitations);
    }

    // ---- a named change is validated before any model call (S11, S12, S23) ----

    [Fact]
    public async Task S12_InvalidCapabilityInput_FailsTheRunBeforeDiscovery_AndNoCapabilityCodeOrModelRuns()
    {
        var h = Create();
        var invalid = Remediation with { Request = CapabilityInput with { Input = ToolArguments.FromJson(new JsonObject { ["unexpected"] = 1 }) } };

        var run = await h.Runner.StartAsync(Request(invalid), Operator);

        Assert.Equal(DelegationStatus.Failed, run.Status);
        Assert.Contains("Capability input is not valid", run.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(h.Model.Requests);
        Assert.Empty(run.Roles);
        Assert.Equal(0, h.Restart.ExecutionCount);
    }

    [Fact]
    public async Task S11_AnUnknownCapability_FailsTheRunBeforeDiscovery_EvenWhenTheProfilesNameIt()
    {
        var profiles = AllProfiles();
        profiles[AgentRoleKind.Diagnostic] = Profile(AgentRoleKind.Diagnostic, capabilities: ["sample.remediate", "sample.unknown"]);
        profiles[AgentRoleKind.Remediation] = Profile(AgentRoleKind.Remediation, capabilities: ["sample.remediate", "sample.unknown"]);
        var h = Create(profiles: profiles);

        var run = await h.Runner.StartAsync(Request(new DelegationRemediation("sample.skill", "sample.unknown", CapabilityInput)), Operator);

        Assert.Equal(DelegationStatus.Failed, run.Status);
        Assert.Empty(h.Model.Requests);
        Assert.Empty(run.Roles);
    }

    [Fact]
    public async Task S23_AChangeOutsideTheRemediationProfile_IsDeniedNamingTheDimension_BeforeAnyModelCall()
    {
        var h = Create();
        var elsewhere = Remediation with { Request = CapabilityInput with { Target = "another-host" } };

        var run = await h.Runner.StartAsync(Request(elsewhere), Operator);

        Assert.Equal(DelegationStatus.Denied, run.Status);
        Assert.Equal(EnvelopeDimension.Targets, run.Denial!.Dimension);
        Assert.Empty(h.Model.Requests);
    }

    // ---- defence in depth: a role the request does not require never begins ----

    [Theory]
    [InlineData(AgentRoleKind.Remediation)]
    [InlineData(AgentRoleKind.Verification)]
    public async Task TheBeginRoleGuard_RefusesARoleTheDiagnosisDoesNotRequire_AndEndsTheRunFailed(AgentRoleKind role)
    {
        var all = AllProfiles();
        var h = Create(profiles: all);
        var runner = h.Runner;
        var stateType = typeof(DelegationRunner).GetNestedType("RunState", BindingFlags.NonPublic)!;
        using var state = (IDisposable)Activator.CreateInstance(stateType, Guid.NewGuid(), Diagnosis(), Operator, Start)!;
        var root = EnvelopeReducer.DeriveRoot(new FixedProfiles(all), remediation: true, new DelegationAuthorityRequest(), Operator, Start).Envelope!;
        stateType.GetMethod("SetRoot")!.Invoke(state, [root]);
        var begin = typeof(DelegationRunner).GetMethod("BeginRoleAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var task = (Task)begin.Invoke(runner, [state, role, CancellationToken.None])!;
        await task;

        Assert.Null(task.GetType().GetProperty("Result")!.GetValue(task));
        Assert.Equal(DelegationStatus.Failed, stateType.GetProperty("Ended")!.GetValue(state));
        Assert.Equal(DelegationRunner.RoleNotRequired, stateType.GetProperty("Error")!.GetValue(state));
        Assert.Empty((System.Collections.IList)stateType.GetProperty("Roles")!.GetValue(state)!);
        Assert.Empty(h.Audit.Events.OfType<DelegationEnvelopeAuditEvent>());
    }

    // ---- S16: a stored pre-ADR diagnosis keeps its four-role root ----

    [Fact]
    public async Task S16_AStoredPreAdrDiagnosis_KeepsItsFourRoleRoot_AndEndsWithoutBeginningRemediationOrVerification()
    {
        var store = new MemoryDelegationStore();
        var all = AllProfiles();
        var fourRoleRoot = EnvelopeReducer.DeriveRoot(new FixedProfiles(all), remediation: true, new DelegationAuthorityRequest(), Operator, Start).Envelope!;
        var discoveryEnvelope = EnvelopeReducer.ReduceForRole(fourRoleRoot, AgentRoleKind.Discovery, all[AgentRoleKind.Discovery], new DelegationAuthorityRequest(), Start).Envelope!;
        var stored = new DelegationRun
        {
            Id = Guid.NewGuid(),
            Node = NodeId.Local,
            Actor = Operator,
            Objective = "Find out why the service stopped.",
            Status = DelegationStatus.Running,
            RootEnvelope = fourRoleRoot,
            Roles =
            [
                new DelegationRoleRun
                {
                    Agent = new AgentIdentity(AgentId.New(), AgentRoleKind.Discovery),
                    Envelope = discoveryEnvelope,
                    Status = DelegationRoleStatus.Completed,
                    Consumed = new BudgetConsumption(1, 10),
                    Report = new SkillReport([new Evidence("discovery-0", EvidenceKind.Fact, "Read 'host.info'.", "cpu 91%", "host.info", Start)], [], null),
                },
            ],
            Journal = [],
            CreatedAtUtc = Start,
            UpdatedAtUtc = Start,
        };
        await store.SaveAsync(stored);
        ModelResponse[] script = [PlanningTestSupport.PlanResponse(), Final(FindingsJson("discovery-0"))];
        var h = Create(script, profiles: ReadOnlyProfiles(), store: store);

        var run = await WithProfiles(h, new FixedProfiles(ReadOnlyProfiles()), store).ResumeAsync(stored.Id, Operator);

        Assert.Equal(DelegationStatus.DiagnosisCompleted, run.Status);
        Assert.Equal(DelegationHasher.ComputeEnvelopeHash(fourRoleRoot), DelegationHasher.ComputeEnvelopeHash(run.RootEnvelope));
        Assert.Equal([AgentRoleKind.Discovery, AgentRoleKind.Diagnostic], run.Roles.Select(r => r.Agent.Role));
        Assert.Null(run.Roles[0].EvidenceLimitations);
    }
}
