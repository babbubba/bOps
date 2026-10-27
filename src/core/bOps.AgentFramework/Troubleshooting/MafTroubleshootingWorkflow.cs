// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Runtime;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;

namespace bOps.AgentFramework.Troubleshooting;

/// <summary>
/// The MAF multi-agent troubleshooting workflow (C01-R23). Microsoft Agent Framework runs it: a triage executor, one specialist executor
/// per available specialist (run concurrently in a round), and a review executor that either concludes or sends another round back to
/// the specialists, all wired with the framework's own <see cref="WorkflowBuilder"/> edges and run by <see cref="InProcessExecution"/>.
/// Every agent is a framework agent with a chat-client pipeline; every model call goes through the governed broker; every tool call goes through the
/// runner's gateway session. Read-only: a tool above Read is refused before policy, and the intervention plan the run ends with is
/// advice that nothing here executes.
/// </summary>
/// <remarks>
/// This version has no checkpoint and no resume (C01-R23 clause 7, an implementation limit, not a prohibition): the run's identity is a
/// task id generated for it, its operational record is the audit chain, it is not written to the task store, an interrupted run is
/// incomplete and never reported as completed, and a retry is a new run. The framework's own state and the evidence are in memory only.
/// </remarks>
public sealed class MafTroubleshootingWorkflow
{
    private readonly AgentRunner runner;
    private readonly IModelInvocationBroker broker;
    private readonly DiagnosticSpecialistCatalog catalog;
    private readonly IAuditSink audit;
    private readonly TimeProvider time;
    private readonly ILogger logger;
    private readonly MafWorkflowLimits limits;

    /// <summary>Creates the workflow over the node's runner, governed broker and registered specialists.</summary>
    /// <param name="runner">The runner whose gateway session every tool call goes through.</param>
    /// <param name="broker">The governed broker every model call goes through.</param>
    /// <param name="catalog">The specialists the host registered.</param>
    /// <param name="audit">Receives the workflow's lifecycle events.</param>
    /// <param name="time">The clock; the system clock by default.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="limits">The run limits; the conservative defaults when <c>null</c>.</param>
    public MafTroubleshootingWorkflow(
        AgentRunner runner,
        IModelInvocationBroker broker,
        DiagnosticSpecialistCatalog catalog,
        IAuditSink audit,
        TimeProvider? time = null,
        ILogger? logger = null,
        MafWorkflowLimits? limits = null)
    {
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        this.broker = broker ?? throw new ArgumentNullException(nameof(broker));
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.audit = audit ?? throw new ArgumentNullException(nameof(audit));
        this.time = time ?? TimeProvider.System;
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        this.limits = (limits ?? new MafWorkflowLimits()).Validate();
    }

    /// <summary>Runs one troubleshooting investigation of <paramref name="goal"/>.</summary>
    /// <param name="goal">The operator's description of the problem.</param>
    /// <param name="actor">The operator on whose authority the run executes.</param>
    /// <param name="progress">Receives progress events as the run advances; may be <c>null</c>.</param>
    /// <param name="ct">Cancelled to abandon the run; the run then ends as cancelled and the exception propagates.</param>
    /// <exception cref="MafWorkflowFailedException">The run ended in an error that is not a limit or a cancellation.</exception>
    public async Task<MafDiagnosticReport> RunAsync(
        string goal, ActorIdentity actor, Action<MafWorkflowEvent>? progress = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);
        ArgumentNullException.ThrowIfNull(actor);

        var runId = Guid.NewGuid();
        var session = runner.OpenWorkflowToolSession(runId, actor, limits.MaxToolCalls);
        var specialists = catalog.Resolve(session.VisibleReadTools, out var unavailable);
        var triage = session.RegisterAgent(TriageExecutor.ExecutorId, []);
        var reviewer = session.RegisterAgent(ReviewExecutor.ExecutorId, []);
        var handles = specialists.ToDictionary(
            available => available.Specialist.Name,
            available => session.RegisterAgent(SpecialistExecutor.IdOf(available.Specialist.Name), available.Tools.Select(tool => tool.Name)),
            StringComparer.Ordinal);

