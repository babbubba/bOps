// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// A projector that sends the request unprojected and records what the runner asked for, so the runtime-side recovery
/// contract (one aggressive request after a provider-reported overflow) is testable without the MAF integration assembly.
/// </summary>
internal sealed class RecordingContextProjector : IModelContextProjector
{
    /// <summary>Every request the runner handed to the context layer, in order.</summary>
    public List<ModelContextRequest> Requests { get; } = [];

    public Task<ModelContextProjectionResult> ProjectAsync(
        ModelContextRequest request, ChatModelDescriptor descriptor, ModelSummarizationAccess? summarization = null,
        CancellationToken ct = default)
    {
        Requests.Add(request);
        return Task.FromResult(new ModelContextProjectionResult(
            request.ToUnprojectedRequest(),
            new ModelContextProjectionStats("recording", null, null, null, 0, 0, 0, 0, 0, 0)));
    }
}
