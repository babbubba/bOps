// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using bOps.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>E2E-13: bounded context, durable evidence and two resume boundaries in one deterministic incident-like run.</summary>
public sealed class HardenEightE2ETests
{
    private const string Goal = "E2E-13 deterministic incident fixture";
    private const string Sentinel = "E2E13-OLD-EVIDENCE-SENTINEL";
    private static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("e2e-13");

    [Fact]
    public async Task E2E13_FortySteps_TwoResumes_PlateausAndRetrievesOldCompleteEvidence()
    {
        var taskId = Guid.Parse("e2e13000-0000-4000-8000-000000000013");
        var output = new string('x', 4_100) + Sentinel + new string('y', 370);
        var model = new E2E13Model(taskId, Sentinel);
        var store = new InMemoryTaskStore();
        var audit = new RecordingAuditSink();
        var options = new AgentRunnerOptions
        {
            MaxSteps = 15,
            MaxLifetimeSteps = 60,
            VerbatimHistorySteps = 3,
        };
        var runner = Runner(model, store, audit, options, new PartialEvidenceTool(output));

        var first = await runner.RunAsync(Goal, Actor, taskId);
        Assert.Equal((AgentTaskStatus.MaxStepsReached, 1), (first.Status, first.ExecutionAttempt));

        var second = await runner.ResumeAsync(first, Actor);
        Assert.Equal((AgentTaskStatus.MaxStepsReached, 2), (second.Status, second.ExecutionAttempt));

        var completed = await runner.ResumeAsync(second, Actor);

        Assert.Equal((AgentTaskStatus.Completed, 3), (completed.Status, completed.ExecutionAttempt));
        Assert.Equal(40, completed.Steps.Count(step => step.ToolCall is not null));
        Assert.Equal(output, completed.Steps[0].Result!.Output);
        Assert.True(completed.Steps[0].Result!.Output!.Length > 4_000);
        Assert.Equal(BoundedHistory.EvidenceId(taskId, 0), BoundedHistory.EvidenceId(completed.Id, completed.Steps[0].Index));
        Assert.Contains(model.StepSystemPrompts.Skip(1), prompt =>
            prompt.Contains(EvidenceLimitationsDigest.Version, StringComparison.Ordinal));
        Assert.Contains(model.StepSystemPrompts, prompt =>
            prompt.Contains("step 0", StringComparison.Ordinal)
            && prompt.Contains("completeness Partial", StringComparison.Ordinal));

        var read = Assert.Single(audit.Events.OfType<EvidenceReadAuditEvent>());
        Assert.Equal(EvidenceReadResultCode.Success, read.ResultCode);
        Assert.Equal(200, read.ReturnedLength);
        Assert.Equal(39, read.StepIndex);
        Assert.True(model.SawRetrievedSentinel);

        foreach (var point in model.Measurements.Values)
        {
            Assert.InRange(point.HistoricalCharacters, 0, BoundedHistory.FixtureHistoricalMaxCharacters);
            Assert.Equal(point.WholeRequestCharacters,
                point.SystemCharacters + point.HistoryCharactersIncludingGoal + point.ToolSchemaCharacters);
        }

        Assert.True(model.Measurements[20].HistoricalCharacters <= BoundedHistory.FixtureHistoricalMaxCharacters);
        Assert.True(model.Measurements[40].HistoricalCharacters <= BoundedHistory.FixtureHistoricalMaxCharacters);
        Assert.InRange(Math.Abs(model.Measurements[40].HistoricalCharacters - model.Measurements[20].HistoricalCharacters),
            0, BoundedHistory.CompactRecordMaxCharacters);

        foreach (var (ordinal, completedSteps) in new[] { (10, 9), (20, 19), (40, 39) })
        {
            var persisted = completed.Steps.Where(step => step.ToolCall is not null).Take(completedSteps).ToList();
            var normal = BoundedHistory.Build(taskId, Goal, persisted, verbatimSteps: 3);
            var replan = BoundedHistory.Build(taskId, Goal, persisted, verbatimSteps: 3,
                requiredVerbatimStep: persisted[^1].Index);
            Assert.Equal(normal.HistoricalCharacters, model.Measurements[ordinal].HistoricalCharacters);
            Assert.InRange(replan.HistoricalCharacters, 0, BoundedHistory.FixtureHistoricalMaxCharacters);
            Assert.Equal(normal.HistoricalCharacters, replan.HistoricalCharacters);

            model.Measurements[ordinal] = model.Measurements[ordinal] with
            {
                LegacyHistoricalCharacters = LegacyHistoricalCharacters(persisted),
                ReplanHistoricalCharacters = replan.HistoricalCharacters,
            };
        }

        foreach (var measurement in model.Measurements.OrderBy(item => item.Key))
        {
            Console.WriteLine(
                $"E2E-13 step {measurement.Key}: before={measurement.Value.LegacyHistoricalCharacters}; " +
                $"after={measurement.Value.HistoricalCharacters}; replan={measurement.Value.ReplanHistoricalCharacters}; " +
                $"whole={measurement.Value.WholeRequestCharacters}; schema={measurement.Value.ToolSchemaCharacters}");
        }
    }

