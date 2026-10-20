// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.AgentFramework;
using bOps.Hosting;
using bOps.Runtime;
using Microsoft.Extensions.Logging;

namespace bOps.Api;

/// <summary>Creates an execution-scoped model whose adapter receives only the current key for each attempt.</summary>
internal sealed class ExecutionChatModelFactory(
    IChatModelRegistry registry, ProviderCredentialResolver credentials, ProviderConfigurationCoordinator coordinator)
{
    internal IChatModel Create(PinnedProviderConfiguration pin)
    {
        coordinator.ValidatePin(pin);
        List<IChatModel> candidates = [new AttemptModel(pin.ProviderId, pin.BaseUrl, pin.Model,
            pin.SupportsNativeToolCalling, pin.RequestTimeout, registry, credentials)];
        candidates.AddRange(pin.Fallbacks.Select(candidate => new AttemptModel(candidate.ProviderId,
            candidate.BaseUrl, candidate.Model, candidate.SupportsNativeToolCalling, candidate.RequestTimeout,
            registry, credentials)));
        return new FallbackChatModel(candidates, pin);
    }

    /// <summary>
    /// A single-candidate model for an explicitly configured summarizer. It has no fallback chain and resolves the
    /// credential of exactly that provider for each invocation through the same resolver as every other candidate.
    /// </summary>
    internal IChatModel CreateSummarizer(string providerId, string model, TimeSpan? requestTimeout)
    {
        var candidate = coordinator.ResolveCandidate(providerId, model, requestTimeout);
        return new AttemptModel(candidate.ProviderId, candidate.BaseUrl, candidate.Model,
            candidate.SupportsNativeToolCalling, candidate.RequestTimeout, registry, credentials);
    }

    private sealed class AttemptModel(string providerId, string baseUrl, string model, bool supportsNativeToolCalling,
        TimeSpan? requestTimeout, IChatModelRegistry registry,
        ProviderCredentialResolver credentials) : IChatModel
    {
        public ChatModelDescriptor Descriptor { get; } = new(providerId, model);

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            var key = credentials.Resolve(providerId);
            if (ProviderCredentialResolver.RequiresCredential(providerId) && string.IsNullOrEmpty(key))
                throw new ModelProtocolException($"No current credential is available for provider '{providerId}'.")
                    { FailureKind = ModelFailureKind.Authentication };
            // The adapter exists for exactly this invocation. It cannot retain a historical key
            // across retries, continuation, approval waits, or process restart.
            var options = new ChatModelOptions(providerId, baseUrl, null, model,
                supportsNativeToolCalling) { RequestTimeout = requestTimeout, ResolvedApiKey = key };
            return registry.Create(options).CompleteAsync(request, ct);
        }
    }
}

/// <summary>Host composition for a runner; Runtime receives only an IChatModel.</summary>
internal sealed class ExecutionRunnerFactory(
    ExecutionChatModelFactory models, IToolRegistry tools, IPolicyEngine policy,
    IApprovalProvider approvals, IAuditSink audit, ITaskStore tasks, TimeProvider time,
    ILogger<AgentRunner> logger, IConfiguration configuration, ISkillRegistry skills,
    IRoleProfileSource roles, IPlanApprovalProvider planApprovals,
    ILogger<DelegationRunner> delegationLogger, IDelegationStore delegations,
    IChatModel? testModel = null,
    ISystemMessageStore? systemMessages = null)
{
    internal AgentRunner CreateAgent(PinnedProviderConfiguration pin, Guid? delegationId = null)
    {
        var options = Options();
        options.Validate(pin.RequestTimeout ?? ChatModelOptions.DefaultRequestTimeout);
        var executionModel = testModel ?? models.Create(pin);
        Func<Guid, PinnedProviderConfiguration, CancellationToken, Task> persistPin =
            (taskId, updated, ct) => PersistPinAsync(taskId, delegationId, updated, ct);
        // AF-03: an explicitly configured summarizer is its own governed broker (same retry/timeout/audit/accounting),
        // independent of the task fallback chain, and never falls back to another destination.
        IModelInvocationBroker? summarizer = null;
        if (options.Summarization is { Enabled: true, IsDedicated: true } summarization)
        {
            var summaryModel = models.CreateSummarizer(summarization.Provider!, summarization.Model!, pin.RequestTimeout);
            if (options.Context.Resolve(summaryModel.Descriptor) is not { MaxContextTokens: not null })
                throw new InvalidOperationException(
                    $"'Agent:Summarization' resolves to {summaryModel.Descriptor.ProviderId}/{summaryModel.Descriptor.ModelId}, " +
                    "which has no exact 'Agent:Context:Profiles' entry with a known MaxContextTokens.");
            summarizer = new ModelInvocationBroker(summaryModel, audit, time, logger, options);
        }
        var broker = new ModelInvocationBroker(
            executionModel, audit, time, logger, options, pin, persistPin,
            new MafContextProjector(options, logger), summarizer);
        return new AgentRunner(executionModel, tools, policy, approvals, audit, tasks, time,
            logger, options, skills, pinnedProviderConfiguration: pin,
            persistPinnedProviderConfiguration: persistPin,
            systemMessages: systemMessages,
            invocationBroker: broker);
    }

    internal DelegationRunner CreateDelegation(PinnedProviderConfiguration pin, Guid delegationId) =>
        new(CreateAgent(pin, delegationId), roles, planApprovals, audit, time, delegationLogger, delegations,
            pinnedProviderConfiguration: pin);

    private async Task PersistPinAsync(Guid taskId, Guid? delegationId, PinnedProviderConfiguration pin, CancellationToken ct)
    {
        if (await tasks.LoadAsync(taskId, ct) is { } task)
            await tasks.SaveAsync(task with { PinnedProviderConfiguration = pin }, CancellationToken.None);
        if (delegationId is { } id && await delegations.LoadAsync(id, ct) is { } run)
            await delegations.SaveAsync(run with { PinnedProviderConfiguration = pin }, CancellationToken.None);
    }

    private AgentRunnerOptions Options() =>
        configuration.GetSection("Agent").Get<AgentRunnerOptions>() ?? new AgentRunnerOptions();
}

/// <summary>The shared runner is used only for policy, resume acquisition, and containment.</summary>
internal sealed class NoExecutionChatModel : IChatModel
{
    public ChatModelDescriptor Descriptor { get; } = new("Unconfigured", "Unconfigured");

    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default) =>
        throw new InvalidOperationException("An execution-scoped runner is required for model calls.");
}
