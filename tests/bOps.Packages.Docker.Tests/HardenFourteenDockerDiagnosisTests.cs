// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Runtime;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Packages.Docker.Tests;

/// <summary>E2E-1: a real stopped Docker workload is diagnosed through the production typed tools and agent loop.</summary>
public sealed class HardenFourteenDockerDiagnosisTests
{
    private static readonly DockerClientFactory ClientFactory = new();
    private static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("harden-14-operator");

    [DockerAvailableFact]
    [Trait("Category", "Harden14")]
    public async Task E2E1_ARealStoppedContainer_IsDiagnosedThroughTypedDockerEvidence_AndAudited()
    {
        await using var container = await TestContainer.CreateAsync(ClientFactory);
        using (var client = ClientFactory.Create())
        {
            Assert.True(await client.Containers.StartContainerAsync(container.Id, new ContainerStartParameters()));
            await client.Containers.StopContainerAsync(container.Id, new ContainerStopParameters { WaitBeforeKillSeconds = 5 });
        }

        var registry = new ToolRegistry(new AlwaysAvailableProbe());
        foreach (var tool in new DockerToolProvider(ClientFactory).GetTools())
        {
            registry.Register(new PackageId("bops.packages.docker"), tool);
        }

        await registry.RefreshCapabilitiesAsync();

        var model = new StoppedContainerModel(container.Name);
        var audit = new RecordingAuditSink();
        var runner = new AgentRunner(
            model,
            registry,
            new ReadOnlyPolicy(),
            new NeverApprove(),
            audit,
            new MemoryStore(),
            TimeProvider.System,
            NullLogger<AgentRunner>.Instance,
            new AgentRunnerOptions());

        var result = await runner.RunAsync($"Diagnose why Docker workload {container.Name} is not serving requests.", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Contains(container.Name, result.Steps[^1].Observation, StringComparison.Ordinal);
        Assert.Contains("stopped", result.Steps[^1].Observation, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            ["docker.containers", "docker.inspect", "docker.logs"],
            result.Steps.Where(step => step.ToolCall is not null).Select(step => step.ToolCall!.ToolName));

        var toolEvents = audit.Events.OfType<ToolCallAuditEvent>().ToList();
        Assert.Equal(["docker.containers", "docker.inspect", "docker.logs"], toolEvents.Select(evt => evt.Tool));
        Assert.All(toolEvents, evt => Assert.Equal(ToolOutcome.Success, evt.Outcome));
        Assert.Equal(5, audit.Events.OfType<ModelCallAuditEvent>().Count());
    }

    private sealed class StoppedContainerModel(string containerName) : IChatModel
    {
        private int _call;

        public ChatModelDescriptor Descriptor { get; } = new("harden-14", "stopped-container-script");

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            var response = _call++ switch
            {
                0 => Plan(),
                1 => Call("list", "docker.containers", ToolArguments.Empty),
                2 => Call("inspect", "docker.inspect", Arguments(("container", containerName))),
                3 => Call("logs", "docker.logs", Arguments(("container", containerName), ("tail", 20))),
                4 => Diagnose(request),
                _ => throw new InvalidOperationException("The deterministic E2E-1 model received an unexpected call."),
            };
            return Task.FromResult(response);
        }

        private ModelResponse Diagnose(ModelRequest request)
        {
            var evidence = string.Join('\n', request.History.Where(turn => turn.Role == ChatRole.Tool).Select(turn => turn.Content));
            Assert.Contains(containerName, evidence, StringComparison.Ordinal);
            Assert.Contains("exited", evidence, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("(no output)", evidence, StringComparison.Ordinal);
            return new ModelResponse(
                $"Diagnosis: Docker container {containerName} is stopped (exited); its bounded recent logs contain no output.",
                [],
                true,
                null);
        }

        private static ModelResponse Plan() => new(
            new JsonObject
            {
                ["rationale"] = "List the workload, inspect its exact state, then read bounded recent logs.",
                ["steps"] = new JsonArray(
                    new JsonObject { ["description"] = "List all containers.", ["expectedTool"] = "docker.containers" },
                    new JsonObject { ["description"] = "Inspect the target container.", ["expectedTool"] = "docker.inspect" },
                    new JsonObject { ["description"] = "Read bounded recent logs.", ["expectedTool"] = "docker.logs" }),
            }.ToJsonString(),
            [],
            false,
            null);

        private static ModelResponse Call(string id, string tool, ToolArguments arguments) =>
            new(null, [new ModelToolCall(id, tool, arguments)], false, null);

        private static ToolArguments Arguments(params (string Name, object Value)[] values)
        {
            var json = new JsonObject();
            foreach (var (name, value) in values)
            {
                json[name] = JsonValue.Create(value);
            }

            return ToolArguments.FromJson(json);
        }
    }

    private sealed class AlwaysAvailableProbe : ICapabilityProbe
    {
        public Task<bool> IsAvailableAsync(string capability, CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class ReadOnlyPolicy : IPolicyEngine
    {
        public PolicyDecision Evaluate(PolicyContext context) =>
            context.Manifest.Risk == RiskLevel.Read
                ? new PolicyDecision(PolicyMode.Automatic, "HARDEN-14 read-only diagnostic")
                : new PolicyDecision(PolicyMode.Forbidden, "E2E-1 never authorizes mutation");
    }

    private sealed class NeverApprove : IApprovalProvider
    {
        public Task<ApprovalDecision> RequestApprovalAsync(
            ToolManifest manifest,
            ToolArguments arguments,
            VerificationSpec? verification,
            string reason,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("E2E-1 must remain read-only and never request approval.");
    }

    private sealed class MemoryStore : ITaskStore
    {
        private readonly Dictionary<Guid, bOps.Abstractions.TaskState> _tasks = [];

        public Task SaveAsync(bOps.Abstractions.TaskState task, CancellationToken ct = default)
        {
            _tasks[task.Id] = task;
            return Task.CompletedTask;
        }

        public Task<bOps.Abstractions.TaskState?> LoadAsync(Guid taskId, CancellationToken ct = default) =>
            Task.FromResult(_tasks.GetValueOrDefault(taskId));

        public Task<IReadOnlyList<bOps.Abstractions.TaskState>> ListByStatusAsync(AgentTaskStatus status, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<bOps.Abstractions.TaskState>>(_tasks.Values.Where(task => task.Status == status).ToList());
    }

    private sealed class RecordingAuditSink : IAuditSink
    {
        public List<AuditEvent> Events { get; } = [];

        public Task WriteAsync(AuditEvent evt, CancellationToken ct = default)
        {
            Events.Add(evt);
            return Task.CompletedTask;
        }
    }
}
