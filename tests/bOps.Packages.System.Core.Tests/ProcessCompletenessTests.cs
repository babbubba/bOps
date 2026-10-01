// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Sys.Core;
using bOps.Runtime;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Packages.System.Core.Tests;

/// <summary>
/// HARDEN-6 review fixes H6-R1 and H6-R2: <c>process.list</c> states whether its listing is whole (cut by the limit, or built from
/// processes that could not be fully read, is <see cref="ToolResultCompleteness.Partial"/>), its <c>limit</c> is refused below 1 by
/// the contract before the tool runs, and <c>process.metrics</c> of a process that is not there is
/// <see cref="ToolResultCompleteness.Unavailable"/>, not a partial reading. The operating system is replaced by fixed observations,
/// so nothing here depends on how many processes the machine running the tests has.
/// </summary>
public sealed class ProcessCompletenessTests
{
    // The registry only offers a tool whose declared platform is the one it runs on.
    private static readonly string CurrentPlatform = OperatingSystem.IsWindows() ? "windows" : "linux";
    private static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("operator");

    public static TheoryData<string> Platforms => new() { "windows", "linux" };

    // ---- process.list: completeness from the observations, before the limit is applied ----

    [Theory]
    [MemberData(nameof(Platforms))]
    public async Task List_FewerProcessesThanTheLimit_IsComplete(string platform)
    {
        var tool = new ListTool(platform, Observed(3));

        var result = await tool.ExecuteAsync(Limit(5));

        Assert.True(result.Succeeded);
        Assert.Equal(ToolResultCompleteness.Complete, result.Completeness);
        Assert.Equal(3, Rows(result).Length);
    }

    [Theory]
    [MemberData(nameof(Platforms))]
    public async Task List_ExactlyAsManyProcessesAsTheLimit_IsComplete(string platform)
    {
        var tool = new ListTool(platform, Observed(3));

        var result = await tool.ExecuteAsync(Limit(3));

        Assert.Equal(ToolResultCompleteness.Complete, result.Completeness);
        Assert.Equal(3, Rows(result).Length);
    }

    [Theory]
    [MemberData(nameof(Platforms))]
    public async Task List_MoreProcessesThanTheLimit_IsPartial_AndKeepsTheLargest(string platform)
    {
        var tool = new ListTool(platform, Observed(4));

        var result = await tool.ExecuteAsync(Limit(3));

        Assert.True(result.Succeeded);
        Assert.Equal(ToolResultCompleteness.Partial, result.Completeness);
        Assert.Equal(["4", "3", "2"], Rows(result).Select(row => row.Split('\t')[0]).ToArray());
    }

    [Theory]
    [MemberData(nameof(Platforms))]
    public async Task List_DefaultLimitOfTwenty_IsPartialOnlyWhenMoreWereObserved(string platform)
    {
        var whole = await new ListTool(platform, Observed(20)).ExecuteAsync(ToolArguments.Empty);
        var cut = await new ListTool(platform, Observed(21)).ExecuteAsync(ToolArguments.Empty);

        Assert.Equal(ToolResultCompleteness.Complete, whole.Completeness);
        Assert.Equal(ToolResultCompleteness.Partial, cut.Completeness);
        Assert.Equal(20, Rows(cut).Length);
    }

    [Theory]
    [MemberData(nameof(Platforms))]
    public async Task List_NothingObserved_IsAnEmptyButCompleteAnswer(string platform)
    {
        var result = await new ListTool(platform, []).ExecuteAsync(Limit(5));

        Assert.True(result.Succeeded);
        Assert.Equal("No processes found.", result.Output);
        Assert.Equal(ToolResultCompleteness.Complete, result.Completeness);
    }

