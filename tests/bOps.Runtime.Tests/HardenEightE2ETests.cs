// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using bOps.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// E2E-13: durable evidence and two resume boundaries in one deterministic incident-like run. The model-facing history bound
/// (the former plateau assertion) belongs to the MAF context layer and is measured by <c>bOps.AgentFramework.Tests</c>.
/// </summary>
public sealed partial class HardenEightE2ETests
{
    private const string Goal = "E2E-13 deterministic incident fixture";
    private const string Sentinel = "E2E13-OLD-EVIDENCE-SENTINEL";
    private static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("e2e-13");

    [GeneratedRegex(@"addressable as (ev1:[0-9a-f]{32}:)n\.")]
    private static partial Regex IndexEntryForStepZero();

    [Fact]
    public async Task E2E13_FortySteps_TwoResumes_CompletesAndRetrievesOldCompleteEvidenceByTheIndexedId()
    {
        var taskId = Guid.Parse("e2e13000-0000-4000-8000-000000000013");
        var output = new string('x', 4_100) + Sentinel + new string('y', 370);
        var model = new E2E13Model();
        var store = new InMemoryTaskStore();
        var audit = new RecordingAuditSink();
        var options = new AgentRunnerOptions
        {
            MaxSteps = 15,
            MaxLifetimeSteps = 60,
        };
        var runner = Runner(model, store, audit, options, new PartialEvidenceTool(output));

        var first = await runner.RunAsync(Goal, Actor, taskId);
        Assert.Equal((AgentTaskStatus.MaxStepsReached, 1), (first.Status, first.ExecutionAttempt));

        var second = await runner.ResumeAsync(first, Actor);
        Assert.Equal((AgentTaskStatus.MaxStepsReached, 2), (second.Status, second.ExecutionAttempt));

        var completed = await runner.ResumeAsync(second, Actor);

        Assert.Equal((AgentTaskStatus.Completed, 3), (completed.Status, completed.ExecutionAttempt));
        Assert.Equal(40, completed.Steps.Count(step => step.ToolCall is not null));

        // Raw persisted evidence is complete and addressable by its stable id, whatever the model was shown.
        Assert.Equal(output, completed.Steps[0].Result!.Output);
        Assert.True(completed.Steps[0].Result!.Output!.Length > 4_000);
        Assert.Equal(EvidenceRead.EvidenceId(taskId, 0), EvidenceRead.EvidenceId(completed.Id, completed.Steps[0].Index));
        Assert.Contains(model.StepSystemPrompts.Skip(1), prompt =>
            prompt.Contains(EvidenceLimitationsDigest.Version, StringComparison.Ordinal));
        Assert.Contains(model.StepSystemPrompts, prompt =>
            prompt.Contains("step 0", StringComparison.Ordinal)
            && prompt.Contains("completeness Partial", StringComparison.Ordinal));

        // The id the model used came from the runtime-authored evidence index of the step prompt (a resumed attempt rebuilds it).
        Assert.True(model.FoundIndexedId, "the evidence index must state the addressing rule in the prompt of the call that read step 0");
        var read = Assert.Single(audit.Events.OfType<EvidenceReadAuditEvent>());
        Assert.Equal(EvidenceReadResultCode.Success, read.ResultCode);
        Assert.Equal(200, read.ReturnedLength);
        Assert.Equal(39, read.StepIndex);
        Assert.True(model.SawRetrievedSentinel);

        // Four plan revisions across three execution attempts, and the index never lists more than its bound.
        Assert.Equal(4, completed.Plans.Count);
        foreach (var prompt in model.StepSystemPrompts)
        {
            var start = prompt.IndexOf(EvidenceIndex.OpenMarker, StringComparison.Ordinal);
            if (start < 0)
            {
                continue;
            }

            var block = prompt[start..(prompt.IndexOf(EvidenceIndex.CloseMarker, StringComparison.Ordinal) + EvidenceIndex.CloseMarker.Length)];
            Assert.True(block.Split("\n- step ").Length - 1 <= EvidenceIndex.MaxEntries);
        }
    }

