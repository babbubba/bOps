// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Sys.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// agentic/04-testing-rules.md, "Agent loop": unknown tool name, tool throwing, tool timing out,
/// policy denial, repeated policy denial, MaxSteps reached, budget exceeded, oversized output
/// truncated, malformed model output, multiple tool calls per turn — plus the V0.1 fail-closed
/// policy-absence guard (rule S3) for non-<see cref="RiskLevel.Read"/> tools, and V0.2's explicit
/// planning and replanning (rule C8, ADR-0014).
///
/// Every task now opens with a dedicated planning call — see <see cref="PlanningTestSupport.PlanResponse"/>
/// for the canned response every test not specifically exercising planning prepends.
/// </summary>
public sealed class AgentRunnerTests
{
    private static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("test-user");

    private static AgentRunner CreateRunner(
        IChatModel model, IToolRegistry registry, IAuditSink audit, AgentRunnerOptions? options = null,
        IPolicyEngine? policyEngine = null, IApprovalProvider? approvalProvider = null, ITaskStore? taskStore = null,
        TimeProvider? timeProvider = null) =>
        new(model, registry, policyEngine ?? new DefaultTestPolicyEngine(), approvalProvider ?? new NeverCalledApprovalProvider(),
            audit, taskStore ?? new InMemoryTaskStore(), timeProvider ?? TimeProvider.System, NullLogger<AgentRunner>.Instance, options ?? new AgentRunnerOptions());

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
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(stepCount: 0),
            new ModelResponse("All good.", [], true, null));
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
            PlanningTestSupport.PlanResponse(expectedTool: "test.read"),
            new ModelResponse(null, [toolCall], false, null),
            new ModelResponse("Done.", [], true, null));
        var registry = CreateRegistryWith(new FakeReadTool(output: "42% CPU"));
        var audit = new RecordingAuditSink();

        var result = await CreateRunner(model, registry, audit).RunAsync("check cpu", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        // Requests[0] is the planning call; Requests[1] asks for the first step; Requests[2] is
        // built after the tool executed, so it is the first request that can contain the
        // observation.
        Assert.Contains(model.Requests[2].History, turn => turn.Content != null && turn.Content.Contains("42% CPU", StringComparison.Ordinal));
        Assert.Contains(audit.Events, e => e is ToolCallAuditEvent { Outcome: ToolOutcome.Success, Tool: "test.read" });
    }

    [Fact]
    public async Task RunAsync_SuppliesHostOwnedIdentityToAContextualTool()
    {
        var taskId = Guid.NewGuid();
        var tool = new RecordingContextualTool();
        var toolCall = new ModelToolCall("call-1", tool.Manifest.Name, ToolArguments.Empty);
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(expectedTool: tool.Manifest.Name),
            new ModelResponse(null, [toolCall], false, null),
            new ModelResponse("Done.", [], true, null));
        var registry = CreateRegistryWith(tool);

        var result = await CreateRunner(model, registry, new RecordingAuditSink())
            .RunAsync("capture context", Actor, taskId);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.False(tool.LegacyOverloadCalled);
        Assert.Equal(NodeId.Local, tool.ReceivedContext?.Node);
        Assert.Equal(taskId, tool.ReceivedContext?.TaskId);
        Assert.Equal(Actor, tool.ReceivedContext?.Actor);
    }

    [Fact]
    public async Task RunAsync_HandlesAnUnknownToolName_ByReplanning_RatherThanACrash()
    {
        var toolCall = new ModelToolCall("call-1", "does.not.exist", ToolArguments.Empty);
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(expectedTool: "does.not.exist"),
            new ModelResponse(null, [toolCall], false, null),
            PlanningTestSupport.PlanResponse(revision: 1), // the replan an unresolved tool name triggers
            new ModelResponse("Giving up.", [], true, null));
        var registry = CreateRegistryWith();
        var audit = new RecordingAuditSink();

        var result = await CreateRunner(model, registry, audit).RunAsync("do the thing", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(2, result.Plans.Count);
        Assert.Contains(audit.Events, e => e is ToolCallAuditEvent
        {
            Authorization: AuthorizationKind.UnknownTool,
            Outcome: ToolOutcome.Denied,
            Package.Value: "unknown",
        });
    }

    [Fact]
    public async Task RunAsync_HandlesAToolThatThrows_AsAFailureRatherThanACrash_WithoutForcingAReplan()
    {
        // A plain tool Failure is excluded from the replan trigger (agentic/01-architecture-rules.md,
        // rule C8): the model already sees the failure as its next observation and routinely
        // corrects course without needing a whole new plan.
        var toolCall = new ModelToolCall("call-1", "test.throws", ToolArguments.Empty);
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(expectedTool: "test.throws"),
            new ModelResponse(null, [toolCall], false, null),
            new ModelResponse("Noted the failure.", [], true, null));
        var registry = CreateRegistryWith(new ThrowingTool());
        var audit = new RecordingAuditSink();

        var result = await CreateRunner(model, registry, audit).RunAsync("do the thing", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Single(result.Plans);
        Assert.Contains(audit.Events, e => e is ToolCallAuditEvent { Outcome: ToolOutcome.Failure, Tool: "test.throws" });
    }

    [Fact]
    public async Task RunAsync_HandlesAToolThatTimesOut_ByReplanning()
    {
        var toolCall = new ModelToolCall("call-1", "test.hangs", ToolArguments.Empty);
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(expectedTool: "test.hangs"),
            new ModelResponse(null, [toolCall], false, null),
            PlanningTestSupport.PlanResponse(revision: 1, expectedTool: "test.hangs"),
            new ModelResponse("Timed out.", [], true, null));
        var registry = CreateRegistryWith(new HangingTool());
        var audit = new RecordingAuditSink();
        var options = new AgentRunnerOptions { DefaultToolTimeout = TimeSpan.FromMilliseconds(50) };

        var result = await CreateRunner(model, registry, audit, options).RunAsync("do the thing", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(2, result.Plans.Count);
        Assert.Contains(audit.Events, e => e is ToolCallAuditEvent { Outcome: ToolOutcome.Timeout, Tool: "test.hangs" });
    }

    [Fact]
    public async Task RunAsync_UsesTheDefaultTimeout_ForAnotherSystemToolWithoutAnOverride()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var tool = new ControlledReadTool(SystemToolManifests.Cpu(CurrentPlatform.Id));
        var call = new ModelToolCall("call-1", tool.Manifest.Name, ToolArguments.Empty);
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(expectedTool: tool.Manifest.Name),
            new ModelResponse(null, [call], false, null),
            PlanningTestSupport.PlanResponse(revision: 1),
            new ModelResponse("Timed out.", [], true, null));
        var audit = new RecordingAuditSink();
        var options = new AgentRunnerOptions
        {
            DefaultToolTimeout = TimeSpan.FromSeconds(30),
            MaxToolTimeout = TimeSpan.FromMinutes(15),
        };

        var registry = CreateRegistryWith(tool);
        await registry.RefreshCapabilitiesAsync();
        var run = CreateRunner(model, registry, audit, options, timeProvider: clock)
            .RunAsync("wait", Actor);
        await tool.Started;
        clock.Advance(TimeSpan.FromSeconds(30));
        var result = await run;

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Contains(audit.Events, e => e is ToolCallAuditEvent
        {
            Tool: "system.cpu",
            Outcome: ToolOutcome.Timeout,
            EffectiveTimeout: var timeout,
        } && timeout == TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task RunAsync_HonoursDumpAnalyzeDeclaredTimeout_PastTheDefault()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var tool = new ControlledReadTool(SystemToolManifests.DumpAnalyze(CurrentPlatform.Id, "test.debugger"));
        var call = new ModelToolCall("call-1", tool.Manifest.Name, DumpAnalyzeArguments());
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(expectedTool: tool.Manifest.Name),
            new ModelResponse(null, [call], false, null),
            new ModelResponse("Done.", [], true, null));
        var audit = new RecordingAuditSink();
        var options = new AgentRunnerOptions
        {
            DefaultToolTimeout = TimeSpan.FromSeconds(30),
            MaxToolTimeout = TimeSpan.FromMinutes(15),
        };

        var registry = CreateRegistryWith(tool);
        await registry.RefreshCapabilitiesAsync();
        var run = CreateRunner(model, registry, audit, options, timeProvider: clock)
            .RunAsync("wait", Actor);
        await tool.Started;
        clock.Advance(TimeSpan.FromSeconds(31));
        await Task.Yield();
        Assert.False(run.IsCompleted);

        tool.Complete();
        var result = await run;

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Contains(audit.Events, e => e is ToolCallAuditEvent
        {
            Tool: "system.dump_analyze",
            Outcome: ToolOutcome.Success,
            EffectiveTimeout: var timeout,
        } && timeout == TimeSpan.FromMinutes(12));
    }

    [Fact]
    public async Task RunAsync_ClampsDumpAnalyzeDeclaredTimeout_ToTheHostCeiling()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var tool = new ControlledReadTool(SystemToolManifests.DumpAnalyze(CurrentPlatform.Id, "test.debugger"));
        var call = new ModelToolCall("call-1", tool.Manifest.Name, DumpAnalyzeArguments());
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(expectedTool: tool.Manifest.Name),
            new ModelResponse(null, [call], false, null),
            PlanningTestSupport.PlanResponse(revision: 1),
            new ModelResponse("Timed out.", [], true, null));
        var audit = new RecordingAuditSink();
        var options = new AgentRunnerOptions
        {
            DefaultToolTimeout = TimeSpan.FromSeconds(30),
            MaxToolTimeout = TimeSpan.FromMinutes(1),
        };

        var registry = CreateRegistryWith(tool);
        await registry.RefreshCapabilitiesAsync();
        var run = CreateRunner(model, registry, audit, options, timeProvider: clock)
            .RunAsync("wait", Actor);
        await tool.Started;
        clock.Advance(TimeSpan.FromSeconds(59));
        await Task.Yield();
        Assert.False(run.IsCompleted);

        clock.Advance(TimeSpan.FromSeconds(1));
        var completed = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.Same(run, completed);
        var result = await run;

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Contains(audit.Events, e => e is ToolCallAuditEvent
        {
            Tool: "system.dump_analyze",
            Outcome: ToolOutcome.Timeout,
            EffectiveTimeout: var timeout,
        } && timeout == TimeSpan.FromMinutes(1));
    }

    private static ToolArguments DumpAnalyzeArguments() => ToolArguments.FromJson(new JsonObject
    {
        ["path"] = @"C:\Windows\Minidump\a.dmp",
    });

    [Fact]
    public async Task RunAsync_ExternalCancellationWinsOverALongerDeclaredTimeout()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var tool = new ControlledReadTool("test.cancelled-timeout", TimeSpan.FromMinutes(10));
        var call = new ModelToolCall("call-1", tool.Manifest.Name, ToolArguments.Empty);
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(expectedTool: tool.Manifest.Name),
            new ModelResponse(null, [call], false, null));
        var options = new AgentRunnerOptions
        {
            DefaultToolTimeout = TimeSpan.FromSeconds(30),
            MaxToolTimeout = TimeSpan.FromMinutes(15),
        };
        using var cancellation = new CancellationTokenSource();

        var run = CreateRunner(model, CreateRegistryWith(tool), new RecordingAuditSink(), options, timeProvider: clock)
            .RunAsync("wait", Actor, ct: cancellation.Token);
        await tool.Started;
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task RunAsync_RefusesANonReadTool_WhenNoPolicyEngineIsWiredYet_AndReplans()
    {
        var toolCall = new ModelToolCall("call-1", "test.highrisk", ToolArguments.Empty);
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(expectedTool: "test.highrisk"),
            new ModelResponse(null, [toolCall], false, null),
            PlanningTestSupport.PlanResponse(revision: 1, expectedTool: "test.highrisk"),
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
        // The model keeps asking for the same forbidden tool. A single denial gets the model a
        // fresh plan (rule C8); a second consecutive denial of the very same tool is what rule C4
        // stops, not letting the model retry forever.
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(expectedTool: "test.highrisk"),
            new ModelResponse(null, [toolCall], false, null),
            PlanningTestSupport.PlanResponse(revision: 1, expectedTool: "test.highrisk"),
            new ModelResponse(null, [toolCall], false, null));
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
            PlanningTestSupport.PlanResponse(expectedTool: "test.highrisk-a"),
            new ModelResponse(null, [callA], false, null),
            PlanningTestSupport.PlanResponse(revision: 1, expectedTool: "test.highrisk-b"),
            new ModelResponse(null, [callB], false, null),
            PlanningTestSupport.PlanResponse(revision: 2, expectedTool: "test.highrisk-a"),
            new ModelResponse(null, [callA], false, null),
            PlanningTestSupport.PlanResponse(revision: 3, expectedTool: "test.highrisk-a"),
            new ModelResponse("Giving up.", [], true, null));
        var registry = CreateRegistryWith(new FakeHighRiskTool("test.highrisk-a"), new FakeHighRiskTool("test.highrisk-b"));
        var audit = new RecordingAuditSink();
        // Every denial here replans (rule C8); three different-tool denials need three replans,
        // one more than the AgentRunnerOptions default, so this is raised explicitly rather than
        // relying on the default happening to be just high enough.
        var options = new AgentRunnerOptions { MaxConsecutivePolicyDenials = 2, MaxReplans = 5 };

        var result = await CreateRunner(model, registry, audit, options).RunAsync("restart things", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
    }

    [Fact]
    public async Task RunAsync_EndsAsMaxStepsReached_WhenTheModelNeverReportsCompletion()
    {
        var model = new FakeChatModel([
            PlanningTestSupport.PlanResponseIndexed("test.read", 10), // never exhausted within MaxSteps
            .. Enumerable.Range(0, 10).Select(i => new ModelResponse(
                null, [new ModelToolCall("call-1", "test.read", new ToolArguments(PlanningTestSupport.IndexArguments(i)))], false, null)),
        ]);
        var registry = CreateRegistryWith(new FakeReadTool(parameters: [PlanningTestSupport.CallIndexParameter]));
        var audit = new RecordingAuditSink();
        var options = new AgentRunnerOptions { MaxSteps = 3 };

        var result = await CreateRunner(model, registry, audit, options).RunAsync("keep checking", Actor);

        Assert.Equal(AgentTaskStatus.MaxStepsReached, result.Status);
        Assert.Equal(3, result.Steps.Count);
    }

    [Fact]
    public async Task RunAsync_EndsAsFailed_WhenTheModelThrowsAModelProtocolException()
    {
        // ThrowingChatModel throws on every call, including the very first planning call — the
        // task must fail cleanly at that point too, not only when a step call throws.
        var model = new ThrowingChatModel(new ModelProtocolException("did not return valid JSON"));
        var registry = CreateRegistryWith();
        var audit = new RecordingAuditSink();

        var result = await CreateRunner(model, registry, audit).RunAsync("do the thing", Actor);

        Assert.Equal(AgentTaskStatus.Failed, result.Status);
        Assert.Single(result.Steps);
        Assert.Empty(result.Plans);
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
            PlanningTestSupport.PlanResponse(expectedTool: "test.read"),
            new ModelResponse(null, [first, second], false, null),
            new ModelResponse("Done.", [], true, null));
        var registry = CreateRegistryWith(new FakeReadTool());
        var audit = new RecordingAuditSink();

        var result = await CreateRunner(model, registry, audit).RunAsync("check twice", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Single(audit.Events.OfType<ToolCallAuditEvent>(), e => e.Outcome == ToolOutcome.Success);
        Assert.Contains(model.Requests[2].History, turn => turn.ToolCallId == "call-2" && turn.Content != null &&
            turn.Content.Contains("only one tool call", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RunAsync_RedactsSensitiveArguments_BeforeTheyReachTheAuditLog()
    {
        // agentic/04-testing-rules.md, "Audit": a Sensitive argument is redacted before reaching
        // the sink (agentic/03-security-rules.md, rule S6).
        var parameters = new[] { new ToolParameter("password", ToolParameterType.String, "A secret.", Sensitive: true) };
        var tool = new FakeReadTool(parameters: parameters);
        var toolCall = new ModelToolCall("call-1", "test.read", ToolArguments.FromJson(new JsonObject { ["password"] = "hunter2" }));
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(expectedTool: "test.read"),
            new ModelResponse(null, [toolCall], false, null),
            new ModelResponse("Done.", [], true, null));
        var registry = CreateRegistryWith(tool);
        var audit = new RecordingAuditSink();

        var result = await CreateRunner(model, registry, audit).RunAsync("do the sensitive thing", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        var toolCallEvent = Assert.Single(audit.Events.OfType<ToolCallAuditEvent>());
        Assert.DoesNotContain("hunter2", toolCallEvent.Arguments.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_EndsAsBudgetExceeded_WhenTheTokenBudgetIsExceeded()
    {
        var toolCall = new ModelToolCall("call-1", "test.read", ToolArguments.Empty);
        var usage = new ModelUsage(1000, 1000, null);
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(expectedTool: "test.read"),
            new ModelResponse(null, [toolCall], false, usage),
            new ModelResponse("Should not get here.", [], true, null));
        var registry = CreateRegistryWith(new FakeReadTool());
        var audit = new RecordingAuditSink();
        var options = new AgentRunnerOptions { MaxTotalTokens = 500 };

        var result = await CreateRunner(model, registry, audit, options).RunAsync("check things", Actor);

        Assert.Equal(AgentTaskStatus.BudgetExceeded, result.Status);
        Assert.Single(result.Steps);
    }

    // ---- V0.3: policy engine and approval flow (rule S3, ADR-0015) ----

    [Fact]
    public async Task RunAsync_ExecutesTheTool_WhenApprovalIsGranted()
    {
        var toolCall = new ModelToolCall("call-1", "test.highrisk", ToolArguments.Empty);
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(expectedTool: "test.highrisk"),
            new ModelResponse(null, [toolCall], false, null),
            new ModelResponse("Done.", [], true, null));
        var registry = CreateRegistryWith(new FakeHighRiskTool());
        var audit = new RecordingAuditSink();
        var policy = new StubPolicyEngine(PolicyMode.Approval, "requires approval for test");
        var approval = new StubApprovalProvider(approved: true, note: "looks fine");

        var result = await CreateRunner(model, registry, audit, policyEngine: policy, approvalProvider: approval)
            .RunAsync("restart the thing", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Contains(audit.Events, e => e is PolicyDecisionAuditEvent { Mode: PolicyMode.Approval });
        Assert.Contains(audit.Events, e => e is ApprovalAuditEvent { Approved: true, Note: "looks fine" });
        Assert.Contains(audit.Events, e => e is ToolCallAuditEvent
        {
            Outcome: ToolOutcome.Success,
            Authorization: AuthorizationKind.UserApproved,
            Tool: "test.highrisk",
        });
    }

    [Fact]
    public async Task RunAsync_DeniesTheTool_WhenApprovalIsRejected_AndReplans()
    {
        var toolCall = new ModelToolCall("call-1", "test.highrisk", ToolArguments.Empty);
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(expectedTool: "test.highrisk"),
            new ModelResponse(null, [toolCall], false, null),
            PlanningTestSupport.PlanResponse(revision: 1),
            new ModelResponse("Understood, not executing.", [], true, null));
        var registry = CreateRegistryWith(new FakeHighRiskTool());
        var audit = new RecordingAuditSink();
        var policy = new StubPolicyEngine(PolicyMode.Approval, "requires approval for test");
        var approval = new StubApprovalProvider(approved: false, note: "too risky right now");

        var result = await CreateRunner(model, registry, audit, policyEngine: policy, approvalProvider: approval)
            .RunAsync("restart the thing", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(2, result.Plans.Count); // the rejection triggered a replan
        Assert.Contains(audit.Events, e => e is ApprovalAuditEvent { Approved: false, Note: "too risky right now" });
        Assert.Contains(audit.Events, e => e is ToolCallAuditEvent
        {
            Outcome: ToolOutcome.Denied,
            Authorization: AuthorizationKind.UserRejected,
            Tool: "test.highrisk",
        });
        // Policy said Approval, not Forbidden — a rejected approval must not be misreported as a policy denial.
        Assert.DoesNotContain(audit.Events, e => e is PolicyDecisionAuditEvent { Mode: PolicyMode.Forbidden });
    }

    [Fact]
    public async Task RunAsync_NeverRequestsApproval_WhenPolicyForbidsOutright()
    {
        var toolCall = new ModelToolCall("call-1", "test.highrisk", ToolArguments.Empty);
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(expectedTool: "test.highrisk"),
            new ModelResponse(null, [toolCall], false, null),
            PlanningTestSupport.PlanResponse(revision: 1),
            new ModelResponse("Understood, not executing.", [], true, null));
        var registry = CreateRegistryWith(new FakeHighRiskTool());
        var audit = new RecordingAuditSink();
        var policy = new StubPolicyEngine(PolicyMode.Forbidden, "forbidden for test");

        // NeverCalledApprovalProvider (the CreateRunner default) throws if approval is ever
        // requested — proving a Forbidden decision short-circuits before reaching it.
        var result = await CreateRunner(model, registry, audit, policyEngine: policy).RunAsync("restart the thing", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Contains(audit.Events, e => e is PolicyDecisionAuditEvent { Mode: PolicyMode.Forbidden });
        Assert.DoesNotContain(audit.Events, e => e is ApprovalAuditEvent);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(-1)]
    [InlineData(99)]
    public async Task RunAsync_TreatsAnUndefinedPolicyModeAsForbidden_NeverAsAutomatic(int undefinedMode)
    {
        // Rule S3: an unknown value resolves to Forbidden. A mode outside the enum used to fall through both the
        // Forbidden and the Approval branch and execute unattended.
        var toolCall = new ModelToolCall("call-1", "test.highrisk", ToolArguments.Empty);
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(expectedTool: "test.highrisk"),
            new ModelResponse(null, [toolCall], false, null),
            PlanningTestSupport.PlanResponse(revision: 1),
            new ModelResponse("Understood, not executing.", [], true, null));
        var audit = new RecordingAuditSink();
        var policy = new StubPolicyEngine((PolicyMode)undefinedMode, "undefined for test");

        // The default approval provider throws if approval is ever requested.
        var result = await CreateRunner(model, CreateRegistryWith(new FakeHighRiskTool()), audit, policyEngine: policy)
            .RunAsync("restart the thing", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        var decision = Assert.Single(audit.Events.OfType<PolicyDecisionAuditEvent>());
        Assert.Equal(PolicyMode.Forbidden, decision.Mode);
        Assert.Contains("undefined", decision.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(audit.Events, e => e is ToolCallAuditEvent { Outcome: ToolOutcome.Success });
        Assert.Contains(audit.Events, e => e is ToolCallAuditEvent { Outcome: ToolOutcome.Denied, Tool: "test.highrisk" });
        Assert.DoesNotContain(audit.Events, e => e is ApprovalAuditEvent);
    }

    [Fact]
    public async Task RunAsync_RequiresApproval_WhenManifestTightensAutomaticPolicy()
    {
        var toolCall = new ModelToolCall("call-1", "test.approval-bound", ToolArguments.Empty);
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(expectedTool: "test.approval-bound"),
            new ModelResponse(null, [toolCall], false, null),
            new ModelResponse("Done.", [], true, null));
        var tool = new ApprovalBoundHighRiskTool();
        var registry = CreateRegistryWith(tool);
        var audit = new RecordingAuditSink();

        var result = await CreateRunner(
            model,
            registry,
            audit,
            policyEngine: new StubPolicyEngine(PolicyMode.Automatic, "configured automatic"),
            approvalProvider: new StubApprovalProvider(approved: true))
            .RunAsync("delete the approved set", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Single(tool.ApprovalDecisions);
        Assert.True(tool.ApprovalDecisions[0].Approved);
        Assert.Equal(1, tool.ExecutionCount);
        Assert.Contains(audit.Events, e => e is PolicyDecisionAuditEvent
        {
            Tool: "test.approval-bound",
            Mode: PolicyMode.Approval,
        });
        Assert.Contains(audit.Events, e => e is ToolCallAuditEvent
        {
            Tool: "test.approval-bound",
            Authorization: AuthorizationKind.UserApproved,
        });
    }

    [Fact]
    public async Task RunAsync_RecordsRejectedDecisionOnApprovalBoundTool_WithoutExecuting()
    {
        var toolCall = new ModelToolCall("call-1", "test.approval-bound", ToolArguments.Empty);
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(expectedTool: "test.approval-bound"),
            new ModelResponse(null, [toolCall], false, null),
            PlanningTestSupport.PlanResponse(revision: 1),
            new ModelResponse("Stopped.", [], true, null));
        var tool = new ApprovalBoundHighRiskTool();
        var registry = CreateRegistryWith(tool);

        var result = await CreateRunner(
            model,
            registry,
            new RecordingAuditSink(),
            policyEngine: new StubPolicyEngine(PolicyMode.Automatic),
            approvalProvider: new StubApprovalProvider(approved: false))
            .RunAsync("delete the approved set", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Single(tool.ApprovalDecisions);
        Assert.False(tool.ApprovalDecisions[0].Approved);
        Assert.Equal(0, tool.ExecutionCount);
    }

    [Fact]
    public async Task RunAsync_RejectsMalformedPathListAndUnknownArguments_BeforeExecution()
    {
        var arguments = ToolArguments.FromJson(new JsonObject
        {
            ["paths"] = "not-an-array",
            ["unexpected"] = true,
        });
        var toolCall = new ModelToolCall("call-1", "test.paths", arguments);
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(expectedTool: "test.paths"),
            new ModelResponse(null, [toolCall], false, null),
            new ModelResponse("Done.", [], true, null));
        var tool = new RecordingReadTool(
            "test.paths",
            [new ToolParameter("paths", ToolParameterType.PathList, "Paths.")]);
        var registry = CreateRegistryWith(tool);

        var result = await CreateRunner(model, registry, new RecordingAuditSink())
            .RunAsync("inspect paths", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(0, tool.ExecutionCount);
        Assert.Contains("Unknown argument 'unexpected'", result.Steps[0].Result!.ErrorMessage, StringComparison.Ordinal);
    }

    // ---- V0.4: post-action verification (rule S4, principle 3) ----

    [Fact]
    public async Task RunAsync_RecordsConfirmedVerification_AndFeedsItBackAsAnObservation()
    {
        var toolCall = new ModelToolCall("call-1", "test.highrisk", ToolArguments.Empty);
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(expectedTool: "test.highrisk"),
            new ModelResponse(null, [toolCall], false, null),
            new ModelResponse("Done.", [], true, null));
        var registry = CreateRegistryWith(new FakeHighRiskTool());
        var audit = new RecordingAuditSink();
        var policy = new StubPolicyEngine(PolicyMode.Automatic);

        var result = await CreateRunner(model, registry, audit, policyEngine: policy).RunAsync("restart the thing", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(VerificationStatus.Confirmed, result.Steps[0].VerificationStatus);
        Assert.Contains(audit.Events, e => e is ToolCallAuditEvent { Tool: "test.highrisk", Verification: VerificationStatus.Confirmed });
        Assert.Contains(model.Requests[2].History, turn =>
            turn.Content != null && turn.Content.Contains("Verification: Confirmed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_Replans_WhenVerificationIsRefuted()
    {
        var toolCall = new ModelToolCall("call-1", "test.highrisk", ToolArguments.Empty);
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(expectedTool: "test.highrisk"),
            new ModelResponse(null, [toolCall], false, null),
            PlanningTestSupport.PlanResponse(revision: 1), // a refuted verification is a deviation (rule C8)
            new ModelResponse("Trying something else.", [], true, null));
        var tool = new FakeHighRiskTool(verificationOutcome: new VerificationOutcome(VerificationStatus.Refuted, "still stopped"));
        var registry = CreateRegistryWith(tool);
        var audit = new RecordingAuditSink();
        var policy = new StubPolicyEngine(PolicyMode.Automatic);

        var result = await CreateRunner(model, registry, audit, policyEngine: policy).RunAsync("restart the thing", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(2, result.Plans.Count);
        Assert.Equal(VerificationStatus.Refuted, result.Steps[0].VerificationStatus);
        Assert.Contains(audit.Events, e => e is ToolCallAuditEvent { Tool: "test.highrisk", Verification: VerificationStatus.Refuted });
        Assert.Contains(model.Requests[2].History, turn =>
            turn.Content != null && turn.Content.Contains("Verification: Refuted — still stopped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_DoesNotReplan_WhenVerificationIsInconclusive()
    {
        // rule S4: Inconclusive is never treated as success, but it is not proof the plan's
        // assumption was wrong either — it is real information handed to the model, which may
        // well proceed without a whole new plan.
        var toolCall = new ModelToolCall("call-1", "test.highrisk", ToolArguments.Empty);
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(expectedTool: "test.highrisk"),
            new ModelResponse(null, [toolCall], false, null),
            new ModelResponse("Noted, moving on.", [], true, null));
        var tool = new FakeHighRiskTool(verificationOutcome: new VerificationOutcome(VerificationStatus.Inconclusive, "cannot tell"));
        var registry = CreateRegistryWith(tool);
        var audit = new RecordingAuditSink();
        var policy = new StubPolicyEngine(PolicyMode.Automatic);

        var result = await CreateRunner(model, registry, audit, policyEngine: policy).RunAsync("restart the thing", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Single(result.Plans);
        Assert.Equal(VerificationStatus.Inconclusive, result.Steps[0].VerificationStatus);
        Assert.Contains(audit.Events, e => e is ToolCallAuditEvent { Tool: "test.highrisk", Verification: VerificationStatus.Inconclusive });
    }

    [Fact]
    public async Task RunAsync_RecordsInconclusiveVerification_WhenTheVerificationToolItselfFails()
    {
        // agentic/04-testing-rules.md, "Verification": "a verification tool that itself fails."
        var toolCall = new ModelToolCall("call-1", "test.inspecting", ToolArguments.Empty);
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(expectedTool: "test.inspecting"),
            new ModelResponse(null, [toolCall], false, null),
            new ModelResponse("Done.", [], true, null));
        var registry = CreateRegistryWith(new InspectingVerifiableTool("test.inspecting", "test.throws"), new ThrowingTool());
        var audit = new RecordingAuditSink();
        var policy = new StubPolicyEngine(PolicyMode.Automatic);

        var result = await CreateRunner(model, registry, audit, policyEngine: policy).RunAsync("restart the thing", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Contains(audit.Events, e => e is ToolCallAuditEvent { Tool: "test.inspecting", Verification: VerificationStatus.Inconclusive });
    }

    [Fact]
    public async Task RunAsync_RecordsInconclusiveVerification_WhenTheVerificationToolIsNotRegistered()
    {
        // A package can legitimately declare a VerifyToolName that turns out not to be resolvable
        // at runtime (disabled package, wrong platform) — the runtime must not crash, and cannot
        // report Confirmed on the strength of nothing (rule S4).
        var toolCall = new ModelToolCall("call-1", "test.inspecting", ToolArguments.Empty);
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(expectedTool: "test.inspecting"),
            new ModelResponse(null, [toolCall], false, null),
            new ModelResponse("Done.", [], true, null));
        var registry = CreateRegistryWith(new InspectingVerifiableTool("test.inspecting", "does.not.exist"));
        var audit = new RecordingAuditSink();
        var policy = new StubPolicyEngine(PolicyMode.Automatic);

        var result = await CreateRunner(model, registry, audit, policyEngine: policy).RunAsync("restart the thing", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Contains(audit.Events, e => e is ToolCallAuditEvent { Tool: "test.inspecting", Verification: VerificationStatus.Inconclusive });
    }

    [Fact]
    public async Task RunAsync_RecordsInconclusiveVerification_WhenEvaluatingVerificationThrows()
    {
        // Rule C1 applied to verification: a package's IVerifiableTool implementation is
        // third-party code too and must not be able to crash the loop.
        var toolCall = new ModelToolCall("call-1", "test.throwing-verification", ToolArguments.Empty);
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(expectedTool: "test.throwing-verification"),
            new ModelResponse(null, [toolCall], false, null),
            new ModelResponse("Done.", [], true, null));
        var registry = CreateRegistryWith(new ThrowingVerificationTool());
        var audit = new RecordingAuditSink();
        var policy = new StubPolicyEngine(PolicyMode.Automatic);

        var result = await CreateRunner(model, registry, audit, policyEngine: policy).RunAsync("restart the thing", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Contains(audit.Events, e => e is ToolCallAuditEvent { Tool: "test.throwing-verification", Verification: VerificationStatus.Inconclusive });
    }

    [Fact]
    public async Task RunAsync_NeverVerifies_AReadTool()
    {
        var toolCall = new ModelToolCall("call-1", "test.read", ToolArguments.Empty);
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(),
            new ModelResponse(null, [toolCall], false, null),
            new ModelResponse("Done.", [], true, null));
        var registry = CreateRegistryWith(new FakeReadTool());
        var audit = new RecordingAuditSink();

        var result = await CreateRunner(model, registry, audit).RunAsync("check things", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        var toolCallEvent = Assert.Single(audit.Events.OfType<ToolCallAuditEvent>());
        Assert.Null(toolCallEvent.Verification);
    }

    // ---- V0.2: explicit planning and replanning (rule C8, ADR-0014) ----

    [Fact]
    public async Task RunAsync_RecordsTheInitialPlan_BeforeTheFirstStep()
    {
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponseWithDistinctSteps(stepCount: 2, rationale: "Check CPU, then report."),
            new ModelResponse("Done.", [], true, null));
        var registry = CreateRegistryWith();
        var audit = new RecordingAuditSink();

        var result = await CreateRunner(model, registry, audit).RunAsync("check things", Actor);

        Assert.Single(result.Plans);
        Assert.Equal(0, result.Plans[0].Revision);
        Assert.Equal("Check CPU, then report.", result.Plans[0].Rationale);
        Assert.Equal(2, result.Plans[0].Steps.Count);
        Assert.Equal(0, result.Steps[0].PlanRevision);
        Assert.Contains(audit.Events, e => e is ModelCallAuditEvent { StepIndex: -1, Outcome: ModelCallOutcome.Success });
    }

    [Fact]
    public async Task RunAsync_DegradesToAnEmptyPlan_WhenTheModelNeverProducesValidPlanJson()
    {
        // Bounded retry (mirroring the JSON-schema fallback in OpenAiCompatibleChatModel, plan
        // §3.1.1): one malformed reply is retried once; still malformed, the task proceeds
        // reactively rather than failing outright — rule C1, applied to planning.
        var model = new FakeChatModel(
            new ModelResponse("I'll figure it out as I go.", [], false, null),
            new ModelResponse("Sure, let me think about that.", [], false, null),
            new ModelResponse("All good.", [], true, null));
        var registry = CreateRegistryWith();
        var audit = new RecordingAuditSink();

        var result = await CreateRunner(model, registry, audit).RunAsync("check things", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Single(result.Plans);
        Assert.Empty(result.Plans[0].Steps);
        Assert.Contains("Planning failed", result.Plans[0].Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_RecoversThePlan_WhenTheRetryProducesValidJson()
    {
        var model = new FakeChatModel(
            new ModelResponse("Sure — first I'll check the CPU.", [], false, null), // malformed (prose, no JSON object)
            PlanningTestSupport.PlanResponse(stepCount: 1, rationale: "Retried and got it right."),
            new ModelResponse("Done.", [], true, null));
        var registry = CreateRegistryWith();
        var audit = new RecordingAuditSink();

        var result = await CreateRunner(model, registry, audit).RunAsync("check things", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Single(result.Plans);
        Assert.Equal("Retried and got it right.", result.Plans[0].Rationale);
    }

    [Fact]
    public async Task RunAsync_Replans_WhenTheModelContinuesPastEveryStepThePlanNamed()
    {
        var toolCall = new ModelToolCall("call-1", "test.read", ToolArguments.Empty);
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(stepCount: 1), // one planned step
            new ModelResponse(null, [toolCall], false, null), // step 0: satisfies the one planned step
            new ModelResponse(null, [toolCall], false, null), // step 1: model keeps going regardless
            PlanningTestSupport.PlanResponse(stepCount: 1, revision: 1), // exhaustion triggers a replan
            new ModelResponse("Done.", [], true, null));
        var registry = CreateRegistryWith(new FakeReadTool());
        var audit = new RecordingAuditSink();

        var result = await CreateRunner(model, registry, audit).RunAsync("check things repeatedly", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(2, result.Plans.Count);
        Assert.Equal(0, result.Steps[0].PlanRevision);
        Assert.Equal(0, result.Steps[1].PlanRevision);
        Assert.Equal(1, result.Steps[2].PlanRevision); // the "Final response" step, under the new plan
    }

    [Fact]
    public async Task RunAsync_EndsAsReplanLimitReached_WhenReplanningKeepsBeingNeeded()
    {
        var toolCall = new ModelToolCall("call-1", "test.hangs", ToolArguments.Empty);
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(),
            new ModelResponse(null, [toolCall], false, null), // times out -> 1st replan
            PlanningTestSupport.PlanResponse(revision: 1, expectedTool: "test.hangs"),
            new ModelResponse(null, [toolCall], false, null)); // times out again -> limit reached
        var registry = CreateRegistryWith(new HangingTool());
        var audit = new RecordingAuditSink();
        var options = new AgentRunnerOptions { DefaultToolTimeout = TimeSpan.FromMilliseconds(50), MaxReplans = 1 };

        var result = await CreateRunner(model, registry, audit, options).RunAsync("do the thing", Actor);

        Assert.Equal(AgentTaskStatus.ReplanLimitReached, result.Status);
        Assert.Equal(2, result.Plans.Count);
    }
}

/// <summary>One planned step for <see cref="PlanningTestSupport"/>: a tool plus the optional discriminating arguments.</summary>
internal sealed record PlanTestStep(string Tool, System.Text.Json.Nodes.JsonObject? ExpectedArguments = null);

/// <summary>
/// Builds canned planning-call responses shared across <see cref="AgentRunnerTests"/>. Responses honour the
/// ADR-0050 semantic contract: a tool planned more than once must carry non-empty expectedArguments on every
/// occurrence, so the helpers refuse to build a plan that production would (rightly) reject.
/// </summary>
internal static class PlanningTestSupport
{
    /// <summary>One step per tool; a repeated tool is rejected here because it would need a discriminator.</summary>
    public static ModelResponse PlanResponseFor(params string[] expectedTools)
    {
        var duplicate = expectedTools.GroupBy(t => t, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Tool '{duplicate.Key}' is planned more than once; use PlanResponseFor(PlanTestStep[]) with discriminating expectedArguments (ADR-0050).");
        }

        return PlanResponseFor([.. expectedTools.Select(t => new PlanTestStep(t))]);
    }

    /// <summary>Explicit steps; a repeated tool needs non-empty <see cref="PlanTestStep.ExpectedArguments"/> on every occurrence.</summary>
    public static ModelResponse PlanResponseFor(params PlanTestStep[] plannedSteps) =>
        PlanResponseFor("A generic test plan.", plannedSteps);

    private static ModelResponse PlanResponseFor(string rationale, PlanTestStep[] plannedSteps)
    {
        var ambiguous = plannedSteps.GroupBy(s => s.Tool, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1 && g.Any(s => s.ExpectedArguments is null or { Count: 0 }));
        if (ambiguous is not null)
        {
            throw new InvalidOperationException(
                $"Repeated tool '{ambiguous.Key}' needs non-empty ExpectedArguments on every occurrence (ADR-0050).");
        }

        var steps = new System.Text.Json.Nodes.JsonArray();
        for (var i = 0; i < plannedSteps.Length; i++)
        {
            var step = new System.Text.Json.Nodes.JsonObject
            {
                ["description"] = $"step {i}",
                ["expectedTool"] = plannedSteps[i].Tool,
            };
            if (plannedSteps[i].ExpectedArguments is { } args)
            {
                step["expectedArguments"] = args.DeepClone();
            }

            steps.Add(step);
        }

        var json = new System.Text.Json.Nodes.JsonObject
        {
            ["rationale"] = rationale,
            ["steps"] = steps,
        }.ToJsonString();
        return new ModelResponse(json, [], false, null);
    }

    /// <summary>A valid, minimal plan: zero or one step. More than one step of the same tool is ambiguous — see <see cref="PlanResponseWithDistinctSteps"/>.</summary>
    public static ModelResponse PlanResponse(
        int stepCount = 1,
        string rationale = "A generic test plan.",
        int revision = 0,
        string expectedTool = "test.read")
    {
        if (stepCount > 1)
        {
            throw new InvalidOperationException(
                "A plan of several steps cannot repeat one tool without discriminators (ADR-0050); use PlanResponseWithDistinctSteps or PlanResponseFor.");
        }

        // `revision` is not encoded in the JSON itself — the runtime assigns the revision number
        // based on why the call was made (initial plan vs. replan), never from the model's text.
        // It is accepted here only so a test reads clearly about which replan a response answers.
        _ = revision;

        var steps = new System.Text.Json.Nodes.JsonArray();
        for (var i = 0; i < stepCount; i++)
        {
            steps.Add(new System.Text.Json.Nodes.JsonObject { ["description"] = $"step {i}", ["expectedTool"] = expectedTool });
        }

        var json = new System.Text.Json.Nodes.JsonObject { ["rationale"] = rationale, ["steps"] = steps }.ToJsonString();
        return new ModelResponse(json, [], false, null);
    }


    /// <summary>The single integer parameter that makes repeated calls of one tool discriminable (ADR-0050).</summary>
    public static readonly ToolParameter CallIndexParameter = new("n", ToolParameterType.Integer, "Which call of the sequence this is.");

    /// <summary>A tool call carrying the call index, matching a <see cref="PlanResponseIndexed"/> step.</summary>
    internal static ModelResponse IndexedCall(string tool, int n, string id = "call", System.Text.Json.Nodes.JsonObject? extraArguments = null)
    {
        var arguments = IndexArguments(n);
        foreach (var (key, value) in extraArguments ?? [])
        {
            arguments[key] = value?.DeepClone();
        }

        return new ModelResponse(null, [new ModelToolCall(id, tool, new ToolArguments(arguments))], false, null);
    }

    /// <summary>A call index as the expected arguments of a planned step, or the arguments of the matching call.</summary>
    public static System.Text.Json.Nodes.JsonObject IndexArguments(int n) => new() { ["n"] = n };

    /// <summary>
    /// A plan of <paramref name="stepCount"/> occurrences of one tool, made valid by the integer <see cref="CallIndexParameter"/>
    /// (the tool must declare it); the matching calls pass <see cref="IndexArguments"/>.
    /// </summary>
    public static ModelResponse PlanResponseIndexed(string tool, int stepCount, string rationale = "A generic test plan.", int startIndex = 0)
        => PlanResponseFor(
            rationale, [.. Enumerable.Range(startIndex, stepCount).Select(i => new PlanTestStep(tool, IndexArguments(i)))]);

    /// <summary>
    /// For tests that only need the plan to have N steps: the first is <paramref name="expectedTool"/>, the
    /// others use distinct synthetic tool names, so no tool repeats and the plan stays valid.
    /// </summary>
    public static ModelResponse PlanResponseWithDistinctSteps(
        int stepCount,
        string rationale = "A generic test plan.",
        int revision = 0,
        string expectedTool = "test.read")
    {
        var steps = new System.Text.Json.Nodes.JsonArray();
        for (var i = 0; i < stepCount; i++)
        {
            steps.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["description"] = $"step {i}",
                ["expectedTool"] = i == 0 ? expectedTool : $"{expectedTool}.step{i}",
            });
        }

        var json = new System.Text.Json.Nodes.JsonObject { ["rationale"] = rationale, ["steps"] = steps }.ToJsonString();
        return new ModelResponse(json, [], false, null);
    }
}
