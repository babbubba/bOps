// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
// The export boundary catches arbitrary failures, records only type names, and rethrows the original after publishing.
#pragma warning disable CA1031
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Runtime;

namespace bOps.AgentFramework.Tests;

internal sealed class BenchmarkModelObservation
{
    internal int? Calls { get; set; }
    internal bool CallLimitReached { get; set; }
    internal int CompletedCalls { get; set; }
    internal int FailedCalls { get; set; }
    internal BenchmarkHttpBudget? Http { get; set; }
    internal bool ConditionTimedOut { get; set; }
    internal AgentRunnerOptions RunnerOptions { get; set; } = KnowledgeBenchmark.RunnerOptions;
}

/// <summary>Test entry-point only. A complete directory is published by a single no-overwrite rename.</summary>
internal sealed class KnowledgeBenchmarkExport
{
    internal Guid RunId { get; } = Guid.NewGuid();
    internal string? Commit { get; set; }
    internal string? RequestedCommit { get; set; }
    internal string? CorpusSha256 { get; set; }
    internal JsonObject CorpusProvenance { get; set; } = new()
        { ["classification"] = "Unverified", ["guarantee"] = "corpus-not-validated" };
    internal JsonObject? Configuration { get; set; }
    internal TimeSpan? ProviderRequestTimeout { get; set; }
    internal KnowledgeRunnerWorld? World { get; set; }
    internal ITaskStore? Store { get; set; }
    internal TaskState? State { get; private set; }
    internal BenchmarkModelObservation ModelObservation { get; } = new();
    private long? brokerLoopMs;
    private string stateSource = "NotObserved";

