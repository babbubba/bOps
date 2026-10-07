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

        Assert.False(model.Measurements[10].ArchiveActive);
        Assert.True(model.Measurements[20].ArchiveActive);
        Assert.True(model.Measurements[40].ArchiveActive);
        Assert.True(model.Measurements[20].HistoricalCharacters <= BoundedHistory.FixtureHistoricalMaxCharacters);
        Assert.True(model.Measurements[40].HistoricalCharacters <= BoundedHistory.FixtureHistoricalMaxCharacters);
        Assert.InRange(Math.Abs(model.Measurements[40].HistoricalCharacters - model.Measurements[20].HistoricalCharacters),
            0, BoundedHistory.CompactRecordMaxCharacters);

        // B5: the three replans below were produced by AgentRunner.ReplanAsync itself (plan exhaustion), and what is measured is
        // the ModelRequest the fake model actually received for each of them, never a rebuild of what it would have been.
        Assert.Equal([10, 20, 40], model.ReplanMeasurements.Keys.OrderBy(key => key));
        Assert.Equal(4, completed.Plans.Count);
        Assert.Equal(3, completed.Plans.Count(plan => plan.Revision > 0));
        foreach (var (ordinal, replan) in model.ReplanMeasurements)
        {
            Assert.InRange(replan.HistoricalCharacters, 0, BoundedHistory.FixtureHistoricalMaxCharacters);
            Assert.InRange(replan.PlanTurnCharacters, 1, AgentRunner.ReplanPlanMaxCharacters);
            Assert.Equal(model.Measurements[ordinal].HistoricalCharacters, replan.HistoricalCharacters);
            Assert.Equal(ordinal - 1, replan.CompletedToolSteps);
        }

        Assert.InRange(
            Math.Abs(model.ReplanMeasurements[40].HistoricalCharacters - model.ReplanMeasurements[20].HistoricalCharacters),
            0, BoundedHistory.CompactRecordMaxCharacters);

        foreach (var (ordinal, completedSteps) in new[] { (10, 9), (20, 19), (40, 39) })
        {
            var persisted = completed.Steps.Where(step => step.ToolCall is not null).Take(completedSteps).ToList();
            model.Measurements[ordinal] = model.Measurements[ordinal] with
            {
                LegacyHistoricalCharacters = LegacyHistoricalCharacters(persisted),
            };
        }

        foreach (var measurement in model.Measurements.OrderBy(item => item.Key))
        {
            var replan = model.ReplanMeasurements[measurement.Key];
            Console.WriteLine(
                $"E2E-13 step {measurement.Key}: before={measurement.Value.LegacyHistoricalCharacters}; " +
                $"after={measurement.Value.HistoricalCharacters}; replan={replan.HistoricalCharacters}; " +
                $"plan-turn={replan.PlanTurnCharacters}; whole={measurement.Value.WholeRequestCharacters}; " +
                $"replan-whole={replan.WholeRequestCharacters}; schema={measurement.Value.ToolSchemaCharacters}");
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
        // The initial plan and each replan are sized so that the call proposed once the current plan is spent is the 9th, 19th and
        // 39th executed tool step: AgentRunner then replans on its own (ADR-0014 rule C8: a call proposed after every planned step was attempted).
        private static readonly int[] PlanSizes = [8, 9, 19, 10];

        private int toolsIssued;
        private int plansIssued;
        private bool readRequested;
        private bool waitingForReadResult;

        internal Dictionary<int, PromptMeasurement> Measurements { get; } = [];
        internal Dictionary<int, ReplanMeasurement> ReplanMeasurements { get; } = [];
        internal List<string> StepSystemPrompts { get; } = [];
        internal bool SawRetrievedSentinel { get; private set; }

        public ChatModelDescriptor Descriptor { get; } = new("fake", "e2e-13");

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            // Planning and replanning are identified by their explicit protocol prompt. An exhausted
            // execution step also offers no native tool under ADR-0046, but it is still a step call.
            if (request.SystemPrompt.Contains("lay out your plan", StringComparison.Ordinal)
                || request.SystemPrompt.Contains("Revise it", StringComparison.Ordinal))
            {
                if (plansIssued > 0)
                {
                    RecordReplan(request);
                }

                return Task.FromResult(PlanningTestSupport.PlanResponse(
                    stepCount: PlanSizes[plansIssued++], expectedTool: "test.partial"));
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
                    schema)
                {
                    ArchiveActive = request.History.Any(turn =>
                        turn.Content?.Contains("archive;range=", StringComparison.Ordinal) == true),
                });
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

        // A replan request is [goal, projected current plan, bounded history..., triggering step]: the goal and the plan are
        // fixed costs reported on their own, and everything after them is the historical component the bound is about.
        private void RecordReplan(ModelRequest request)
        {
            var ordinal = toolsIssued + 1;
            Assert.Contains("Your previous plan no longer matches", request.SystemPrompt, StringComparison.Ordinal);
            Assert.StartsWith("Plan (revision", request.History[1].Content, StringComparison.Ordinal);
            var historical = HistoricalCharacters(request.History.Skip(1).ToList()); // skips the goal and, with it, the plan turn
            var planTurn = request.History[1].Content!.Length;
            ReplanMeasurements[ordinal] = new ReplanMeasurement(
                historical,
                planTurn,
                request.SystemPrompt.Length + request.History.Sum(turn => turn.Content?.Length ?? 0),
                CompletedToolSteps(request.History));
        }

        private static int CompletedToolSteps(IReadOnlyList<ChatTurn> turns)
        {
            var verbatim = turns.Count(turn => turn.ToolCalls is { Count: > 0 });
            var block = turns.FirstOrDefault(turn =>
                turn.Content?.Contains("<<<BOPS_HISTORY/v1>>>", StringComparison.Ordinal) == true)?.Content ?? string.Empty;
            var compact = block.Split('\n').Count(line => line.StartsWith("s=", StringComparison.Ordinal));
            var archived = System.Text.RegularExpressions.Regex.Match(block, @"count=(\d+)") is { Success: true } match
                ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
                : 0;
            return verbatim + compact + archived;
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
        internal bool ArchiveActive { get; init; }
    }

    /// <summary>What the fake model measured in the <see cref="ModelRequest"/> a real <c>ReplanAsync</c> built.</summary>
    private sealed record ReplanMeasurement(
        int HistoricalCharacters,
        int PlanTurnCharacters,
        int WholeRequestCharacters,
        int CompletedToolSteps);

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
