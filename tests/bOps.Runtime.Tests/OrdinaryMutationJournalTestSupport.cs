// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>Thrown where a process would have died (F-25B crash harness); a cancellation, so it unwinds the run at once and nothing after it runs.</summary>
internal sealed class SimulatedCrashException : OperationCanceledException
{
    public SimulatedCrashException()
        : base("The process died here.")
    {
    }

    public SimulatedCrashException(string message)
        : base(message)
    {
    }

    public SimulatedCrashException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A journal-capable task store that can die at a chosen write (ADR-0051 F-25B harness): <see cref="CrashBefore"/> dies before the write
/// is persisted, <see cref="CrashAfter"/> after it, <see cref="Fail"/> fails it as a disk would without dying. Once dead, nothing more
/// is persisted. <see cref="Revive"/> is a new process opening the same store. The atomicity is <see cref="InMemoryTaskStore"/>'s
/// (one lock). Writes are named <c>save</c>, <c>create</c>, <c>transition</c>, <c>intent</c>, <c>outcome</c>, <c>reconcile</c>, <c>acquire</c>.
/// </summary>
internal sealed class CrashableJournalTaskStore(InMemoryTaskStore? inner = null) : ITaskStore, ITaskMutationJournalStore
{
    private readonly List<string> _writes = [];

    public InMemoryTaskStore Inner { get; } = inner ?? new InMemoryTaskStore();

    public bool Dead { get; private set; }

    /// <summary>Dies before the named write is persisted.</summary>
    public Func<string, bool>? CrashBefore { get; set; }

    /// <summary>Dies after the named write is persisted.</summary>
    public Func<string, bool>? CrashAfter { get; set; }

    /// <summary>Fails the named write as a full disk would, without dying; nothing is written.</summary>
    public Func<string, bool>? Fail { get; set; }

    /// <summary>Runs before the named write, outside any lock — a test's hook to interleave another writer.</summary>
    public Func<string, Task>? BeforeWrite { get; set; }

    /// <summary>Every write that reached the store (accepted or not), by name.</summary>
    public IReadOnlyList<string> Writes
    {
        get
        {
            lock (_writes)
            {
                return [.. _writes];
            }
        }
    }

    /// <summary>The process dies now: nothing more is persisted.</summary>
    public void Kill() => Dead = true;

    /// <summary>A new process opens the same store: it lives again, and nothing is set to crash.</summary>
    public void Revive()
    {
        Dead = false;
        CrashBefore = null;
        CrashAfter = null;
        Fail = null;
        BeforeWrite = null;
    }

    public IReadOnlyList<TaskMutationJournalEntry> JournalOf(Guid taskId) => Inner.JournalOf(taskId);

    public Task SaveAsync(TaskState task, CancellationToken ct = default) =>
        WriteAsync("save", async () =>
        {
            await Inner.SaveAsync(task, ct);
            return true;
        });

    public Task<bool> TryCreateAsync(TaskState task, CancellationToken ct = default) => WriteAsync("create", () => Inner.TryCreateAsync(task, ct));

    public Task<bool> TryTransitionAsync(TaskState task, AgentTaskStatus expectedStatus, int expectedExecutionAttempt, CancellationToken ct = default) =>
        WriteAsync("transition", () => Inner.TryTransitionAsync(task, expectedStatus, expectedExecutionAttempt, ct));

    public Task<bool> TryRecordIntentAsync(TaskMutationIntent intent, CancellationToken ct = default) =>
        WriteAsync("intent", () => Inner.TryRecordIntentAsync(intent, ct));

    public Task<bool> TryRecordOutcomeAsync(
        TaskMutationKey key, TaskMutationOutcome outcome, TaskMutationState state, TaskState owningTask, CancellationToken ct = default) =>
        WriteAsync("outcome", () => Inner.TryRecordOutcomeAsync(key, outcome, state, owningTask, ct));

