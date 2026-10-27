// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.AgentFramework.Troubleshooting;

/// <summary>
/// Thrown by the workflow's own accounting when a run-wide limit (model calls, tokens) is reached before a call, so the call is never
/// made. The executor that catches it ends its work and the run finishes as limit-reached, with the evidence collected so far.
/// </summary>
public sealed class WorkflowLimitException : Exception
{
    /// <summary>Creates the exception for a closed limit code (for example <c>token_budget</c>).</summary>
    /// <param name="code">The closed machine code of the limit.</param>
    public WorkflowLimitException(string code)
        : base($"Workflow limit reached: {code}.")
    {
        Code = code;
    }

    /// <summary>Creates an exception with no code.</summary>
    public WorkflowLimitException()
        : this("limit")
    {
    }

    /// <summary>Creates an exception with a message and no code.</summary>
    public WorkflowLimitException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = "limit";
    }

    /// <summary>The closed machine code of the limit that was reached.</summary>
    public string Code { get; }
}
