// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

// CA2000: ownership of every world, process and skills source passes to the test, which disposes it.
#pragma warning disable CA2000

using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Knowledge;
using bOps.Knowledge.Tests;
using bOps.Runtime;
using bOps.Runtime.Tests;
using Microsoft.Agents.AI;
using BopsChatRole = bOps.Abstractions.ChatRole;

namespace bOps.AgentFramework.Tests;

/// <summary>
/// ADR-0059: a refused <c>runtime.knowledge_read</c> ends the reading, not the task, unless the refusal is a security signal. The bridge
/// classifies every refusal from its closed result code; these tests pin the class of each cause, the consequence of each class, and
/// the loop protection that remains. Offline and deterministic: scripted replies, temporary data, no model.
/// </summary>
public sealed class KnowledgeRefusalRecoveryTests
{
    private const string Goal = "fix the nginx 502";
    private const string Checklist = "nginx -t; ss -ltnp :80";
    private static readonly ModelUsage Usage = new(100, 10, null);

    // ---- the bridge: one refusal per cause, classified by the code ----

    public static TheoryData<string> RecoverableCauses => new() { "malformed", "unknown-skill", "unknown-resource", "out-of-range", "unsupported", "too-large" };

    [Theory]
    [MemberData(nameof(RecoverableCauses))]
    public async Task ARecoverableCause_AfterTheAllowance_WithdrawsTheFunction_AndNeverStopsTheTask(string cause)
    {
        var content = new Mutable(Checklist);
        var audit = new RecordingAuditSink();
        using var bridge = await OpenAsync(content, audit, new KnowledgeReadBudget(MaxReads: 4, MaxRejections: 1, MaxCharactersPerRead: cause == "too-large" ? 5 : 8000));

        var first = await bridge.HandleAsync(Refused(cause, "a"), Authority(), Context());
        var second = await bridge.HandleAsync(Refused(cause, "b"), Authority(), Context());

        Assert.Equal(ExpectedCode(cause), first.Code);
        Assert.Equal(ExpectedCode(cause), second.Code);
        Assert.False(first.ProtocolBoundExceeded);
        Assert.False(second.ProtocolBoundExceeded); // the second recoverable refusal exceeds the allowance of 1: it withdraws, it does not stop
        Assert.Equal(0, bridge.ReadsRemaining);
        var events = audit.Events.OfType<KnowledgeReadAuditEvent>().ToArray();
        Assert.Equal([null, true], events.Select(evt => evt.Withdrawn));
        Assert.Equal(0, events.Sum(evt => evt.ReturnedLength));
        Assert.Contains("optional", Reply(first), StringComparison.Ordinal);
        Assert.Contains("withdrawn", Reply(second), StringComparison.Ordinal);
        Assert.Contains("operational tool", Reply(second), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFrameworkFailure_IsAnInfrastructureFault_Recoverable_AndAuditedUnderItsOwnCode()
    {
        var content = new Mutable(Checklist);
        var audit = new RecordingAuditSink();
        using var bridge = await OpenAsync(content, audit, new KnowledgeReadBudget(MaxReads: 4, MaxRejections: 1));
        content.Fail = true; // the framework function throws while reading verified content

        var first = await bridge.HandleAsync(ReadResource("a"), Authority(), Context());
        var second = await bridge.HandleAsync(ReadResource("b"), Authority(), Context());

        Assert.Equal([KnowledgeReadResultCode.InfrastructureFailure, KnowledgeReadResultCode.InfrastructureFailure], [first.Code, second.Code]);
        Assert.All(audit.Events.OfType<KnowledgeReadAuditEvent>(), evt => Assert.NotEqual(KnowledgeReadResultCode.IntegrityMismatch, evt.ResultCode));
        Assert.All(audit.Events.OfType<KnowledgeReadAuditEvent>(), evt => Assert.Equal(0, evt.ReturnedLength)); // fail closed: nothing returned
        Assert.False(second.ProtocolBoundExceeded);
        Assert.Equal(0, bridge.ReadsRemaining);
        Assert.Equal([null, true], audit.Events.OfType<KnowledgeReadAuditEvent>().Select(evt => evt.Withdrawn));
        Assert.Contains("could not be read now", Reply(first), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContentThatIsNotThePinnedContent_IsAnIntegrityRefusal_AndKeepsTheHardStop()
    {
        var content = new Mutable(Checklist);
        var audit = new RecordingAuditSink();
        using var bridge = await OpenAsync(content, audit, new KnowledgeReadBudget(MaxReads: 4, MaxRejections: 1));
        content.Value = "rm -rf /etc/nginx"; // the source changes under the task

        var first = await bridge.HandleAsync(ReadResource("a"), Authority(), Context());
        var second = await bridge.HandleAsync(ReadResource("b"), Authority(), Context());

        Assert.Equal([KnowledgeReadResultCode.IntegrityMismatch, KnowledgeReadResultCode.IntegrityMismatch], [first.Code, second.Code]);
        Assert.False(first.ProtocolBoundExceeded);
        Assert.True(second.ProtocolBoundExceeded); // unchanged: more than MaxRejections integrity refusals stop the task
        Assert.All(audit.Events.OfType<KnowledgeReadAuditEvent>(), evt => Assert.Null(evt.Withdrawn));
        Assert.DoesNotContain("rm -rf", Reply(first) + Reply(second), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AfterTheWithdrawal_EvenAValidCallIsNotServed_AndTheThirdCallEndsTheTask()
    {
        var audit = new RecordingAuditSink();
        using var bridge = await OpenAsync(new Mutable(Checklist), audit, new KnowledgeReadBudget(MaxReads: 4, MaxRejections: 1));
        await bridge.HandleAsync(Refused("unknown-skill", "a"), Authority(), Context());
        await bridge.HandleAsync(Refused("unknown-skill", "b"), Authority(), Context()); // withdraws

        var valid = await bridge.HandleAsync(ReadResource("c"), Authority(), Context());
        var again = await bridge.HandleAsync(ReadResource("d"), Authority(), Context());
        var third = await bridge.HandleAsync(ReadResource("e"), Authority(), Context());

        Assert.All([valid, again, third], outcome => Assert.Equal(KnowledgeReadResultCode.NotOffered, outcome.Code));
        Assert.DoesNotContain(valid.ContinuationTurns, turn => turn.Content?.Contains(Checklist, StringComparison.Ordinal) == true);
        Assert.False(valid.ProtocolBoundExceeded);
        Assert.False(again.ProtocolBoundExceeded);
        Assert.True(third.ProtocolBoundExceeded);
        Assert.Equal(KnowledgeReadBridge.MaxUnavailableCalls, 2);
        Assert.Contains("Do not call it again", Reply(valid), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADuplicateRead_IsServedAndAudited_TheBudgetBoundsTheLoop_AndTheSpentBudgetKeepsItsHardStop()
    {
        var content = new Mutable(Checklist);
        var audit = new RecordingAuditSink();
        using var bridge = await OpenAsync(content, audit, new KnowledgeReadBudget(MaxReads: 2, MaxRejections: 4));

        var first = await bridge.HandleAsync(ReadResource("a"), Authority(), Context());
        var second = await bridge.HandleAsync(ReadResource("b"), Authority(), Context());
        var spent = new[]
        {
            await bridge.HandleAsync(ReadResource("c"), Authority(), Context()),
            await bridge.HandleAsync(ReadResource("d"), Authority(), Context()),
            await bridge.HandleAsync(ReadResource("e"), Authority(), Context()),
        };

        Assert.Equal([KnowledgeReadResultCode.Success, KnowledgeReadResultCode.Success], [first.Code, second.Code]);
        Assert.Equal([false, true], audit.Events.OfType<KnowledgeReadAuditEvent>().Take(2).Select(evt => evt.Repeat));
        Assert.All(spent, outcome => Assert.Equal(KnowledgeReadResultCode.LimitExceeded, outcome.Code));
        Assert.Equal([false, false, true], spent.Select(outcome => outcome.ProtocolBoundExceeded));
        Assert.Equal(0, bridge.ReadsRemaining);
    }

    [Fact]
    public async Task TheRefusalReply_IsClosedAndActionable_ItNeverEchoesWhatTheModelSent()
    {
        using var bridge = await OpenAsync(new Mutable(Checklist), new RecordingAuditSink());
        var claimed = "zzz-model-claimed-skill";

        var outcome = await bridge.HandleAsync(
            Raw("a", new JsonObject { ["operation"] = "load", ["skill"] = claimed }), Authority(), Context());

        var reply = Reply(outcome);
        Assert.StartsWith("Runtime control call rejected: UnknownSkill.", reply, StringComparison.Ordinal);
        Assert.Contains("Reading knowledge is optional", reply, StringComparison.Ordinal);
        Assert.Contains("knowledge index", reply, StringComparison.Ordinal);
        Assert.Contains("operational tool", reply, StringComparison.Ordinal);
        Assert.DoesNotContain(claimed, reply, StringComparison.Ordinal);
        Assert.DoesNotContain(Checklist, reply, StringComparison.Ordinal);
    }

    // ---- the runner: the task survives, the operational tool runs, the hard stops remain ----

    [Fact]
    public async Task FiveBadRefsAndMore_NoLongerFailTheTask_TheFunctionIsWithdrawn_AndTheOperationalToolRuns()
    {
        using var world = new KnowledgeRunnerWorld();
        using var process = Boot(world, new TestPack()); // the shipped defaults: MaxRejections 4, so the fifth refusal withdraws
        var rig = process.Runner(
            [Script.Plan("test.read"),
             Script.Knowledge("k1", "load", "no-such-skill"), Script.Knowledge("k2", "load", "no-such-skill"), Script.Knowledge("k3", "load", "no-such-skill"),
             Script.Knowledge("k4", "load", "no-such-skill"), Script.Knowledge("k5", "load", "no-such-skill"),
             Script.Tool("c1", "test.read"), Script.Final("done")]);

        var final = await rig.Runner.RunAsync(Goal, KnowledgeRunnerWorld.ActorA);

        Assert.Equal(AgentTaskStatus.Completed, final.Status);
        Assert.Equal(1, rig.Read.ExecutionCount); // the operational tool of the step ran
        var reads = world.Events.OfType<KnowledgeReadAuditEvent>().ToArray();
        Assert.Equal(Enumerable.Repeat(KnowledgeReadResultCode.UnknownSkill, 5), reads.Select(read => read.ResultCode));
        Assert.Equal([null, null, null, null, true], reads.Select(read => read.Withdrawn));

        // Offered on every step request up to the withdrawal; gone from the request that follows it.
        Assert.All(rig.Model.Requests.Skip(1).Take(5), request => Assert.Contains(request.AvailableTools, tool => tool.Name == "runtime.knowledge_read"));
        Assert.All(rig.Model.Requests.Skip(6), request => Assert.DoesNotContain(request.AvailableTools, tool => tool.Name == "runtime.knowledge_read"));
        var seen = rig.Model.Requests[6].History.Last(turn => turn.Role == BopsChatRole.Tool).Content;
        Assert.Contains("withdrawn for the rest of this attempt", seen, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACallAfterTheWithdrawal_StillEndsTheTask_AsRuntimeFailure_TheLoopProtectionRemains()
    {
        using var world = new KnowledgeRunnerWorld();
        using var process = Boot(world, new TestPack(), new KnowledgeReadBudget(MaxReads: 4, MaxRejections: 1, MaxCharactersPerRead: 8000));
        var rig = process.Runner(
            [Script.Plan("test.read"),
             Script.Knowledge("k1", "load", "no-such-skill"), Script.Knowledge("k2", "load", "no-such-skill"), // the second withdraws
             Script.Knowledge("k3", "load", "nginx-triage"), Script.Knowledge("k4", "load", "nginx-triage"), Script.Knowledge("k5", "load", "nginx-triage")]);

        var final = await rig.Runner.RunAsync(Goal, KnowledgeRunnerWorld.ActorA);

        Assert.Equal(AgentTaskStatus.Failed, final.Status);
        Assert.Equal(TaskTerminalKind.RuntimeFailure, final.TerminalReason!.Kind);
        Assert.Equal(0, rig.Read.ExecutionCount);
        Assert.Equal(
            [KnowledgeReadResultCode.UnknownSkill, KnowledgeReadResultCode.UnknownSkill, KnowledgeReadResultCode.NotOffered, KnowledgeReadResultCode.NotOffered, KnowledgeReadResultCode.NotOffered],
            world.Events.OfType<KnowledgeReadAuditEvent>().Select(read => read.ResultCode));
        Assert.DoesNotContain(rig.Model.Requests.SelectMany(request => request.History), turn => turn.Content?.Contains("ss -ltnp", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task AReadLoopOnOneItem_IsBoundedByTheBudget_AndTheTaskStillCompletes()
    {
        using var world = new KnowledgeRunnerWorld();
        using var process = Boot(world, new TestPack(), new KnowledgeReadBudget(MaxReads: 2, MaxRejections: 4, MaxCharactersPerRead: 8000));
        var rig = process.Runner(
            [Script.Plan("test.read"),
             Script.Knowledge("k1", "load", "nginx-triage"), Script.Knowledge("k2", "load", "nginx-triage"), // spends the budget
             Script.Knowledge("k3", "load", "nginx-triage"), Script.Knowledge("k4", "load", "nginx-triage"), // not offered: tolerated
             Script.Tool("c1", "test.read"), Script.Final("done")]);

        var final = await rig.Runner.RunAsync(Goal, KnowledgeRunnerWorld.ActorA);

        Assert.Equal(AgentTaskStatus.Completed, final.Status);
        Assert.Equal(1, rig.Read.ExecutionCount);
        var reads = world.Events.OfType<KnowledgeReadAuditEvent>().ToArray();
        Assert.Equal(
            [KnowledgeReadResultCode.Success, KnowledgeReadResultCode.Success, KnowledgeReadResultCode.NotOffered, KnowledgeReadResultCode.NotOffered],
            reads.Select(read => read.ResultCode));
        Assert.Equal([false, true], reads.Take(2).Select(read => read.Repeat));
    }

    [Fact]
    public async Task AWithdrawnAuthority_IsASecuritySignal_NotARecoverableRefusal_AndKeepsTheHardStop()
    {
        using var world = new KnowledgeRunnerWorld();
        using var process = Boot(world, new TestPack(), new KnowledgeReadBudget(MaxReads: 4, MaxRejections: 1, MaxCharactersPerRead: 8000));
        var rig = process.Runner(
            [Script.Plan("test.read"), Script.Knowledge("k1", "discover"), Script.Knowledge("k2", "load", "nginx-triage"), Script.Knowledge("k3", "load", "nginx-triage")],
            beforeResponse: index =>
            {
                if (index == 2)
                {
                    Assert.True(process.Registry.Unregister(process.ProviderId));
                }
            });

        var final = await rig.Runner.RunAsync(Goal, KnowledgeRunnerWorld.ActorA);

        Assert.Equal(AgentTaskStatus.Failed, final.Status);
        Assert.Equal(TaskTerminalKind.RuntimeFailure, final.TerminalReason!.Kind);
        var reads = world.Events.OfType<KnowledgeReadAuditEvent>().ToArray();
        Assert.Equal([KnowledgeReadResultCode.Success, KnowledgeReadResultCode.NotAuthorized, KnowledgeReadResultCode.NotAuthorized], reads.Select(read => read.ResultCode));
        Assert.All(reads, read => Assert.Null(read.Withdrawn)); // no security refusal is ever recoverable
        Assert.DoesNotContain(rig.Model.Requests.SelectMany(request => request.History), turn => turn.Content?.Contains("ss -ltnp", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task TheWithdrawalFlag_IsAuditedWithoutContent_AndSerializesOnlyOnThatEvent()
    {
        var audit = new RecordingAuditSink();
        using var bridge = await OpenAsync(new Mutable(Checklist), audit, new KnowledgeReadBudget(MaxReads: 4, MaxRejections: 1));
        await bridge.HandleAsync(Refused("unknown-skill", "a"), Authority(), Context());
        await bridge.HandleAsync(Refused("unknown-skill", "b"), Authority(), Context());

        var json = audit.Events.OfType<KnowledgeReadAuditEvent>().Select(evt => JsonSerializer.Serialize(evt)).ToArray();

        Assert.DoesNotContain("Withdrawn", json[0], StringComparison.Ordinal);
        Assert.Contains("\"Withdrawn\":true", json[1], StringComparison.Ordinal);
        Assert.All(json, line => Assert.DoesNotContain(Checklist, line, StringComparison.Ordinal));
    }

    // ---- fixtures ----

    private static KnowledgeReadResultCode ExpectedCode(string cause) => cause switch
    {
        "malformed" => KnowledgeReadResultCode.Malformed,
        "unknown-skill" => KnowledgeReadResultCode.UnknownSkill,
        "unknown-resource" => KnowledgeReadResultCode.UnknownResource,
        "out-of-range" => KnowledgeReadResultCode.OutOfRange,
        "unsupported" => KnowledgeReadResultCode.Unsupported,
        "too-large" => KnowledgeReadResultCode.TooLarge,
        _ => throw new ArgumentOutOfRangeException(nameof(cause)),
    };

    private static ModelResponse Refused(string cause, string id) => cause switch
    {
        "malformed" => Raw(id, new JsonObject { ["skill"] = "nginx-triage" }), // no operation
        "unknown-skill" => Raw(id, new JsonObject { ["operation"] = "load", ["skill"] = "no-such-skill" }),
        "unknown-resource" => Raw(id, new JsonObject { ["operation"] = "read_resource", ["skill"] = "nginx-triage", ["resource"] = "no-such-resource" }),
        "out-of-range" => Raw(id, new JsonObject { ["operation"] = "discover", ["page"] = 9 }),
        "unsupported" => new ModelResponse(null, [new ModelToolCall(id, "load_skill", ToolArguments.Empty)], false, Usage),
        "too-large" => Raw(id, new JsonObject { ["operation"] = "load", ["skill"] = "nginx-triage" }),
        _ => throw new ArgumentOutOfRangeException(nameof(cause)),
    };

    private static ModelResponse ReadResource(string id) =>
        Raw(id, new JsonObject { ["operation"] = "read_resource", ["skill"] = "nginx-triage", ["resource"] = "checklist" });

    private static ModelResponse Raw(string id, JsonObject arguments) =>
        new(null, [new ModelToolCall(id, KnowledgeReadBridge.PublicToolName, ToolArguments.FromJson(arguments))], false, Usage);

    private static string Reply(KnowledgeReadOutcome outcome) => outcome.ContinuationTurns[^1].Content ?? string.Empty;

    private static KnowledgeReadAuthority Authority() => new(["test.read", KnowledgeReadBridge.PublicToolName], "test.read", true);

    private static ModelInvocationContext Context() => new(Guid.NewGuid(), 1, ActorIdentity.FromOperatingSystemUser("refusal-test"), []);

    /// <summary>A bridge over a two-skill fixture pack whose one resource reads from <paramref name="content"/>, so a test can alter or break it after the pin.</summary>
    private static async Task<KnowledgeReadBridge> OpenAsync(Mutable content, RecordingAuditSink audit, KnowledgeReadBudget? budget = null)
    {
        var pack = new FixtureSkillPack("refusal-pack", "1.0.0",
        [
            new FixtureSkill("nginx-triage", "nginx 502 triage", "1. read resource checklist", new Dictionary<string, string> { ["checklist"] = Checklist }),
            new FixtureSkill("disk-cleanup", "free space safely", "1. list the biggest folders", new Dictionary<string, string>()),
        ]);
        var pin = await KnowledgeReadBridge.PinFixtureAsync(pack);
        var nginx = new AgentInlineSkill(new AgentSkillFrontmatter("nginx-triage", "nginx 502 triage"), "1. read resource checklist");
        nginx.AddResource("checklist", (Func<string>)(() =>
        {
            if (content.Fail)
            {
                throw new IOException("the snapshot disk failed");
            }

            return content.Value;
        }));
        var disk = KnowledgeReadBridge.BuildSkills(pack with { Skills = [pack.Skills[1]] });
        return await KnowledgeReadBridge.CreateFromSourceAsync(
            ExperimentalProviderAdmission.ByTestHost("bops-test-host"), pin, new AgentInMemorySkillsSource([nginx, .. disk]), audit, budget: budget);
    }

    private sealed class Mutable(string value)
    {
        public string Value { get; set; } = value;

        public bool Fail { get; set; }
    }

    /// <summary>A world with the pack granted to tenant-a, bound to its tasks, and available from the source.</summary>
    private static KnowledgeProcess Boot(KnowledgeRunnerWorld world, TestPack pack, KnowledgeReadBudget? budget = null)
    {
        var raw = pack.Build();
        world.Source.Add(KnowledgeRunnerWorld.TenantA, pack.PackId, pack.Version, () => raw);
        var reference = new PackReference(pack.PackId, pack.Version);
        return world.Boot(
            [new PackGrant(KnowledgeRunnerWorld.TenantA, reference, pack.Kind)],
            bindings: new Dictionary<TenantId, PackReference> { [KnowledgeRunnerWorld.TenantA] = reference },
            budget: budget);
    }
}