    public Task<bool> TryReconcileAsync(Guid taskId, AgentTaskStatus expectedStatus, int expectedExecutionAttempt,
        IReadOnlyList<TaskMutationResolution> resolutions, CancellationToken ct = default) =>
        WriteAsync("reconcile", () => Inner.TryReconcileAsync(taskId, expectedStatus, expectedExecutionAttempt, resolutions, ct));

    public Task<bool> TryAcquireAsync(TaskState acquired, AgentTaskStatus expectedStatus, int expectedExecutionAttempt,
        IReadOnlyList<TaskMutationKey> recordedInHistory, CancellationToken ct = default) =>
        WriteAsync("acquire", () => Inner.TryAcquireAsync(acquired, expectedStatus, expectedExecutionAttempt, recordedInHistory, ct));

    public Task<TaskJournalSnapshot?> LoadWithJournalAsync(Guid taskId, CancellationToken ct = default) => Inner.LoadWithJournalAsync(taskId, ct);

    public Task<TaskState?> LoadAsync(Guid taskId, CancellationToken ct = default) => Inner.LoadAsync(taskId, ct);

    public Task<IReadOnlyList<TaskState>> ListByStatusAsync(AgentTaskStatus status, CancellationToken ct = default) =>
        Inner.ListByStatusAsync(status, ct);

    private async Task<bool> WriteAsync(string name, Func<Task<bool>> write)
    {
        lock (_writes)
        {
            _writes.Add(name);
        }

        if (BeforeWrite is { } hook)
        {
            await hook(name);
        }

        if (Dead)
        {
            return false;
        }

        if (Fail?.Invoke(name) == true)
        {
            throw new IOException("the disk is full");
        }

        if (CrashBefore?.Invoke(name) == true)
        {
            Dead = true;
            throw new SimulatedCrashException();
        }

        var written = await write();
        if (CrashAfter?.Invoke(name) == true)
        {
            Dead = true;
            throw new SimulatedCrashException();
        }

        return written;
    }
}

/// <summary>A task store with transitions but without the journal capability: a third-party store after ADR-0051.</summary>
internal sealed class TransitionOnlyTaskStore(InMemoryTaskStore inner) : ITaskStore, ITaskTransitionStore
{
    public InMemoryTaskStore Inner { get; } = inner;

    public Task SaveAsync(TaskState task, CancellationToken ct = default) => Inner.SaveAsync(task, ct);

    public Task<TaskState?> LoadAsync(Guid taskId, CancellationToken ct = default) => Inner.LoadAsync(taskId, ct);

    public Task<IReadOnlyList<TaskState>> ListByStatusAsync(AgentTaskStatus status, CancellationToken ct = default) => Inner.ListByStatusAsync(status, ct);

    public Task<bool> TryCreateAsync(TaskState task, CancellationToken ct = default) => Inner.TryCreateAsync(task, ct);

    public Task<bool> TryTransitionAsync(TaskState task, AgentTaskStatus expectedStatus, int expectedExecutionAttempt, CancellationToken ct = default) =>
        Inner.TryTransitionAsync(task, expectedStatus, expectedExecutionAttempt, ct);
}

/// <summary>What <see cref="CountingMutatingTool"/> does when invoked.</summary>
internal enum MutationBehaviour
{
    /// <summary>Applies its effect and returns <see cref="CountingMutatingTool.Result"/>.</summary>
    Complete,

    /// <summary>Applies half its effect, then the process dies.</summary>
    HalfThenCrash,

    /// <summary>Applies its effect, then the process dies before the tool returns.</summary>
    EffectThenCrash,

    /// <summary>Applies its effect, then waits for <see cref="CountingMutatingTool.Release"/> (or the token) before returning.</summary>
    Block,

    /// <summary>Waits until its token is cancelled (a timeout, a cancellation or the attempt budget), then reports it.</summary>
    Hang,
}

/// <summary>
/// The side-effecting tool of the F-25B harness: <c>test.mutate</c>, <see cref="RiskLevel.Medium"/>, verified by <c>test.observe</c>. It
/// counts every invocation and every applied effect, so the harness can prove a mutation never runs twice unknowingly.
/// </summary>
internal sealed class CountingMutatingTool(string name = "test.mutate", string verifier = "test.observe") : IVerifiableTool
{
    private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _executions;

