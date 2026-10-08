// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>PRE-3B1: a control function is valid only when offered, and protocol artifacts are never final answers.</summary>
public sealed class TerminalProtocolArtifactTests
{
    private static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("test-user");
    private const string Big = "diag.big";
    private const string Next = "diag.next";

    private const string QwenArtifact =
        "<tool_call>\n<function=runtime_evidence_read>\n<parameter=step>1</parameter>\n<parameter=source>result</parameter>\n"
        + "<parameter=offset>4000</parameter>\n<parameter=length>200</parameter>\n</function>\n</tool_call>";

    private sealed class FixedTool(string name, string output) : ITool
    {
        public int ExecutionCount { get; private set; }

        public ToolManifest Manifest { get; } = new()
        {
            Name = name,
            Description = "Fixed read tool.",
            Risk = RiskLevel.Read,
            Platforms = [CurrentPlatform.Id],
            Requires = [],
            Parameters = [],
        };

        public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
        {
            ExecutionCount++;
            return Task.FromResult(ToolCallResult.Success(output));
        }
    }

    private static (AgentRunner Runner, RecordingAuditSink Audit, FixedTool Big, FixedTool Next) Build(IChatModel model)
    {
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        var big = new FixedTool(Big, "small");
        var next = new FixedTool(Next, "next-done");
        registry.Register(new PackageId("test.package"), big);
        registry.Register(new PackageId("test.package"), next);
        var audit = new RecordingAuditSink();
        var runner = new AgentRunner(model, registry, new DefaultTestPolicyEngine(), new NeverCalledApprovalProvider(),
            audit, new InMemoryTaskStore(), TimeProvider.System, NullLogger<AgentRunner>.Instance,
            new AgentRunnerOptions { MaxSteps = 20, MaxLifetimeSteps = 40 });
        return (runner, audit, big, next);
    }

    private static ModelResponse TwoStepPlan() => new(new JsonObject
    {
        ["rationale"] = "test",
        ["steps"] = new JsonArray(
            new JsonObject { ["description"] = "big", ["expectedTool"] = Big },
            new JsonObject { ["description"] = "next", ["expectedTool"] = Next }),
    }.ToJsonString(), [], false, null);

    private static ModelToolCall NativeCall(string name, JsonObject arguments) =>
        new(Guid.NewGuid().ToString("N"), name, ToolArguments.FromJson(arguments));

    private static ModelResponse Calls(params ModelToolCall[] calls) => new(null, calls, false, null);

    private static ModelResponse Tool(string name) => Calls(NativeCall(name, new JsonObject()));

    private static ModelResponse Text(string text) => new(text, [], true, null);

    private static ModelToolCall Control() => NativeCall(EvidenceRead.ControlFunctionName, new JsonObject
    {
        ["step"] = 0,
        ["source"] = "result",
        ["offset"] = 0,
        ["length"] = 10,
    });

    // T1 (offered control keeps working) is EvidenceControlPlaneTests.T1_T2_T5_*.

    [Fact]
    public async Task T2_UnofferedTypedRead_IsAControlError_NothingIsRead_CursorUnchanged()
    {
        var model = new FakeChatModel(TwoStepPlan(), Calls(Control()), Tool(Big), Tool(Next), Text("done"));
        var (runner, audit, big, next) = Build(model);

        var result = await runner.RunAsync("diagnose", Actor);

        Assert.Equal([Big], model.Requests[1].AvailableTools.Select(tool => tool.Name));
        var read = Assert.Single(audit.Events.OfType<EvidenceReadAuditEvent>());
        Assert.Equal((EvidenceReadResultCode.Malformed, 0), (read.ResultCode, read.ReturnedLength));
        Assert.Contains(model.Requests[2].History, turn => turn.Role == ChatRole.Tool
            && turn.Content!.Contains("NotOffered", StringComparison.Ordinal));
        Assert.Equal((1, 1), (big.ExecutionCount, next.ExecutionCount));
        Assert.Equal([Big, Next], result.Steps.Where(step => step.ToolCall is not null).Select(step => step.ToolCall!.ToolName));
        Assert.Equal([0, 1, 2], result.Steps.Select(step => step.Index));
    }

