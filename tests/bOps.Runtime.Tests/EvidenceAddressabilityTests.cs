// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using static bOps.Runtime.Tests.EvidenceScenario;

namespace bOps.Runtime.Tests;

/// <summary>
/// AF-05G G1 (ADR-0052): persisted tool evidence is exactly addressable whatever its size. <c>MaxObservationCharacters</c> only
/// shortens the runtime observation; it does not decide what the model-facing context may later have to recover, because the
/// context layer can remove an old tool result that was never longer than it. Every result here is far below 4000 characters.
/// </summary>
public sealed class EvidenceAddressabilityTests
{
    private const string First = "diag.first";
    private const string Second = "diag.second";
    private const string Marker = "SMALL-RESULT-SENTINEL";
    private const int MarkerOffset = 1500;

    // 2000 characters: under MaxObservationCharacters (4000), so nothing here is "truncated"; the marker sits at a known offset.
    private static readonly string SmallOutput = new string('a', MarkerOffset) + Marker + new string('b', 2000 - MarkerOffset - Marker.Length);

    private static ModelToolCall Control(int step = 0, int offset = MarkerOffset, int length = 21) =>
        new(Guid.NewGuid().ToString("N"), EvidenceRead.ControlFunctionName, ToolArguments.FromJson(new JsonObject
        {
            ["step"] = step, ["source"] = "result", ["offset"] = offset, ["length"] = length,
        }));

    private static ModelResponse Read(int offset = MarkerOffset, int length = 21) => new(null, [Control(0, offset, length)], false, null);

    private static string[] Offered(ModelRequest request) => [.. request.AvailableTools.Select(tool => tool.Name)];

    private static bool ReplyMentions(ModelRequest request, string text) =>
        request.History.Any(turn => turn.Role == ChatRole.Tool && turn.Content?.Contains(text, StringComparison.Ordinal) == true);

    private static (ResultTool First, ResultTool Second, ToolRegistry Registry) Tools(ToolCallResult? first = null)
    {
        var one = new ResultTool(First, first ?? Complete(SmallOutput));
        var two = new ResultTool(Second, Complete("second-done"));
        return (one, two, Registry(one, two));
    }

    private static string IndexBlock(string systemPrompt)
    {
        var start = systemPrompt.IndexOf(EvidenceIndex.OpenMarker, StringComparison.Ordinal);
        var end = systemPrompt.IndexOf(EvidenceIndex.CloseMarker, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "the evidence index is missing");
        return systemPrompt[start..(end + EvidenceIndex.CloseMarker.Length)];
    }

    // G1-T3. Requests: 0 plan, 1 step 0, 2 step 1, 3 final.
    [Fact]
    public async Task G1T3_ASmallResult_OffersTheReadAndTheIndex_ButNeverReadsOrChangesTheExpectedTool()
    {
        var model = new FakeChatModel(Plan(First, Second), Call(First), Call(Second), Final("done"));
        var (_, second, registry) = Tools();
        var audit = new RecordingAuditSink();

        var state = await Runner(model, registry, audit).RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.Equal(4, model.Requests.Count); // no extra model call: the model never used the offered read
        Assert.Empty(audit.Events.OfType<EvidenceReadAuditEvent>());
        Assert.Equal([First], Offered(model.Requests[1])); // nothing persisted yet, nothing to read
        Assert.Equal([Second, EvidenceRead.ControlFunctionName], Offered(model.Requests[2]));
        Assert.Equal([EvidenceRead.ControlFunctionName], Offered(model.Requests[3])); // plan complete: control only, no operational tool
        Assert.Equal(1, second.ExecutionCount);
        Assert.Contains($"- step 0 {First}: id {EvidenceRead.EvidenceId(state.Id, 0)}; result 2000 chars", IndexBlock(model.Requests[2].SystemPrompt), StringComparison.Ordinal);
        Assert.Equal([First, Second], state.Steps.Where(step => step.ToolCall is not null && !FinalResponse.IsFinalStep(step)).Select(step => step.ToolCall!.ToolName));
    }