    internal async Task ExecuteTaskAsync(Func<Task<TaskState>> execute)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            State = await execute();
            stateSource = "Returned";
        }
        finally
        {
            timer.Stop();
            brokerLoopMs = timer.ElapsedMilliseconds;
        }
    }

    internal static async Task RunAsync(string root, string condition, Func<KnowledgeBenchmarkExport, Task> execute)
    {
        if (condition is not ("A" or "B")) throw new InvalidOperationException("Explicit A/B condition required.");
        var export = new KnowledgeBenchmarkExport();
        string destination;
        string staging;
        try
        {
            destination = Path.Combine(Path.GetFullPath(root), condition);
            staging = Path.Combine(Path.GetFullPath(root), $".{condition}-{export.RunId}.partial");
            if (!Directory.Exists(root)) throw new IOException("Existing output root required.");
            if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Condition output already exists.");
            Directory.CreateDirectory(staging);
        }
        catch (Exception)
        {
            throw new IOException("Benchmark output directory unavailable or already occupied; no artifacts saved.");
        }

        Exception? failure = null;
        string? recoveryError = null;
        var timer = Stopwatch.StartNew();
        var started = DateTimeOffset.UtcNow;
        try
        {
            try { await execute(export); }
            catch (Exception ex) { failure = ex; }
            if (export.State is null && export.Store is not null)
            {
                try
                {
                    export.State = await export.Store.LoadAsync(export.RunId, CancellationToken.None);
                    if (export.State is not null) export.stateSource = "Persisted";
                }
                catch (Exception ex) { recoveryError = ex.GetType().Name; }
            }
            timer.Stop();
            try
            {
                await export.WriteAsync(staging, condition, failure, recoveryError, started, DateTimeOffset.UtcNow, timer.ElapsedMilliseconds);
                Directory.Move(staging, destination); // Never replaces an existing condition, including concurrent publishers.
            }
            catch (Exception)
            {
                throw new IOException("Benchmark export could not be published; no complete artifacts saved (staging may remain).");
            }
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
        finally { export.World?.Dispose(); }
    }

    // Metadata can be supplied by an operator: retain exact configuration fingerprints, never arbitrary payloads.
    internal static JsonObject SafeConfiguration(JsonElement root)
    {
        var safe = new JsonObject();
        foreach (var name in new[] { "baseline", "llamaCppVersion", "model", "modelFileSha256", "quantization", "ctxSize",
                     "nPredict", "reasoning", "sampling", "bopsCommit", "corpusSha256", "providerProfileSha256", "runtimeLimits", "retryPolicy" })
            if (root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null)
                safe[name + "Sha256"] = bOps.Knowledge.KnowledgePackFormat.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(value.GetRawText()));
        foreach (var name in new[] { "ctxSize", "nPredict", "reasoning" })
            if (root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                safe[name] = JsonNode.Parse(value.GetRawText());
        foreach (var name in new[] { "sampling", "runtimeLimits", "retryPolicy" })
            if (root.TryGetProperty(name, out var settings) && settings.ValueKind == JsonValueKind.Object)
            {
                var numeric = new JsonObject();
                foreach (var property in settings.EnumerateObject())
                    if (property.Name is "temperature" or "topP" or "topK" or "minP" or "seed" or "repeatPenalty"
                        or "maxSteps" or "maxLifetimeSteps" or "maxTotalTokens" or "maxReplans" or "maxLifetimeReplans"
                        or "requestTimeoutMs" or "attemptTimeoutMs" or "totalTimeoutMs" or "maxAttempts" or "maxRetries"
                        or "baseDelayMs" or "maxDelayMs" or "maxAttemptDurationMs")
                        if (property.Value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null)
                            numeric[property.Name] = JsonNode.Parse(property.Value.GetRawText());
                safe[name] = numeric;
            }
        return safe;
    }

    private async Task WriteAsync(string staging, string condition, Exception? failure, string? recoveryError,
        DateTimeOffset started, DateTimeOffset ended, long elapsedMs)
    {
        var events = World?.Events.ToArray();
        var models = events?.OfType<ModelCallAuditEvent>().ToArray();
        var tools = events?.OfType<ToolCallAuditEvent>().ToArray();
        var reads = events?.OfType<KnowledgeReadAuditEvent>().ToArray();
        var audit = new List<string>();
        var auditStatus = World is null ? "NotObserved" : "Complete";
        string? auditError = null;
        if (World is not null)
        {
            try
            {
                await foreach (var line in File.ReadLinesAsync(World.AuditFile, CancellationToken.None))
                {
                    try
                    {
                        using var envelope = JsonDocument.Parse(line);
                        using var evt = JsonDocument.Parse(envelope.RootElement.GetProperty("EventJson").GetString()!);
                        audit.Add(SafeAudit(evt.RootElement).ToJsonString());
                    }
                    catch (JsonException) { auditStatus = "Partial"; auditError = "InvalidAuditJson"; break; }
                }
            }
            catch (Exception ex) { auditStatus = "Partial"; auditError = ex.GetType().Name; }
        }
        var lifecycle = events?.OfType<TaskLifecycleAuditEvent>().LastOrDefault();
        var terminal = State?.TerminalReason;
        var kind = (failure as ModelProtocolException)?.FailureKind ?? terminal?.FailureKind ?? (failure is not null ? models?.LastOrDefault(m => m.Outcome == ModelCallOutcome.Failure)?.FailureKind : null);
        var cause = ModelObservation.Http?.Refused > 0 ? "HttpBudgetExhausted"
            : ModelObservation.CallLimitReached ? "LlmCallLimit"
            : ModelObservation.ConditionTimedOut || failure is TimeoutException or OperationCanceledException { InnerException: TimeoutException } || kind == ModelFailureKind.Timeout || terminal?.Kind == TaskTerminalKind.AttemptDurationBudget ? "Timeout"
            : failure is OperationCanceledException || terminal?.Kind == TaskTerminalKind.Cancelled ? "Cancelled"
            : failure is ModelProtocolException or HttpRequestException ? "ProviderOrProtocolError"
            : failure is not null ? (State is null && recoveryError is null ? "InfrastructureBeforeObservedTask" : "InfrastructureError")
            : terminal?.Kind == TaskTerminalKind.ModelFailure ? "ProviderOrProtocolError"
            : State?.Status.ToString() ?? "TaskNotObserved";
        var httpAttempts = ModelObservation.Http?.Attempts;
        if (httpAttempts is not null)
        {
            audit.AddRange(httpAttempts.Select(attempt => JsonSerializer.Serialize(new
            {
                schemaVersion = 2, type = "BenchmarkHttpAttempt", TaskId = RunId,
                attempt.TimestampUtc, attempt.HttpAttempt, attempt.Outcome, attempt.ProviderStatusCode
            })));
            // Persisted event order is stable for equal timestamps; HTTP records join the payload-free projection.
            audit = audit.OrderBy(line => JsonNode.Parse(line)!["TimestampUtc"]!.GetValue<DateTimeOffset>()).ToList();
        }
        var result = new
        {
            schemaVersion = 2, provenanceScope = "process-local observation; exported metadata cannot attest imported files", RunId, condition, baseline = "Qwen8192", Commit, RequestedCommit, Configuration, CorpusSha256, CorpusProvenance, ProviderRequestTimeout,
            startedAtUtc = started, endedAtUtc = ended, totalExecutionMs = elapsedMs,
            totalBrokerLoopMs = brokerLoopMs, modelLoadMs = (long?)null, generationMs = (long?)null,
            timingsRequireServerTelemetry = true, taskId = State?.Id, status = State?.Status.ToString(),
            stateSource, lastAuditedStatus = lifecycle?.Status.ToString(), lastAuditedTerminalKind = lifecycle?.TerminalKind?.ToString(),
            lastAuditedAccounting = lifecycle is null ? null : new { lifecycle.TokensUsed, lifecycle.LifetimeSteps, lifecycle.LifetimeReplans },
            configurationRepresentation = "Allowlisted numeric/boolean settings and fingerprints; arbitrary text/payloads withheld",
            terminalCause = cause, terminalKind = terminal?.Kind.ToString(), failureKind = kind?.ToString(),
            exceptionType = failure?.GetType().Name, stateRecoveryError = recoveryError,
            // A failed step/tool observation is never a final answer.
            finalObservation = State is { Status: AgentTaskStatus.Completed, Steps.Count: > 0 } ? State.Steps[^1].Observation : null,
            accounting = State?.Accounting, llmCalls = ModelObservation.Calls,
            completeAsyncCalls = ModelObservation.Calls,
            completedCompleteAsyncCalls = ModelObservation.Calls is null ? (int?)null : ModelObservation.CompletedCalls,
            failedCompleteAsyncCalls = ModelObservation.Calls is null ? (int?)null : ModelObservation.FailedCalls,
            logicalModelCalls = models?.Count(m => m.ModelAttempt == 1),
            httpRequestsSent = ModelObservation.Http?.Sent, httpRequestsRefused = ModelObservation.Http?.Refused,
            httpRequestLimit = ModelObservation.Http is null ? (int?)null : KnowledgeBenchmark.MaximumCalls,
            httpBudgetExhausted = ModelObservation.Http is null ? (bool?)null : ModelObservation.Http.Refused > 0,
            httpAttempts,
            effectiveBenchmarkTimeouts = ModelObservation.Http is null ? null : new
            {
                attempt = ModelObservation.RunnerOptions.ModelCallAttemptTimeout,
                logicalCall = ModelObservation.RunnerOptions.ModelCallBudget,
                condition = ModelObservation.RunnerOptions.MaxAttemptDuration
            },
            usageScope = "Only usage returned by the adapter and observed in model audit; not total HTTP usage (corrective JSON replies may be omitted by the adapter)",
            llmCallLimitReached = ModelObservation.Calls is null ? (bool?)null : ModelObservation.CallLimitReached,
            metricSource = events is null ? "NotObserved" : "RecordingSink",
            reportedPromptTokens = models?.Any(m => m.Usage is not null) == true ? models.Sum(m => (long?)m.Usage?.PromptTokens) : null,
            reportedCompletionTokens = models?.Any(m => m.Usage is not null) == true ? models.Sum(m => (long?)m.Usage?.CompletionTokens) : null,
            usageReportingAttempts = models?.Count(m => m.Usage is not null),
            modelAttempts = models?.Select(m => new { m.DurationMs, m.ModelAttempt, m.ProviderAttempt, outcome = m.Outcome.ToString(),
                failureKind = m.FailureKind?.ToString(), retryDecision = m.RetryDecision?.ToString(), m.RetryDelayMs, m.ProviderStatusCode, m.Usage }),
            // These counts describe audited calls. EffectiveTimeout proves dispatch; absence cannot prove execution.
            auditedToolAttempts = tools?.Length,
            auditedToolsExecuted = tools?.Count(t => t.EffectiveTimeout is not null),
            auditedToolsRefused = tools?.Count(t => t.Outcome == ToolOutcome.Denied),
            auditedToolsExecutionUnknown = tools?.Count(t => t.EffectiveTimeout is null && t.Outcome != ToolOutcome.Denied),
            tools = tools?.Select(t => new { t.Tool, outcome = t.Outcome.ToString(), t.Duration, t.EffectiveTimeout, failureKind = t.FailureKind?.ToString() }),
            knowledgeReads = reads?.Select(r => new { resultCode = r.ResultCode.ToString() }),
            knowledgeUsed = reads is null ? (bool?)null : reads.Any(r => r.ResultCode == KnowledgeReadResultCode.Success),
            auditStatus, auditError, auditEventCount = audit.Count,
            auditRepresentation = httpAttempts is null ? "Payload-free projection of persisted events in original order"
                : "Payload-free persisted model/tool events plus process-local HTTP observations, ordered by timestamp"
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result, KnowledgeBenchmark.JsonOptions);
        using var validated = JsonDocument.Parse(bytes);
        await WriteNewAsync(Path.Combine(staging, "result.json"), bytes);
        await WriteNewAsync(Path.Combine(staging, "audit.jsonl"), System.Text.Encoding.UTF8.GetBytes(string.Join("\n", audit) + (audit.Count > 0 ? "\n" : "")));
    }

    private static JsonObject SafeAudit(JsonElement evt)
    {
        var safe = new JsonObject();
        foreach (var property in evt.EnumerateObject())
            if (property.Name is "$type" or "TimestampUtc" or "TaskId" or "StepIndex" or "Stage" or "Status" or "TerminalKind"
                or "Outcome" or "FailureKind" or "ResultCode" or "Code" or "Tool" or "Duration" or "DurationMs"
                or "EffectiveTimeout" or "ModelAttempt" or "ProviderAttempt" or "RetryDecision" or "RetryDelayMs"
                or "ProviderStatusCode" or "ExecutionAttempt" or "TokensUsed" or "LifetimeSteps" or "LifetimeReplans")
                safe[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        if (evt.TryGetProperty("Usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            var safeUsage = new JsonObject();
            foreach (var name in new[] { "PromptTokens", "CompletionTokens", "EstimatedCostUsd" })
                if (usage.TryGetProperty(name, out var value)) safeUsage[name] = JsonNode.Parse(value.GetRawText());
            safe["Usage"] = safeUsage;
        }
        return safe;
    }

    private static async Task WriteNewAsync(string path, byte[] bytes)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await file.WriteAsync(bytes, CancellationToken.None);
#pragma warning disable CA1849 // FlushAsync cannot request durable flush-to-disk before directory publication.
        file.Flush(flushToDisk: true);
#pragma warning restore CA1849
    }
}
