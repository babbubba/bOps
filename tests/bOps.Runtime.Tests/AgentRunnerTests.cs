using bOps.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// agentic/04-testing-rules.md, "Agent loop": unknown tool name, tool throwing, tool timing out,
/// policy denial, repeated policy denial, MaxSteps reached, budget exceeded, oversized output
/// truncated, malformed model output, multiple tool calls per turn — plus the V0.1 fail-closed
/// policy-absence guard (rule S3) for non-<see cref="RiskLevel.Read"/> tools.
/// </summary>
public sealed class AgentRunnerTests
{
    private static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("test-user");

    private static AgentRunner CreateRunner(
        IChatModel model, IToolRegistry registry, IAuditSink audit, AgentRunnerOptions? options = null) =>
        new(model, registry, audit, TimeProvider.System, NullLogger<AgentRunner>.Instance, options ?? new AgentRunnerOptions());

    private static ToolRegistry CreateRegistryWith(params ITool[] tools)
    {
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        foreach (var tool in tools)
        {
            registry.Register(new PackageId("test.package"), tool);
        }

        return registry;
    }

    [Fact]
    public async Task RunAsync_CompletesImmediately_WhenTheModelReturnsAFinalResponseWithNoToolCalls()
    {
        var model = new FakeChatModel(new ModelResponse("All good.", [], true, null));
        var registry = CreateRegistryWith();
        var audit = new RecordingAuditSink();

        var result = await CreateRunner(model, registry, audit).RunAsync("check things", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Single(result.Steps);
        Assert.Contains(audit.Events, e => e is ModelCallAuditEvent { Outcome: ModelCallOutcome.Success });
    }

    [Fact]
    public async Task RunAsync_ExecutesAReadTool_AndFeedsTheResultBackAsAnObservation()
    {
        var toolCall = new ModelToolCall("call-1", "test.read", ToolArguments.Empty);
        var model = new FakeChatModel(
            new ModelResponse(null, [toolCall], false, null),
            new ModelResponse("Done.", [], true, null));
        var registry = CreateRegistryWith(new FakeReadTool(output: "42% CPU"));
        var audit = new RecordingAuditSink();

        var result = await CreateRunner(model, registry, audit).RunAsync("check cpu", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Contains(model.Requests[1].History, turn => turn.Content != null && turn.Content.Contains("42% CPU", StringComparison.Ordinal));
        Assert.Contains(audit.Events, e => e is ToolCallAuditEvent { Outcome: ToolOutcome.Success, Tool: "test.read" });
    }

    [Fact]
    public async Task RunAsync_HandlesAnUnknownToolName_AsAnObservationRatherThanACrash()
    {
        var toolCall = new ModelToolCall("call-1", "does.not.exist", ToolArguments.Empty);
        var model = new FakeChatModel(
            new ModelResponse(null, [toolCall], false, null),
            new ModelResponse("Giving up.", [], true, null));
        var registry = CreateRegistryWith();
        var audit = new RecordingAuditSink();

        var result = await CreateRunner(model, registry, audit).RunAsync("do the thing", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Contains(audit.Events, e => e is ToolCallAuditEvent
        {
            Authorization: AuthorizationKind.UnknownTool,
            Outcome: ToolOutcome.Denied,
            Package.Value: "unknown",
        });
    }

    [Fact]
    public async Task RunAsync_HandlesAToolThatThrows_AsAFailureRatherThanACrash()
    {
        var toolCall = new ModelToolCall("call-1", "test.throws", ToolArguments.Empty);
        var model = new FakeChatModel(
            new ModelResponse(null, [toolCall], false, null),
            new ModelResponse("Noted the failure.", [], true, null));
        var registry = CreateRegistryWith(new ThrowingTool());
        var audit = new RecordingAuditSink();

        var result = await CreateRunner(model, registry, audit).RunAsync("do the thing", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Contains(audit.Events, e => e is ToolCallAuditEvent { Outcome: ToolOutcome.Failure, Tool: "test.throws" });
    }

    [Fact]
    public async Task RunAsync_HandlesAToolThatTimesOut_AsADistinctOutcomeFromFailure()
    {
        var toolCall = new ModelToolCall("call-1", "test.hangs", ToolArguments.Empty);
        var model = new FakeChatModel(
            new ModelResponse(null, [toolCall], false, null),
            new ModelResponse("Timed out.", [], true, null));
        var registry = CreateRegistryWith(new HangingTool());
        var audit = new RecordingAuditSink();
        var options = new AgentRunnerOptions { DefaultToolTimeout = TimeSpan.FromMilliseconds(50) };

        var result = await CreateRunner(model, registry, audit, options).RunAsync("do the thing", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Contains(audit.Events, e => e is ToolCallAuditEvent { Outcome: ToolOutcome.Timeout, Tool: "test.hangs" });
    }

    [Fact]
    public async Task RunAsync_RefusesANonReadTool_WhenNoPolicyEngineIsWiredYet()
    {
        var toolCall = new ModelToolCall("call-1", "test.highrisk", ToolArguments.Empty);
        var model = new FakeChatModel(
            new ModelResponse(null, [toolCall], false, null),
            new ModelResponse("Understood, not executing.", [], true, null));
        var registry = CreateRegistryWith(new FakeHighRiskTool());
        var audit = new RecordingAuditSink();

        var result = await CreateRunner(model, registry, audit).RunAsync("restart the thing", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Contains(audit.Events, e => e is PolicyDecisionAuditEvent { Mode: PolicyMode.Forbidden, Tool: "test.highrisk" });
    }

    [Fact]
    public async Task RunAsync_EndsAsPolicyBlocked_AfterRepeatedDenialsOfTheSameTool()
    {
        var toolCall = new ModelToolCall("call-1", "test.highrisk", ToolArguments.Empty);
        // The model keeps asking for the same forbidden tool; it never gets a chance to give up on
        // its own, so rule C4 (repeated denials terminate the task) is what has to stop this.
        var model = new FakeChatModel(Enumerable.Range(0, 10)
            .Select(_ => new ModelResponse(null, [toolCall], false, null))
            .ToArray());
        var registry = CreateRegistryWith(new FakeHighRiskTool());
        var audit = new RecordingAuditSink();
        var options = new AgentRunnerOptions { MaxConsecutivePolicyDenials = 2 };

        var result = await CreateRunner(model, registry, audit, options).RunAsync("restart the thing", Actor);

        Assert.Equal(AgentTaskStatus.PolicyBlocked, result.Status);
        Assert.Equal(2, result.Steps.Count);
        Assert.Equal(2, audit.Events.Count(e => e is PolicyDecisionAuditEvent { Mode: PolicyMode.Forbidden }));
    }

    [Fact]
    public async Task RunAsync_DoesNotBlockOnDenials_WhenTheyAreForDifferentTools()
    {
        var callA = new ModelToolCall("call-1", "test.highrisk-a", ToolArguments.Empty);
        var callB = new ModelToolCall("call-2", "test.highrisk-b", ToolArguments.Empty);
        var model = new FakeChatModel(
            new ModelResponse(null, [callA], false, null),
            new ModelResponse(null, [callB], false, null),
            new ModelResponse(null, [callA], false, null),
            new ModelResponse("Giving up.", [], true, null));
        var registry = CreateRegistryWith(new FakeHighRiskTool("test.highrisk-a"), new FakeHighRiskTool("test.highrisk-b"));
        var audit = new RecordingAuditSink();
        var options = new AgentRunnerOptions { MaxConsecutivePolicyDenials = 2 };

        var result = await CreateRunner(model, registry, audit, options).RunAsync("restart things", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
    }

    [Fact]
    public async Task RunAsync_EndsAsMaxStepsReached_WhenTheModelNeverReportsCompletion()
    {
        var toolCall = new ModelToolCall("call-1", "test.read", ToolArguments.Empty);
        var model = new FakeChatModel(Enumerable.Range(0, 10)
            .Select(_ => new ModelResponse(null, [toolCall], false, null))
            .ToArray());
        var registry = CreateRegistryWith(new FakeReadTool());
        var audit = new RecordingAuditSink();
        var options = new AgentRunnerOptions { MaxSteps = 3 };

        var result = await CreateRunner(model, registry, audit, options).RunAsync("keep checking", Actor);

        Assert.Equal(AgentTaskStatus.MaxStepsReached, result.Status);
        Assert.Equal(3, result.Steps.Count);
    }

    [Fact]
    public async Task RunAsync_EndsAsFailed_WhenTheModelThrowsAModelProtocolException()
    {
        var model = new ThrowingChatModel(new ModelProtocolException("did not return valid JSON"));
        var registry = CreateRegistryWith();
        var audit = new RecordingAuditSink();

        var result = await CreateRunner(model, registry, audit).RunAsync("do the thing", Actor);

        Assert.Equal(AgentTaskStatus.Failed, result.Status);
        Assert.Single(result.Steps);
        // ADR-0013 / rule S9: a failed model call must still be audited — a task that fails at
        // step 0 must not leave zero trace in the audit log.
        Assert.Contains(audit.Events, e => e is ModelCallAuditEvent
        {
            Outcome: ModelCallOutcome.Failure,
            ErrorMessage: "did not return valid JSON",
        });
    }

    [Fact]
    public async Task RunAsync_EndsAsFailed_WhenTheModelThrowsAnUnrelatedException()
    {
        // Rule C1: nothing thrown escapes an iteration — not only the exception type the runtime
        // happens to know about. A provider package with its own bug must not crash the loop.
        var model = new ThrowingChatModel(new InvalidOperationException("provider package bug"));
        var registry = CreateRegistryWith();
        var audit = new RecordingAuditSink();

        var result = await CreateRunner(model, registry, audit).RunAsync("do the thing", Actor);

        Assert.Equal(AgentTaskStatus.Failed, result.Status);
        Assert.Contains(audit.Events, e => e is ModelCallAuditEvent { Outcome: ModelCallOutcome.Failure });
    }

    [Fact]
    public async Task RunAsync_ExecutesOnlyTheFirstToolCall_WhenTheModelRequestsSeveralInOneTurn()
    {
        var first = new ModelToolCall("call-1", "test.read", ToolArguments.Empty);
        var second = new ModelToolCall("call-2", "test.read", ToolArguments.Empty);
        var model = new FakeChatModel(
            new ModelResponse(null, [first, second], false, null),
            new ModelResponse("Done.", [], true, null));
        var registry = CreateRegistryWith(new FakeReadTool());
        var audit = new RecordingAuditSink();

        var result = await CreateRunner(model, registry, audit).RunAsync("check twice", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Single(audit.Events.OfType<ToolCallAuditEvent>(), e => e.Outcome == ToolOutcome.Success);
        Assert.Contains(model.Requests[1].History, turn => turn.ToolCallId == "call-2" && turn.Content != null &&
            turn.Content.Contains("only one tool call", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RunAsync_EndsAsBudgetExceeded_WhenTheTokenBudgetIsExceeded()
    {
        var toolCall = new ModelToolCall("call-1", "test.read", ToolArguments.Empty);
        var usage = new ModelUsage(1000, 1000, null);
        var model = new FakeChatModel(
            new ModelResponse(null, [toolCall], false, usage),
            new ModelResponse("Should not get here.", [], true, null));
        var registry = CreateRegistryWith(new FakeReadTool());
        var audit = new RecordingAuditSink();
        var options = new AgentRunnerOptions { MaxTotalTokens = 500 };

        var result = await CreateRunner(model, registry, audit, options).RunAsync("check things", Actor);

        Assert.Equal(AgentTaskStatus.BudgetExceeded, result.Status);
        Assert.Single(result.Steps);
    }
}
