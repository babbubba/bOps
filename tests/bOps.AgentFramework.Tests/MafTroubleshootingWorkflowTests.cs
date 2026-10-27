// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.AgentFramework.Troubleshooting;
using bOps.Abstractions;
using bOps.Runtime;
using Xunit.Abstractions;
using static bOps.AgentFramework.Tests.MafWorkflowWorld;
using static bOps.AgentFramework.Tests.RoleScriptedModel;

namespace bOps.AgentFramework.Tests;

/// <summary>
/// C01-R23 end to end, offline: a real <see cref="AgentRunner"/>, <see cref="ModelInvocationBroker"/> and tool registry under the real
/// MAF workflow, with a scripted model and fake tools. Each test states the behaviour it proves; none of them only checks that two agents
/// produced text.
/// </summary>
public sealed class MafTroubleshootingWorkflowTests(ITestOutputHelper log)
{
    [Fact]
    public async Task E2E_SlowdownsAndRestarts_SystemAgentCallsTwoTools_ReviewerAsksForMore_SystemAgentCallsAgain_DiagnosisSeparatesFactsFromHypotheses()
    {
        var world = Create();
        world.Model.Script("triage", Text(TriagePlan(("system", "Find why the host slows down and restarts."))));
        world.Model.Script(
            "system",
            Call("c1", "fake.sys.events"),
            Call("c2", "fake.sys.services"),
            Text(request => SpecialistReply("Kernel-Power 41 and a stopped service.", EvidenceIds(request), "Could be a power problem.")),
            Call("c3", "fake.sys.dumps"),
            Text(request => SpecialistReply("No dump; one display-driver LiveKernel report.", EvidenceIds(request))));
        world.Model.Script(
            "reviewer",
            Text(_ => ReviewReply(false, "The data do not show a power fault.", [], [("system", "Check the dumps and the display-driver events.")], ["kernel dumps"])),
            Text(request => ReviewReply(true, "Most likely the display driver; a power fault is not demonstrated.", AllEvidenceIds(request).Take(1).ToArray())));
        var events = new List<MafWorkflowEvent>();

        var report = await world.Workflow.RunAsync("Windows slows down and restarts", Actor, events.Add);

        // The real sequence of agents, model calls and tool calls, printed for the demo (run with a detailed console logger).
        log.WriteLine("--- workflow progress ---");
        events.ForEach(item => log.WriteLine($"{item.Kind,-20} {item.Agent ?? "-",-18} round {item.Round}  {item.Detail}"));
        log.WriteLine("--- model calls by agent, in order ---");
        log.WriteLine(string.Join(" → ", world.Model.Requests.Select(item => item.Role)));
        log.WriteLine("--- audit chain of the run ---");
        foreach (var evt in world.Audit.Events.Where(item => item.TaskId == report.RunId))
        {
            log.WriteLine(evt switch
            {
                WorkflowRunAuditEvent run => $"workflowRun {run.Stage} round={run.Round} code={run.Code} toolCalls={run.ToolCalls} tokens={run.Tokens}",
                ToolCallAuditEvent tool => $"toolCall    {tool.Workflow?.AgentName} {tool.Tool} {tool.Authorization} {tool.Outcome} invocation={tool.Workflow?.InvocationId} step={tool.StepIndex}",
                ModelCallAuditEvent model => $"modelCall   {model.Workflow?.AgentName} {model.Outcome} step={model.StepIndex}",
                _ => evt.GetType().Name,
            });
        }

        Assert.Equal(MafWorkflowOutcome.Completed, report.Outcome);
        Assert.Equal(2, report.Rounds);
        Assert.Equal(["system"], report.SelectedSpecialists);
        // Two tools in round one, a third after the reviewer found the evidence insufficient: all three really ran, once each.
        Assert.Equal(3, report.ToolCalls);
        Assert.Equal([1, 1, 1], [world.Executions("fake.sys.events"), world.Executions("fake.sys.services"), world.Executions("fake.sys.dumps")]);
        Assert.All(report.Evidence, entry => Assert.Equal(WorkflowToolStatus.Executed, entry.Status));
        // Facts cite real evidence; hypotheses stay hypotheses; the reviewer's unsupported claim and missing data are carried through.
        var fact = Assert.Single(report.Facts);
        Assert.All(fact.EvidenceIds, id => Assert.Contains(report.Evidence, entry => entry.EvidenceId == id));
        Assert.Contains(report.Hypotheses, claim => claim.Text.Contains("display driver", StringComparison.Ordinal));
        Assert.Contains(report.Unsupported, text => text.Contains("power supply", StringComparison.Ordinal));
        Assert.Contains("display driver", report.Diagnosis, StringComparison.Ordinal);
        Assert.NotEmpty(report.InterventionPlan);
        // The CLI-facing progress shows the whole cycle in order.
        Assert.Equal(
            [MafWorkflowEventKind.Started, MafWorkflowEventKind.Triaged, MafWorkflowEventKind.RoundStarted],
            events.Take(3).Select(item => item.Kind));
        Assert.Contains(events, item => item.Kind == MafWorkflowEventKind.Reviewed && item.Detail.Contains("insufficient", StringComparison.Ordinal));
        Assert.Equal(2, events.Count(item => item.Kind == MafWorkflowEventKind.RoundStarted));
        Assert.Equal(3, events.Count(item => item.Kind == MafWorkflowEventKind.ToolCompleted));
        // The specialist reports the evidence it collected in each round (two reads in round one, one in round two).
        Assert.Equal(
            ["2 evidence item(s)", "1 evidence item(s)"],
            events.Where(item => item.Kind == MafWorkflowEventKind.SpecialistCompleted).Select(item => item.Detail[(item.Detail.IndexOf(';', StringComparison.Ordinal) + 2)..]));
    }

