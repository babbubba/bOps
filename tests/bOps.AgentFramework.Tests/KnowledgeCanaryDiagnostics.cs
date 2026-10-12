// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text.Json;
using bOps.Abstractions;
using bOps.Runtime;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace bOps.AgentFramework.Tests;

/// <summary>Only allowlisted metadata leaves the temporary host; never serialize runtime objects or exception messages.</summary>
internal sealed class KnowledgeCanaryDiagnostics : ILogger<AgentRunner>
{
    private readonly List<object> exceptions = [];
    private readonly List<object> replies = [];
    private readonly List<string> logSites = [];
    private readonly List<string> phases = [];
    private readonly List<int> httpStatuses = [];
    private string phase = "preflight";

    internal string Phase
    {
        get => phase;
        set { phase = value; phases.Add(value); }
    }
    internal bool RunReturned { get; set; }
    internal bool CheckingAssertions { get; set; }
    internal int HttpRequests { get; set; }
    internal void RecordHttpStatus(int status) => httpStatuses.Add(status);

    internal void CaptureException(Exception exception)
    {
        if (exception.Message.StartsWith("Live canary call cap", StringComparison.Ordinal)) logSites.Add("canary-call-cap");
        for (var current = exception; current is not null; current = current.InnerException)
        {
            exceptions.Add(new
            {
                Phase,
                Type = current.GetType().FullName,
                current.HResult,
                FailureKind = (current as ModelProtocolException)?.FailureKind.ToString(),
                HttpStatus = (current as ModelProtocolException)?.ProviderStatusCode ??
                    (current is HttpRequestException http ? (int?)http.StatusCode : null),
                SocketCode = (current as System.Net.Sockets.SocketException)?.SocketErrorCode.ToString(),
                Cause = SafeCause(current.Message),
                // Method identities locate the throw without exposing source paths or exception data.
                Methods = new StackTrace(current).GetFrames().Take(12)
                    .Select(frame => frame.GetMethod())
                    .Select(method => $"{method?.DeclaringType?.FullName}.{method?.Name}").ToArray(),
            });
        }
    }

    private static string? SafeCause(string message)
    {
        // These are fixed runtime validation phrases, not provider text or interpolated tool/property names.
        string[] causes = ["The plan reply was empty", "The plan reply did not contain a JSON object",
            "The plan reply was not a JSON object", "The plan reply was not valid plan JSON",
            "did not name a non-empty expectedTool", "expectedArguments was not a JSON object",
            "cannot constrain unavailable tool", "has invalid expectedArguments", "has an invalid activation",
            "needs non-empty expectedArguments", "no evidence exists before the initial plan",
            "Live canary call cap", "did not produce a valid plan after", "Final checklist missing",
            "Successful Knowledge read missing", "Unexpected tool audit", "Checklist leaked into system prompt",
            "Checklist served without attribution", "The model never received the checklist as attributed Knowledge data"];
        return causes.FirstOrDefault(cause => message.Contains(cause, StringComparison.Ordinal));
    }

    internal static string SafeTool(string name) => name switch
    {
        "test.read" or "test.other" or "runtime.knowledge_read" => name,
        _ => "<unrecognized-tool>",
    };

    internal void RecordResponse(ModelResponse response) => replies.Add(new
    {
        Phase,
        TextPresent = !string.IsNullOrWhiteSpace(response.TextResponse),
        response.IsFinal,
        Usage = response.Usage is { } usage ? new { usage.PromptTokens, usage.CompletionTokens } : null,
        Proposed = response.ToolCalls.Select(call => new
        {
            Tool = SafeTool(call.ToolName),
            InvalidName = call.ToolNameError is not null,
            InvalidArguments = call.ArgumentsError is not null,
        }).ToArray(),
    });

