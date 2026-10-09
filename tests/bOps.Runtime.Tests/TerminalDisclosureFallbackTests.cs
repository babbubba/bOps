// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using static bOps.Runtime.Tests.EvidenceScenario;

namespace bOps.Runtime.Tests;

/// <summary>
/// PRE-3B2 (ADR-0042 amendment): a failed disclosure re-ask may fall back only to an already-valid user-facing original; a
/// protocol/control artifact or an empty response is never a valid fallback, so when its bounded correction fails the task fails.
/// </summary>
public sealed class TerminalDisclosureFallbackTests
{
    private const string PartialTool = "test.partial";

    private const string Artifact =
        "<tool_call>\n<function=runtime_evidence_read>\n<parameter=step>0</parameter>\n</function>\n</tool_call>";

    private const string ValidProse = "The evidence indicates repeated corrected hardware errors.";

    /// <summary>Replays the script; once exhausted, the last entry (an exception) keeps being thrown.</summary>
    private sealed class ScriptedModel(params object[] script) : IChatModel
    {
        private int _next;

        public int Calls => _next;

        public ChatModelDescriptor Descriptor { get; } = new("fake", "fake-model");

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            var item = script[Math.Min(_next++, script.Length - 1)];
            return item is Exception failure ? Task.FromException<ModelResponse>(failure) : Task.FromResult((ModelResponse)item);
        }
    }

    private static async Task<(TaskState State, ScriptedModel Model)> RunAsync(params object[] afterTool)
    {
        var model = new ScriptedModel([Plan(PartialTool), Call(PartialTool), .. afterTool]);
        var state = await Runner(model, Registry(new ResultTool(PartialTool, Partial())), new RecordingAuditSink())
            .RunAsync("diagnose", Actor);
        return (state, model);
    }

    private static ModelProtocolException Failure(ModelFailureKind kind) =>
        new("model call failed") { FailureKind = kind };

    private static void AssertFailedWithoutArtifact(TaskState state)
    {
        Assert.Equal(AgentTaskStatus.Failed, state.Status);
        Assert.NotEqual(TaskTerminalKind.Completed, state.TerminalReason?.Kind);
        Assert.DoesNotContain(state.Steps, step => step.Observation?.Contains("<tool_call>", StringComparison.Ordinal) == true
            || step.Result?.Output?.Contains("<tool_call>", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task T1_ValidProse_DisclosureSufficient_NoReAsk()
    {
        var (state, model) = await RunAsync(Final(DisclosedAnswer));

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.Equal(3, model.Calls);
        Assert.Equal(DisclosedAnswer, state.Steps[^1].Observation);
        Assert.Equal("Final response", state.Steps[^1].Description);
    }

    [Fact]
    public async Task T2_ValidProse_InsufficientDisclosure_CorrectionAccepted()
    {
        var (state, _) = await RunAsync(Final(ValidProse), Final(DisclosedAnswer));

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.Equal(DisclosedAnswer, state.Steps[^1].Observation);
        Assert.Equal(FinalResponse.ReAskAcceptedMarker, state.Steps[^1].Description);
    }

    [Theory]
    [InlineData(ModelFailureKind.Timeout)]
    [InlineData(ModelFailureKind.Unknown)]
    public async Task T3_T4_T12_ValidProse_ReAskFails_CompletesWithTheByteIdenticalOriginal(ModelFailureKind kind)
    {
        var (state, _) = await RunAsync(Final(ValidProse), Failure(kind));

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.Equal(ValidProse, state.Steps[^1].Observation);
        Assert.Equal(FinalResponse.ReAskNotUsedMarker, state.Steps[^1].Description);
    }

    [Fact]
    public async Task T5_ArtifactOriginal_CorrectionSucceeds_ArtifactNeverUsed()
    {
        var (state, _) = await RunAsync(Final(Artifact), Final(DisclosedAnswer));

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.Equal(DisclosedAnswer, state.Steps[^1].Observation);
        Assert.DoesNotContain(state.Steps, step => step.Observation?.Contains("<tool_call>", StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData(ModelFailureKind.Timeout)]
    [InlineData(ModelFailureKind.Unknown)]
    public async Task T6_T7_Qwen_ArtifactOriginal_CorrectionFails_TaskFails(ModelFailureKind kind)
    {
        var (state, _) = await RunAsync(Final(Artifact), Failure(kind));

        AssertFailedWithoutArtifact(state);
    }

    [Fact]
    public async Task T8_ArtifactRepeatedAfterCorrection_TaskFails()
    {
        var (state, _) = await RunAsync(Final(Artifact), Final(Artifact));

        AssertFailedWithoutArtifact(state);
    }

    [Fact]
    public async Task T9_EmptyOriginal_CorrectionFails_TaskFails()
    {
        var (state, _) = await RunAsync(Final(""), Failure(ModelFailureKind.Timeout));

        Assert.Equal(AgentTaskStatus.Failed, state.Status);
        Assert.NotEqual(TaskTerminalKind.Completed, state.TerminalReason?.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n ")]
    public async Task T9_EmptyOriginal_CorrectionAlsoEmpty_TaskFails(string empty)
    {
        var (state, _) = await RunAsync(Final(empty), Final(empty));

        Assert.Equal(AgentTaskStatus.Failed, state.Status);
        Assert.Equal(TaskTerminalKind.EmptyResponse, state.TerminalReason?.Kind);
    }

    [Fact]
    public async Task T10_EmptyOriginal_CorrectionSucceeds_Completes()
    {
        var (state, _) = await RunAsync(Final("  "), Final(DisclosedAnswer));

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.Equal(DisclosedAnswer, state.Steps[^1].Observation);
    }

    [Theory]
    [InlineData(Artifact)]
    [InlineData(Artifact + "\n\n## Evidence limitations\n- partial")]
    public async Task T11_ReAskReturnsAnArtifact_NotUsed_ValidOriginalPreserved(string reply)
    {
        var (state, _) = await RunAsync(Final(ValidProse), Final(reply));

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.Equal(ValidProse, state.Steps[^1].Observation);
        Assert.Equal(FinalResponse.ReAskNotUsedMarker, state.Steps[^1].Description);
    }
}
