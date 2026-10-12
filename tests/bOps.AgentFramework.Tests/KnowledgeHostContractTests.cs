// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

// CA2000: ownership of every world and process passes to the test, which disposes it.
#pragma warning disable CA2000

using bOps.Abstractions;
using bOps.Knowledge.Tests;
using bOps.Runtime;

namespace bOps.AgentFramework.Tests;

/// <summary>
/// KNOW-01B (ADR-0056): what <see cref="AgentRunner"/> insists on from whatever host and session it is given, with doubles so each
/// rule is exercised alone. The runner persists only what the host admitted, accepts only a read-only runtime control function,
/// never lets a resumed task move to other content, never lets the host's failure escape, and always disposes the session.
/// </summary>
public sealed class KnowledgeHostContractTests
{
    private const string Goal = "fix the nginx 502";
    private static readonly ActorIdentity ActorA = KnowledgeRunnerWorld.ActorA;

    [Fact]
    public async Task AHostThatThrowsWhileOpeningANewTask_FailsTheTaskClosed_AndNothingEscapes()
    {
        using var world = new KnowledgeRunnerWorld();
        using var process = world.Boot([]);
        var rig = process.Runner([Script.Plan("test.read")], new DelegateHost((_, _) => throw new InvalidOperationException("host bug")));

        var state = await rig.Runner.RunAsync(Goal, ActorA);

        Assert.Equal(AgentTaskStatus.Failed, state.Status);
        Assert.Equal(TaskTerminalKind.KnowledgeUnavailable, state.TerminalReason!.Kind);
        Assert.Empty(rig.Model.Requests);
        Assert.Null(state.KnowledgeBinding);
        Assert.Contains("NotAdmitted", state.Steps[^1].Observation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("test.read", RiskLevel.Read)]
    [InlineData("runtime.evidence_read", RiskLevel.Read)]
    [InlineData("runtime.knowledge_read", RiskLevel.Medium)]
    public async Task ASessionWhoseControlFunctionIsNotAReadOnlyRuntimeFunction_IsRefused_AndDisposed(string name, RiskLevel risk)
    {
        ArgumentNullException.ThrowIfNull(name);
        using var world = new KnowledgeRunnerWorld();
        using var process = world.Boot([]);
        var session = new DoubleSession(name, risk);
        var rig = process.Runner([Script.Plan("test.read")], new DelegateHost((_, _) => KnowledgeTaskOpening.Bound(session, Binding("a"))));

        var state = await rig.Runner.RunAsync(Goal, ActorA);

        Assert.Equal(TaskTerminalKind.KnowledgeUnavailable, state.TerminalReason!.Kind);
        Assert.Empty(rig.Model.Requests);
        Assert.Null(state.KnowledgeBinding);
        Assert.Equal(1, session.Disposals);
    }

    [Fact]
    public async Task ATaskPinnedByTheHostsBinding_CannotBeResumedOntoOtherContent_EvenIfTheHostAdmitsIt()
    {
        using var world = new KnowledgeRunnerWorld();
        using var process = world.Boot([]);
        var taskId = Guid.NewGuid();
        var first = new DoubleSession();
        var firstRig = process.Runner([Script.Plan("test.read"), Script.Tool("c1", "test.read")], new DelegateHost((_, _) => KnowledgeTaskOpening.Bound(first, Binding("a"))));
        var stopped = await firstRig.Runner.RunAsync(Goal, ActorA, taskId);
        Assert.Equal(AgentTaskStatus.Failed, stopped.Status);
        Assert.Equal(Binding("a").ManifestHash, (await process.Store.LoadAsync(taskId))!.KnowledgeBinding!.ManifestHash);
        Assert.Equal(1, first.Disposals);

        // Another host (or the same one, misconfigured) answers the resume with a different manifest hash.
        var other = new DoubleSession();
        var task = (await process.Store.LoadAsync(taskId))!;
        var rig = process.Runner([], new DelegateHost((_, _) => KnowledgeTaskOpening.Bound(other, Binding("b"))));
        var state = await rig.Runner.ResumeAsync(task, ActorA);

        Assert.Equal(AgentTaskStatus.Failed, state.Status);
        Assert.Equal(TaskTerminalKind.KnowledgeUnavailable, state.TerminalReason!.Kind);
        Assert.Empty(rig.Model.Requests);
        Assert.Equal(Binding("a").ManifestHash, state.KnowledgeBinding!.ManifestHash); // the pin is never replaced
        Assert.Equal(1, other.Disposals);
    }

    [Fact]
    public async Task AHostThatBindsNothingForAPinnedTaskOnResume_IsNotAWayToDropTheKnowledge()
    {
        using var world = new KnowledgeRunnerWorld();
        using var process = world.Boot([]);
        var taskId = Guid.NewGuid();
        var firstRig = process.Runner([Script.Plan("test.read"), Script.Tool("c1", "test.read")], new DelegateHost((_, _) => KnowledgeTaskOpening.Bound(new DoubleSession(), Binding("a"))));
        await firstRig.Runner.RunAsync(Goal, ActorA, taskId);

        var task = (await process.Store.LoadAsync(taskId))!;
        var rig = process.Runner([Script.Final("done")], new DelegateHost((_, _) => KnowledgeTaskOpening.NotBound));
        var state = await rig.Runner.ResumeAsync(task, ActorA);

        Assert.Equal(AgentTaskStatus.Failed, state.Status);
        Assert.Equal(TaskTerminalKind.KnowledgeUnavailable, state.TerminalReason!.Kind);
        Assert.Empty(rig.Model.Requests);
    }

    [Fact]
    public async Task TheSessionIsDisposedExactlyOnce_WhenTheAttemptCompletes_AndWhenItFails()
    {
        using var world = new KnowledgeRunnerWorld();
        using var process = world.Boot([]);

        var completing = new DoubleSession();
        var done = await process.Runner(
            [Script.Plan("test.read"), Script.Tool("c1", "test.read"), Script.Final("ok")],
            new DelegateHost((_, _) => KnowledgeTaskOpening.Bound(completing, Binding("a")))).Runner.RunAsync(Goal, ActorA);
        Assert.Equal(AgentTaskStatus.Completed, done.Status);
        Assert.Equal(1, completing.Disposals);

        var failing = new DoubleSession();
        var failed = await process.Runner(
            [Script.Plan("test.read")],
            new DelegateHost((_, _) => KnowledgeTaskOpening.Bound(failing, Binding("a")))).Runner.RunAsync(Goal, ActorA);
        Assert.Equal(AgentTaskStatus.Failed, failed.Status);
        Assert.Equal(1, failing.Disposals);
    }

    [Fact]
    public async Task ASessionIsOfferedBesideTheStepTool_AndOnlyItsOwnNameIsHandedToIt()
    {
        using var world = new KnowledgeRunnerWorld();
        using var process = world.Boot([]);
        var session = new DoubleSession();
        var rig = process.Runner(
            [Script.Plan("test.read"), Script.Tool("c1", "test.read"), Script.Final("ok")],
            new DelegateHost((_, _) => KnowledgeTaskOpening.Bound(session, Binding("a"))));

        await rig.Runner.RunAsync(Goal, ActorA);

        // The session sees each reply of the step with the authority the runner computed; the planning call is not a step.
        Assert.NotEmpty(session.Authorities);
        Assert.All(session.Authorities, authority =>
        {
            Assert.True(authority.Eligible);
            Assert.Contains("runtime.knowledge_read", authority.OfferedTools);
        });
        // The planned step's expected tool travels with the reply; once the plan is exhausted there is none.
        Assert.Equal(["test.read", null], session.Authorities.Select(authority => authority.ExpectedTool));
        Assert.Equal(["test.read", "runtime.knowledge_read"], rig.Model.Requests[1].AvailableTools.Select(tool => tool.Name));
        Assert.DoesNotContain(rig.Model.Requests[0].AvailableTools, tool => tool.Name == "runtime.knowledge_read");
    }

    [Fact]
    public async Task ADelegatedRole_NeverReceivesKnowledge_TheHostIsNotEvenAsked()
    {
        using var world = new KnowledgeRunnerWorld();
        using var process = world.Boot([]);
        var asked = 0;
        var session = new DoubleSession();
        var rig = process.Runner(
            [Script.Plan("test.read"), Script.Tool("c1", "test.read"), Script.Final("ok")],
            new DelegateHost((_, _) =>
            {
                asked++;
                return KnowledgeTaskOpening.Bound(session, Binding("a"));
            }));
        var envelope = new AuthorityEnvelope(
            ActorA, Depth: 1, [], [], ["test.read"], RiskLevel.Read, BlastRadius.Single, ["local"], ["test"],
            new DelegationBudget(10, 100_000, DateTimeOffset.UtcNow.AddHours(1)), null);
        var scope = DelegatedExecutionScope.For(Guid.NewGuid(), new AgentIdentity(AgentId.New(), AgentRoleKind.Discovery), envelope);

        var result = await rig.Runner.RunDelegatedAsync(Goal, ActorA, scope);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(0, asked);
        Assert.Null(result.KnowledgeBinding);
        Assert.Empty(session.Authorities);
        Assert.DoesNotContain(rig.Model.Requests.SelectMany(request => request.AvailableTools), tool => tool.Name == "runtime.knowledge_read");
    }

    private static TaskKnowledgeBinding Binding(string seed) => new(
        1, ActorA, "tenant-a", "acme.nginx-runbooks", "1.0.0", "Skill", "acme", "acme-2026",
        new string(seed[0], 64), new Dictionary<string, string> { ["nginx-triage/SKILL.md"] = new string('c', 64) }, "partition/" + new string(seed[0], 64));

    private sealed class DelegateHost(Func<Guid, ActorIdentity, KnowledgeTaskOpening> open) : IKnowledgeTaskHost
    {
        public Task<KnowledgeTaskOpening> OpenForNewTaskAsync(Guid taskId, ActorIdentity actor, CancellationToken ct = default) =>
            Task.FromResult(open(taskId, actor));

        public Task<KnowledgeTaskOpening> OpenForResumeAsync(Guid taskId, ActorIdentity actor, TaskKnowledgeBinding binding, CancellationToken ct = default) =>
            Task.FromResult(open(taskId, actor));
    }

    private sealed class DoubleSession(string name = "runtime.knowledge_read", RiskLevel risk = RiskLevel.Read) : IKnowledgeReadSession
    {
        public int Disposals { get; private set; }

        public List<KnowledgeReadAuthority> Authorities { get; } = [];

        public ToolManifest Manifest { get; } = new()
        {
            Name = name,
            Description = "double",
            Risk = risk,
            Platforms = [CurrentPlatform.Id],
            Requires = [],
            Parameters = [],
        };

        public Task<KnowledgeReadOutcome> HandleAsync(
            ModelResponse response, KnowledgeReadAuthority authority, ModelInvocationContext context, CancellationToken ct = default)
        {
            Authorities.Add(authority);
            return Task.FromResult(KnowledgeReadOutcome.NotHandled);
        }

        public void Dispose() => Disposals++;
    }
}
