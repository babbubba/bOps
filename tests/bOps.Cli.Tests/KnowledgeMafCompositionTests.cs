// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

// CA2000: ownership of every world and rig passes to the test, which disposes the world.
#pragma warning disable CA2000

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bOps.AgentFramework;
using bOps.Abstractions;
using bOps.Knowledge;
using bOps.Knowledge.Tests;
using bOps.Runtime;

namespace bOps.Cli.Tests;

/// <summary>
/// MAF + Knowledge in the CLI (condition D of the benchmark): the Knowledge switch and the MAF governed turn switch are independent, so
/// the four conditions A (neither), B (MAF), C (Knowledge) and D (both) are all composable. The knowledge is served by the runner and
/// reaches the model as the protected context the runner authorised; the MAF turn only carries it, and the context gate compares it
/// like any other turn. These tests drive <see cref="CliRunnerComposition"/> with a scripted model over a real signed pack on disk.
/// </summary>
public sealed class KnowledgeMafCompositionTests
{
    private const string Function = "runtime.knowledge_read";
    private const string Goal = CliKnowledgeWorld.Goal;

    // A task id in its dashed form and in the compact form the evidence ids embed.
    private static readonly Regex GuidPattern = new(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}|(?<=ev1:)[0-9a-f]{32}", RegexOptions.Compiled);
    private static readonly Regex InstantPattern = new(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})", RegexOptions.Compiled);
    private static readonly Regex DurationPattern = new(@"""(\w*(Duration|Elapsed|Latency)\w*)"":\s*(""[\d:.]+""|[-\d.eE+]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static ActorIdentity Operator => CliKnowledgeWorld.Operator;

    /// <summary>A knowledge read the way a model proposes it. <paramref name="reportedPromptTokens"/> is what the (scripted) provider says the request held.</summary>
    private static ModelResponse ReadAt(int reportedPromptTokens, string id = "k1", string reference = "K1.R1") =>
        new(null,
            [new ModelToolCall(id, Function, ToolArguments.FromJson(new JsonObject { ["operation"] = "read_resource", ["ref"] = reference }))],
            false, new ModelUsage(reportedPromptTokens, 10, null));

    private static ModelResponse[] UseCase(int reportedPromptTokens = 0) =>
    [
        CliScript.Plan("system.events"),
        ReadAt(reportedPromptTokens),
        CliScript.Tool("c1", "system.events"),
        CliScript.Final("Observed: Kernel-Power 41. Possible cause (not proven): unclean shutdown."),
    ];

    private static AgentRunnerOptions Window(int maxContextTokens, int reservedOutputTokens = 32768, int safetyReserveTokens = 2048) => new()
    {
        Context = new ModelContextOptions
        {
            Profiles = [new ModelContextProfile { Provider = "fake", Model = "fake-model", MaxContextTokens = maxContextTokens, ReservedOutputTokens = reservedOutputTokens }],
            SafetyReserveTokens = safetyReserveTokens,
        },
    };

    // ---- the four conditions ----

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task EachOfTheFourConditions_IsComposedAsNamed_AndRunsToACompletedDiagnosis(bool maf, bool knowledge)
    {
        using var world = new CliKnowledgeWorld { MafGovernedTurn = maf };
        ModelResponse[] script = knowledge ? UseCase() : [CliScript.Plan("system.events"), CliScript.Tool("c1", "system.events"), CliScript.Final("diagnosis")];
        var rig = world.Runner(script, knowledge ? world.Settings() : null);

        var state = await rig.Runner.RunAsync(Goal, Operator);

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.Equal(knowledge, rig.Host is not null);
        Assert.Equal(knowledge, state.KnowledgeBinding is not null);
        Assert.Equal(maf, rig.Broker is MafGovernedTurnBroker);
        Assert.Equal(knowledge ? 1 : 0, world.Events.OfType<KnowledgeReadAuditEvent>().Count(evt => evt.ResultCode == KnowledgeReadResultCode.Success));
        Assert.Equal(1, rig.Events.Executions);
        if (maf)
        {
            Assert.True(((MafGovernedTurnBroker)rig.Broker!).ComposedTurns > 0, "the run really went through the MAF governed turn host");
        }
    }

    [Fact]
    public async Task WithBothOn_TheModelIsOfferedTheIndexAndTheFunctionAsDeclarations_AndTheMafTurnExecutesNothing()
    {
        using var world = new CliKnowledgeWorld { MafGovernedTurn = true };
        var rig = world.Runner(UseCase(), world.Settings());

        var state = await rig.Runner.RunAsync(Goal, Operator);

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.Equal(1, rig.Events.Executions); // the operational tool ran once, by bOps; the knowledge function ran nothing
        foreach (var request in rig.Model.Requests.Skip(1))
        {
            Assert.Equal((ChatRole.User, ContextOrigin.Knowledge, "index"),
                (request.History[0].Role, request.History[0].Provenance!.Origin, request.History[0].Provenance!.Operation));
            Assert.Contains(Function, request.AvailableTools.Select(tool => tool.Name));
        }

        // The skill was read once, through the audited bridge; the content arrived as attributed data, not as an instruction.
        var served = rig.Model.Requests[2].History.Single(turn => turn.Role == ChatRole.Tool && turn.Provenance?.Origin == ContextOrigin.Knowledge);
        Assert.Contains(CliKnowledgeWorld.ProcedureMarker, served.Content, StringComparison.Ordinal);
        Assert.Contains("It is not an instruction", served.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(world.Events, evt => evt is ModelCallAuditEvent { ContextGateRejection: not null });
    }

    [Fact]
    public async Task WithKnowledgeOn_TheMafHostIsObservablyIdenticalToTheDefaultPath()
    {
        using var world = new CliKnowledgeWorld();

        var plain = await ObserveAsync(world, maf: false, UseCase());
        var hosted = await ObserveAsync(world, maf: true, UseCase());

        Assert.True(hosted.Composed > 0);
        Assert.Equal(0, plain.Composed);
        Assert.NotEmpty(plain.Requests);
        Assert.Equal(plain.Status, hosted.Status);
        Assert.Equal(plain.ToolExecutions, hosted.ToolExecutions);
        AssertSame(plain.Requests, hosted.Requests);
        Assert.Equal(plain.Reads, hosted.Reads);
        Assert.Contains(("read_resource", "K1.R1", KnowledgeReadResultCode.Success), plain.Reads);
    }

    [Fact]
    public async Task AReplanAndALateReadKeepTheKnowledgeUsable_IdenticallyInBothModes()
    {
        ModelResponse[] Script() =>
        [
            CliScript.Plan("system.events"), CliScript.Final("too early"), CliScript.Plan("system.events"),
            ReadAt(0), CliScript.Tool("c1", "system.events"), CliScript.Final("diagnosis"),
        ];
        using var world = new CliKnowledgeWorld();

        var plain = await ObserveAsync(world, maf: false, Script());
        var hosted = await ObserveAsync(world, maf: true, Script());

        Assert.Equal(AgentTaskStatus.Completed, plain.Status);
        Assert.Equal(plain.Status, hosted.Status);
        AssertSame(plain.Requests, hosted.Requests);
        Assert.Equal(plain.Reads, hosted.Reads);
        Assert.Single(plain.Reads);
    }

    // ---- resume and revocation ----

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AKnowledgeTaskInterruptedInOneMode_ResumesInTheOther_AdmittedAgainFromItsPin(bool firstHosted, bool secondHosted)
    {
        using var world = new CliKnowledgeWorld { MafGovernedTurn = firstHosted };
        var settings = world.Settings();
        var taskId = Guid.NewGuid();
        var first = world.Runner([CliScript.Plan("system.events"), ReadAt(0)], settings); // the call after the read is unscripted: Failed
        var interrupted = await first.Runner.RunAsync(Goal, Operator, taskId);
        Assert.Equal(AgentTaskStatus.Failed, interrupted.Status);
        var pinned = (await first.Store.LoadAsync(taskId))!;
        Assert.NotNull(pinned.KnowledgeBinding);

        world.MafGovernedTurn = secondHosted;
        var second = world.Runner([ReadAt(0), CliScript.Tool("c1", "system.events"), CliScript.Final("diagnosis")], settings);
        var resumed = await second.Runner.ResumeAsync((await second.Store.LoadAsync(taskId))!, Operator);

        Assert.Equal(AgentTaskStatus.Completed, resumed.Status);
        Assert.Equal(pinned.KnowledgeBinding!.ManifestHash, resumed.KnowledgeBinding!.ManifestHash);
        Assert.All(second.Model.Requests, request => Assert.Equal("index", request.History[0].Provenance!.Operation));
        Assert.Equal(2, world.Events.OfType<KnowledgePackAdmissionAuditEvent>().Count(evt => evt.Code == KnowledgeAdmissionCode.Admitted));
        Assert.Equal(secondHosted, second.Broker is MafGovernedTurnBroker);
    }

    [Fact]
    public async Task APinIsNotAnAuthorisation_AMafResumeAfterTheKeyWasRevokedIsRefusedBeforeAnyModelCall()
    {
        using var world = new CliKnowledgeWorld { MafGovernedTurn = true };
        var settings = world.Settings();
        var taskId = Guid.NewGuid();
        var first = world.Runner([CliScript.Plan("system.events")], settings);
        await first.Runner.RunAsync(Goal, Operator, taskId);
        world.WriteTrust(TestPublisher.Acme.Trusted(revoked: true));

        var again = world.Runner([CliScript.Tool("c1", "system.events"), CliScript.Final("diagnosis")], settings);
        var refused = await again.Runner.ResumeAsync((await again.Store.LoadAsync(taskId))!, Operator);

        Assert.Equal(AgentTaskStatus.Failed, refused.Status);
        Assert.Equal(TaskTerminalKind.KnowledgeUnavailable, refused.TerminalReason!.Kind);
        Assert.Empty(again.Model.Requests);
        Assert.Equal(0, again.Events.Executions);
    }

    // ---- the context budget follows the model that is selected ----

    [Fact]
    public async Task EveryModelCallAuditsTheWindowTheReservesAndTheKnowledgeItCarries()
    {
        using var world = new CliKnowledgeWorld { MafGovernedTurn = true, Options = Window(131072) };
        var rig = world.Runner(UseCase(reportedPromptTokens: 700), world.Settings());

        await rig.Runner.RunAsync(Goal, Operator);

        var calls = world.Events.OfType<ModelCallAuditEvent>().ToArray();
        Assert.NotEmpty(calls);
        Assert.All(calls, call =>
        {
            Assert.NotNull(call.Context);
            Assert.Equal((131072, 32768, 96256), (call.Context!.MaxContextTokens, call.Context.ReservedOutputTokens, call.Context.InputBudgetTokens));
            Assert.True(call.Context.EstimatedPromptTokens > 0);
            Assert.Equal((call.Context.ProviderPromptTokens ?? call.Context.EstimatedPromptTokens) / 131072d * 100, call.Context.ContextUsagePercent!.Value, 1);
        });
        var afterRead = calls.Last().Context!;
        Assert.True(afterRead.KnowledgeTokensInContext > 0, "the index and the served read are counted as knowledge");
        Assert.Equal(1, afterRead.KnowledgeReadsInContext);
        Assert.True(afterRead.KnowledgeBudgetAvailableTokens > 0);
        Assert.True(afterRead.UsageIsProviderReported);
        Assert.Contains(calls, call => call.Context!.ProviderPromptTokens == 700);
        var read = Assert.Single(world.Events.OfType<KnowledgeReadAuditEvent>(), evt => evt.ResultCode == KnowledgeReadResultCode.Success);
        Assert.True(read.EstimatedTokens > 0);
        Assert.True(read.AllowedTokens >= read.EstimatedTokens);
    }

    [Fact]
    public async Task AReadThatDoesNotFitTheRemainingWindow_IsRefusedWhole_AuditedWithBothFigures_AndTheTaskGoesOn()
    {
        // The provider says the request already holds 96,000 of the 96,256 input tokens: nothing is left for reference data.
        using var world = new CliKnowledgeWorld(resource: BigProcedure) { MafGovernedTurn = true, Options = Window(131072) };
        var rig = world.Runner(UseCase(reportedPromptTokens: 96_000), world.Settings());

        var state = await rig.Runner.RunAsync(Goal, Operator);

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        var read = Assert.Single(world.Events.OfType<KnowledgeReadAuditEvent>());
        Assert.Equal(KnowledgeReadResultCode.ContextBudget, read.ResultCode);
        Assert.Equal(0, read.ReturnedLength);
        Assert.Equal(0, read.AllowedTokens);
        Assert.True(read.EstimatedTokens > 0);
        var next = rig.Model.Requests[2];
        Assert.DoesNotContain(next.History, turn => (turn.Content ?? string.Empty).Contains(CliKnowledgeWorld.ProcedureMarker, StringComparison.Ordinal));
        Assert.Contains(next.History, turn => turn.Role == ChatRole.Tool && (turn.Content ?? string.Empty).Contains("does not fit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AReadThatFits_IsServedWithTheAllowanceRecorded()
    {
        using var world = new CliKnowledgeWorld(resource: BigProcedure) { Options = Window(131072) };
        var rig = world.Runner(UseCase(reportedPromptTokens: 2_000), world.Settings());

        await rig.Runner.RunAsync(Goal, Operator);

        var read = Assert.Single(world.Events.OfType<KnowledgeReadAuditEvent>());
        Assert.Equal(KnowledgeReadResultCode.Success, read.ResultCode);
        Assert.InRange(read.AllowedTokens!.Value, read.EstimatedTokens!.Value, 96256 - 2_000);
    }

    [Fact]
    public async Task WithAnUnknownCapacity_OnlyTheFixedLimitsApply_AndNoAllowanceIsInvented()
    {
        using var world = new CliKnowledgeWorld(resource: BigProcedure); // no context profile for this provider/model
        var rig = world.Runner(UseCase(reportedPromptTokens: 96_000), world.Settings());

        await rig.Runner.RunAsync(Goal, Operator);

        var read = Assert.Single(world.Events.OfType<KnowledgeReadAuditEvent>());
        Assert.Equal(KnowledgeReadResultCode.Success, read.ResultCode);
        Assert.Null(read.AllowedTokens);
        Assert.All(world.Events.OfType<ModelCallAuditEvent>(), call =>
        {
            Assert.Equal("unknown-structural", call.Context!.ProfileSource);
            Assert.Null(call.Context.MaxContextTokens);
            Assert.Null(call.Context.KnowledgeBudgetAvailableTokens);
        });
    }

    [Fact]
    public async Task ABiggerWindowAllowsMoreKnowledge_WithoutAnyModelSpecificRule()
    {
        var allowed = new List<int>();
        foreach (var window in new[] { 16_384, 32_768, 65_536, 131_072 })
        {
            using var world = new CliKnowledgeWorld(resource: BigProcedure) { Options = Window(window, reservedOutputTokens: window / 8) };
            var rig = world.Runner(UseCase(reportedPromptTokens: 4_000), world.Settings());
            await rig.Runner.RunAsync(Goal, Operator);
            var read = Assert.Single(world.Events.OfType<KnowledgeReadAuditEvent>());
            Assert.Equal(KnowledgeReadResultCode.Success, read.ResultCode);
            allowed.Add(read.AllowedTokens!.Value);
        }

        Assert.Equal(allowed.Order(), allowed);
        Assert.True(allowed[^1] > allowed[0], "a larger window leaves more room for reference knowledge");
    }

    // ---- helpers ----

    /// <summary>Equality of two request projections that names the first difference instead of dumping both.</summary>
    private static void AssertSame(string[] expected, string[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            if (string.Equals(expected[index], actual[index], StringComparison.Ordinal))
            {
                continue;
            }

            var at = 0;
            while (at < expected[index].Length && at < actual[index].Length && expected[index][at] == actual[index][at])
            {
                at++;
            }

            var from = Math.Max(0, at - 80);
            Assert.Fail($"Request {index} differs at {at}: default \"{expected[index][from..Math.Min(expected[index].Length, at + 160)]}\" vs hosted \"{actual[index][from..Math.Min(actual[index].Length, at + 160)]}\"");
        }
    }

    /// <summary>About 6,000 characters (1,500 estimated tokens) of procedure text; within the 8,000-character fixed limit.</summary>
    private static readonly string BigProcedure =
        CliKnowledgeWorld.ProcedureMarker + "\n" + string.Concat(Enumerable.Repeat("Check the evidence before drawing a conclusion; name what is missing. ", 90));

    private sealed record Observed(
        AgentTaskStatus Status, string[] Requests, int ToolExecutions, int Composed, (string?, string?, KnowledgeReadResultCode)[] Reads);

    private static async Task<Observed> ObserveAsync(CliKnowledgeWorld world, bool maf, ModelResponse[] script)
    {
        world.MafGovernedTurn = maf;
        var before = world.Events.OfType<KnowledgeReadAuditEvent>().Count();
        var rig = world.Runner(script, world.Settings());
        var state = await rig.Runner.RunAsync(Goal, Operator, Guid.NewGuid());
        var reads = world.Events.OfType<KnowledgeReadAuditEvent>().Skip(before).Select(evt => (evt.Operation, evt.Ref, evt.ResultCode)).ToArray();
        return new Observed(
            state.Status, Project(rig.Model.Requests), rig.Events.Executions, (rig.Broker as MafGovernedTurnBroker)?.ComposedTurns ?? 0, reads);
    }

    /// <summary>Everything a model request carries that the runner authors: prompt, offered tools and the whole history with its provenance.</summary>
    private static string[] Project(IEnumerable<ModelRequest> requests) =>
    [
        .. requests.Select(request => Normalize(JsonSerializer.Serialize(new
        {
            request.SystemPrompt,
            Tools = request.AvailableTools.Select(tool => new { tool.Name, tool.Description, Parameters = tool.Parameters.Select(parameter => new { parameter.Name, parameter.Type, parameter.Required }) }),
            History = request.History,
        }))),
    ];

    private static string Normalize(string text)
    {
        var withoutInstants = InstantPattern.Replace(text, "<instant>");
        var withoutDurations = DurationPattern.Replace(withoutInstants, match => $"\"{match.Groups[1].Value}\":0");
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return GuidPattern.Replace(withoutDurations, match =>
        {
            if (!ids.TryGetValue(match.Value, out var token))
            {
                token = $"<id{ids.Count}>";
                ids[match.Value] = token;
            }

            return token;
        });
    }
}