    [Theory]
    [MemberData(nameof(Platforms))]
    public async Task List_AProcessWhoseWorkingSetCouldNotBeRead_IsPartial_EvenWithinTheLimit(string platform)
    {
        var tool = new ListTool(platform, [.. Observed(2), new ProcessListObservation(99, "protected", null)]);

        var result = await tool.ExecuteAsync(Limit(10));

        Assert.Equal(ToolResultCompleteness.Partial, result.Completeness);
        Assert.Contains("99\tprotected\t0 MB", result.Output!, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Platforms))]
    public async Task List_AProcessWhoseNameCouldNotBeRead_IsPartial_AndKeepsTheLegacyPlaceholder(string platform)
    {
        var tool = new ListTool(platform, [.. Observed(2), new ProcessListObservation(99, null, 5L * 1024 * 1024)]);

        var result = await tool.ExecuteAsync(Limit(10));

        Assert.Equal(ToolResultCompleteness.Partial, result.Completeness);
        Assert.Contains("99\t<unknown>\t5 MB", result.Output!, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Platforms))]
    public async Task List_CalledDirectlyWithALimitBelowOne_IsAValidationFailure_WithoutObserving(string platform)
    {
        var tool = new ListTool(platform, Observed(3));

        var result = await tool.ExecuteAsync(Limit(0));

        Assert.False(result.Succeeded);
        Assert.Equal(ToolFailureKind.Validation, result.FailureKind);
        Assert.Equal(0, tool.Observations);
    }

    [Fact]
    public void List_Selection_CountsEveryObservationBeforeTheLimit()
    {
        var selection = ProcessListToolBase.Select(Observed(5), 2);

        Assert.Equal(5, selection.ObservedProcesses);
        Assert.Equal(2, selection.Processes.Count);
        Assert.True(selection.Truncated);
        Assert.Equal(0, selection.Unreadable);
    }

    [Fact]
    public void List_Manifest_DeclaresLimitMinimumOne_AndNoInventedMaximum()
    {
        var limit = Assert.Single(SystemToolManifests.ProcessList("linux").Parameters, parameter => parameter.Name == "limit");

        Assert.Equal(1, limit.Minimum);
        Assert.Null(limit.Maximum);
    }

    // ---- through the runtime ----

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task List_ALimitBelowOne_IsRejectedByTheContract_BeforeTheToolRuns(int limit)
    {
        var tool = new ListTool(CurrentPlatform, Observed(3));

        var (task, audit) = await RunAsync(tool, "process.list", new JsonObject { ["limit"] = limit });

        var step = Assert.Single(task.Steps, step => step.ToolCall?.ToolName == "process.list");
        Assert.False(step.Result!.Succeeded);
        Assert.Equal(ToolFailureKind.Validation, step.Result.FailureKind);
        Assert.Equal($"Argument 'limit' = {limit} is below minimum 1.", step.Result.ErrorMessage);
        Assert.Equal(0, tool.Observations);
        Assert.Equal(ToolFailureKind.Validation, Assert.Single(audit.Events.OfType<ToolCallAuditEvent>()).FailureKind);
    }

    [Fact]
    public async Task List_TruncatedByTheLimit_IsPartialOnTheStepAndInTheAudit()
    {
        var (task, audit) = await RunAsync(new ListTool(CurrentPlatform, Observed(4)), "process.list", new JsonObject { ["limit"] = 2 });

        var step = Assert.Single(task.Steps, step => step.ToolCall?.ToolName == "process.list");
        Assert.True(step.Result!.Succeeded);
        Assert.Equal(ToolResultCompleteness.Partial, step.Result.Completeness);
        Assert.Equal(ToolResultCompleteness.Partial, Assert.Single(audit.Events.OfType<ToolCallAuditEvent>()).Completeness);
    }

    [Fact]
    public async Task Metrics_OfAProcessThatIsNotThere_IsUnavailableOnTheStepAndInTheAudit()
    {
        var tool = new MetricsTool(CurrentPlatform, new ProcessSample { Exists = false });

        var (task, audit) = await RunAsync(tool, "process.metrics", new JsonObject { ["pid"] = 4242, ["sampleMilliseconds"] = 200 });

        var step = Assert.Single(task.Steps, step => step.ToolCall?.ToolName == "process.metrics");
        Assert.True(step.Result!.Succeeded, step.Result.ErrorMessage);
        var json = JsonNode.Parse(step.Result.Output!)!.AsObject();
        Assert.False(json["exists"]!.GetValue<bool>());
        Assert.True(json["partial"]!.GetValue<bool>()); // the legacy field is unchanged; only its typed reading is corrected
        Assert.Equal(ToolResultCompleteness.Unavailable, step.Result.Completeness);
        Assert.Equal(ToolResultCompleteness.Unavailable, Assert.Single(audit.Events.OfType<ToolCallAuditEvent>()).Completeness);
    }

    [Fact]
    public async Task Metrics_OfALiveProcessWithAnUnreadableCounter_StaysPartial()
    {
        var tool = new MetricsTool(CurrentPlatform, new ProcessSample { Exists = true, WorkingSetBytes = 1024 * 1024 });

        var result = await tool.ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["pid"] = 4242, ["sampleMilliseconds"] = 200 }));

        Assert.True(result.Succeeded);
        Assert.Equal(ToolResultCompleteness.Partial, result.Completeness);
    }

    // ---- helpers ----

    private static ToolArguments Limit(int limit) => ToolArguments.FromJson(new JsonObject { ["limit"] = limit });

    /// <summary><paramref name="count"/> fully readable processes, PID n using n MB.</summary>
    private static ProcessListObservation[] Observed(int count) =>
        Enumerable.Range(1, count).Select(pid => new ProcessListObservation(pid, $"p{pid}", pid * 1024L * 1024)).ToArray();

    private static string[] Rows(ToolCallResult result) => result.Output!.Split('\n').Skip(1).ToArray();

    private static async Task<(TaskState Task, RecordingAudit Audit)> RunAsync(ITool tool, string toolName, JsonObject arguments)
    {
        var registry = new ToolRegistry(new Probe());
        registry.Register(new PackageId("test.package"), tool);
        var model = new ScriptedModel(
            new ModelResponse(new JsonObject
            {
                ["rationale"] = "Read the process evidence.",
                ["steps"] = new JsonArray(new JsonObject { ["description"] = "read", ["expectedTool"] = toolName }),
            }.ToJsonString(), [], false, null),
            new ModelResponse(null, [new ModelToolCall("call-1", toolName, ToolArguments.FromJson(arguments))], false, null),
            new ModelResponse("done", [], true, null));
        var audit = new RecordingAudit();
        var runner = new AgentRunner(
            model, registry, new ReadOnlyPolicy(), new NoApprovals(), audit, new MemoryStore(), TimeProvider.System,
            NullLogger<AgentRunner>.Instance, new AgentRunnerOptions());

        return (await runner.RunAsync("inspect processes", Actor), audit);
    }

    private sealed class ListTool(string platform, IReadOnlyList<ProcessListObservation> observed) : ProcessListToolBase(platform)
    {
        public int Observations { get; private set; }

        protected override Task<IReadOnlyList<ProcessListObservation>> ObserveAsync(CancellationToken ct)
        {
            Observations++;
            return Task.FromResult(observed);
        }
    }

    private sealed class MetricsTool(string platform, ProcessSample sample) : ProcessMetricsToolBase(platform)
    {
        protected override Task<ProcessSample> SampleAsync(int pid, CancellationToken ct) => Task.FromResult(sample);
    }

    private sealed class ScriptedModel(params ModelResponse[] responses) : IChatModel
    {
        private int calls;

        public ChatModelDescriptor Descriptor { get; } = new("scripted", "scripted-model");

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default) =>
            Task.FromResult(responses[Math.Min(calls++, responses.Length - 1)]);
    }

    private sealed class RecordingAudit : IAuditSink
    {
        public List<AuditEvent> Events { get; } = [];

        public Task WriteAsync(AuditEvent evt, CancellationToken ct = default)
        {
            Events.Add(evt);
            return Task.CompletedTask;
        }
    }

    private sealed class ReadOnlyPolicy : IPolicyEngine
    {
        public PolicyDecision Evaluate(PolicyContext context) =>
            context.Manifest.Risk == RiskLevel.Read
                ? new PolicyDecision(PolicyMode.Automatic, "Read is automatic")
                : new PolicyDecision(PolicyMode.Forbidden, "not a Read tool");
    }

    private sealed class NoApprovals : IApprovalProvider
    {
        public Task<ApprovalDecision> RequestApprovalAsync(
            ToolManifest manifest, ToolArguments arguments, VerificationSpec? verification, string reason, CancellationToken ct = default) =>
            throw new InvalidOperationException("A Read tool must never need an approval.");
    }

    private sealed class MemoryStore : ITaskStore
    {
        private readonly Dictionary<Guid, TaskState> tasks = [];

        public Task SaveAsync(TaskState task, CancellationToken ct = default)
        {
            tasks[task.Id] = task;
            return Task.CompletedTask;
        }

        public Task<TaskState?> LoadAsync(Guid taskId, CancellationToken ct = default) =>
            Task.FromResult(tasks.TryGetValue(taskId, out var task) ? task : null);

        public Task<IReadOnlyList<TaskState>> ListByStatusAsync(AgentTaskStatus status, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TaskState>>(tasks.Values.Where(task => task.Status == status).ToList());
    }

    private sealed class Probe : ICapabilityProbe
    {
        public Task<bool> IsAvailableAsync(string capability, CancellationToken ct = default) => Task.FromResult(true);
    }
}