    // Diagnostics must survive a failed store read without masking the original canary failure.
#pragma warning disable CA1031
    internal async Task<string> SnapshotAsync(KnowledgeHostCanary? canary, CanaryRig? rig, TaskState? state, int calls)
    {
        var events = canary?.AuditEvents.ToArray() ?? [];
        var taskId = state?.Id ?? events.LastOrDefault(evt => evt.TaskId != Guid.Empty)?.TaskId;
        TaskState? persisted = null;
        try
        {
            if (rig is not null && taskId is { } id) persisted = await rig.Store.LoadAsync(id);
        }
        catch (Exception ex)
        {
            CaptureException(ex);
        }

        var terminal = state ?? persisted;
        var modelEvents = events.OfType<ModelCallAuditEvent>().ToArray();
        var failure = modelEvents.LastOrDefault(evt => evt.Outcome == ModelCallOutcome.Failure)?.FailureKind
            ?? terminal?.TerminalReason?.FailureKind;
        var admission = events.OfType<KnowledgePackAdmissionAuditEvent>().ToArray();
        var reads = events.OfType<KnowledgeReadAuditEvent>().ToArray();
        var category = reads.Any(evt => evt.ResultCode == KnowledgeReadResultCode.NotAuthorized) && terminal?.Status != AgentTaskStatus.Completed
            ? "knowledge-authorization"
            : terminal?.TerminalReason?.Kind == TaskTerminalKind.KnowledgeUnavailable
            ? admission.Any(evt => evt.Code == KnowledgeAdmissionCode.NotEntitled) ? "knowledge-authorization" : "knowledge-infrastructure"
            : logSites.Contains("canary-call-cap") ? "test-call-cap"
            : failure is ModelFailureKind.Unreachable or ModelFailureKind.Timeout or ModelFailureKind.Transient or ModelFailureKind.RateLimited
                ? "infrastructure"
                : failure == ModelFailureKind.Authentication ? "provider-authorization"
                : failure is not null ? "model-provider"
                : CheckingAssertions && terminal?.Status == AgentTaskStatus.Completed ? "test"
                : Phase == "preflight" ? "infrastructure-preflight"
                : modelEvents.Any(evt => evt.ContextGateRejection is not null) ? "broker-context-gate"
                : terminal?.Status == AgentTaskStatus.Completed ? "success" : "runtime-or-test (see exception methods)";

        return JsonSerializer.Serialize(new
        {
            Phase, RunReturned, CheckingAssertions, Category = category,
            ReachedPhases = phases.Distinct().ToArray(),
            ToolPhaseReached = events.OfType<ToolCallAuditEvent>().Any() || reads.Length > 0,
            FinalizationReached = terminal?.TerminalReason is not null,
            TaskId = taskId,
            Status = terminal?.Status.ToString(),
            TerminalCause = terminal?.TerminalReason?.Kind.ToString(),
            FailureKind = terminal?.TerminalReason?.FailureKind?.ToString(),
            StoreStatus = persisted?.Status.ToString(),
            Plans = terminal?.Plans.Count,
            Steps = terminal?.Steps.Count,
            ActualLlmCalls = calls,
            HttpRequests, HttpStatuses = httpStatuses.ToArray(),
            ComposedBrokerTurns = rig?.Maf.ComposedTurns,
            Executed = new { Read = rig?.Read.ExecutionCount ?? 0, Other = rig?.Other.ExecutionCount ?? 0 },
            ModelAudit = modelEvents.Select(evt => new
            {
                evt.StepIndex, Outcome = evt.Outcome.ToString(), FailureKind = evt.FailureKind?.ToString(),
                evt.ProviderStatusCode, Retry = evt.RetryDecision?.ToString(), evt.ModelAttempt, evt.DurationMs,
                ContextGateRefused = evt.ContextGateRejection is not null,
                Cause = evt.ErrorMessage is null ? null : SafeCause(evt.ErrorMessage),
                Usage = evt.Usage is { } usage ? new { usage.PromptTokens, usage.CompletionTokens } : null,
            }).ToArray(),
            ToolAudit = events.OfType<ToolCallAuditEvent>().Select(evt => new
            {
                Tool = SafeTool(evt.Tool), Outcome = evt.Outcome.ToString(), Failure = evt.FailureKind?.ToString(),
                Authorization = evt.Authorization.ToString(), evt.StepIndex,
            }).ToArray(),
            KnowledgeAdmission = admission.Select(evt => evt.Code.ToString()).ToArray(),
            KnowledgeReads = reads.Select(evt => new { Result = evt.ResultCode.ToString(), Cause = evt.CauseCode?.ToString() }).ToArray(),
            TokenAccounting = terminal?.Accounting?.TokensUsed,
            Replies = replies.ToArray(), Exceptions = exceptions.ToArray(), LogSites = logSites.ToArray(),
        });
    }
#pragma warning restore CA1031

    internal async Task WriteAsync(ITestOutputHelper output, KnowledgeHostCanary? canary, CanaryRig? rig, TaskState? state, int calls) =>
        output.WriteLine("HOST-CANARY-DIAG " + await SnapshotAsync(canary, rig, state, calls));

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        // Do not invoke the formatter: its values can contain provider text, observations and goals.
        if (state is IEnumerable<KeyValuePair<string, object?>> fields &&
            fields.FirstOrDefault(field => field.Key == "{OriginalFormat}").Value is string template)
        {
            if (template.Contains("planning failed", StringComparison.Ordinal)) Phase = "planning";
            if (template.Contains("model did not produce a valid plan", StringComparison.Ordinal)) logSites.Add("invalid-plan-after-corrections");
            if (template.Contains("planning failed", StringComparison.Ordinal)) logSites.Add("planning-failed");
        }
        if (exception is not null) CaptureException(exception);
    }
}
