// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
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
        return new AttemptModel(pin, registry, credentials);
    }

    private sealed class AttemptModel(PinnedProviderConfiguration pin, IChatModelRegistry registry,
        ProviderCredentialResolver credentials) : IChatModel
    {
        public ChatModelDescriptor Descriptor { get; } = new(pin.ProviderId, pin.Model);

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            var key = credentials.Resolve(pin.ProviderId);
            if (ProviderCredentialResolver.RequiresCredential(pin.ProviderId) && string.IsNullOrEmpty(key))
                throw new ModelProtocolException($"No current credential is available for provider '{pin.ProviderId}'.")
                    { FailureKind = ModelFailureKind.Authentication };
            // The adapter exists for exactly this invocation. It cannot retain a historical key
            // across retries, continuation, approval waits, or process restart.
            var options = new ChatModelOptions(pin.ProviderId, pin.BaseUrl, null, pin.Model,
                pin.SupportsNativeToolCalling) { RequestTimeout = pin.RequestTimeout, ResolvedApiKey = key };
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
    internal AgentRunner CreateAgent(PinnedProviderConfiguration pin)
    {
        var options = Options();
        options.Validate(pin.RequestTimeout ?? ChatModelOptions.DefaultRequestTimeout);
        return new AgentRunner(testModel ?? models.Create(pin), tools, policy, approvals, audit, tasks, time,
            logger, options, skills, pinnedProviderConfiguration: pin);
    }

    internal DelegationRunner CreateDelegation(PinnedProviderConfiguration pin) =>
        new(CreateAgent(pin), roles, planApprovals, audit, time, delegationLogger, delegations,
            pinnedProviderConfiguration: pin);

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
