// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;
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
        IPolicyEngine? policyEngine = null, IApprovalProvider? approvalProvider = null, ITaskStore? taskStore = null) =>
        new(model, registry, policyEngine ?? new DefaultTestPolicyEngine(), approvalProvider ?? new NeverCalledApprovalProvider(),
            audit, taskStore ?? new InMemoryTaskStore(), TimeProvider.System, NullLogger<AgentRunner>.Instance, options ?? new AgentRunnerOptions());

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
            PlanningTestSupport.PlanResponse(),
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
            PlanningTestSupport.PlanResponse(),
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
            PlanningTestSupport.PlanResponse(),
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
            PlanningTestSupport.PlanResponse(),
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
            PlanningTestSupport.PlanResponse(),
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
            PlanningTestSupport.PlanResponse(),
            new ModelResponse(null, [toolCall], false, null),
            PlanningTestSupport.PlanResponse(revision: 1),
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
    public async Task RunAsync_RefusesANonReadTool_WhenNoPolicyEngineIsWiredYet_AndReplans()
    {
        var toolCall = new ModelToolCall("call-1", "test.highrisk", ToolArguments.Empty);
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(),
            new ModelResponse(null, [toolCall], false, null),
            PlanningTestSupport.PlanResponse(revision: 1),
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
            PlanningTestSupport.PlanResponse(),
            new ModelResponse(null, [toolCall], false, null),
            PlanningTestSupport.PlanResponse(revision: 1),
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
            PlanningTestSupport.PlanResponse(),
            new ModelResponse(null, [callA], false, null),
            PlanningTestSupport.PlanResponse(revision: 1),
            new ModelResponse(null, [callB], false, null),
            PlanningTestSupport.PlanResponse(revision: 2),
            new ModelResponse(null, [callA], false, null),
            PlanningTestSupport.PlanResponse(revision: 3),
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
        var toolCall = new ModelToolCall("call-1", "test.read", ToolArguments.Empty);
        var model = new FakeChatModel([
            PlanningTestSupport.PlanResponse(stepCount: 50), // never exhausted within MaxSteps
            .. Enumerable.Range(0, 10).Select(_ => new ModelResponse(null, [toolCall], false, null)),
        ]);
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
            PlanningTestSupport.PlanResponse(),
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
            PlanningTestSupport.PlanResponse(),
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
            PlanningTestSupport.PlanResponse(),
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
            PlanningTestSupport.PlanResponse(),
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
            PlanningTestSupport.PlanResponse(),
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
            PlanningTestSupport.PlanResponse(),
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

    [Fact]
    public async Task RunAsync_RequiresApproval_WhenManifestTightensAutomaticPolicy()
    {
        var toolCall = new ModelToolCall("call-1", "test.approval-bound", ToolArguments.Empty);
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(),
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
            PlanningTestSupport.PlanResponse(),
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
            PlanningTestSupport.PlanResponse(),
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
            PlanningTestSupport.PlanResponse(),
            new ModelResponse(null, [toolCall], false, null),
            new ModelResponse("Done.", [], true, null));
        var registry = CreateRegistryWith(new FakeHighRiskTool());
        var audit = new RecordingAuditSink();
        var policy = new StubPolicyEngine(PolicyMode.Automatic);

        var result = await CreateRunner(model, registry, audit, policyEngine: policy).RunAsync("restart the thing", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Contains(audit.Events, e => e is ToolCallAuditEvent { Tool: "test.highrisk", Verification: VerificationStatus.Confirmed });
        Assert.Contains(model.Requests[2].History, turn =>
            turn.Content != null && turn.Content.Contains("Verification: Confirmed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_Replans_WhenVerificationIsRefuted()
    {
        var toolCall = new ModelToolCall("call-1", "test.highrisk", ToolArguments.Empty);
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(),
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
            PlanningTestSupport.PlanResponse(),
            new ModelResponse(null, [toolCall], false, null),
            new ModelResponse("Noted, moving on.", [], true, null));
        var tool = new FakeHighRiskTool(verificationOutcome: new VerificationOutcome(VerificationStatus.Inconclusive, "cannot tell"));
        var registry = CreateRegistryWith(tool);
        var audit = new RecordingAuditSink();
        var policy = new StubPolicyEngine(PolicyMode.Automatic);

        var result = await CreateRunner(model, registry, audit, policyEngine: policy).RunAsync("restart the thing", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Single(result.Plans);
        Assert.Contains(audit.Events, e => e is ToolCallAuditEvent { Tool: "test.highrisk", Verification: VerificationStatus.Inconclusive });
    }

    [Fact]
    public async Task RunAsync_RecordsInconclusiveVerification_WhenTheVerificationToolItselfFails()
    {
        // agentic/04-testing-rules.md, "Verification": "a verification tool that itself fails."
        var toolCall = new ModelToolCall("call-1", "test.inspecting", ToolArguments.Empty);
        var model = new FakeChatModel(
            PlanningTestSupport.PlanResponse(),
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
            PlanningTestSupport.PlanResponse(),
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
            PlanningTestSupport.PlanResponse(),
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
            PlanningTestSupport.PlanResponse(stepCount: 2, rationale: "Check CPU, then report."),
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
            PlanningTestSupport.PlanResponse(revision: 1),
            new ModelResponse(null, [toolCall], false, null)); // times out again -> limit reached
        var registry = CreateRegistryWith(new HangingTool());
        var audit = new RecordingAuditSink();
        var options = new AgentRunnerOptions { DefaultToolTimeout = TimeSpan.FromMilliseconds(50), MaxReplans = 1 };

        var result = await CreateRunner(model, registry, audit, options).RunAsync("do the thing", Actor);

        Assert.Equal(AgentTaskStatus.ReplanLimitReached, result.Status);
        Assert.Equal(2, result.Plans.Count);
    }
}

/// <summary>Builds canned planning-call responses shared across <see cref="AgentRunnerTests"/>.</summary>
internal static class PlanningTestSupport
{
    public static ModelResponse PlanResponse(int stepCount = 5, string rationale = "A generic test plan.", int revision = 0)
    {
        var steps = new System.Text.Json.Nodes.JsonArray();
        for (var i = 0; i < stepCount; i++)
        {
            steps.Add(new System.Text.Json.Nodes.JsonObject { ["description"] = $"step {i}", ["expectedTool"] = null });
        }

        // `revision` is not encoded in the JSON itself — the runtime assigns the revision number
        // based on why the call was made (initial plan vs. replan), never from the model's text.
        // It is accepted here only so a test reads clearly about which replan a response answers.
        _ = revision;

        var json = new System.Text.Json.Nodes.JsonObject { ["rationale"] = rationale, ["steps"] = steps }.ToJsonString();
        return new ModelResponse(json, [], false, null);
    }
}