    public ToolManifest Manifest { get; } = new()
    {
        Name = name,
        Description = "Changes a test service.",
        Risk = RiskLevel.Medium,
        Platforms = [CurrentPlatform.Id],
        Requires = [],
        Parameters =
        [
            new ToolParameter("target", ToolParameterType.String, "The service to change.", Required: true),
            new ToolParameter("secret", ToolParameterType.String, "A credential the change needs.", Required: false, Sensitive: true),
        ],
        Verification = new VerificationSpec(verifier, ["target"], "Observes the service."),
    };

    public int Executions => Volatile.Read(ref _executions);

    /// <summary>Whole effects applied; a half effect counts in <see cref="PartialEffects"/>.</summary>
    public int EffectsApplied { get; private set; }

    public int PartialEffects { get; private set; }

    public MutationBehaviour Behaviour { get; set; } = MutationBehaviour.Complete;

    public ToolCallResult Result { get; set; } = ToolCallResult.Success("changed");

    /// <summary>Runs where the process dies (before the crash is thrown): the harness kills the store here.</summary>
    public Action? OnCrash { get; set; }

    /// <summary>Runs at the start of every invocation (for example to advance a fake clock).</summary>
    public Action? OnExecute { get; set; }

    /// <summary>The arguments of every invocation, in order.</summary>
    public List<ToolArguments> Invocations { get; } = [];

    public Task Started => _started.Task;

    public void Release() => _release.TrySetResult();

    public void Reset()
    {
        _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _executions);
        lock (Invocations)
        {
            Invocations.Add(arguments);
        }

        OnExecute?.Invoke();
        _started.TrySetResult();
        switch (Behaviour)
        {
            case MutationBehaviour.HalfThenCrash:
                PartialEffects++;
                OnCrash?.Invoke();
                throw new SimulatedCrashException();
            case MutationBehaviour.EffectThenCrash:
                EffectsApplied++;
                OnCrash?.Invoke();
                throw new SimulatedCrashException();
            case MutationBehaviour.Block:
                EffectsApplied++;
                await _release.Task.WaitAsync(ct);
                return Result;
            case MutationBehaviour.Hang:
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return Result;
            default:
                EffectsApplied++;
                return Result;
        }
    }

    public Task<VerificationOutcome> EvaluateVerificationAsync(
        ToolArguments originalArguments, ToolCallResult verificationToolResult, CancellationToken ct = default) =>
        Task.FromResult(verificationToolResult.Succeeded && Enum.TryParse<VerificationStatus>(verificationToolResult.Output, out var status)
            ? new VerificationOutcome(status, $"observed: {status}")
            : new VerificationOutcome(VerificationStatus.Inconclusive, "the observation did not complete"));
}

/// <summary>What <see cref="CountingObserveTool"/> does when invoked.</summary>
internal enum ObserveBehaviour
{
    /// <summary>Returns <see cref="CountingObserveTool.Verdict"/>.</summary>
    Answer,

    /// <summary>Throws an ordinary exception (contained as a failed read).</summary>
    Throw,

    /// <summary>The process dies inside the read.</summary>
    Crash,

    /// <summary>Waits until its token is cancelled (its own bounded timeout).</summary>
    Hang,
}

/// <summary>The <see cref="RiskLevel.Read"/> verifier of the harness: <c>test.observe</c>; counts every invocation.</summary>
internal sealed class CountingObserveTool(string name = "test.observe") : ITool
{
    private int _verifications;

    public ToolManifest Manifest { get; } = new()
    {
        Name = name,
        Description = "Observes a test service.",
        Risk = RiskLevel.Read,
        Platforms = [CurrentPlatform.Id],
        Requires = [],
        Parameters = [new ToolParameter("target", ToolParameterType.String, "The service to observe.", Required: true)],
    };

    public int Verifications => Volatile.Read(ref _verifications);

    public VerificationStatus Verdict { get; set; } = VerificationStatus.Confirmed;

