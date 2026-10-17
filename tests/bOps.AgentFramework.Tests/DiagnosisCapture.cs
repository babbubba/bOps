// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
using System.Text.Json;
using bOps.Abstractions;
using bOps.Runtime;

namespace bOps.AgentFramework.Tests;

/// <summary>How much of an agent answer the harness can vouch for. An absent answer is never invented.</summary>
internal enum DiagnosisAvailability
{
    /// <summary>The agent produced no usable answer text.</summary>
    None,

    /// <summary>Text the agent actually produced, but the runtime did not accept it as the answer of a completed task.</summary>
    Partial,

    /// <summary>The persisted final-response step of a task the runtime completed.</summary>
    Complete,
}

/// <summary>Where the captured text came from. Closed set: it is exported in <c>result.json</c>.</summary>
internal enum DiagnosisSource
{
    None,

    /// <summary>The runtime's own persisted final-response step (<see cref="FinalResponse.IsFinalStep"/>).</summary>
    PersistedFinalStep,

    /// <summary>A tool-less model reply seen at the model boundary that the runtime never persisted as a final step.</summary>
    ObservedModelReply,
}

/// <summary>Why a diagnosis is not <see cref="DiagnosisAvailability.Complete"/>. Closed set: it is exported in <c>result.json</c>.</summary>
internal enum DiagnosisReason
{
    None,

    /// <summary>A final step was persisted, but the task did not end <c>Completed</c>.</summary>
    TaskNotCompleted,

    /// <summary>The provider stopped at its output limit; only the partial text of the raw reply body was kept.</summary>
    TruncatedAtOutputLimit,

    /// <summary>The reply was produced but the runtime never persisted it as an answer (rejected, failed later, or the task stopped).</summary>
    NotPersisted,
}

/// <summary>The text of one agent answer plus how it was obtained. The text is sensitive: it never reaches logs, errors or telemetry.</summary>
internal sealed record CapturedDiagnosis(string Text, DiagnosisAvailability Availability, DiagnosisSource Source, DiagnosisReason Reason)
{
    public override string ToString() => $"CapturedDiagnosis({Availability}, {Source}, {Reason}, {Text.Length} chars)";
}

/// <summary>
/// D7: captures the answer the agent actually produced, from mechanisms the runtime already offers, without changing a prompt, the
/// runner or any provider. Two sources, in this order of authority:
/// <list type="number">
/// <item>the persisted final-response step of the task state (<see cref="FinalResponse.IsFinalStep"/>), also when the task did not end
/// <c>Completed</c> (timeout, cancellation or a later failure after the answer was persisted);</item>
/// <item>the last tool-less step reply seen at the model boundary, for tasks that fail before a final step is persisted. A reply cut by the
/// provider's output limit contributes the partial <c>content</c> of the raw reply body the adapter keeps in its call details.</item>
/// </list>
/// A tool or step observation is never a diagnosis. With neither source the result is <c>null</c>, not an empty text.
/// </summary>
internal sealed class DiagnosisCapture
{
    private readonly object gate = new();
    private CapturedDiagnosis? observed;

    /// <summary>Feeds one model exchange. Called by the benchmark model wrapper; never throws and keeps nothing but the latest candidate.</summary>
    internal void Observe(ModelRequest request, ModelResponse response)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);
        // Planning and replanning replies are plan JSON, not an answer.
        if (BenchmarkKnowledgeDiscovery.IsPlanningRequest(request)) return;
        if (response.ToolCalls.Count > 0) return;
        CapturedDiagnosis? candidate = null;
        if (string.Equals(response.Details?.FinishReason, "length", StringComparison.OrdinalIgnoreCase))
        {
            // The adapter withholds truncated text from TextResponse and keeps it only in the raw body.
            if (PartialContent(response.Details?.ResponseJson) is { } partial)
                candidate = new(partial, DiagnosisAvailability.Partial, DiagnosisSource.ObservedModelReply, DiagnosisReason.TruncatedAtOutputLimit);
        }
        else if (IsAnswerShaped(response))
        {
            candidate = new(response.TextResponse!, DiagnosisAvailability.Partial, DiagnosisSource.ObservedModelReply, DiagnosisReason.NotPersisted);
        }

        if (candidate is null) return;
        lock (gate) observed = candidate;
    }

    /// <summary>
    /// The diagnosis of one task. <paramref name="state"/> is the returned or store-recovered task state, <c>null</c> when none was observed.
    /// </summary>
    internal CapturedDiagnosis? Resolve(TaskState? state)
    {
        var persisted = state?.Steps.LastOrDefault(step => FinalResponse.IsFinalStep(step) && !string.IsNullOrWhiteSpace(step.Observation));
        if (persisted is not null)
        {
            var completed = state!.Status == AgentTaskStatus.Completed;
            return new(persisted.Observation!, completed ? DiagnosisAvailability.Complete : DiagnosisAvailability.Partial,
                DiagnosisSource.PersistedFinalStep, completed ? DiagnosisReason.None : DiagnosisReason.TaskNotCompleted);
        }

        lock (gate) return observed;
    }

    // The runtime's own notion of "an original final answer" (AgentRunner.IsOriginalFinalAnswer), minus the guard's check replies.
    private static bool IsAnswerShaped(ModelResponse response) =>
        !string.IsNullOrWhiteSpace(response.TextResponse)
        && !TerminalProtocolArtifact.IsArtifact(response.TextResponse)
        && !EvidenceGroundingLedger.IsCheckReplyShape(response.TextResponse)
        && EvidenceRead.Recognize(response).Kind == RuntimeDirectiveRecognitionKind.None;

    // choices[0].message.content of an OpenAI-compatible body; reasoning content is deliberately not part of the answer.
    private static string? PartialContent(string? responseJson)
    {
        if (string.IsNullOrWhiteSpace(responseJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(responseJson);
            if (document.RootElement.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(content.GetString()))
                return content.GetString();
        }
        catch (JsonException) { }
        return null;
    }
}