    [Fact]
    public async Task E2E_SystemAndPostgreSql_RunConcurrentlyInOneRound_AndTheReviewerSeesBothReports()
    {
        var sysStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pgStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var overlap = 0;

        // Each tool returns only when the other specialist's tool has started: sequential execution would never get past the wait.
        async Task Rendezvous(TaskCompletionSource self, TaskCompletionSource other, CancellationToken ct)
        {
            self.TrySetResult();
            await other.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            Interlocked.Increment(ref overlap);
        }

        var world = Create(
            postgres: true,
            extraTools:
            [
                new ProbeTool("fake.sys.slow", "System side done.", onExecute: ct => Rendezvous(sysStarted, pgStarted, ct)),
                new ProbeTool("fake.pg.slow", "Database side done.", onExecute: ct => Rendezvous(pgStarted, sysStarted, ct)),
            ]);
        world.Model.Script("triage", Text(TriagePlan(("system", "Check the host."), ("postgresql", "Check blocking sessions."))));
        world.Model.Script(
            "system",
            Call("s1", "fake.sys.slow"),
            Text(request => SpecialistReply("Service stopped twice.", EvidenceIds(request))));
        world.Model.Script(
            "postgresql",
            Call("p1", "fake.pg.slow"),
            Text(request => SpecialistReply("Two sessions blocked.", EvidenceIds(request))));
        world.Model.Script("reviewer", Text(request => ReviewReply(true, "Both areas examined.", AllEvidenceIds(request).Take(2).ToArray())));

        var report = await world.Workflow.RunAsync("PostgreSQL is slow and its service stops now and then", Actor);

        Assert.Equal(2, overlap);
        Assert.Equal(MafWorkflowOutcome.Completed, report.Outcome);
        Assert.Equal(["postgresql", "system"], report.SelectedSpecialists.Order());
        Assert.Equal(1, report.Rounds);
        Assert.Equal(2, report.ToolCalls);
        // The reviewer received the reports of both specialists and the evidence ids of both.
        var reviewerRequest = Assert.Single(world.Model.Requests, item => item.Role == "reviewer").Request;
        var reviewerInput = string.Join('\n', reviewerRequest.History.Select(turn => turn.Content));
        Assert.Contains("specialist=system", reviewerInput, StringComparison.Ordinal);
        Assert.Contains("specialist=postgresql", reviewerInput, StringComparison.Ordinal);
        Assert.Equal(2, AllEvidenceIds(reviewerRequest).Length);
    }