    public ObserveBehaviour Behaviour { get; set; } = ObserveBehaviour.Answer;

    /// <summary>Runs where the process dies (before the crash is thrown).</summary>
    public Action? OnCrash { get; set; }

    /// <summary>Released by the test to let a blocked observation answer; <c>null</c> answers at once.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _verifications);
        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(ct);
        }

        switch (Behaviour)
        {
            case ObserveBehaviour.Throw:
                throw new InvalidOperationException("the observation failed");
            case ObserveBehaviour.Crash:
                OnCrash?.Invoke();
                throw new SimulatedCrashException();
            case ObserveBehaviour.Hang:
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return ToolCallResult.Success("unreachable");
            default:
                return ToolCallResult.Success(Verdict.ToString());
        }
    }
}

/// <summary>
/// A model for the harness: a planning call gets a plan of <c>test.mutate</c> then <c>test.observe</c> (or <see cref="Replan"/> after a
/// replan); a step call proposes the offered tool with <see cref="Arguments"/> (or a final answer when nothing is offered). Each
/// restart builds a new one, so no in-memory state crosses a restart.
/// </summary>
internal sealed class JournalScriptModel : IChatModel
{
    private readonly List<ModelRequest> _requests = [];

    public ChatModelDescriptor Descriptor { get; } = new("fake", "journal-script");

    public IReadOnlyList<ModelRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>The planned tools of the initial plan.</summary>
    public string[] Plan { get; init; } = ["test.mutate", "test.observe"];

    /// <summary>The planned tools of every replan.</summary>
    public string[] Replan { get; init; } = ["test.observe"];

    /// <summary>The arguments of the proposed call of each tool; <c>{ "target": "svc-1" }</c> by default.</summary>
    public Func<string, int, JsonObject> Arguments { get; init; } = (_, _) => new JsonObject { ["target"] = "svc-1" };

    /// <summary>Called before a step call answers: a test's hook to block or cancel.</summary>
    public Func<ModelRequest, Task>? OnStep { get; init; }

    /// <summary>When set, answers the n-th step call (1-based) instead of the default; <c>null</c> falls back to the default.</summary>
    public Func<int, ModelRequest, ModelResponse?>? Step { get; init; }

    public int StepCalls { get; private set; }

    public int PlanningCalls { get; private set; }

    public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
    {
        lock (_requests)
        {
            _requests.Add(request);
        }

        if (IsPlanning(request))
        {
            var replan = request.SystemPrompt.Contains("Revise it", StringComparison.Ordinal);
            PlanningCalls++;
            var steps = new JsonArray();
            foreach (var (tool, i) in (replan ? Replan : Plan).Select((tool, i) => (tool, i)))
            {
                steps.Add(new JsonObject { ["description"] = $"step {i}", ["expectedTool"] = tool });
            }

            return new ModelResponse(new JsonObject { ["rationale"] = "test plan", ["steps"] = steps }.ToJsonString(), [], false, null);
        }

        var calls = ++StepCalls;
        if (OnStep is { } hook)
        {
            await hook(request);
        }

        if (Step?.Invoke(calls, request) is { } scripted)
        {
            return scripted;
        }

        if (request.AvailableTools.FirstOrDefault(tool => tool.Name != EvidenceRead.ControlManifest.Name) is { } offered)
        {
            return new ModelResponse(null,
                [new ModelToolCall($"call-{calls}", offered.Name, new ToolArguments(Arguments(offered.Name, calls)))], false, null);
        }

        return new ModelResponse("done", [], true, null);
    }

    internal static bool IsPlanning(ModelRequest request) =>
        request.SystemPrompt.Contains("lay out your plan", StringComparison.Ordinal)
        || request.SystemPrompt.Contains("Revise it", StringComparison.Ordinal);
}

/// <summary>
/// An approver for the harness: it refuses every request whose reason carries the duplicate-of-reconciled marker (so a duplicate is
/// never run by a test that does not mean it), and answers <see cref="Approves"/> to the others. It can block until released.
/// </summary>
internal sealed class MarkerAwareApprover : IApprovalProvider
{
    private readonly List<string> _reasons = [];

