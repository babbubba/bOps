// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using Microsoft.Extensions.Logging;

namespace bOps.Runtime;

/// <summary>
/// The task-owned information required to govern one logical model call. It contains no provider
/// credential and gives the broker the destinations for attempt records, audit correlation and
/// cumulative token accounting.
/// </summary>
public sealed class ModelInvocationContext
{
    /// <summary>Creates the context for one logical model call.</summary>
    public ModelInvocationContext(
        Guid taskId,
        int stepIndex,
        ActorIdentity actor,
        IList<ModelCallRecord> calls)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(calls);
        TaskId = taskId;
        StepIndex = stepIndex;
        Actor = actor;
        Calls = calls;
    }

    /// <summary>The task whose model call is being made.</summary>
    public Guid TaskId { get; }

    /// <summary>The task step, or the existing negative planning sentinel.</summary>
    public int StepIndex { get; }

    /// <summary>The actor whose task caused the inference.</summary>
    public ActorIdentity Actor { get; }

    /// <summary>The append-only destination for one record per provider attempt.</summary>
    public IList<ModelCallRecord> Calls { get; }

    /// <summary>Delegation correlation stamped on attempt audit events, when applicable.</summary>
    public DelegationCorrelation? Delegation { get; init; }

    /// <summary>Workflow-run correlation stamped on attempt audit events (C01-R23), <c>null</c> for a call that is not part of a workflow run.</summary>
    public WorkflowCorrelation? Workflow { get; init; }

    /// <summary>Accounts successful response usage against the task or role's cumulative token budget.</summary>
    public Action<int>? AccountTokens { get; init; }

    /// <summary>
    /// Validates a successful transport response that has a caller-required shape. A non-null
    /// result records the attempt as malformed without transport retry and returns the response to
    /// the caller for its existing bounded correction behavior.
    /// </summary>
    public Func<ModelResponse, string?>? MalformedResponse { get; init; }

    /// <summary>The reference knowledge the request carries, when the task has knowledge; audited with the call, never content.</summary>
    public ModelKnowledgeContext? KnowledgeContext { get; init; }

    internal CancellationToken ExecutionBudgetToken { get; init; }

    internal Func<bool>? ExecutionBudgetExpired { get; init; }

    /// <summary>
    /// The deadline of the enclosing logical model call when this context serves a nested inference (a summary), so the
    /// nested broker cannot outlive its parent. <c>null</c> for a top-level call.
    /// </summary>
    internal ModelCallDeadline? LogicalCallDeadline { get; init; }

    /// <summary>
    /// The same task, records, accounting and execution budgets, without a caller-specific response validator, bound to the
    /// logical-call deadline of the call that requested the summary.
    /// </summary>
    internal ModelInvocationContext ForSummarization(ModelCallDeadline parentDeadline) => new(TaskId, StepIndex, Actor, Calls)
    {
        Delegation = Delegation,
        Workflow = Workflow,
        AccountTokens = AccountTokens,
        ExecutionBudgetToken = ExecutionBudgetToken,
        ExecutionBudgetExpired = ExecutionBudgetExpired,
        LogicalCallDeadline = parentDeadline,
    };
}

/// <summary>
/// The single bOps-owned boundary for model inference. Implementations own retry, timeout,
/// fallback, audit, attempt recording and token accounting; callers own only request construction
/// and interpretation of the governed response.
/// </summary>
public interface IModelInvocationBroker
{
    /// <summary>The currently selected provider/model descriptor, for metadata only.</summary>
    ChatModelDescriptor Descriptor { get; }

    /// <summary>Executes one governed logical model call.</summary>
    Task<ModelResponse> InvokeAsync(
        ModelRequest request,
        ModelInvocationContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Executes a candidate-specific context request. Implementations without a projector send it unprojected
    /// (<see cref="ModelContextRequest.ToUnprojectedRequest"/>).
    /// </summary>
    Task<ModelResponse> InvokeAsync(
        ModelContextRequest request,
        ModelInvocationContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        return InvokeAsync(request.ToUnprojectedRequest(), context, ct);
    }
}

/// <summary>Runtime implementation of the governed model-invocation boundary (ADR-0039, ADR-0045, ADR-0052).</summary>
public sealed class ModelInvocationBroker : IModelInvocationBroker
{
    private static readonly TimeSpan MinimumModelAttemptWindow = TimeSpan.FromSeconds(1);

    private readonly IChatModel model;
    private readonly IAuditSink audit;
    private readonly TimeProvider timeProvider;
    private readonly ILogger logger;
    private readonly AgentRunnerOptions options;
    private readonly PinnedProviderConfiguration? pinnedProviderConfiguration;
    private readonly Func<Guid, PinnedProviderConfiguration, CancellationToken, Task>? persistPinnedProviderConfiguration;
    private readonly IModelContextProjector? contextProjector;
    private readonly IModelInvocationBroker? dedicatedSummarizer;
    private IModelInvocationBroker? currentCandidateSummarizer;

