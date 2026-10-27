// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.AgentFramework.Troubleshooting;

/// <summary>
/// Thrown when a MAF troubleshooting run ends in an error that is not a limit and not a cancellation. The run is terminal and has no
/// resume: nothing it executed is repeated by retrying, because a retry is a new run with a new task id. The audit chain holds the
/// model-call and tool-call events of the run up to the failure.
/// </summary>
public sealed class MafWorkflowFailedException : Exception
{
    /// <summary>Creates the exception for a run id and a bounded reason.</summary>
    public MafWorkflowFailedException(Guid runId, string message, Exception? innerException)
        : base(message, innerException)
    {
        RunId = runId;
    }

    /// <summary>Creates an exception with no run id.</summary>
    public MafWorkflowFailedException()
    {
    }

    /// <summary>Creates an exception with a message and no run id.</summary>
    public MafWorkflowFailedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with a message and an inner exception and no run id.</summary>
    public MafWorkflowFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The workflow run that failed; the task id its audit events carry.</summary>
    public Guid RunId { get; }
}