    public bool Approves { get; set; } = true;

    public TaskCompletionSource? Gate { get; set; }

    public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<string> Reasons
    {
        get
        {
            lock (_reasons)
            {
                return [.. _reasons];
            }
        }
    }

    public int Requests => Reasons.Count;

    public async Task<ApprovalDecision> RequestApprovalAsync(
        ToolManifest manifest, ToolArguments arguments, VerificationSpec? verification, string reason, CancellationToken ct = default)
    {
        lock (_reasons)
        {
            _reasons.Add(reason);
        }

        Waiting.TrySetResult();
        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(ct);
        }

        var duplicate = reason.Contains(MutationJournalPolicy.DuplicateOfReconciledMarker, StringComparison.Ordinal);
        return new ApprovalDecision(Approves && !duplicate, ActorIdentity.FromOperatingSystemUser("approver"),
            duplicate ? "refused: duplicates a reconciled change" : null);
    }
}

/// <summary>The harness policy: <see cref="RiskLevel.Read"/> is automatic, every other call gets <paramref name="mode"/>.</summary>
internal sealed class MutationPolicyEngine(PolicyMode mode) : IPolicyEngine
{
    public PolicyDecision Evaluate(PolicyContext context) =>
        context.Manifest.Risk == RiskLevel.Read
            ? new PolicyDecision(PolicyMode.Automatic, "test policy: Read is automatic")
            : new PolicyDecision(mode, $"test policy: {mode}");
}

/// <summary>Builds runners for the harness: every restart is a new runner over the same store and the same audit log.</summary>
internal static class JournalHarness
{
    internal static readonly ActorIdentity Operator = ActorIdentity.FromOperatingSystemUser("operator");
    internal static readonly ActorIdentity Administrator = new("api-user", "admin-1", "Administrator");

    internal static ToolRegistry Registry(params ITool[] tools)
    {
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        foreach (var tool in tools)
        {
            registry.Register(new PackageId("test.package"), tool);
        }

        return registry;
    }

    internal static AgentRunner Runner(
        IChatModel model, ITaskStore store, IAuditSink audit, IToolRegistry registry, PolicyMode policy = PolicyMode.Automatic,
        IApprovalProvider? approver = null, AgentRunnerOptions? options = null, TimeProvider? time = null,
        IEntitlementService? entitlements = null) =>
        new(model, registry, new MutationPolicyEngine(policy), approver ?? new MarkerAwareApprover(), audit, store,
            time ?? TimeProvider.System, NullLogger<AgentRunner>.Instance, options ?? new AgentRunnerOptions(), entitlementService: entitlements);

    internal static IReadOnlyList<TaskMutationAuditEvent> Mutations(this ConcurrentAuditSink audit) =>
        [.. audit.Events.OfType<TaskMutationAuditEvent>()];

    internal static IReadOnlyList<TaskLifecycleAuditEvent> Lifecycle(this ConcurrentAuditSink audit) =>
        [.. audit.Events.OfType<TaskLifecycleAuditEvent>()];

    internal static int Count(this ConcurrentAuditSink audit, TaskMutationAuditStage stage) => audit.Mutations().Count(e => e.Stage == stage);

    internal static int Count(this ConcurrentAuditSink audit, TaskLifecycleStage stage) => audit.Lifecycle().Count(e => e.Stage == stage);
}

/// <summary>A thread-safe <see cref="RecordingAuditSink"/>: the concurrency tests write from several attempts at once.</summary>
internal sealed class ConcurrentAuditSink : IAuditSink
{
    private readonly List<AuditEvent> _events = [];

    public IReadOnlyList<AuditEvent> Events
    {
        get
        {
            lock (_events)
            {
                return [.. _events];
            }
        }
    }

    public Task WriteAsync(AuditEvent evt, CancellationToken ct = default)
    {
        lock (_events)
        {
            _events.Add(evt);
        }

        return Task.CompletedTask;
    }
}
