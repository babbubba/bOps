// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>A Read tool that returns a fixed result and counts its executions, for the HARDEN-9 evidence scenarios.</summary>
internal sealed class ResultTool(string name, ToolCallResult result, IReadOnlyList<ToolParameter>? parameters = null) : ITool
{
    public int ExecutionCount { get; private set; }

    public ToolManifest Manifest { get; } = new()
    {
        Name = name,
        Description = "A fake evidence tool for tests.",
        Risk = RiskLevel.Read,
        Platforms = [CurrentPlatform.Id],
        Requires = [],
        Parameters = parameters ?? [],
    };

    public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ExecutionCount++;
        return Task.FromResult(result);
    }
}

/// <summary>A non-Read tool that executes and returns a fixed result, with the verification registration demands.</summary>
internal sealed class ResultHighRiskTool(string name, ToolCallResult result) : IVerifiableTool
{
    public ToolManifest Manifest { get; } = new()
    {
        Name = name,
        Description = "A fake action tool for tests.",
        Risk = RiskLevel.High,
        Platforms = [CurrentPlatform.Id],
        Requires = [],
        Parameters = [],
        Verification = new VerificationSpec("test.read", [], "Checks nothing, for tests."),
    };

    public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) => Task.FromResult(result);

    public Task<VerificationOutcome> EvaluateVerificationAsync(
        ToolArguments originalArguments, ToolCallResult verificationToolResult, CancellationToken ct = default) =>
        Task.FromResult(new VerificationOutcome(VerificationStatus.Confirmed, null));
}

/// <summary>Builders shared by the HARDEN-9 tests (ADR-0042).</summary>
internal static class EvidenceScenario
{
    internal static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("test-user");

    internal const string OriginalAnswer = "The machine looks healthy; nothing was found.";

    internal const string DisclosedAnswer = "The machine looks healthy.\n\nEvidence limitations\n- test.partial returned a partial result.";

    internal static ModelResponse Call(string tool, string id = "call-1") =>
        new(null, [new ModelToolCall(id, tool, ToolArguments.Empty)], false, null);

    internal static ModelResponse Final(string text, ModelUsage? usage = null) => new(text, [], true, usage);

    internal static ModelResponse Plan() => PlanningTestSupport.PlanResponse(stepCount: 0);

    internal static ToolCallResult Partial(string output = "partial output") =>
        ToolCallResult.Success(output) with { Completeness = ToolResultCompleteness.Partial };

    internal static ToolCallResult Complete(string output = "complete output") =>
        ToolCallResult.Success(output) with { Completeness = ToolResultCompleteness.Complete };

    internal static ToolCallResult Failed(ToolFailureKind kind, string message = "it failed") =>
        ToolCallResult.Failure(message) with { FailureKind = kind };

    internal static ToolRegistry Registry(params ITool[] tools)
    {
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        foreach (var tool in tools)
        {
            registry.Register(new PackageId("test.package"), tool);
        }

        return registry;
    }

    internal static AgentRunner Runner(
        IChatModel model, IToolRegistry registry, RecordingAuditSink audit, AgentRunnerOptions? options = null,
        IPolicyEngine? policy = null, ITaskStore? store = null) =>
        new(model, registry, policy ?? new DefaultTestPolicyEngine(), new NeverCalledApprovalProvider(), audit,
            store ?? new InMemoryTaskStore(), TimeProvider.System, NullLogger<AgentRunner>.Instance, options ?? new AgentRunnerOptions());

    internal static PlanStep ToolStep(int index, string tool, ToolCallResult result, string? observation = null, string? description = null) =>
        new(index, description ?? tool, new ModelToolCall($"call-{index}", tool, ToolArguments.Empty), result,
            observation ?? result.Output ?? result.ErrorMessage);

    internal static PlanStep DeniedStep(int index, string tool, ToolFailureKind kind) =>
        new(index, RuntimeStepTokens.Denied, new ModelToolCall($"call-{index}", tool, ToolArguments.Empty),
            ToolCallResult.Failure("refused") with { FailureKind = kind }, "ERROR: refused");

    /// <summary>The system prompt of the model call at <paramref name="requestIndex"/>.</summary>
    internal static string SystemPromptOf(FakeChatModel model, int requestIndex) => model.Requests[requestIndex].SystemPrompt;
}