    // G1-T2 (runtime half): a fact-less tool keeps its raw evidence readable; facts are never a substitute for EvidenceRead.
    [Fact]
    public async Task G1T2_AFactlessSmallResult_IsStillReadableExactly()
    {
        var model = new FakeChatModel(Plan(First, Second), Call(First), Read(), Call(Second), Final("done"));
        var (_, _, registry) = Tools(Complete(SmallOutput) with { Facts = [] });
        var audit = new RecordingAuditSink();

        var state = await Runner(model, registry, audit).RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        var read = Assert.Single(audit.Events.OfType<EvidenceReadAuditEvent>());
        Assert.Equal((EvidenceReadResultCode.Success, 21), (read.ResultCode, read.ReturnedLength)); // StepIndex is the reading step (1), not the source
        Assert.True(ReplyMentions(model.Requests[3], Marker));
        Assert.DoesNotContain("EvidenceGrounding", model.Requests[3].SystemPrompt, StringComparison.Ordinal); // no facts anywhere
    }

    // G1-T4. Requests: 0 plan, 1 step 0, 2..5 the four served reads' replies, 6 after the fourth read, 7 after the refused fifth.
    [Fact]
    public async Task G1T4_ExhaustionIsUnchanged_ForSmallResultsToo()
    {
        var model = new FakeChatModel(
            Plan(First, Second), Call(First), Read(0, 10), Read(10, 10), Read(20, 10), Read(30, 10), Read(40, 10), Call(Second), Final("done"));
        var (_, second, registry) = Tools();
        var audit = new RecordingAuditSink();

        var state = await Runner(model, registry, audit).RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.Equal(1, second.ExecutionCount);
        Assert.Equal(
            [.. Enumerable.Repeat(EvidenceReadResultCode.Success, 4), EvidenceReadResultCode.LimitExceeded],
            audit.Events.OfType<EvidenceReadAuditEvent>().Select(read => read.ResultCode));
        for (var index = 2; index <= 5; index++)
        {
            Assert.Equal([Second, EvidenceRead.ControlFunctionName], Offered(model.Requests[index]));
        }

        Assert.Equal([Second], Offered(model.Requests[6]));
        Assert.Contains(EvidenceRead.ExhaustedFeedback, model.Requests[6].SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("EvidenceRead/v1", model.Requests[6].SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain(EvidenceIndex.OpenMarker, model.Requests[6].SystemPrompt, StringComparison.Ordinal);
    }

    // G1-T5. Requests: 0 plan, 1-2 steps (the second leaves the one-step plan), 3 replan, 4 replan after the read.
    [Fact]
    public async Task G1T5_AReplan_CanAddressAnOldSmallResult_ByTheTextProtocol()
    {
        var taskId = Guid.Parse("0a0a0a0a-1234-1234-1234-123456789abc");
        var directive = new ModelResponse(
            $"{{\"runtime\":\"EvidenceRead/v1\",\"evidenceId\":\"{EvidenceRead.EvidenceId(taskId, 0)}\",\"source\":\"result\",\"offset\":{MarkerOffset},\"length\":21}}",
            [], false, null);
        var model = new FakeChatModel(Plan(First), Call(First, "c0"), Call(First, "c1"), directive, Plan(First), Final("done"));
        var (_, _, registry) = Tools();
        var audit = new RecordingAuditSink();

        var state = await Runner(model, registry, audit).RunAsync("diagnose", Actor, taskId);

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.Equal(2, state.Plans.Count);
        var replanPrompt = model.Requests[3].SystemPrompt;
        Assert.Contains($"- step 0 {First}: id {EvidenceRead.EvidenceId(taskId, 0)}; result 2000 chars", IndexBlock(replanPrompt), StringComparison.Ordinal);
        Assert.Empty(model.Requests[3].AvailableTools);
        var read = Assert.Single(audit.Events.OfType<EvidenceReadAuditEvent>());
        Assert.Equal(EvidenceReadResultCode.Success, read.ResultCode);
        Assert.Contains(Marker, string.Join('\n', model.Requests[4].History.Select(turn => turn.Content)), StringComparison.Ordinal);
    }

    // G1-T6. Persist, reopen, resume: the same ids, the same index, the same raw drill-down.
    [Fact]
    public async Task G1T6_PersistReopenResume_GivesTheSameIndexAndTheSameDrillDown()
    {
        var store = new InMemoryTaskStore();
        var liveModel = new FakeChatModel(Plan(First, Second), Call(First)); // the third call finds no scripted response: the attempt fails
        var (_, _, liveRegistry) = Tools();
        var interrupted = await Runner(liveModel, liveRegistry, new RecordingAuditSink(), store: store).RunAsync("diagnose", Actor);
        Assert.Equal(AgentTaskStatus.Failed, interrupted.Status);
        var liveIndex = IndexBlock(liveModel.Requests[2].SystemPrompt);

        var stored = await store.LoadAsync(interrupted.Id);
        var reloaded = JsonSerializer.Deserialize<TaskState>(JsonSerializer.Serialize(stored))!;
        Assert.Equal(liveIndex, EvidenceIndex.Build(reloaded.Id, reloaded.Steps));

        var resumeStore = new InMemoryTaskStore();
        resumeStore.Seed(reloaded);
        var resumedModel = new FakeChatModel(Read(), Call(Second), Final("done"));
        var (_, second, resumedRegistry) = Tools();
        var audit = new RecordingAuditSink();
        var resumed = await Runner(resumedModel, resumedRegistry, audit, store: resumeStore)
            .ResumeAsync(reloaded, new ActorIdentity("api-user", "resumer", "Resumer"));

        Assert.Equal(AgentTaskStatus.Completed, resumed.Status);
        Assert.Equal(liveIndex, IndexBlock(resumedModel.Requests[0].SystemPrompt));
        Assert.Equal([Second, EvidenceRead.ControlFunctionName], Offered(resumedModel.Requests[0]));
        Assert.Equal(EvidenceReadResultCode.Success, Assert.Single(audit.Events.OfType<EvidenceReadAuditEvent>()).ResultCode);
        Assert.True(ReplyMentions(resumedModel.Requests[1], Marker));
        Assert.Equal(1, second.ExecutionCount);
    }

    // G1-T7: the closed rejections are unchanged for a small result.
    [Fact]
    public async Task G1T7_CrossTaskMalformedAndOutOfRange_AreStillRejectedForASmallResult()
    {
        var foreign = new ModelResponse(
            $"{{\"runtime\":\"EvidenceRead/v1\",\"evidenceId\":\"{EvidenceRead.EvidenceId(Guid.NewGuid(), 0)}\",\"source\":\"result\",\"offset\":0,\"length\":10}}",
            [], false, null);
        var malformed = new ModelResponse(null,
            [new ModelToolCall("m", EvidenceRead.ControlFunctionName, ToolArguments.FromJson(new JsonObject { ["step"] = "x" }))], false, null);
        var model = new FakeChatModel(
            Plan(First, Second), Call(First), foreign, malformed, Read(offset: 999_999, length: 10), Read(0, 10), Call(Second), Final("done"));
        var (_, _, registry) = Tools();
        var audit = new RecordingAuditSink();

        var state = await Runner(model, registry, audit).RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.Equal(
            [EvidenceReadResultCode.CrossTaskRejected, EvidenceReadResultCode.Malformed, EvidenceReadResultCode.OutOfRange, EvidenceReadResultCode.Success],
            audit.Events.OfType<EvidenceReadAuditEvent>().Select(read => read.ResultCode));
    }
}