    [Fact]
    public async Task PostgreSqlAgentNotAvailable_TheWorkflowStillTroubleshootsTheSystem_AndSaysWhyTheOtherWasNotUsed()
    {
        var world = Create(postgres: false);
        world.Model.Script("triage", Text(TriagePlan(("system", "Check the host."), ("postgresql", "Check the database."))));
        world.Model.Script(
            "system",
            Call("s1", "fake.sys.events"),
            Text(request => SpecialistReply("Kernel-Power 41.", EvidenceIds(request))));
        world.Model.Script("reviewer", Text(request => ReviewReply(true, "Host examined.", AllEvidenceIds(request))));

        var report = await world.Workflow.RunAsync("PostgreSQL is slow", Actor);

        Assert.Equal(MafWorkflowOutcome.Completed, report.Outcome);
        Assert.Equal(["system"], report.SelectedSpecialists);
        Assert.Contains(report.ExcludedSpecialists, text => text.StartsWith("postgresql:", StringComparison.Ordinal) && text.Contains("no tool is available", StringComparison.Ordinal));
        // The triage agent was only offered the specialist that can work: the unavailable one is not in its instructions.
        var triage = Assert.Single(world.Model.Requests, item => item.Role == "triage").Request;
        Assert.DoesNotContain("postgresql", triage.SystemPrompt, StringComparison.Ordinal);
        Assert.Equal(0, world.Model.CallsFor("postgresql"));
    }

    [Fact]
    public async Task TriageNamingNoAvailableSpecialist_InvestigatesNothing_AndRunsNoTool()
    {
        var world = Create();
        world.Model.Script("triage", Text(TriagePlan(("oracle", "Check the database."))));

        var report = await world.Workflow.RunAsync("Oracle is slow", Actor);

        Assert.Equal(MafWorkflowOutcome.NothingInvestigated, report.Outcome);
        Assert.Empty(report.SelectedSpecialists);
        Assert.Equal(0, report.ToolCalls);
        Assert.Equal(1, world.Model.TotalCalls);
        Assert.Contains(report.Limitations, text => text.Contains("oracle", StringComparison.Ordinal));
    }

    [Fact]
    public void WorkflowRunAuditEvent_RoundTripsThroughTheAuditJson_WithItsCorrelationOnlyWhenPresent()
    {
        var plain = new WorkflowRunAuditEvent
        {
            TimestampUtc = DateTimeOffset.UnixEpoch, Node = NodeId.Local, TaskId = Guid.NewGuid(), Actor = Actor, Stage = WorkflowRunStage.Started,
            ToolAuthorityHashes = new Dictionary<string, string> { ["specialist-system"] = "abc" },
        };

        var json = System.Text.Json.JsonSerializer.Serialize<AuditEvent>(plain);
        var back = Assert.IsType<WorkflowRunAuditEvent>(System.Text.Json.JsonSerializer.Deserialize<AuditEvent>(json));

        Assert.Equal("abc", back.ToolAuthorityHashes!["specialist-system"]);
        Assert.Null(back.Workflow);
        Assert.DoesNotContain("\"Workflow\"", json, StringComparison.Ordinal);
        var withCorrelation = plain with { Workflow = new WorkflowCorrelation(plain.TaskId, "triage", Guid.NewGuid(), "c1") };
        Assert.Contains("\"Workflow\"", System.Text.Json.JsonSerializer.Serialize<AuditEvent>(withCorrelation), StringComparison.Ordinal);
    }

    private static JsonObject Args(string name, object value) => new() { [name] = JsonValue.Create(value) };
}