    private static AgentRunner Runner(
        IChatModel model,
        ITaskStore store,
        IAuditSink audit,
        AgentRunnerOptions options,
        ITool tool)
    {
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        registry.Register(new PackageId("test.package"), tool);
        return new AgentRunner(model, registry, new DefaultTestPolicyEngine(), new NeverCalledApprovalProvider(),
            audit, store, TimeProvider.System, NullLogger<AgentRunner>.Instance, options);
    }

    private sealed class E2E13Model : IChatModel
    {
        // Each plan step is discriminated by the tool call index (ADR-0050); the call that proposes the replan is never executed,
        // so every replan's indices skip it.
        // The initial plan and each replan are sized so that the call proposed once the current plan is spent is the 9th, 19th and
        // 39th executed tool step: AgentRunner then replans on its own (ADR-0014 rule C8: a call proposed after every planned step was attempted).
        private static readonly int[] PlanSizes = [8, 9, 19, 10];

        private int toolsIssued;
        private int plansIssued;
        private bool readRequested;
        private bool waitingForReadResult;

        internal List<string> StepSystemPrompts { get; } = [];
        internal bool SawRetrievedSentinel { get; private set; }
        internal bool FoundIndexedId { get; private set; }
        internal string PromptAtRead { get; private set; } = string.Empty;

        public ChatModelDescriptor Descriptor { get; } = new("fake", "e2e-13");

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            // Planning and replanning are identified by their explicit protocol prompt. An exhausted
            // execution step also offers no native tool under ADR-0046, but it is still a step call.
            if (request.SystemPrompt.Contains("lay out your plan", StringComparison.Ordinal)
                || request.SystemPrompt.Contains("Revise it", StringComparison.Ordinal))
            {
                return Task.FromResult(PlanningTestSupport.PlanResponseIndexed("test.partial",
                    PlanSizes[plansIssued], startIndex: PlanSizes.Take(plansIssued).Sum() + plansIssued++));
            }

            StepSystemPrompts.Add(request.SystemPrompt);
            if (waitingForReadResult)
            {
                SawRetrievedSentinel = request.History.Any(turn =>
                    turn.Content?.Contains(Sentinel, StringComparison.Ordinal) == true);
                waitingForReadResult = false;
                return Task.FromResult(Call(toolsIssued++));
            }

            if (toolsIssued == 39 && !readRequested)
            {
                readRequested = true;
                waitingForReadResult = true;
                var match = IndexEntryForStepZero().Match(request.SystemPrompt);
                FoundIndexedId = match.Success;
                PromptAtRead = request.SystemPrompt;
                var id = match.Success ? match.Groups[1].Value + "0" : "ev1:unknown:0";
                var directive =
                    $"{{\"runtime\":\"EvidenceRead/v1\",\"evidenceId\":\"{id}\",\"source\":\"result\",\"offset\":4000,\"length\":200}}";
                return Task.FromResult(new ModelResponse(directive, [], false, null));
            }

            if (toolsIssued >= 40)
            {
                return Task.FromResult(new ModelResponse("done", [], true, null));
            }

            return Task.FromResult(Call(toolsIssued++));
        }

        private static ModelResponse Call(int index) =>
            new(null, [new ModelToolCall($"e2e-{index}", "test.partial", new ToolArguments(PlanningTestSupport.IndexArguments(index)))], false, null);
    }

    private sealed class PartialEvidenceTool(string output) : ITool
    {
        public ToolManifest Manifest { get; } = new()
        {
            Name = "test.partial",
            Description = "Returns deterministic partial evidence.",
            Risk = RiskLevel.Read,
            Platforms = [CurrentPlatform.Id],
            Requires = [],
            Parameters = [PlanningTestSupport.CallIndexParameter],
        };

        public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) =>
            Task.FromResult(ToolCallResult.Success(output) with { Completeness = ToolResultCompleteness.Partial });
    }
}
