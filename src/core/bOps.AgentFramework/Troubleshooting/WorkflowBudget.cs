// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.AgentFramework.Troubleshooting;

/// <summary>
/// The model-call and token budget shared by every agent of one workflow run. Tool calls are budgeted by the runtime's
/// <c>WorkflowToolSession</c>, which audits a refusal; the time limit is a cancellation the host applies to the whole run.
/// </summary>
internal sealed class WorkflowBudget(MafWorkflowLimits limits)
{
    private long tokens;
    private int modelCalls;

    /// <summary>The known tokens the run has used: prompt plus completion of every model attempt that reported usage.</summary>
    internal long Tokens => Interlocked.Read(ref tokens);

    /// <summary>The model calls the run has started.</summary>
    internal int ModelCalls => Volatile.Read(ref modelCalls);

    /// <summary>Reserves one model call, or refuses it before it is made.</summary>
    /// <exception cref="WorkflowLimitException">The model-call or token budget is already used up.</exception>
    internal void BeginModelCall()
    {
        if (Tokens > limits.MaxTokens)
        {
            throw new WorkflowLimitException("token_budget");
        }

        if (Interlocked.Increment(ref modelCalls) > limits.MaxModelCalls)
        {
            throw new WorkflowLimitException("model_call_budget");
        }
    }

    /// <summary>Accounts the usage a successful model call reported.</summary>
    internal void ChargeTokens(int used) => Interlocked.Add(ref tokens, used);

    /// <summary>Whether the token budget is already exceeded (strictly more than the limit, as everywhere else).</summary>
    internal bool TokensExceeded => Tokens > limits.MaxTokens;
}
