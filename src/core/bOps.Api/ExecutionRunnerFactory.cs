// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
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
    IChatModel? testModel = null)
{
    internal AgentRunner CreateAgent(PinnedProviderConfiguration pin, Guid? delegationId = null)
    {
        var options = Options();
        options.Validate(pin.RequestTimeout ?? ChatModelOptions.DefaultRequestTimeout);
        return new AgentRunner(testModel ?? models.Create(pin), tools, policy, approvals, audit, tasks, time,
            logger, options, skills, pinnedProviderConfiguration: pin,
            persistPinnedProviderConfiguration: (taskId, updated, ct) => PersistPinAsync(taskId, delegationId, updated, ct));
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