    /// <summary>Creates one execution-scoped governed broker over the execution-scoped bOps model.</summary>
    public ModelInvocationBroker(
        IChatModel model,
        IAuditSink audit,
        TimeProvider timeProvider,
        ILogger logger,
        AgentRunnerOptions options,
        PinnedProviderConfiguration? pinnedProviderConfiguration = null,
        Func<Guid, PinnedProviderConfiguration, CancellationToken, Task>? persistPinnedProviderConfiguration = null,
        IModelContextProjector? contextProjector = null,
        IModelInvocationBroker? dedicatedSummarizer = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        this.model = model;
        this.audit = audit;
        this.timeProvider = timeProvider;
        this.logger = logger;
        this.options = options;
        this.pinnedProviderConfiguration = pinnedProviderConfiguration;
        this.persistPinnedProviderConfiguration = persistPinnedProviderConfiguration;
        this.contextProjector = contextProjector;
        this.dedicatedSummarizer = dedicatedSummarizer;
    }

    /// <inheritdoc />
    public ChatModelDescriptor Descriptor => model.Descriptor;

    /// <summary>Test seam for the wait between same-candidate attempts.</summary>
    internal Func<TimeSpan, CancellationToken, Task>? RetryDelay { get; set; }

    /// <summary>Test seam for the equal-jitter source, whose value is constrained to [0, 1).</summary>
    internal Func<double>? RetryJitter { get; set; }

    /// <inheritdoc />
    public async Task<ModelResponse> InvokeAsync(
        ModelRequest request,
        ModelInvocationContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        return await InvokeCoreAsync(request, context, projection: null, ct);
    }

    /// <inheritdoc />
    public async Task<ModelResponse> InvokeAsync(
        ModelContextRequest request,
        ModelInvocationContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (contextProjector is null)
        {
            return await InvokeCoreAsync(request.ToUnprojectedRequest(), context, projection: null, ct);
        }

        return await InvokeCoreAsync(
            request.ToUnprojectedRequest(),
            context,
            (descriptor, summarization, token) => contextProjector.ProjectAsync(request, descriptor, summarization, token),
            ct);
    }

