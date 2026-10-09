// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;
using static bOps.Runtime.Tests.JournalHarness;
using Fixture = bOps.Runtime.Tests.OrdinaryMutationJournalDurabilityTests.Fixture;

namespace bOps.Runtime.Tests;

/// <summary>
/// F-25B's core property (task packet §12.4, ADR-0051 §1.3, P3/P4): <b>a mutation is never executed twice without someone knowing
/// it may already have happened</b>. Every crash or stale-executor injection that leaves a durable intent × every reconciliation
/// outcome × every policy mode runs the full restart → recover → reconcile → resume flow, with a model that, on resume, proposes the
/// identical call first and a different read second.
/// </summary>
public sealed class OrdinaryMutationNoDoubleExecutionTests
{
    private const string Goal = "restart the test service";

    internal enum Injection
    {
        T2b,
        T3,
        T4,
        T5,
        T6,
        T7,
        T8,
        T9a,
        T9b,
        T9c,
        T10,
        T10b,
    }

    internal enum Reconciliation
    {
        None,
        Verify,
        AcceptDone,
        Abandon,
    }

    public static TheoryData<string, string, PolicyMode> Cases()
    {
        var data = new TheoryData<string, string, PolicyMode>();
        foreach (var injection in Enum.GetValues<Injection>())
        {
            foreach (var reconciliation in Enum.GetValues<Reconciliation>())
            {
                foreach (var policy in new[] { PolicyMode.Automatic, PolicyMode.Approval })
                {
                    data.Add(injection.ToString(), reconciliation.ToString(), policy);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task NeverExecutesAnAmbiguousMutationTwice(string injectionName, string reconciliationName, PolicyMode policy)
    {
        var injection = Enum.Parse<Injection>(injectionName);
        var reconciliation = Enum.Parse<Reconciliation>(reconciliationName);
        var f = new Fixture { Policy = policy };

        // ---- attempt 1: the injection ----
        Task<TaskState>? stale = null;
        var crashRunner = f.NewRunner();
        switch (injection)
        {
            case Injection.T2b:
                f.Store.CrashAfter = write => write == "intent";
                break;
            case Injection.T3:
                crashRunner.OnIntentCommitted = _ => Crash(f);
                break;
            case Injection.T4:
                f.Mutate.Behaviour = MutationBehaviour.HalfThenCrash;
                f.Mutate.OnCrash = f.Store.Kill;
                break;
            case Injection.T5:
                f.Mutate.Behaviour = MutationBehaviour.EffectThenCrash;
                f.Mutate.OnCrash = f.Store.Kill;
                break;
            case Injection.T6:
                crashRunner.OnInvocationReturned = _ => Crash(f);
                break;
            case Injection.T7:
                f.Observe.Behaviour = ObserveBehaviour.Crash;
                f.Observe.OnCrash = f.Store.Kill;
                break;
            case Injection.T8:
                f.Store.CrashBefore = write => write == "outcome";
                break;
            case Injection.T9a:
                f.CrashAtTheTaskWriteAfterTheOutcome();
                break;
            case Injection.T9b:
                f.Mutate.Result = new ToolCallResult(ToolOutcome.Timeout, null, "no answer in time") { FailureKind = ToolFailureKind.Timeout };
                f.Observe.Verdict = VerificationStatus.Refuted;
                f.CrashAtTheTaskWriteAfterTheOutcome();
                break;
            case Injection.T9c:
                f.Store.Fail = write => write == "outcome";
                break;
            case Injection.T10 or Injection.T10b:
                f.Mutate.Behaviour = MutationBehaviour.Block;
                break;
        }

        if (injection is Injection.T10 or Injection.T10b)
        {
            stale = Task.Run(() => crashRunner.RunAsync(Goal, Operator, f.TaskId));
            await f.Mutate.Started.WaitAsync(TimeSpan.FromSeconds(30));
        }
        else if (injection == Injection.T9c)
        {
            await Assert.ThrowsAsync<IOException>(() => crashRunner.RunAsync(Goal, Operator, f.TaskId));
            f.Store.Revive();
        }
        else
        {
            await f.CrashAsync(crashRunner);
        }

        f.Observe.Behaviour = ObserveBehaviour.Answer;
        f.Mutate.Behaviour = MutationBehaviour.Complete;
        Assert.Single(f.Journal);
        var executionsAfterAttempt1 = f.Mutate.Executions;
        Assert.True(executionsAfterAttempt1 <= 1);

        // ---- restart: a new runner; recovery, then the reconciliation of the case ----
        var admin = f.NewRunner();
        Assert.Equal(TaskRecoveryOutcome.Recovered, (await admin.TryRecoverAsync(f.TaskId, 1, Administrator, executingHere: false)).Outcome);
        if (injection == Injection.T10b)
        {
            f.Mutate.Release();
            await stale!;
        }

        f.Observe.Verdict = VerificationStatus.Confirmed;
        switch (reconciliation)
        {
            case Reconciliation.Verify:
                await admin.ReconcileMutationsAsync(f.TaskId, TaskReconcileAction.Verify, Administrator);
                break;
            case Reconciliation.AcceptDone:
                await admin.ReconcileMutationsAsync(f.TaskId, TaskReconcileAction.AcceptDone, Administrator);
                break;
            case Reconciliation.Abandon:
                await admin.ReconcileMutationsAsync(f.TaskId, TaskReconcileAction.Abandon, Administrator);
                break;
        }

        // Recovery and reconciliation never invoke the mutating tool.
        Assert.Equal(executionsAfterAttempt1, f.Mutate.Executions);

        // ---- resume: the model proposes the identical call first, a different read second ----
        var before = await f.TaskAsync();
        var model = new JournalScriptModel
        {
            Step = (n, _) => n switch
            {
                1 => new ModelResponse(null, [new ModelToolCall("again", "test.mutate", new ToolArguments(new JsonObject { ["target"] = "svc-1" }))], false, null),
                2 => new ModelResponse(null, [new ModelToolCall("read", "test.observe", new ToolArguments(new JsonObject { ["target"] = "svc-1" }))], false, null),
                _ => new ModelResponse("done", [], true, null),
            },
        };
        var resumer = f.NewRunner(model);
        var acquisition = await resumer.TryAcquireResumeAsync(f.TaskId, Operator);
        if (injection == Injection.T10)
        {
            f.Mutate.Release();
            await stale!;
        }

        if (acquisition.Outcome == TaskResumeOutcome.Acquired)
        {
            await resumer.ExecuteAcquiredResumeAsync(acquisition.Task!, Operator);
        }
        else
        {
            // Where the task stayed blocked, no new execution attempt was created.
            var blocked = await f.TaskAsync();
            Assert.Equal(before.ExecutionAttempt, blocked.ExecutionAttempt);
            Assert.True(MutationJournalPolicy.BlocksResume(f.Journal[0].State) || acquisition.Refusal!.Code == TaskResumeRefusal.ResumeConflict,
                $"{injection}/{reconciliation}/{policy}: refused {acquisition.Refusal?.Code} with a non-blocking journal");
        }

        // The property: the durable intent was executed at most once, whatever happened next.
        Assert.True(f.Mutate.Executions <= 1, $"{injection}/{reconciliation}/{policy}: executed {f.Mutate.Executions} times");
        Assert.Single(f.Journal, entry => entry.Intent.Key.ExecutionAttempt == 1);

        // Any human asked about the identical call was told it duplicates a reconciled change.
        foreach (var reason in f.Approver.Reasons.Skip(policy == PolicyMode.Approval ? 1 : 0))
        {
            Assert.Contains(MutationJournalPolicy.DuplicateOfReconciledMarker, reason, StringComparison.Ordinal);
        }
    }

    private static Task Crash(Fixture f)
    {
        f.Store.Kill();
        throw new SimulatedCrashException();
    }
}