    [Fact]
    public async Task T3_UnknownRuntimeFunction_IsAControlError_NeverAPackageToolNorFinal()
    {
        var model = new FakeChatModel(TwoStepPlan(), Calls(NativeCall("runtime.foo", new JsonObject())), Tool(Big), Tool(Next), Text("done"));
        var (runner, audit, _, _) = Build(model);

        var result = await runner.RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.DoesNotContain(result.Steps, step => step.ToolCall?.ToolName == "runtime.foo");
        Assert.Equal(EvidenceReadResultCode.Malformed, Assert.Single(audit.Events.OfType<EvidenceReadAuditEvent>()).ResultCode);
    }

    [Fact]
    public async Task T4_T7_T8_QwenXmlArtifact_IsNotAFinalAnswer_AndTheCorrectionConsumesNothing()
    {
        var model = new FakeChatModel(TwoStepPlan(), Text(QwenArtifact), Tool(Big), Tool(Next), Text("done"));
        var (runner, audit, big, next) = Build(model);

        var result = await runner.RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.DoesNotContain(result.Steps, step => step.Result?.Output?.Contains("<tool_call>", StringComparison.Ordinal) == true
            || step.Observation?.Contains("<tool_call>", StringComparison.Ordinal) == true);
        // The cursor never moved on the artifact: the first planned tool still runs first, and no PRE-2 correction ran.
        Assert.Equal([Big, Next], result.Steps.Where(step => step.ToolCall is not null).Select(step => step.ToolCall!.ToolName));
        Assert.DoesNotContain(result.Steps, step => step.ExecutionClassification is
            PlannedStepExecutionClassification.SemanticMismatch or PlannedStepExecutionClassification.ArgumentValidationFailure);
        Assert.Equal((1, 1), (big.ExecutionCount, next.ExecutionCount));
        Assert.Contains(model.Requests[2].History, turn => turn.Role == ChatRole.User
            && turn.Content!.Contains("markup, not an answer", StringComparison.Ordinal));
        Assert.Empty(audit.Events.OfType<EvidenceReadAuditEvent>());
    }

    [Fact]
    public async Task T9_ArtifactRepeatedAfterCorrection_NeverCompletes()
    {
        var model = new FakeChatModel(TwoStepPlan(), Text(QwenArtifact), Text(QwenArtifact));
        var (runner, _, big, next) = Build(model);

        var result = await runner.RunAsync("diagnose", Actor);

        Assert.NotEqual(AgentTaskStatus.Completed, result.Status);
        Assert.Equal((0, 0), (big.ExecutionCount, next.ExecutionCount));
        Assert.DoesNotContain(result.Steps, step => step.Result?.Output?.Contains("<tool_call>", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(result.Steps, step => step.ToolCall is not null);
    }

    [Fact]
    public async Task T5_MalformedTextualEvidenceRead_IsNotAcceptedAsFinalProse()
    {
        const string malformed = "{\"runtime\":\"EvidenceRead/v1\",\"evidenceId\":";
        var model = new FakeChatModel(TwoStepPlan(), Text(malformed), Text(malformed), Text(malformed), Text(malformed), Text(malformed));
        var (runner, _, _, _) = Build(model);

        var result = await runner.RunAsync("diagnose", Actor);

        Assert.NotEqual(AgentTaskStatus.Completed, result.Status);
    }

    [Fact]
    public async Task T6_ProseMentioningTheControlFunction_StaysAFinalResponse()
    {
        const string prose = "I could not retrieve the additional evidence because runtime.evidence_read was unavailable.";
        var model = new FakeChatModel(TwoStepPlan(), Text(prose));
        var (runner, _, _, _) = Build(model);

        var result = await runner.RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(prose, result.Steps[^1].Result?.Output ?? result.Steps[^1].Observation);
    }

    [Theory]
    [InlineData("<function=runtime_evidence_read>", true)]
    [InlineData("  <tool_call>{}</tool_call>", true)]
    [InlineData("{\"name\":\"runtime.evidence_read\",\"arguments\":{\"step\":1}}", true)]
    [InlineData("{\"name\":\"docker.restart\",\"parameters\":{}}", true)]
    [InlineData("The model attempted to call runtime.evidence_read but the request was invalid.", false)]
    [InlineData("Use <tool_call> markup to call tools.", false)]
    [InlineData("{\"status\":\"ok\"}", false)]
    [InlineData("{not json", false)]
    [InlineData("", false)]
    public void Classifier_MatchesEnvelopesOnly(string text, bool expected) =>
        Assert.Equal(expected, TerminalProtocolArtifact.IsArtifact(text));
}
