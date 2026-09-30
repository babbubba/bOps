// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bOps.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// HARDEN-6 / ADR-0022: typed argument constraints are enforced by the runtime before policy, approval and execution; a failed
/// call carries a structured <see cref="ToolFailureKind"/>, a successful one may declare its <see cref="ToolResultCompleteness"/>,
/// both reach the step, the audit and the model-facing observation, and a result that says neither behaves exactly as before.
/// </summary>
public sealed partial class ToolContractConstraintTests
{
    private static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("test-user");

    private static readonly ToolParameter SinceMinutes = new("sinceMinutes", ToolParameterType.Integer, "Crash window in minutes (1-10080, default 1440).", Required: false)
    {
        Minimum = 1,
        Maximum = 10080,
    };

    [GeneratedRegex(@"maximum (?<value>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex MaximumPattern();

    // ---- enforcement before policy and execution ----

    [Theory]
    [InlineData(0, "Argument 'sinceMinutes' = 0 is below minimum 1.")]
    [InlineData(-5, "Argument 'sinceMinutes' = -5 is below minimum 1.")]
    [InlineData(10081, "Argument 'sinceMinutes' = 10081 exceeds maximum 10080.")]
    [InlineData(43200, "Argument 'sinceMinutes' = 43200 exceeds maximum 10080.")]
    public async Task OutOfRangeArgument_IsRejectedBeforePolicyAndExecution_WithAModelReadableMessage(int value, string expectedMessage)
    {
        var tool = new ScriptedTool("system.crashes", [SinceMinutes]);
        var policy = new CountingPolicyEngine();
        var run = await RunSingleCallAsync(tool, policy, new JsonObject { ["sinceMinutes"] = value });

        Assert.Equal(0, policy.Evaluations);
        Assert.Equal(0, tool.ExecutionCount);
        var step = run.Result.Steps[0];
        Assert.Equal(ToolOutcome.Failure, step.Result!.Outcome);
        Assert.Equal(ToolFailureKind.Validation, step.Result.FailureKind);
        Assert.Equal(expectedMessage, step.Result.ErrorMessage);
        Assert.Contains($"ERROR (validation): {expectedMessage}", ToolTurns(run.Model)[0], StringComparison.Ordinal);

        var audited = Assert.Single(run.Audit.Events.OfType<ToolCallAuditEvent>());
        Assert.Equal(ToolFailureKind.Validation, audited.FailureKind);
        Assert.Null(audited.Completeness);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1440)]
    [InlineData(10080)]
    public async Task BoundaryAndInRangeValues_AreAccepted_AndReachPolicyAndTheTool(int value)
    {
        var tool = new ScriptedTool("system.crashes", [SinceMinutes]);
        var policy = new CountingPolicyEngine();
        var run = await RunSingleCallAsync(tool, policy, new JsonObject { ["sinceMinutes"] = value });

        Assert.Equal(1, policy.Evaluations);
        Assert.Equal(1, tool.ExecutionCount);
        Assert.True(run.Result.Steps[0].Result!.Succeeded);
    }

    [Fact]
    public async Task OmittedOptionalArgument_IsNotConstrained()
    {
        var tool = new ScriptedTool("system.crashes", [SinceMinutes]);
        var run = await RunSingleCallAsync(tool, new CountingPolicyEngine(), new JsonObject());

        Assert.Equal(1, tool.ExecutionCount);
        Assert.True(run.Result.Steps[0].Result!.Succeeded);
    }

    public static TheoryData<ToolParameter, JsonNode, string?> ConstraintMatrix => new()
    {
        { new("n", ToolParameterType.Number, "n") { Minimum = 0.5, Maximum = 2.5 }, JsonValue.Create(0.25)!, "Argument 'n' = 0.25 is below minimum 0.5." },
        { new("n", ToolParameterType.Number, "n") { Minimum = 0.5, Maximum = 2.5 }, JsonValue.Create(2.75)!, "Argument 'n' = 2.75 exceeds maximum 2.5." },
        { new("n", ToolParameterType.Number, "n") { Minimum = 0.5, Maximum = 2.5 }, JsonValue.Create(0.5)!, null },
        { new("n", ToolParameterType.Number, "n") { Minimum = 0.5, Maximum = 2.5 }, JsonValue.Create(2.5)!, null },
        { new("n", ToolParameterType.Integer, "n") { Minimum = 0 }, JsonValue.Create(-1)!, "Argument 'n' = -1 is below minimum 0." },
        { new("n", ToolParameterType.Integer, "n") { Minimum = 0 }, JsonValue.Create(int.MaxValue)!, null },
        { new("n", ToolParameterType.Integer, "n") { Maximum = 5 }, JsonValue.Create(int.MinValue)!, null },
        { new("s", ToolParameterType.String, "s") { MinLength = 2, MaxLength = 4 }, JsonValue.Create("a")!, "Argument 's' length 1 is below minimum 2." },
        { new("s", ToolParameterType.String, "s") { MinLength = 2, MaxLength = 4 }, JsonValue.Create("abcde")!, "Argument 's' length 5 exceeds maximum 4." },
        { new("s", ToolParameterType.String, "s") { MinLength = 2, MaxLength = 4 }, JsonValue.Create("ab")!, null },
        { new("s", ToolParameterType.String, "s") { MinLength = 2, MaxLength = 4 }, JsonValue.Create("abcd")!, null },
        { new("p", ToolParameterType.Path, "p") { MaxLength = 3 }, JsonValue.Create("abcd")!, "Argument 'p' length 4 exceeds maximum 3." },
        { new("l", ToolParameterType.PathList, "l") { MinItems = 1, MaxItems = 2 }, new JsonArray(), "Argument 'l' contains 0 items, below minimum 1." },
        { new("l", ToolParameterType.PathList, "l") { MinItems = 1, MaxItems = 2 }, new JsonArray("a", "b", "c"), "Argument 'l' contains 3 items, exceeding maximum 2." },
        { new("l", ToolParameterType.PathList, "l") { MinItems = 1, MaxItems = 2 }, new JsonArray("a"), null },
        { new("l", ToolParameterType.PathList, "l") { MinItems = 1, MaxItems = 2 }, new JsonArray("a", "b"), null },
    };

    [Theory]
    [MemberData(nameof(ConstraintMatrix))]
    public async Task EveryConstraintKind_IsEnforcedAtItsBoundary(ToolParameter parameter, JsonNode value, string? expectedError)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        ArgumentNullException.ThrowIfNull(value);
        var tool = new ScriptedTool("test.bounded", [parameter]);
        var policy = new CountingPolicyEngine();
        var run = await RunSingleCallAsync(tool, policy, new JsonObject { [parameter.Name] = value.DeepClone() });

        var result = run.Result.Steps[0].Result!;
        if (expectedError is null)
        {
            Assert.True(result.Succeeded);
            Assert.Equal(1, tool.ExecutionCount);
            return;
        }

        Assert.Equal(expectedError, result.ErrorMessage);
        Assert.Equal(ToolFailureKind.Validation, result.FailureKind);
        Assert.Equal(0, tool.ExecutionCount);
        Assert.Equal(0, policy.Evaluations);
    }

