// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Runtime;
using Docker.DotNet;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Packages.Docker.Tests;

/// <summary>
/// The governed volume lifecycle through the real <see cref="AgentRunner"/>, tool registry and audit path against a real daemon
/// (ADR-0033): the runner carries the verification arguments by name, asks a human for the tool that declares
/// <c>RequiresExplicitApproval</c> even when the policy would let it run, and records the verdict of each check. Only the model is scripted.
/// </summary>
public sealed class DockerRunnerFlowTests
{
    private static readonly DockerClientFactory Factory = new();
    private static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("operator");

    private sealed class ScriptedModel(params ModelResponse[] responses) : IChatModel
    {
        private int _calls;

        public ChatModelDescriptor Descriptor { get; } = new("scripted", "scripted-model");

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default) =>
            Task.FromResult(responses[_calls++]);
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

    /// <summary>Lets everything run without a human, so any approval seen came from the tool's own <c>RequiresExplicitApproval</c>.</summary>
    private sealed class PermissivePolicy : IPolicyEngine
    {
        public PolicyDecision Evaluate(PolicyContext context) => new(PolicyMode.Automatic, "test policy allows everything");
    }

    private sealed class ApprovingProvider : IApprovalProvider
    {
        public List<string> Asked { get; } = [];

        public Task<ApprovalDecision> RequestApprovalAsync(
            ToolManifest manifest, ToolArguments arguments, VerificationSpec? verification, string reason, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(manifest);
            Asked.Add(manifest.Name);
            return Task.FromResult(new ApprovalDecision(true, Actor, "approved by the test"));
        }
    }

    private sealed class MemoryStore : ITaskStore
    {
        private readonly Dictionary<Guid, TaskState> _tasks = [];

        public Task SaveAsync(TaskState task, CancellationToken ct = default)
        {
            _tasks[task.Id] = task;
            return Task.CompletedTask;
        }

        public Task<TaskState?> LoadAsync(Guid taskId, CancellationToken ct = default) =>
            Task.FromResult(_tasks.TryGetValue(taskId, out var task) ? task : null);

        public Task<IReadOnlyList<TaskState>> ListByStatusAsync(AgentTaskStatus status, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TaskState>>(_tasks.Values.Where(task => task.Status == status).ToList());
    }

    private sealed class Probe : ICapabilityProbe
    {
        public Task<bool> IsAvailableAsync(string capability, CancellationToken ct = default) => Task.FromResult(true);
    }

    private static ModelResponse Plan(params string[] tools) =>
        new(new JsonObject
        {
            ["rationale"] = "Create a volume and remove it again.",
            ["steps"] = new JsonArray(tools.Select(tool => (JsonNode)new JsonObject { ["description"] = tool, ["expectedTool"] = tool }).ToArray()),
        }.ToJsonString(), [], false, null);

    private static ModelResponse Calls(string id, string tool, string volume) =>
        new(null, [new ModelToolCall(id, tool, ToolArguments.FromJson(new JsonObject { ["volume"] = volume }))], false, null);

    [DockerAvailableFact]
    public async Task AVolumeIsCreatedAndRemovedThroughTheRunner_EachVerified_AndOnlyTheRemovalAsksForAHuman()
    {
        var name = $"bops-test-{Guid.NewGuid():N}";
        var registry = new ToolRegistry(new Probe());
        foreach (var tool in new DockerToolProvider(Factory).GetTools())
        {
            registry.Register(new PackageId("bops.packages.docker"), tool);
        }

        await registry.RefreshCapabilitiesAsync();

        var audit = new RecordingAudit();
        var approvals = new ApprovingProvider();
        var runner = new AgentRunner(
            new ScriptedModel(
                Plan("docker.volume.create", "docker.volume.remove"),
                Calls("call-1", "docker.volume.create", name),
                Calls("call-2", "docker.volume.remove", name),
                new ModelResponse("done", [], true, null)),
            registry,
            new PermissivePolicy(),
            approvals,
            audit,
            new MemoryStore(),
            TimeProvider.System,
            NullLogger<AgentRunner>.Instance,
            new AgentRunnerOptions());

        try
        {
            var task = await runner.RunAsync("Create a scratch volume and remove it.", Actor);

            Assert.True(
                task.Status == AgentTaskStatus.Completed,
                $"{task.Status}: {string.Join(" | ", task.Steps.Select(step => $"{step.ToolCall?.ToolName}={step.Result?.Outcome}:{step.Result?.ErrorMessage}:{step.Observation}"))}");
            var calls = audit.Events.OfType<ToolCallAuditEvent>().ToList();
            var create = Assert.Single(calls, call => call.Tool == "docker.volume.create");
            var remove = Assert.Single(calls, call => call.Tool == "docker.volume.remove");
            Assert.Equal(ToolOutcome.Success, create.Outcome);
            Assert.Equal(VerificationStatus.Confirmed, create.Verification);
            Assert.Equal(ToolOutcome.Success, remove.Outcome);
            Assert.Equal(VerificationStatus.Confirmed, remove.Verification);
            Assert.Equal(name, create.Arguments["volume"]!.GetValue<string>());

            // The Low-risk creation ran on the policy's say-so; the High-risk removal asked a human because its manifest says so.
            Assert.Equal(["docker.volume.remove"], approvals.Asked);
        }
        finally
        {
            using var client = Factory.Create();
            try
            {
                await client.Volumes.RemoveAsync(name, force: true, CancellationToken.None);
            }
            catch (DockerApiException)
            {
                // Already removed by the test itself.
            }
        }
    }
}