        var run = new MafRun
        {
            RunId = runId,
            Goal = goal,
            Limits = limits,
            Budget = new WorkflowBudget(limits),
            Session = session,
            Broker = broker,
            Audit = audit,
            Time = time,
            Specialists = specialists,
            Unavailable = unavailable,
            TriageHandle = triage,
            ReviewerHandle = reviewer,
            SpecialistHandles = handles,
            Progress = progress,
        };

        run.Report(
            MafWorkflowEventKind.Started, null, 0,
            $"run {runId}; specialists available: {(specialists.Count == 0 ? "none" : string.Join(", ", specialists.Select(available => available.Specialist.Name)))}"
            + (unavailable.Count == 0 ? string.Empty : $"; not available on this node: {string.Join(", ", unavailable)}"));
        await run.AuditAsync(
            WorkflowRunStage.Started, 0, [.. session.Agents.Select(agent => agent.Name)], null, ct,
            session.Agents.ToDictionary(agent => agent.Name, agent => agent.AuthorityHash, StringComparer.Ordinal));

        using var timeout = new CancellationTokenSource(limits.MaxDuration, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        Exception? failure = null;
        try
        {
            var workflow = Build(run);
            await using var streaming = await InProcessExecution.RunStreamingAsync(
                workflow, new WorkflowMessages.Goal(goal), runId.ToString(), linked.Token);
            await foreach (var evt in streaming.WatchStreamAsync(linked.Token))
            {
                switch (evt)
                {
                    case ExecutorFailedEvent executorFailed:
                        failure ??= executorFailed.Data;
                        break;
                    case WorkflowErrorEvent workflowError:
                        failure ??= workflowError.Data as Exception;
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await run.AuditAsync(WorkflowRunStage.Cancelled, run.CurrentRound, null, "cancelled", CancellationToken.None);
            throw;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            run.Final ??= ReportAssembler.FromReports(run, MafWorkflowOutcome.LimitReached, "time_limit", run.CurrentRound, null);
        }

        if (run.Final is null && timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            run.Final = ReportAssembler.FromReports(run, MafWorkflowOutcome.LimitReached, "time_limit", run.CurrentRound, null);
        }

        if (ct.IsCancellationRequested)
        {
            await run.AuditAsync(WorkflowRunStage.Cancelled, run.CurrentRound, null, "cancelled", CancellationToken.None);
            ct.ThrowIfCancellationRequested();
        }

        if (run.Final is null)
        {
            var reason = failure is null ? "the workflow ended without a report" : $"{failure.GetType().Name} in an executor";
            await run.AuditAsync(WorkflowRunStage.Failed, run.CurrentRound, null, "workflow_failed", CancellationToken.None);
            logger.LogError(failure, "MAF troubleshooting run {RunId} failed", runId);
            throw new MafWorkflowFailedException(runId, $"The MAF troubleshooting workflow failed: {reason}. No resume exists; a retry is a new run.", failure);
        }

        var finished = run.Final;
        await run.AuditAsync(
            finished.Outcome == MafWorkflowOutcome.LimitReached ? WorkflowRunStage.LimitReached : WorkflowRunStage.Completed,
            finished.Rounds, null, finished.LimitCode ?? finished.Outcome.ToString().ToLowerInvariant(), CancellationToken.None);
        run.Report(MafWorkflowEventKind.Finished, null, finished.Rounds, finished.Outcome.ToString());
        return finished;
    }

    /// <summary>
    /// Wires the executors with the framework's own edges: the triage executor broadcasts the round to every specialist, a barrier edge
    /// gathers their reports into the reviewer, and the reviewer's follow-up rounds go back to the same specialists. A specialist with no
    /// assignment in a round reports so without a model call; the others run concurrently.
    /// </summary>
    private Workflow Build(MafRun run)
    {
        ExecutorBinding triage = new TriageExecutor(run);
        ExecutorBinding review = new ReviewExecutor(run);
        var specialists = run.Specialists
            .Select(available => (ExecutorBinding)new SpecialistExecutor(run, available, logger))
            .ToArray();
        return new WorkflowBuilder(triage)
            .AddFanOutEdge(triage, specialists)
            .AddFanInBarrierEdge(specialists, review)
            .AddFanOutEdge(review, specialists)
            .WithOutputFrom(triage, review)
            .WithName("bops-maf-troubleshooting")
            .Build();
    }
}