    private async Task<ModelResponse> InvokeCoreAsync(
        ModelRequest request,
        ModelInvocationContext context,
        Func<ChatModelDescriptor, ModelSummarizationAccess?, CancellationToken, Task<ModelContextProjectionResult>>? projection,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        // One deadline for the whole logical call (and, when this is a nested summary call, no later than its parent's).
        var deadline = new ModelCallDeadline(timeProvider, options.ModelCallBudget, context.LogicalCallDeadline);
        var summaryLedger = new ModelSummarizationLedger();
        var modelAttempt = 0;
        var providerAttempt = 0;
        while (true)
        {
            modelAttempt++;
            providerAttempt++;
            if (context.ExecutionBudgetExpired?.Invoke() == true)
            {
                throw new AttemptDurationBudgetExceededException();
            }

            var startedAtUtc = timeProvider.GetUtcNow();
            var startedAt = timeProvider.GetTimestamp();
            var descriptor = model.Descriptor;
            ModelSummarizationAccess? summarizationAccess = null;
            var projected = projection is null
                ? null
                : await projection(descriptor, summarizationAccess = BuildSummarizationAccess(context, summaryLedger, deadline), ct);
            if (projected is not null && context.ExecutionBudgetExpired?.Invoke() == true)
            {
                // MAF treats a failed summary as soft; an exhausted execution budget must still end the attempt.
                throw new AttemptDurationBudgetExceededException();
            }

            // ModelCallBudget governs the whole logical call, projection (and any summary inference) included, so the
            // attempt window is derived only now, from what projection left over.
            var remaining = deadline.Remaining;
            var budgetExhaustedBeforeAttempt = remaining < MinimumModelAttemptWindow;
            var attemptTimeout = remaining < options.ModelCallAttemptTimeout ? remaining : options.ModelCallAttemptTimeout;
            var attemptRequest = projected?.Request ?? request;
            // Generation limit: a limit the caller put on the request wins; otherwise the configured one, if any. With neither, nothing
            // is requested and the provider's own default applies (unknown to bOps); the observation below records which case it was.
            if (attemptRequest.MaxOutputTokens is null && options.MaxOutputTokens is { } configuredLimit)
            {
                attemptRequest = attemptRequest with { MaxOutputTokens = configuredLimit };
            }

            RecordSummaryTelemetry(projected?.Stats, summarizationAccess);
            var fallbackOrdinal = (model as IFallbackChatModelControl)?.FallbackOrdinal
                ?? pinnedProviderConfiguration?.FallbackOrdinal ?? 0;
            using var contextActivity = BOpsTelemetry.ActivitySource.StartActivity("bops.model_context");
            TagProjection(contextActivity, descriptor, projected?.Stats);
            RecordProjectionMetrics(descriptor, projected?.Stats);
            AttemptFailure? failure = null;
            ModelResponse? response = null;
            var executionBudgetInterrupted = false;
            try
            {
                if (budgetExhaustedBeforeAttempt)
                {
                    // The provider is not invoked: the window left is below the minimum usable attempt window.
                    failure = new AttemptFailure(ModelFailureKind.Timeout,
                        "The model-call budget was exhausted before the model attempt could start.",
                        null, null, null);
                }
                else
                {
                    response = await AttemptModelCallAsync(attemptRequest, attemptTimeout, context.ExecutionBudgetToken, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (context.ExecutionBudgetExpired?.Invoke() == true)
            {
                executionBudgetInterrupted = true;
                failure = new AttemptFailure(ModelFailureKind.Timeout,
                    "The execution attempt exhausted its active-duration budget during the model call.", null, null, null);
            }
            catch (OperationCanceledException)
            {
                failure = new AttemptFailure(ModelFailureKind.Timeout,
                    $"The model call attempt did not complete within {attemptTimeout.TotalSeconds:0.###} s.", null, null, null);
            }
            catch (ModelProtocolException ex)
            {
                failure = new AttemptFailure(
                    ex.FailureKind,
                    ModelFailureText.Sanitize(ex.Message),
                    ex.ProviderStatusCode,
                    ex.RetryAfter,
                    ex.Details);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(
                    ex,
                    "Task {TaskId}: the model adapter threw an unclassified {ExceptionType}",
                    context.TaskId,
                    ex.GetType().Name);
                failure = new AttemptFailure(
                    ModelFailureKind.Unknown,
                    ModelFailureText.Sanitize($"{ex.GetType().Name}: {ex.Message}"),
                    null,
                    null,
                    null);
            }

            if (response is not null)
            {
                if (projected?.Stats is { } stats && response.Usage is { } projectionUsage)
                {
                    var estimated = stats.EstimatedPromptTokensAfter;
                    var delta = projectionUsage.PromptTokens - estimated;
                    contextActivity?.SetTag("bops.context.actual_prompt_tokens", projectionUsage.PromptTokens);
                    contextActivity?.SetTag("bops.context.estimate_delta", delta);
                    BOpsTelemetry.ContextEstimateDelta.Record(delta,
                        new KeyValuePair<string, object?>("provider", descriptor.ProviderId),
                        new KeyValuePair<string, object?>("model", descriptor.ModelId));
                }

                var elapsedMs = (long)timeProvider.GetElapsedTime(startedAt).TotalMilliseconds;
                context.AccountTokens?.Invoke(UsageTokens(response));

                var output = ModelOutputClassifier.Observe(response, attemptRequest.MaxOutputTokens);
                RecordOutputTelemetry(contextActivity, descriptor, output);
                var truncated = string.Equals(response.Details?.FinishReason, "length", StringComparison.OrdinalIgnoreCase);
                var responseProblem = truncated && !response.ToolCalls.Any(call => call.ToolNameError is null && call.ArgumentsError is null)
                    ? "The model reached its output limit without a complete usable response."
                    : response.ToolCalls.Count == 0 && string.IsNullOrWhiteSpace(response.TextResponse)
                        ? "The model returned no text and no tool call."
                        : null;
                // Fail closed even for adapters that expose partial text. Preserve raw diagnostics and native calls,
                // and return the empty response to the caller's existing bounded correction loop, never retry here.
                if (truncated)
                {
                    response = response with { TextResponse = null, IsFinal = false };
                }

                if ((responseProblem ?? context.MalformedResponse?.Invoke(response)) is { } problem)
                {
                    var reason = ModelFailureText.Sanitize(problem);
                    context.Calls.Add(BuildCallRecord(
                        descriptor, startedAtUtc, elapsedMs, ModelCallOutcome.Failure,
                        response.Usage, response.Details, reason) with
                    {
                        ModelAttempt = modelAttempt,
                        ProviderAttempt = providerAttempt,
                        FallbackOrdinal = fallbackOrdinal,
                        FailureKind = ModelFailureKind.MalformedResponse,
                        RetryDecision = ModelRetryDecision.NotRetryable,
                        Output = output,
                    });
                    await WriteModelCallAuditAsync(
                        context, ModelCallOutcome.Failure, reason, response.Usage, descriptor,
                        response.Details?.ActualModel, elapsedMs, modelAttempt, providerAttempt,
                        fallbackOrdinal, ModelFailureKind.MalformedResponse,
                        ModelRetryDecision.NotRetryable, null, response.Details?.ProviderStatusCode, attemptRequest.History, ct, output,
                        ObserveContext(projected?.Stats, response.Usage, context));
                    return AccountSummary(response, projected?.Stats, descriptor, summaryLedger, contextActivity);
                }

                context.Calls.Add(BuildCallRecord(
                    descriptor, startedAtUtc, elapsedMs, ModelCallOutcome.Success,
                    response.Usage, response.Details, null) with
                {
                    ModelAttempt = modelAttempt,
                    ProviderAttempt = providerAttempt,
                    FallbackOrdinal = fallbackOrdinal,
                    Output = output,
                });
                await WriteModelCallAuditAsync(
                    context, ModelCallOutcome.Success, null, response.Usage, descriptor,
                    response.Details?.ActualModel, elapsedMs, modelAttempt, providerAttempt,
                    fallbackOrdinal, null, null, null, response.Details?.ProviderStatusCode, attemptRequest.History, ct, output,
                    ObserveContext(projected?.Stats, response.Usage, context));
                return AccountSummary(response, projected?.Stats, descriptor, summaryLedger, contextActivity);
            }

            if (failure is null)
            {
                throw new InvalidOperationException("A model attempt ended with neither a response nor a classified failure.");
            }

            var failedMs = (long)timeProvider.GetElapsedTime(startedAt).TotalMilliseconds;
            var (decision, delay) = executionBudgetInterrupted
                ? (ModelRetryDecision.NotRetryable, (TimeSpan?)null)
                : budgetExhaustedBeforeAttempt
                    ? (ModelRetryDecision.BudgetExhausted, (TimeSpan?)null)
                    : DecideModelRetry(failure, providerAttempt, deadline);
            if (!budgetExhaustedBeforeAttempt
                && (decision is ModelRetryDecision.AttemptsExhausted or ModelRetryDecision.RetryAfterExceedsLimit)
                && ModelFailureText.IsRetryable(failure.Kind)
                && model is IFallbackChatModelControl fallback
                && fallback.HasNextCandidate)
            {
                var remainingForFallback = deadline.Remaining;
                if (remainingForFallback < MinimumModelAttemptWindow || context.ExecutionBudgetExpired?.Invoke() == true)
                {
                    decision = ModelRetryDecision.BudgetExhausted;
                }
                else if (fallback.TryAdvance())
                {
                    decision = ModelRetryDecision.Fallback;
                }
            }

            long? delayMs = delay is { } wait ? (long)wait.TotalMilliseconds : null;
            // An attempt that never started has no requested limit to speak of; any other failure keeps what was asked for.
            var failureOutput = budgetExhaustedBeforeAttempt
                ? null
                : ModelOutputClassifier.ObserveNoResponse(failure.Details, attemptRequest.MaxOutputTokens);
            context.Calls.Add(BuildCallRecord(
                descriptor, startedAtUtc, failedMs, ModelCallOutcome.Failure,
                null, failure.Details, failure.Message) with
            {
                ModelAttempt = modelAttempt,
                ProviderAttempt = providerAttempt,
                FallbackOrdinal = fallbackOrdinal,
                FailureKind = failure.Kind,
                RetryDecision = decision,
                RetryDelayMs = delayMs,
                ProviderStatusCode = failure.StatusCode,
                Output = failureOutput,
            });
            await WriteModelCallAuditAsync(
                context, ModelCallOutcome.Failure, failure.Message, null, descriptor,
                failure.Details?.ActualModel, failedMs, modelAttempt, providerAttempt,
                fallbackOrdinal, failure.Kind, decision, delayMs, failure.StatusCode, attemptRequest.History, ct, failureOutput,
                ObserveContext(projected?.Stats, null, context));

            if (executionBudgetInterrupted)
            {
                throw new AttemptDurationBudgetExceededException();
            }

            if (decision == ModelRetryDecision.Fallback)
            {
                if (pinnedProviderConfiguration is not null && persistPinnedProviderConfiguration is not null)
                {
                    await persistPinnedProviderConfiguration(
                        context.TaskId,
                        pinnedProviderConfiguration,
                        CancellationToken.None);
                }

                providerAttempt = 0;
                continue;
            }

            if (decision != ModelRetryDecision.Retry)
            {
                throw new ModelProtocolException(DescribeTerminalFailure(failure, decision, providerAttempt))
                {
                    FailureKind = failure.Kind,
                };
            }

            if (logger.IsEnabled(LogLevel.Warning))
            {
                logger.LogWarning(
                    "Task {TaskId}: model call attempt {Attempt} failed ({Kind}); retrying in {DelayMs} ms",
                    context.TaskId,
                    providerAttempt,
                    failure.Kind,
                    delayMs);
            }

            try
            {
                await DelayForRetryAsync(delay!.Value, context.ExecutionBudgetToken, ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && context.ExecutionBudgetExpired?.Invoke() == true)
            {
                throw new AttemptDurationBudgetExceededException();
            }
        }
    }

    /// <summary>
    /// Builds the access the MAF summarizer uses. Default: a governed broker over the task's CURRENT candidate
    /// (never the primary again, and with no fallback control, so a summary failure cannot advance the task's sticky
    /// chain). Dedicated: the host-composed broker for the explicitly configured provider/model, which is independent of
    /// the task's chain and never falls back to another destination. Either way the summary inference runs inside the
    /// logical-call <paramref name="deadline"/> of this call: it receives no budget of its own beyond what remains of it.
    /// </summary>
    private ModelSummarizationAccess? BuildSummarizationAccess(
        ModelInvocationContext context, ModelSummarizationLedger ledger, ModelCallDeadline deadline)
    {
        var summarization = options.Summarization;
        if (!summarization.Enabled)
        {
            return null;
        }

        var dedicated = summarization.IsDedicated;
        if (dedicated && dedicatedSummarizer is null)
        {
            // The operator named a destination this host did not compose (for example the model-less shared runner).
            // Never substitute the task model for an explicitly configured destination: no summary is requested.
            return null;
        }

        var broker = dedicated
            ? dedicatedSummarizer!
            : currentCandidateSummarizer ??= new ModelInvocationBroker(
                new CurrentCandidateModel(model), audit, timeProvider, logger, options, pinnedProviderConfiguration);
        var descriptor = broker.Descriptor;
        return new ModelSummarizationAccess(
            broker, descriptor, dedicated, options.Context.Resolve(descriptor), () => context.ForSummarization(deadline), ledger, summarization);
    }

    private static void RecordSummaryTelemetry(ModelContextProjectionStats? stats, ModelSummarizationAccess? access)
    {
        if (stats?.Summary is not { } summary || access is null)
        {
            return;
        }

        var dedicated = new KeyValuePair<string, object?>("dedicated", access.Dedicated);
        var outcome = new KeyValuePair<string, object?>("outcome", summary.Outcome);
        if (!string.Equals(summary.Outcome, "not-triggered", StringComparison.Ordinal))
        {
            BOpsTelemetry.SummaryEventsTotal.Add(1, new KeyValuePair<string, object?>("event", "requested"), dedicated);
            BOpsTelemetry.SummaryEventsTotal.Add(1, new KeyValuePair<string, object?>("event", summary.Outcome), dedicated);
            BOpsTelemetry.SummaryInputEstimatedTokens.Record(summary.InputEstimatedTokens, outcome, dedicated);
        }

        using var activity = BOpsTelemetry.ActivitySource.StartActivity("bops.model_summary");
        if (activity is null)
        {
            return;
        }

        activity.SetTag("bops.summary.requested", !string.Equals(summary.Outcome, "not-triggered", StringComparison.Ordinal));
        activity.SetTag("bops.summary.outcome", summary.Outcome);
        activity.SetTag("bops.summary.reason", summary.Reason);
        activity.SetTag("bops.summary.provider", access.Descriptor.ProviderId);
        activity.SetTag("bops.summary.model", access.Descriptor.ModelId);
        activity.SetTag("bops.summary.dedicated", access.Dedicated);
        activity.SetTag("bops.summary.input_estimated_tokens", summary.InputEstimatedTokens);
        activity.SetTag("bops.summary.output_estimated_tokens", summary.OutputEstimatedTokens);
        activity.SetTag("bops.summary.groups_before", summary.GroupsBefore);
        activity.SetTag("bops.summary.groups_after", summary.GroupsAfter);
    }

    /// <summary>
    /// Folds the summary inference of this logical call into the usage the runtime accumulates, so summary tokens count
    /// toward the task total. The per-attempt records already hold each call's true usage separately.
    /// </summary>
    private static ModelResponse AccountSummary(
        ModelResponse response,
        ModelContextProjectionStats? stats,
        ChatModelDescriptor descriptor,
        ModelSummarizationLedger ledger,
        System.Diagnostics.Activity? activity)
    {
        if (stats?.Summary is null && ledger.Calls == 0)
        {
            return response;
        }

        var primaryPrompt = response.Usage?.PromptTokens ?? 0;
        var primaryCompletion = response.Usage?.CompletionTokens ?? 0;
        var allInPrompt = primaryPrompt + ledger.PromptTokens;
        var allInCompletion = primaryCompletion + ledger.CompletionTokens;
        activity?.SetTag("bops.task.primary_tokens", primaryPrompt + primaryCompletion);
        activity?.SetTag("bops.task.summary_tokens", ledger.TotalTokens);
        activity?.SetTag("bops.task.all_in_tokens", allInPrompt + allInCompletion);
        var provider = new KeyValuePair<string, object?>("provider", descriptor.ProviderId);
        var model = new KeyValuePair<string, object?>("model", descriptor.ModelId);
        BOpsTelemetry.TaskCallTokens.Record(primaryPrompt + primaryCompletion,
            new KeyValuePair<string, object?>("part", "primary"), provider, model);
        BOpsTelemetry.TaskCallTokens.Record(ledger.TotalTokens,
            new KeyValuePair<string, object?>("part", "summary"), provider, model);
        BOpsTelemetry.TaskCallTokens.Record(allInPrompt + allInCompletion,
            new KeyValuePair<string, object?>("part", "all_in"), provider, model);
        if (ledger.Calls == 0)
        {
            return response;
        }

        BOpsTelemetry.SummaryTokens.Record(ledger.PromptTokens, new KeyValuePair<string, object?>("part", "prompt"));
        BOpsTelemetry.SummaryTokens.Record(ledger.CompletionTokens, new KeyValuePair<string, object?>("part", "completion"));
        BOpsTelemetry.SummaryDurationMs.Record(ledger.Duration.TotalMilliseconds);
        return response with
        {
            Usage = new ModelUsage(
                (int)Math.Min(int.MaxValue, allInPrompt),
                (int)Math.Min(int.MaxValue, allInCompletion),
                CombineCost(response.Usage?.EstimatedCostUsd, ledger.EstimatedCostUsd)),
        };
    }

    /// <summary>
    /// All-in estimated cost: the known primary cost plus the known summary cost. A part with no reported cost contributes nothing
    /// (it is never estimated), so the result is a lower bound when only one part reports; it is <c>null</c> only when neither does.
    /// </summary>
    internal static decimal? CombineCost(decimal? primary, decimal? summary) =>
        primary is null ? summary : summary is null ? primary : primary + summary;

    /// <summary>Exposes only the task's current candidate, with no fallback control and no retained credentials.</summary>
    private sealed class CurrentCandidateModel(IChatModel inner) : IChatModel
    {
        public ChatModelDescriptor Descriptor => inner.Descriptor;

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default) =>
            inner.CompleteAsync(request, ct);
    }

    private static void TagProjection(
        System.Diagnostics.Activity? activity,
        ChatModelDescriptor descriptor,
        ModelContextProjectionStats? stats)
    {
        if (activity is null || stats is null)
        {
            return;
        }

        activity.SetTag("bops.model.provider", descriptor.ProviderId);
        activity.SetTag("bops.model.id", descriptor.ModelId);
        activity.SetTag("bops.context.profile_source", stats.ProfileSource);
        activity.SetTag("bops.context.max_tokens", stats.MaxContextTokens);
        activity.SetTag("bops.context.reserved_output_tokens", stats.ReservedOutputTokens);
        activity.SetTag("bops.context.input_budget_tokens", stats.InputBudgetTokens);
        activity.SetTag("bops.context.fixed_tokens", stats.EstimatedFixedTokens);
        activity.SetTag("bops.context.protected_tokens", stats.EstimatedProtectedTokens);
        activity.SetTag("bops.context.tool_schema_tokens", stats.EstimatedToolTokens);
        activity.SetTag("bops.context.estimated_prompt_tokens_before", stats.EstimatedPromptTokensBefore);
        activity.SetTag("bops.context.estimated_prompt_tokens_after", stats.EstimatedPromptTokensAfter);
        activity.SetTag("bops.context.history_tokens_before", stats.EstimatedHistoryTokensBefore);
        activity.SetTag("bops.context.history_tokens_after", stats.EstimatedHistoryTokensAfter);
        activity.SetTag("bops.context.history_messages_before", stats.HistoricalMessagesBefore);
        activity.SetTag("bops.context.history_messages_after", stats.HistoricalMessagesAfter);
        activity.SetTag("bops.context.history_groups_before", stats.HistoricalGroupsBefore);
        activity.SetTag("bops.context.history_groups_after", stats.HistoricalGroupsAfter);
        activity.SetTag("bops.context.history_characters_before", stats.HistoricalCharactersBefore);
        activity.SetTag("bops.context.history_characters_after", stats.HistoricalCharactersAfter);
        activity.SetTag("bops.context.strategies", stats.Strategies);
        activity.SetTag("bops.context.compacted_tool_groups", stats.CompactedToolGroups);
        activity.SetTag("bops.context.dropped_turns", stats.DroppedTurns);
        activity.SetTag("bops.context.dropped_groups", stats.DroppedGroups);
    }

    /// <summary>
    /// Records the closed, content-free facts of one reply on the model-context activity and the output counters: the requested limit,
    /// the provider's finish reason, token counts, reasoning length and the classification. Never a reply, reasoning or request text.
    /// </summary>
    private static void RecordOutputTelemetry(
        System.Diagnostics.Activity? activity, ChatModelDescriptor descriptor, ModelOutputObservation output)
    {
        var outputClass = output.Class.ToString();
        BOpsTelemetry.ModelOutputTotal.Add(1,
            new KeyValuePair<string, object?>("provider", descriptor.ProviderId),
            new KeyValuePair<string, object?>("model", descriptor.ModelId),
            new KeyValuePair<string, object?>("output_class", outputClass),
            new KeyValuePair<string, object?>("finish_reason", output.FinishReason ?? "none"),
            new KeyValuePair<string, object?>("limit_requested", output.RequestedMaxOutputTokens is not null),
            new KeyValuePair<string, object?>("limit_evidence", output.LimitEvidence.ToString()));
        if (output.CompletionTokens is { } completion)
        {
            BOpsTelemetry.ModelCompletionTokens.Record(completion,
                new KeyValuePair<string, object?>("output_class", outputClass),
                new KeyValuePair<string, object?>("truncated", output.Truncated));
        }

        if (activity is null)
        {
            return;
        }

        activity.SetTag("bops.model.output_class", outputClass);
        activity.SetTag("bops.model.finish_reason", output.FinishReason);
        activity.SetTag("bops.model.truncated", output.Truncated);
        activity.SetTag("bops.model.limit_evidence", output.LimitEvidence.ToString());
        activity.SetTag("bops.model.requested_max_output_tokens", output.RequestedMaxOutputTokens);
        activity.SetTag("bops.model.completion_tokens", output.CompletionTokens);
        activity.SetTag("bops.model.reasoning_tokens", output.ReasoningTokens);
        activity.SetTag("bops.model.reasoning_characters", output.ReasoningCharacters);
        activity.SetTag("bops.model.content_characters", output.ContentCharacters);
    }

    private static void RecordProjectionMetrics(ChatModelDescriptor descriptor, ModelContextProjectionStats? stats)
    {
        if (stats is null)
        {
            return;
        }

        var provider = new KeyValuePair<string, object?>("provider", descriptor.ProviderId);
        var model = new KeyValuePair<string, object?>("model", descriptor.ModelId);
        BOpsTelemetry.ContextTokensBefore.Record(
            stats.EstimatedPromptTokensBefore, provider, model);
        BOpsTelemetry.ContextTokensAfter.Record(
            stats.EstimatedPromptTokensAfter, provider, model);
    }

    private async Task<ModelResponse> AttemptModelCallAsync(
        ModelRequest request,
        TimeSpan timeout,
        CancellationToken executionBudgetToken,
        CancellationToken ct)
    {
        using var timeoutSource = new CancellationTokenSource(ClampTimerDue(timeout), timeProvider);
        using var attemptSource = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutSource.Token, executionBudgetToken);
        // AF-05 O8 (evaluated, deliberately not changed): the wait is not linked to the caller's token. A conforming adapter observes
        // 'attemptSource' and ends the call itself; linking the wait too would abandon an in-flight response whose usage the task
        // must still account (ADR-0040 5.2) and would surface a different exception type. A non-conforming adapter is bounded by the
        // attempt timeout and the execution budget.
        using var waitSource = CancellationTokenSource.CreateLinkedTokenSource(timeoutSource.Token, executionBudgetToken);
        var call = model.CompleteAsync(request, attemptSource.Token);
        try
        {
            return await call.WaitAsync(waitSource.Token);
        }
        catch (OperationCanceledException) when (!call.IsCompleted)
        {
            _ = call.ContinueWith(
                static abandoned => _ = abandoned.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }
    }

    private async Task DelayForRetryAsync(
        TimeSpan delay,
        CancellationToken executionBudgetToken,
        CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, executionBudgetToken);
        if (RetryDelay is not null)
        {
            await RetryDelay(delay, linked.Token);
            return;
        }

        await Task.Delay(delay, timeProvider, linked.Token);
    }

    private (ModelRetryDecision Decision, TimeSpan? Delay) DecideModelRetry(
        AttemptFailure failure,
        int attempt,
        ModelCallDeadline deadline)
    {
        if (!ModelFailureText.IsRetryable(failure.Kind))
        {
            return (ModelRetryDecision.NotRetryable, null);
        }

        if (attempt >= options.ModelCallMaxAttempts)
        {
            return (ModelRetryDecision.AttemptsExhausted, null);
        }

        TimeSpan? retryAfter = failure.RetryAfter is { } asked
            ? asked < TimeSpan.Zero ? TimeSpan.Zero : asked
            : null;
        if (retryAfter > options.ModelRetryMaxDelay)
        {
            return (ModelRetryDecision.RetryAfterExceedsLimit, null);
        }

        var backoff = ModelRetryBackoff(attempt);
        var delay = retryAfter is { } honoured && honoured > backoff ? honoured : backoff;
        var remaining = deadline.Remaining;
        return delay + MinimumModelAttemptWindow > remaining
            ? (ModelRetryDecision.BudgetExhausted, null)
            : (ModelRetryDecision.Retry, delay);
    }

    private TimeSpan ModelRetryBackoff(int attempt)
    {
        var exponential = options.ModelRetryBaseDelay.Ticks * Math.Pow(2, Math.Min(attempt - 1, 30));
        var capped = Math.Min(exponential, options.ModelRetryMaxDelay.Ticks);
        var jitter = Math.Clamp((RetryJitter ?? Random.Shared.NextDouble)(), 0.0, 1.0);
        return TimeSpan.FromTicks((long)(capped * (0.5 + (0.5 * jitter))));
    }

    private string DescribeTerminalFailure(
        AttemptFailure failure,
        ModelRetryDecision decision,
        int attempts)
    {
        var tried = decision switch
        {
            ModelRetryDecision.AttemptsExhausted => $" Gave up after {attempts} attempts.",
            ModelRetryDecision.BudgetExhausted =>
                $" Gave up after {attempts} attempt(s): another wait would exceed the {options.ModelCallBudget.TotalSeconds:0.###} s model-call budget.",
            ModelRetryDecision.RetryAfterExceedsLimit =>
                $" The provider asked to retry after {failure.RetryAfter!.Value.TotalSeconds:0.###} s, longer than the "
                + $"{options.ModelRetryMaxDelay.TotalSeconds:0.###} s limit.",
            _ when attempts > 1 => $" Not retried after attempt {attempts}.",
            _ => string.Empty,
        };
        var detail = string.IsNullOrEmpty(failure.Message) ? string.Empty : $" Details: {failure.Message}";
        return $"{ModelFailureText.OperatorReason(failure.Kind)}{tried}{detail}";
    }

    /// <summary>
    /// The context facts of one attempt for its audit event: the projector's estimates, the provider's prompt tokens when it reported
    /// them (kept apart from the estimates), and the share held by reference knowledge. Counts only; <c>null</c> without a projection.
    /// </summary>
    private static ModelContextObservation? ObserveContext(
        ModelContextProjectionStats? stats, ModelUsage? usage, ModelInvocationContext context)
    {
        if (stats is null)
        {
            return null;
        }

        int? providerPrompt = usage is { PromptTokens: > 0 } ? usage.PromptTokens : null;
        double? usagePercent = null;
        bool? providerReported = null;
        if (stats.MaxContextTokens is { } window and > 0)
        {
            var prompt = providerPrompt ?? stats.EstimatedPromptTokensAfter;
            providerReported = providerPrompt is not null;
            usagePercent = Math.Round(100.0 * prompt / window, 1);
        }

        // With a profile, EstimatedFixedTokens is protected + tools + the safety reserve, so the reserve is what is left of it; the
        // input budget of the knowledge accounting is window - output reserve - safety reserve.
        int? inputBudget = stats.InputBudgetTokens is { } capacity
            ? capacity - (stats.EstimatedFixedTokens - stats.EstimatedProtectedTokens - stats.EstimatedToolTokens)
            : null;
        return new ModelContextObservation
        {
            ProfileSource = stats.ProfileSource,
            MaxContextTokens = stats.MaxContextTokens,
            ReservedOutputTokens = stats.ReservedOutputTokens,
            InputBudgetTokens = inputBudget,
            EstimatedProtectedTokens = stats.EstimatedProtectedTokens,
            EstimatedToolTokens = stats.EstimatedToolTokens,
            EstimatedHistoryTokensBefore = stats.EstimatedHistoryTokensBefore,
            EstimatedHistoryTokensAfter = stats.EstimatedHistoryTokensAfter,
            EstimatedPromptTokens = stats.EstimatedPromptTokensAfter,
            ProviderPromptTokens = providerPrompt,
            ContextUsagePercent = usagePercent,
            UsageIsProviderReported = providerReported,
            CompactedToolGroups = stats.CompactedToolGroups,
            DroppedTurns = stats.DroppedTurns,
            Aggressive = stats.Strategies.EndsWith("Aggressive", StringComparison.Ordinal),
            KnowledgeTokensInContext = context.KnowledgeContext?.TokensInContext,
            KnowledgeBudgetAvailableTokens = context.KnowledgeContext?.BudgetAvailableTokens,
            KnowledgeReadsInContext = context.KnowledgeContext?.ReadsInContext,
        };
    }

    private Task WriteModelCallAuditAsync(
        ModelInvocationContext context,
        ModelCallOutcome outcome,
        string? error,
        ModelUsage? usage,
        ChatModelDescriptor descriptor,
        string? actualModel,
        long durationMs,
        int modelAttempt,
        int providerAttempt,
        int fallbackOrdinal,
        ModelFailureKind? kind,
        ModelRetryDecision? decision,
        long? retryDelayMs,
        int? statusCode,
        IReadOnlyList<ChatTurn> history,
        CancellationToken ct,
        ModelOutputObservation? output = null,
        ModelContextObservation? contextObservation = null)
    {
        AuditEvent evt = new ModelCallAuditEvent
        {
            TimestampUtc = timeProvider.GetUtcNow(),
            Node = NodeId.Local,
            TaskId = context.TaskId,
            StepIndex = context.StepIndex,
            Actor = context.Actor,
            Provider = descriptor.ProviderId,
            Model = descriptor.ModelId,
            Outcome = outcome,
            ErrorMessage = error,
            Usage = usage,
            ActualModel = actualModel,
            DurationMs = durationMs,
            ModelAttempt = modelAttempt,
            ProviderAttempt = providerAttempt,
            FallbackOrdinal = fallbackOrdinal,
            FailureKind = kind,
            RetryDecision = decision,
            RetryDelayMs = retryDelayMs,
            ProviderStatusCode = statusCode,
            ConfigurationGeneration = pinnedProviderConfiguration?.Generation,
            ConfigurationSnapshotHash = pinnedProviderConfiguration?.SnapshotHash,
            PrimaryProvider = pinnedProviderConfiguration?.ProviderId,
            PrimaryModel = pinnedProviderConfiguration?.Model,
            ContextProvenance = history.Count == 0 ? null : ContextProvenanceCount.Summarize(history),
            Output = output,
            Context = contextObservation,
        };
        if (context.Delegation is not null)
        {
            evt = evt with { Delegation = context.Delegation };
        }

        if (context.Workflow is not null)
        {
            evt = evt with { Workflow = context.Workflow };
        }

        return audit.WriteAsync(evt, ct);
    }

    private ModelCallRecord BuildCallRecord(
        ChatModelDescriptor descriptor,
        DateTimeOffset startedAtUtc,
        long durationMs,
        ModelCallOutcome outcome,
        ModelUsage? usage,
        ModelCallDetails? details,
        string? error)
    {
        var request = CapPayload(details?.RequestJson, out var requestCut);
        var reply = CapPayload(details?.ResponseJson, out var replyCut);
        return new ModelCallRecord(
            descriptor.ProviderId,
            descriptor.ModelId,
            details?.ActualModel,
            startedAtUtc,
            durationMs,
            outcome,
            usage,
            details?.FinishReason,
            error,
            request,
            reply,
            requestCut || replyCut)
        {
            ProviderStatusCode = details?.ProviderStatusCode,
            ConfigurationGeneration = pinnedProviderConfiguration?.Generation,
            ConfigurationSnapshotHash = pinnedProviderConfiguration?.SnapshotHash,
            PrimaryProvider = pinnedProviderConfiguration?.ProviderId,
            PrimaryModel = pinnedProviderConfiguration?.Model,
        };
    }

    private string? CapPayload(string? body, out bool truncated)
    {
        truncated = false;
        if (body is null || options.MaxModelPayloadCharacters <= 0)
        {
            return null;
        }

        if (body.Length <= options.MaxModelPayloadCharacters)
        {
            return body;
        }

        truncated = true;
        var keep = options.MaxModelPayloadCharacters;
        if (char.IsHighSurrogate(body[keep - 1]))
        {
            keep--;
        }

        return $"{body[..keep]}…[truncated {body.Length - keep} characters]";
    }

    private static int UsageTokens(ModelResponse response) =>
        response.Usage is null ? 0 : response.Usage.PromptTokens + response.Usage.CompletionTokens;

    private static TimeSpan ClampTimerDue(TimeSpan due)
    {
        var max = TimeSpan.FromMilliseconds(int.MaxValue - 1);
        return due <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : due > max ? max : due;
    }

    private sealed record AttemptFailure(
        ModelFailureKind Kind,
        string Message,
        int? StatusCode,
        TimeSpan? RetryAfter,
        ModelCallDetails? Details);
}