    private static int LegacyHistoricalCharacters(IReadOnlyList<PlanStep> steps) =>
        steps.Sum(step =>
            step.ToolCall!.ToolName.Length
            + step.ToolCall.Arguments.ToJson().ToJsonString().Length
            + AgentRunner.WrapToolOutput(step.Observation ?? string.Empty).Length);

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

    private sealed class E2E13Model(Guid taskId, string sentinel) : IChatModel
    {
        private int toolsIssued;
        private bool planned;
        private bool readRequested;
        private bool waitingForReadResult;

        internal Dictionary<int, PromptMeasurement> Measurements { get; } = [];
        internal List<string> StepSystemPrompts { get; } = [];
        internal bool SawRetrievedSentinel { get; private set; }

        public ChatModelDescriptor Descriptor { get; } = new("fake", "e2e-13");

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            if (!planned)
            {
                planned = true;
                return Task.FromResult(PlanningTestSupport.PlanResponse(stepCount: 50));
            }

            StepSystemPrompts.Add(request.SystemPrompt);
            var ordinal = toolsIssued + 1;
            if (ordinal is 10 or 20 or 40 && !Measurements.ContainsKey(ordinal))
            {
                var historical = HistoricalCharacters(request.History);
                var history = historical + (request.History.Count == 0 ? 0 : request.History[0].Content?.Length ?? 0);
                var schema = JsonSerializer.Serialize(request.AvailableTools).Length;
                Measurements.Add(ordinal, new PromptMeasurement(
                    historical,
                    request.SystemPrompt.Length + history + schema,
                    request.SystemPrompt.Length,
                    history,
                    schema));
            }

            if (waitingForReadResult)
            {
                SawRetrievedSentinel = request.History.Any(turn =>
                    turn.Content?.Contains(sentinel, StringComparison.Ordinal) == true);
                waitingForReadResult = false;
                return Task.FromResult(Call(toolsIssued++));
            }

            if (toolsIssued == 39 && !readRequested)
            {
                readRequested = true;
                waitingForReadResult = true;
                var directive =
                    $"{{\"runtime\":\"EvidenceRead/v1\",\"evidenceId\":\"{BoundedHistory.EvidenceId(taskId, 0)}\",\"source\":\"result\",\"offset\":4000,\"length\":200}}";
                return Task.FromResult(new ModelResponse(directive, [], false, null));
            }

            if (toolsIssued >= 40)
            {
                return Task.FromResult(new ModelResponse("done", [], true, null));
            }

            return Task.FromResult(Call(toolsIssued++));
        }

        private static ModelResponse Call(int index) =>
            new(null, [new ModelToolCall($"e2e-{index}", "test.partial", ToolArguments.Empty)], false, null);

        private static int HistoricalCharacters(IReadOnlyList<ChatTurn> turns) => turns.Skip(1).Sum(turn =>
            (turn.Content?.Length ?? 0)
            + (turn.ToolCalls?.Sum(call => call.ToolName.Length + call.Arguments.ToJson().ToJsonString().Length) ?? 0));
    }

    private sealed record PromptMeasurement(
        int HistoricalCharacters,
        int WholeRequestCharacters,
        int SystemCharacters,
        int HistoryCharactersIncludingGoal,
        int ToolSchemaCharacters)
    {
        internal int LegacyHistoricalCharacters { get; init; }
        internal int ReplanHistoricalCharacters { get; init; }
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
            Parameters = [],
        };

        public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) =>
            Task.FromResult(ToolCallResult.Success(output) with { Completeness = ToolResultCompleteness.Partial });
    }
}