    [Fact]
    public async Task ConstraintMessages_DoNotDependOnTheCurrentCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        var commaDecimal = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        commaDecimal.NumberFormat.NumberDecimalSeparator = ",";
        CultureInfo.CurrentCulture = commaDecimal;
        try
        {
            var tool = new ScriptedTool("test.bounded", [new ToolParameter("n", ToolParameterType.Number, "n") { Maximum = 2.5 }]);
            var run = await RunSingleCallAsync(tool, new CountingPolicyEngine(), new JsonObject { ["n"] = 2.75 });

            Assert.Equal("Argument 'n' = 2.75 exceeds maximum 2.5.", run.Result.Steps[0].Result!.ErrorMessage);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    // ---- E2E-2: the model corrects a call the contract rejected ----

    [Fact]
    public async Task E2E2_TheModelCorrectsAnOutOfRangeCall_FromTheStructuredFeedback()
    {
        var tool = new ScriptedTool("system.crashes", [SinceMinutes]);
        var policy = new CountingPolicyEngine();
        var audit = new RecordingAuditSink();
        string? feedbackSeenByModel = null;
        var model = new ScriptedChatModel((request, call) => call switch
        {
            0 => PlanningTestSupport.PlanResponse(),
            1 => ToolCall("call-1", "system.crashes", new JsonObject { ["sinceMinutes"] = 43200 }),
            2 => CorrectFromFeedback(request, seen => feedbackSeenByModel = seen),
            _ => new ModelResponse("Crash window reviewed.", [], true, null),
        });

        var result = await CreateRunner(model, CreateRegistry(tool), audit, policy).RunAsync("why did it crash last month", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);

        // 1. the invalid call was rejected by the contract and the tool never saw it
        var rejected = result.Steps[0];
        Assert.Equal(ToolFailureKind.Validation, rejected.Result!.FailureKind);
        Assert.Equal("Argument 'sinceMinutes' = 43200 exceeds maximum 10080.", rejected.Result.ErrorMessage);
        Assert.Equal(1, policy.Evaluations); // only the corrected call ever reached policy
        Assert.DoesNotContain(tool.ReceivedArguments, arguments => arguments["sinceMinutes"]!.GetValue<int>() == 43200);

        // 2. the model received the structured feedback as the observation of that call, before it issued the next one
        Assert.Contains("ERROR (validation): Argument 'sinceMinutes' = 43200 exceeds maximum 10080.", feedbackSeenByModel, StringComparison.Ordinal);
        Assert.Contains("ERROR (validation): Argument 'sinceMinutes' = 43200 exceeds maximum 10080.", ToolTurns(model)[0], StringComparison.Ordinal);

        // 3. the model corrected the call from that feedback, and the second call met the contract
        var corrected = result.Steps[1];
        Assert.True(corrected.Result!.Succeeded);
        Assert.Equal(1, tool.ExecutionCount);
        Assert.Equal(10080, Assert.Single(tool.ReceivedArguments)["sinceMinutes"]!.GetValue<int>());

        // 4. both calls are audited, the rejection with its kind
        var audited = audit.Events.OfType<ToolCallAuditEvent>().ToArray();
        Assert.Equal(2, audited.Length);
        Assert.Equal(ToolFailureKind.Validation, audited[0].FailureKind);
        Assert.Equal(ToolOutcome.Success, audited[1].Outcome);
        Assert.Null(audited[1].FailureKind);

        // 5. and the tool's output reached the model afterwards
        Assert.Contains("done", ToolTurns(model)[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task E2E2_TheFeedbackIsWhatDrivesTheCorrection_NotTheScript()
    {
        // The same model given a contract whose maximum is 60 corrects to 60: the value comes from the feedback it read.
        var parameter = SinceMinutes with { Maximum = 60 };
        var tool = new ScriptedTool("system.crashes", [parameter]);
        var model = new ScriptedChatModel((request, call) => call switch
        {
            0 => PlanningTestSupport.PlanResponse(),
            1 => ToolCall("call-1", "system.crashes", new JsonObject { ["sinceMinutes"] = 43200 }),
            2 => CorrectFromFeedback(request),
            _ => new ModelResponse("Reviewed.", [], true, null),
        });

        var result = await CreateRunner(model, CreateRegistry(tool), new RecordingAuditSink(), new CountingPolicyEngine()).RunAsync("crashes", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(60, Assert.Single(tool.ReceivedArguments)["sinceMinutes"]!.GetValue<int>());
    }

    // ---- FailureKind propagation: tool / runtime -> result -> step -> audit -> model observation ----

    [Fact]
    public async Task ToolTimeout_IsClassifiedTimeout_InTheResultTheAuditAndTheObservation()
    {
        var run = await RunSingleCallAsync(new HangingTool("test.hangs"), new CountingPolicyEngine(), new JsonObject(), timeout: TimeSpan.FromMilliseconds(50));

        var result = run.Result.Steps[0].Result!;
        Assert.Equal(ToolOutcome.Timeout, result.Outcome);
        Assert.Equal(ToolFailureKind.Timeout, result.FailureKind);
        Assert.Equal(ToolFailureKind.Timeout, Assert.Single(run.Audit.Events.OfType<ToolCallAuditEvent>()).FailureKind);
        Assert.StartsWith("ERROR (timeout): ", ObservationOf(run), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolThatReportsItsOwnTimeoutOutcome_IsClassifiedTimeoutFromTheOutcomeNotTheMessage()
    {
        var tool = new ScriptedTool("test.slow", [], new ToolCallResult(ToolOutcome.Timeout, null, "the collector gave up"));
        var run = await RunSingleCallAsync(tool, new CountingPolicyEngine(), new JsonObject());

        Assert.Equal(ToolFailureKind.Timeout, run.Result.Steps[0].Result!.FailureKind);
        Assert.Equal(ToolFailureKind.Timeout, Assert.Single(run.Audit.Events.OfType<ToolCallAuditEvent>()).FailureKind);
    }

    [Fact]
    public async Task ToolException_IsClassifiedInternal()
    {
        var run = await RunSingleCallAsync(new ThrowingTool("test.throws"), new CountingPolicyEngine(), new JsonObject());

        Assert.Equal(ToolFailureKind.Internal, run.Result.Steps[0].Result!.FailureKind);
        Assert.Equal(ToolFailureKind.Internal, Assert.Single(run.Audit.Events.OfType<ToolCallAuditEvent>()).FailureKind);
        Assert.StartsWith("ERROR (internal): ", ObservationOf(run), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PolicyDenial_IsClassifiedAuthorization_AndNeverReachesTheTool()
    {
        var tool = new ScriptedTool("test.read", []);
        var run = await RunSingleCallAsync(tool, new StubPolicyEngine(PolicyMode.Forbidden, "not on this node"), new JsonObject());

        Assert.Equal(0, tool.ExecutionCount);
        var result = run.Result.Steps[0].Result!;
        Assert.Equal(ToolFailureKind.Authorization, result.FailureKind);
        Assert.Equal(ToolFailureKind.Authorization, run.Audit.Events.OfType<ToolCallAuditEvent>().Single().FailureKind);
        Assert.StartsWith("ERROR (authorization): ", ObservationOf(run), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownTool_IsAContractViolation_ClassifiedValidation()
    {
        var run = await RunSingleCallAsync(new ScriptedTool("test.read", []), new CountingPolicyEngine(), new JsonObject(), calledTool: "test.missing");

        Assert.Equal(ToolFailureKind.Validation, run.Result.Steps[0].Result!.FailureKind);
        Assert.Equal(ToolFailureKind.Validation, run.Audit.Events.OfType<ToolCallAuditEvent>().Single().FailureKind);
    }

    [Fact]
    public async Task ToolDeclaredEnvironmentFailure_IsPreservedAndReachesTheAuditAndTheModel()
    {
        var tool = new ScriptedTool("test.docker", [], ToolCallResult.Failure("The Docker daemon is unreachable.") with { FailureKind = ToolFailureKind.Environment });
        var run = await RunSingleCallAsync(tool, new CountingPolicyEngine(), new JsonObject());

        Assert.Equal(ToolFailureKind.Environment, run.Result.Steps[0].Result!.FailureKind);
        Assert.Equal(ToolFailureKind.Environment, run.Audit.Events.OfType<ToolCallAuditEvent>().Single().FailureKind);
        Assert.StartsWith("ERROR (environment): The Docker daemon is unreachable.", ObservationOf(run), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyFailureWithoutAKind_KeepsTheLegacyObservationAndAuditsNoKind()
    {
        var tool = new ScriptedTool("test.legacy", [], ToolCallResult.Failure("it broke"));
        var run = await RunSingleCallAsync(tool, new CountingPolicyEngine(), new JsonObject());

        var result = run.Result.Steps[0].Result!;
        Assert.Equal(ToolFailureKind.Unspecified, result.FailureKind);
        Assert.StartsWith("ERROR: it broke", ObservationOf(run), StringComparison.Ordinal);
        Assert.Null(run.Audit.Events.OfType<ToolCallAuditEvent>().Single().FailureKind);
    }

    // ---- Completeness propagation ----

    [Theory]
    [InlineData(ToolResultCompleteness.Complete)]
    [InlineData(ToolResultCompleteness.Partial)]
    [InlineData(ToolResultCompleteness.Unavailable)]
    public async Task DeclaredCompleteness_ReachesTheStepAndTheAudit_WhileSuccessStaysSuccess(ToolResultCompleteness completeness)
    {
        var tool = new ScriptedTool("system.crashes", [], ToolCallResult.Success("{\"items\":[]}") with { Completeness = completeness });
        var run = await RunSingleCallAsync(tool, new CountingPolicyEngine(), new JsonObject());

        var result = run.Result.Steps[0].Result!;
        Assert.True(result.Succeeded);
        Assert.Equal(completeness, result.Completeness);
        var audited = run.Audit.Events.OfType<ToolCallAuditEvent>().Single();
        Assert.Equal(completeness, audited.Completeness);
        Assert.Null(audited.FailureKind);
        Assert.Equal(ToolOutcome.Success, audited.Outcome);
    }

    [Fact]
    public async Task LegacySuccessWithoutCompleteness_IsUnspecified_AndAuditsNothing()
    {
        var run = await RunSingleCallAsync(new ScriptedTool("test.read", []), new CountingPolicyEngine(), new JsonObject());

        Assert.Equal(ToolResultCompleteness.Unspecified, run.Result.Steps[0].Result!.Completeness);
        Assert.Null(run.Audit.Events.OfType<ToolCallAuditEvent>().Single().Completeness);
    }

    // ---- helpers ----

    /// <summary>The content of every tool-result turn the model has been given, in order.</summary>
    private static string[] ToolTurns(ScriptedChatModel model) =>
        [.. model.Requests[^1].History.Where(turn => turn.Role == ChatRole.Tool).Select(turn => turn.Content ?? string.Empty)];

    private static string ObservationOf(Run run) => run.Result.Steps[0].Observation ?? string.Empty;

    private static ModelResponse ToolCall(string id, string tool, JsonObject arguments) =>
        new(null, [new ModelToolCall(id, tool, ToolArguments.FromJson(arguments))], false, null);

    /// <summary>
    /// A deterministic stand-in for a model that reads the last observation and, when it reports a maximum, retries with it.
    /// Nothing about the corrected value is scripted: it comes from the text the runtime fed back.
    /// </summary>
    private static ModelResponse CorrectFromFeedback(ModelRequest request, Action<string>? observe = null)
    {
        var observation = request.History.Last(turn => turn.Role == ChatRole.Tool).Content ?? string.Empty;
        observe?.Invoke(observation);
        var match = MaximumPattern().Match(observation);
        return match.Success
            ? ToolCall("call-2", "system.crashes", new JsonObject { ["sinceMinutes"] = int.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture) })
            : new ModelResponse("I could not correct the call.", [], true, null);
    }

    private static ToolRegistry CreateRegistry(params ITool[] tools)
    {
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        foreach (var tool in tools)
        {
            registry.Register(new PackageId("test.package"), tool);
        }

        return registry;
    }

    private static AgentRunner CreateRunner(IChatModel model, IToolRegistry registry, IAuditSink audit, IPolicyEngine policy, AgentRunnerOptions? options = null) =>
        new(model, registry, policy, new NeverCalledApprovalProvider(), audit, new InMemoryTaskStore(), TimeProvider.System,
            NullLogger<AgentRunner>.Instance, options ?? new AgentRunnerOptions());

    private static async Task<Run> RunSingleCallAsync(
        ITool tool, IPolicyEngine policy, JsonObject arguments, TimeSpan? timeout = null, string? calledTool = null)
    {
        var model = new ScriptedChatModel((_, call) => call switch
        {
            0 => PlanningTestSupport.PlanResponse(),
            1 => ToolCall("call-1", calledTool ?? tool.Manifest.Name, arguments),
            _ => new ModelResponse("Done.", [], true, null),
        });
        var audit = new RecordingAuditSink();
        var options = new AgentRunnerOptions { DefaultToolTimeout = timeout ?? TimeSpan.FromSeconds(30) };
        var result = await CreateRunner(model, CreateRegistry(tool), audit, policy, options).RunAsync("do the thing", Actor);
        return new Run(result, audit, model);
    }

    private sealed record Run(TaskState Result, RecordingAuditSink Audit, ScriptedChatModel Model);

    private sealed class CountingPolicyEngine : IPolicyEngine
    {
        public int Evaluations { get; private set; }

        public PolicyDecision Evaluate(PolicyContext context)
        {
            Evaluations++;
            return new PolicyDecision(PolicyMode.Automatic, "counted");
        }
    }

    private sealed class ScriptedChatModel(Func<ModelRequest, int, ModelResponse> script) : IChatModel
    {
        private readonly List<ModelRequest> requests = [];

        public ChatModelDescriptor Descriptor { get; } = new("fake", "scripted-model");

        public List<ModelRequest> Requests => requests;

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            requests.Add(request);
            return Task.FromResult(script(request, requests.Count - 1));
        }
    }

    private sealed class ScriptedTool(string name, IReadOnlyList<ToolParameter> parameters, ToolCallResult? result = null) : ITool
    {
        private readonly List<JsonObject> received = [];

        public int ExecutionCount => received.Count;

        public IReadOnlyList<JsonObject> ReceivedArguments => received;

        public ToolManifest Manifest { get; } = new()
        {
            Name = name,
            Description = "Scripted for constraint tests.",
            Risk = RiskLevel.Read,
            Platforms = [CurrentPlatform.Id],
            Requires = [],
            Parameters = parameters,
        };

        public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
        {
            received.Add(arguments.ToJson().DeepClone().AsObject());
            return Task.FromResult(result ?? ToolCallResult.Success("done"));
        }
    }
}
