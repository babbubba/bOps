// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Cli;
using bOps.Memory;
using bOps.Policy;
using bOps.Runtime;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Cli.Tests;

/// <summary>
/// <c>bops delegate</c> driven end to end from its command line: the real parser, the real command, the real
/// <see cref="DelegationRunner"/> over a real SQLite store and a real console approval, with a scripted model and small real tools
/// in place of a provider. The composition root (policy file, role profiles, config) is proved by <see cref="CliProcessTests"/>.
/// </summary>
public sealed class DelegateCommandTests : IDisposable
{
    private static readonly ActorIdentity Operator = ActorIdentity.FromOperatingSystemUser("alice");
    private static readonly PackageId Package = new("bops.tests.sample");
    private const string RunningReading = "service is running";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"bops-cli-{Guid.NewGuid():N}");

    public DelegateCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder that cannot be removed is not a test failure.
        }
    }

    // ---- fixtures ----

    private sealed class ScriptedModel(params ModelResponse[] responses) : IChatModel
    {
        private int _calls;

        public int Calls => _calls;

        public ChatModelDescriptor Descriptor { get; } = new("fake", "fake-model");

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default) =>
            _calls >= responses.Length
                ? throw new InvalidOperationException("The scripted model has no more responses.")
                : Task.FromResult(responses[_calls++]);
    }

    private sealed class BlockingModel : IChatModel
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ChatModelDescriptor Descriptor { get; } = new("fake", "fake-model");

        public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            Reached.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class ReadTool(string name, string output) : ITool
    {
        public ToolManifest Manifest { get; } = new()
        {
            Name = name,
            Description = "Reads something.",
            Risk = RiskLevel.Read,
            Platforms = [CurrentPlatform.Id],
            Requires = [],
            Parameters = [],
        };

        public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) =>
            Task.FromResult(ToolCallResult.Success(output));
    }

    private sealed class RestartTool : IVerifiableTool
    {
        public int Executions { get; private set; }

        public ToolManifest Manifest { get; } = new()
        {
            Name = "service.restart",
            Description = "Restarts the service.",
            Risk = RiskLevel.High,
            Platforms = [CurrentPlatform.Id],
            Requires = [],
            Parameters = [],
            Verification = new VerificationSpec("test.read", [], "Reads the service state."),
        };

        public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
        {
            Executions++;
            return Task.FromResult(ToolCallResult.Success("restarted"));
        }

        public Task<VerificationOutcome> EvaluateVerificationAsync(
            ToolArguments originalArguments, ToolCallResult verificationToolResult, CancellationToken ct = default) =>
            Task.FromResult(new VerificationOutcome(
                verificationToolResult.Output == RunningReading ? VerificationStatus.Confirmed : VerificationStatus.Refuted, "read the service"));
    }

    private sealed class RestoreCapability : ICapability
    {
        public CapabilityManifest Manifest { get; } = new(
            "sample.remediate", "1.0.0", "Restores the sample service.", RiskLevel.High, [], [], [], TimeSpan.FromSeconds(5),
            SupportsDryRun: true, new VerificationSpec("test.read", [], "Reads the service state."));

        public Task<SkillReport> PrepareAsync(CapabilityRequest request, IToolInvoker toolInvoker, CancellationToken ct = default) =>
            Task.FromResult(new SkillReport(
                [new Evidence("cap-e1", EvidenceKind.Fact, "The service is down.", "down", "host.info", DateTimeOffset.UtcNow)],
                [new Finding("cap-f1", "The service is down.", ["cap-e1"], RiskLevel.High)],
                new ExecutionPlan(
                    "sample.remediate", "1.0.0", "Restart the service.",
                    [new ExecutionPlanStep(0, "service.restart", ToolArguments.Empty, "Restart the service.")])));
    }

    private sealed class SampleSkill : ISkillProvider
    {
        public string SkillId => "sample.skill";

        public IReadOnlyList<ICapability> GetCapabilities() => [new RestoreCapability()];

        public IEnumerable<ITool> GetTools() => [];
    }

    private sealed class AutomaticPolicy : IPolicyEngine
    {
        public PolicyDecision Evaluate(PolicyContext context) => new(PolicyMode.Automatic, "test");
    }

    private sealed class NeverAsked : IApprovalProvider
    {
        public Task<ApprovalDecision> RequestApprovalAsync(
            ToolManifest manifest, ToolArguments arguments, VerificationSpec? verification, string reason, CancellationToken ct = default) =>
            throw new InvalidOperationException("No step approval was expected.");
    }

    private sealed class AlwaysAvailable : ICapabilityProbe
    {
        public Task<bool> IsAvailableAsync(string capability, CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class NullAudit : IAuditSink
    {
        public Task WriteAsync(AuditEvent evt, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>The real SQLite store, until the process "dies" right after a step's intent is committed: nothing after it is stored.</summary>
    private sealed class DiesAfterIntent(SqliteDelegationStore inner) : IDelegationStore
    {
        private bool _dead;

        public Task<DelegationStartResult> StartAsync(DelegationRun run, CancellationToken ct = default) => inner.StartAsync(run, ct);

        public async Task SaveAsync(DelegationRun run, CancellationToken ct = default)
        {
            if (_dead)
            {
                return;
            }

            await inner.SaveAsync(run, ct);
            if (run.Journal.Count == 1 && run.Journal[0].Outcome is null)
            {
                _dead = true;
                throw new OperationCanceledException("The process died here.");
            }
        }

        public Task<DelegationRun?> LoadAsync(Guid delegationId, CancellationToken ct = default) => inner.LoadAsync(delegationId, ct);

        public Task<IReadOnlyList<DelegationRun>> ListByStatusAsync(DelegationStatus status, CancellationToken ct = default) => inner.ListByStatusAsync(status, ct);

        public Task<IReadOnlyList<DelegationRun>> ListRecentAsync(int limit, CancellationToken ct = default) => inner.ListRecentAsync(limit, ct);
    }

    private sealed class Profiles(Dictionary<AgentRoleKind, RoleProfile> profiles) : IRoleProfileSource
    {
        public RoleProfile? GetProfile(AgentRoleKind role) => profiles.GetValueOrDefault(role);
    }

    private static Dictionary<AgentRoleKind, RoleProfile> SampleProfiles() =>
        Enum.GetValues<AgentRoleKind>().ToDictionary(role => role, role =>
        {
            var usesSkills = role is AgentRoleKind.Diagnostic or AgentRoleKind.Remediation;
            return new RoleProfile(
                role,
                AllowedSkills: usesSkills ? ["sample.skill"] : [],
                AllowedCapabilities: usesSkills ? ["sample.remediate"] : [],
                AllowedTools: role switch
                {
                    AgentRoleKind.Remediation => ["service.restart"],
                    AgentRoleKind.Verification => ["test.read"],
                    _ => ["host.info"],
                },
                MaxRisk: role == AgentRoleKind.Remediation ? RiskLevel.High : RiskLevel.Read,
                MaxBlastRadius: BlastRadius.Single,
                AllowedTargets: ["local"],
                AllowedEnvironments: ["test"],
                MaxSteps: 10,
                MaxTokens: role is AgentRoleKind.Discovery or AgentRoleKind.Diagnostic ? 100_000 : 0,
                MaxDuration: TimeSpan.FromMinutes(10),
                Window: null);
        });

    private static ModelResponse Plan()
    {
        var steps = new System.Text.Json.Nodes.JsonArray { new System.Text.Json.Nodes.JsonObject { ["description"] = "look", ["expectedTool"] = null } };
        var json = new System.Text.Json.Nodes.JsonObject { ["rationale"] = "A plan.", ["steps"] = steps }.ToJsonString();
        return new ModelResponse(json, [], false, null);
    }

    private static ModelResponse[] HappyScript() =>
    [
        Plan(), new ModelResponse(null, [new ModelToolCall("c1", "host.info", ToolArguments.Empty)], false, null), new ModelResponse("Done.", [], true, null),
        Plan(), new ModelResponse("{\"findings\":[{\"summary\":\"The service has stopped.\",\"evidenceIds\":[\"discovery-0\"],\"severity\":\"high\"}]}", [], true, null),
    ];

    private sealed class Rig
    {
        public required DelegateCommand Command { get; init; }

        public required SqliteDelegationStore Store { get; init; }

        public required DelegationRunner Runner { get; init; }

        public required IChatModel Model { get; init; }

        public required RestartTool Restart { get; init; }

        public required StringWriter Output { get; init; }

        public required StringWriter Error { get; init; }
    }

    private Rig NewRig(
        string typed = "y\n", IRoleProfileSource? profiles = null, IChatModel? model = null, string reading = RunningReading, bool diesAfterIntent = false)
    {
        var registry = new ToolRegistry(new AlwaysAvailable());
        var restart = new RestartTool();
        registry.Register(Package, new ReadTool("host.info", "cpu 91%"));
        registry.Register(Package, new ReadTool("test.read", reading));
        registry.Register(Package, restart);
        var skills = new SkillRegistry();
        skills.Register(Package, new SampleSkill());

        var chat = model ?? new ScriptedModel(HappyScript());
        var audit = new NullAudit();
        var agent = new AgentRunner(
            chat, registry, new AutomaticPolicy(), new NeverAsked(), audit, new SqliteTaskStore(Path.Combine(_dir, $"tasks-{Guid.NewGuid():N}.db")),
            TimeProvider.System, NullLogger<AgentRunner>.Instance, new AgentRunnerOptions { MaxObservationCharacters = 1024 }, skills);
        var store = new SqliteDelegationStore(Path.Combine(_dir, "delegations.db"));
        var output = new StringWriter();
        var error = new StringWriter();
        var runner = new DelegationRunner(
            agent, profiles ?? new Profiles(SampleProfiles()), new ConsolePlanApprovalProvider(new StringReader(typed), output, Operator), audit,
            TimeProvider.System, NullLogger<DelegationRunner>.Instance, diesAfterIntent ? new DiesAfterIntent(store) : store);
        return new Rig
        {
            Command = new DelegateCommand(runner, diesAfterIntent ? new DiesAfterIntent(store) : store, Operator, output, error),
            Store = store,
            Runner = runner,
            Model = chat,
            Restart = restart,
            Output = output,
            Error = error,
        };
    }

    private static DelegateInvocation Parse(params string[] args)
    {
        var (invocation, error) = DelegateArguments.Parse(args);
        return invocation ?? throw new InvalidOperationException(error);
    }

    private static string[] Fix(params string[] extra) =>
        [.. new[] { "Find out why the service stopped and fix it", "--skill", "sample.skill", "--capability", "sample.remediate", "--target", "local", "--environment", "test" }, .. extra];

    private static AuthorityEnvelope Root() => new(
        Operator, Depth: 0, ["sample.skill"], ["sample.remediate"], ["host.info", "test.read", "service.restart"], RiskLevel.High, BlastRadius.Single,
        ["local"], ["test"], new DelegationBudget(30, 300_000, DateTimeOffset.UtcNow.AddHours(1)));

    private static async Task<Guid> SeedAsync(
        Rig rig, DelegationStatus status, IReadOnlyList<StepJournalEntry>? journal = null, bool withChange = true)
    {
        var run = new DelegationRun
        {
            Id = Guid.NewGuid(),
            Node = NodeId.Local,
            Actor = Operator,
            Objective = "Find out why the service stopped and fix it",
            Remediation = withChange
                ? new DelegationRemediationRequest("sample.skill", "sample.remediate", new CapabilityRequest(ToolArguments.Empty, "local", "test", BlastRadius.Single))
                : null,
            Status = status,
            RootEnvelope = Root(),
            Roles = [],
            Journal = journal ?? [],
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        await rig.Store.StartAsync(run);
        return run.Id;
    }

    private static StepJournalEntry Unsettled() => new()
    {
        StepIndex = 0,
        ToolName = "service.restart",
        ArgumentsHash = DelegationHasher.ComputeArgumentsHash(ToolArguments.Empty),
        IntentAtUtc = DateTimeOffset.UtcNow,
        Reconciliation = new StepReconciliation(ReconciliationAction.EscalatedToOperator, VerificationStatus.Inconclusive, ActorIdentity.RuntimeSystem, DateTimeOffset.UtcNow),
    };

    // ---- start ----

    [Fact]
    public async Task Start_ShowsThePlanAndTheAuthority_AndAYesRunsItToCompletion()
    {
        var rig = NewRig("y\nchecked\n");

        var code = await rig.Command.RunAsync(Parse(Fix()));

        Assert.Equal(0, code);
        var shown = rig.Output.ToString();
        Assert.Contains("Plan approval required", shown, StringComparison.Ordinal);
        Assert.Contains("service.restart", shown, StringComparison.Ordinal);
        Assert.Contains("Authority the change will run under", shown, StringComparison.Ordinal);
        Assert.Contains("Completed", shown, StringComparison.Ordinal);
        Assert.Contains("independent verification confirmed it", shown, StringComparison.Ordinal);
        Assert.Contains("approved by alice", shown, StringComparison.Ordinal);
        Assert.Equal(1, rig.Restart.Executions);
        Assert.Empty(rig.Error.ToString());
    }

    [Fact]
    public async Task Start_ANo_RejectsThePlan_AndNothingIsChanged()
    {
        var rig = NewRig("n\n");

        var code = await rig.Command.RunAsync(Parse(Fix()));

        Assert.Equal(3, code);
        Assert.Contains("Rejected", rig.Output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Nothing was changed", rig.Output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, rig.Restart.Executions);
    }

    [Fact]
    public async Task Start_WithoutAChange_OnlyDiagnoses_AndAsksNobody()
    {
        var rig = NewRig("");

        var code = await rig.Command.RunAsync(Parse("Why is the service slow?"));

        Assert.Equal(0, code);
        Assert.Contains("DiagnosisCompleted", rig.Output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Plan approval required", rig.Output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, rig.Restart.Executions);
    }

    [Fact]
    public async Task Start_WithNoRoleProfiles_IsDeniedOnTheProfileDimension_BeforeAnyModelCall()
    {
        var rig = NewRig(profiles: new PolicyRoleProfileSource(PolicyConfig.AllForbidden));

        var code = await rig.Command.RunAsync(Parse(Fix()));

        Assert.Equal(2, code);
        Assert.Contains("Denied", rig.Output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Refused on Profile", rig.Output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, ((ScriptedModel)rig.Model).Calls);
        Assert.Equal(0, rig.Restart.Executions);
    }

    [Fact]
    public async Task Start_UnderTheSafeDefaultPolicy_IsDeniedToo_BecauseDelegationIsOffUntilGranted()
    {
        var rig = NewRig(profiles: new PolicyRoleProfileSource(PolicyConfig.SafeDefault));

        var code = await rig.Command.RunAsync(Parse(Fix()));

        Assert.Equal(2, code);
        Assert.Contains("Refused on Profile", rig.Output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_WithTheSameIdempotencyKey_ReturnsTheEarlierRunAndRunsNothing()
    {
        var rig = NewRig("y\n");
        var first = await rig.Command.RunAsync(Parse(Fix("--idempotency-key", "ticket-42")));
        var firstOutput = rig.Output.ToString();
        var restarts = rig.Restart.Executions;

        var again = await rig.Command.RunAsync(Parse(Fix("--idempotency-key", "ticket-42")));

        Assert.Equal(0, first);
        Assert.Equal(0, again);
        Assert.Equal(restarts, rig.Restart.Executions);
        var ids = System.Text.RegularExpressions.Regex.Matches(rig.Output.ToString(), "Delegation ([0-9a-f-]{36}):").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.Single(ids);
        Assert.Contains(ids[0], firstOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_InterruptedWithCtrlC_EndsCancelled_AndIsStoredAsSuch()
    {
        var model = new BlockingModel();
        var rig = NewRig(model: model);
        using var interrupt = new CancellationTokenSource();

        var running = rig.Command.RunAsync(Parse(Fix()), interrupt.Token);
        await model.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await interrupt.CancelAsync();
        var code = await running.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(130, code);
        Assert.Contains("Cancelled", rig.Output.ToString(), StringComparison.Ordinal);
        var stored = await rig.Store.ListByStatusAsync(DelegationStatus.Cancelled);
        Assert.Single(stored);
    }

    // ---- status, resume, cancel, reconcile ----

    [Fact]
    public async Task Status_ReadsAStoredRun_WithItsRolesPlanAndJournal()
    {
        var rig = NewRig("y\n");
        await rig.Command.RunAsync(Parse(Fix()));
        var id = (await rig.Store.ListRecentAsync(1))[0].Id;
        rig.Output.GetStringBuilder().Clear();

        var code = await rig.Command.RunAsync(Parse("status", id.ToString()));

        Assert.Equal(0, code);
        var shown = rig.Output.ToString();
        Assert.Contains($"Delegation {id}: Completed", shown, StringComparison.Ordinal);
        Assert.Contains("Discovery", shown, StringComparison.Ordinal);
        Assert.Contains("Verification", shown, StringComparison.Ordinal);
        Assert.Contains("verification Confirmed", shown, StringComparison.Ordinal);
        Assert.Contains("Step 0 service.restart: Succeeded", shown, StringComparison.Ordinal);
        Assert.Contains("approved by alice", shown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_OfARunThatIsNotStored_SaysSo_OnTheErrorStream()
    {
        var rig = NewRig();

        var code = await rig.Command.RunAsync(Parse("status", Guid.NewGuid().ToString()));

        Assert.Equal(1, code);
        Assert.Contains("No delegation run", rig.Error.ToString(), StringComparison.Ordinal);
        Assert.Empty(rig.Output.ToString());
    }

    [Fact]
    public async Task Status_OfARunThatWasLeftRunning_SaysItIsNotFinished_AndWhatToDo()
    {
        var rig = NewRig();
        var id = await SeedAsync(rig, DelegationStatus.Running);

        var code = await rig.Command.RunAsync(Parse("status", id.ToString()));

        Assert.Equal(10, code);
        Assert.Contains($"bops delegate resume {id}", rig.Output.ToString(), StringComparison.Ordinal);
        Assert.Contains($"bops delegate cancel {id}", rig.Output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resume_ContinuesAStoredRun_AndPutsItsPlanToAHumanAgain()
    {
        var rig = NewRig("y\n");
        var id = await SeedAsync(rig, DelegationStatus.Running);

        var code = await rig.Command.RunAsync(Parse("resume", id.ToString()));

        Assert.Equal(0, code);
        Assert.Contains("Plan approval required", rig.Output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Completed", rig.Output.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, rig.Restart.Executions);
        Assert.Equal(1, (await rig.Store.LoadAsync(id))!.ResumeCount);
    }

    [Fact]
    public async Task Cancel_EndsAStoredRunThatWasLeftRunning_WithItsOwnExitCode()
    {
        var rig = NewRig();
        var id = await SeedAsync(rig, DelegationStatus.Running);

        var code = await rig.Command.RunAsync(Parse("cancel", id.ToString()));

        Assert.Equal(130, code);
        Assert.Contains("The run was cancelled", rig.Output.ToString(), StringComparison.Ordinal);
        Assert.Equal(DelegationStatus.Cancelled, (await rig.Store.LoadAsync(id))!.Status);
    }

    [Fact]
    public async Task Cancel_OfARunWaitingForReconciliation_PointsAtReconcile()
    {
        var rig = NewRig();
        var id = await SeedAsync(rig, DelegationStatus.RequiresReconciliation, [Unsettled()]);

        var code = await rig.Command.RunAsync(Parse("cancel", id.ToString()));

        Assert.Equal(1, code);
        Assert.Contains("reconcil", rig.Error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reconcile_OfARunThatNeedsIt_TellsTheOperatorHowToSettleIt()
    {
        var rig = NewRig();
        var id = await SeedAsync(rig, DelegationStatus.RequiresReconciliation, [Unsettled()]);

        var code = await rig.Command.RunAsync(Parse("status", id.ToString()));

        Assert.Equal(4, code);
        Assert.Contains("never retried", rig.Output.ToString(), StringComparison.Ordinal);
        Assert.Contains($"bops delegate reconcile {id} --accept", rig.Output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reconcile_Abandon_EndsTheRunForGood()
    {
        var rig = NewRig();
        var id = await SeedAsync(rig, DelegationStatus.RequiresReconciliation, [Unsettled()]);

        var code = await rig.Command.RunAsync(Parse("reconcile", id.ToString(), "--abandon", "--note", "not worth pursuing"));

        Assert.Equal(3, code);
        Assert.Contains("Abandoned", rig.Output.ToString(), StringComparison.Ordinal);
        Assert.Equal(DelegationStatus.Abandoned, (await rig.Store.LoadAsync(id))!.Status);
    }

    [Fact]
    public async Task ACrashBetweenAStepAndItsOutcome_IsSettledByTheOperator_AndTheStepIsNeverRunTwice()
    {
        // The process dies right after the intent of the restart is stored; it may or may not have happened.
        var first = NewRig("y\n", diesAfterIntent: true);
        await first.Command.RunAsync(Parse(Fix()));
        var id = (await first.Store.ListRecentAsync(1))[0].Id;
        Assert.Equal(DelegationStatus.Running, (await first.Store.LoadAsync(id))!.Status);

        // A new process reads the system: the service is not running, so the step cannot be confirmed and nobody retries it.
        var second = NewRig("y\n", reading: "service is stopped");
        var resumed = await second.Command.RunAsync(Parse("resume", id.ToString()));

        Assert.Equal(4, resumed);
        Assert.Contains("RequiresReconciliation", second.Output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Step 0 service.restart: EscalatedToOperator", second.Output.ToString(), StringComparison.Ordinal);
        Assert.Contains($"bops delegate reconcile {id} --accept", second.Output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, second.Restart.Executions);

        // The operator looked at the machine and says the step is done; the run is resumed and verifies what they accepted.
        var third = NewRig("y\n");
        var accepted = await third.Command.RunAsync(Parse("reconcile", id.ToString(), "--accept", "--note", "checked by hand"));
        Assert.Equal(10, accepted);
        Assert.Contains("Step 0 service.restart: OperatorAcceptedDone", third.Output.ToString(), StringComparison.Ordinal);

        var fourth = NewRig("y\n");
        var finished = await fourth.Command.RunAsync(Parse("resume", id.ToString()));

        Assert.True(finished == 0, fourth.Output.ToString() + fourth.Error);
        Assert.Contains("Completed", fourth.Output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Plan approval required", fourth.Output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, first.Restart.Executions + second.Restart.Executions + third.Restart.Executions + fourth.Restart.Executions);
    }

    [Fact]
    public async Task Reconcile_OfARunThatIsNotWaiting_IsRefusedWithAMessage()
    {
        var rig = NewRig();
        var id = await SeedAsync(rig, DelegationStatus.Running);

        var code = await rig.Command.RunAsync(Parse("reconcile", id.ToString(), "--abandon"));

        Assert.Equal(1, code);
        Assert.Contains("not waiting for reconciliation", rig.Error.ToString(), StringComparison.Ordinal);
    }
}
