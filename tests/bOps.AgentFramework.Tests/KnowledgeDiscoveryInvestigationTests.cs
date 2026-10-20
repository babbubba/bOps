// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

// CA2000: ownership of every world, client and transport passes to the test, which disposes them.
#pragma warning disable CA2000

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Knowledge;
using bOps.Knowledge.Tests;
using bOps.Packages.Providers.LlamaCpp;

namespace bOps.AgentFramework.Tests;

/// <summary>
/// KNOW-DISC-INV01: can the benchmark model discover, select and read the knowledge of condition B, with the exact FIX12 composition
/// (signed pack <c>acme.windows-event-runbooks@1.0.0</c>, scenario goal, governed replay, runner options) at this commit? Scripted
/// providers only: no model, server, capture or network. Each case pins one of the six discovery outcomes the harness now exports.
/// </summary>
public sealed class KnowledgeDiscoveryInvestigationTests
{
    private const string Function = "runtime.knowledge_read";
    private const string Skill = "windows-event-triage";
    private const string Description = "Procedure di interpretazione delle evidenze diagnostiche nei log Windows";

    [WindowsOnlyFact]
    public async Task B_EveryStepRequestPresentsTheIndexAndOffersTheFunction_PlanningSeesNothing_ANonReadingReplyIsOfferedNotSelected()
    {
        using var temporary = new TempDirectory();
        using var run = await RunAsync(temporary, "B", [KnowledgeBenchmark.Plan, KnowledgeBenchmark.Events, Script.Final("offline done")]);

        Assert.Equal(AgentTaskStatus.Completed, run.State.Status);
        // Plan, operational step, final step; the runner may add a bounded correction request after the final answer.
        Assert.True(run.Probe.Requests.Count >= 3);

        // Planning: no function, no index, no control text; the plan can only name system.events (ADR-0057 H3).
        var planning = run.Probe.Requests[0];
        Assert.Empty(planning.AvailableTools);
        Assert.DoesNotContain(planning.History, turn => turn.Provenance?.Operation == "index");
        Assert.DoesNotContain(Skill, planning.SystemPrompt, StringComparison.Ordinal);
        Assert.Equal("system.events", Assert.Single(Assert.Single(run.State.Plans).Steps).ExpectedTool);

        // Every step request (the operational step and the final step) carries the index before the goal and offers the function.
        foreach (var step in run.Probe.Requests.Skip(1))
        {
            Assert.Contains(step.AvailableTools, tool => tool.Name == Function);
            var index = step.History[0];
            Assert.Equal((ChatRole.User, ContextOrigin.Knowledge, "index"), (index.Role, index.Provenance!.Origin, index.Provenance.Operation));
            Assert.Contains($"\"skill\":\"{Skill}\"", index.Content, StringComparison.Ordinal);
            Assert.Contains(Description, index.Content, StringComparison.Ordinal);
            Assert.Contains("\"call\":{\"operation\":\"read_resource\",\"ref\":\"K1.R1\"}", index.Content, StringComparison.Ordinal);
            Assert.Equal(ContextProvenance.Task, step.History[1].Provenance);
            Assert.Contains("Listed: 1 of 1 authorized entries (K1); 0 not listed.", step.SystemPrompt, StringComparison.Ordinal);
            Assert.Contains($"{Function} may be called, only if useful", step.SystemPrompt, StringComparison.Ordinal);
            Assert.DoesNotContain(Description, step.SystemPrompt, StringComparison.Ordinal);
        }

        // The two imperatives the model weighs against the optional read, at this commit.
        Assert.Contains("Invoke the operational tool at most once", run.Probe.Requests[1].SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("Give your final answer now from the evidence already collected", run.Probe.Requests[2].SystemPrompt, StringComparison.Ordinal);

        var published = Assert.Single(run.World.Events.OfType<KnowledgeIndexAuditEvent>());
        Assert.Equal(KnowledgeIndexStatus.Published, published.Status);
        var listing = Assert.Single(published.Listed);
        Assert.Equal(("K1", Skill, 1), (listing.Ref, listing.Skill, listing.Resources));
        Assert.True(listing.Score > 0, "The scenario goal shares tokens with the skill's name and description.");

        var discovery = run.Discovery();
        Assert.Equal("Presented", discovery["indexStage"]!.GetValue<string>());
        Assert.Equal("OfferedNotSelected", discovery["readStage"]!.GetValue<string>());
        Assert.Equal(0, discovery["planningRequestsWithKnowledge"]!.GetValue<int>());
        Assert.Equal(run.Probe.Requests.Count - 1, discovery["stepRequests"]!.GetValue<int>());
        Assert.Equal(run.Probe.Requests.Count - 1, discovery["stepRequestsWithIndex"]!.GetValue<int>());
        Assert.Equal(run.Probe.Requests.Count - 1, discovery["stepRequestsOfferingFunction"]!.GetValue<int>());
        Assert.Equal(published.IndexHash, discovery["requests"]![1]!["indexHash"]!.GetValue<string>());
        Assert.Empty(run.World.Events.OfType<KnowledgeReadAuditEvent>());
    }

    [WindowsOnlyFact]
    public async Task B_TheListedRefAloneReadsTheProcedure_InOneGovernedCall_WithoutTheSkillName()
    {
        using var temporary = new TempDirectory();
        var byRef = Script.KnowledgeRaw("k1", new JsonObject { ["operation"] = "read_resource", ["ref"] = "K1.R1" });
        using var run = await RunAsync(temporary, "B", [KnowledgeBenchmark.Plan, byRef, KnowledgeBenchmark.Events, Script.Final("offline done")]);

        Assert.Equal(AgentTaskStatus.Completed, run.State.Status);
        var read = Assert.Single(run.World.Events.OfType<KnowledgeReadAuditEvent>());
        Assert.Equal((KnowledgeReadResultCode.Success, "read_resource", "K1.R1"), (read.ResultCode, read.Operation, read.Ref));
        var served = Assert.Single(run.Probe.Requests[2].History, turn => turn.Role == ChatRole.Tool && turn.Provenance?.Origin == ContextOrigin.Knowledge);
        Assert.Contains("Kernel-Power 41 registra un riavvio", served.Content, StringComparison.Ordinal);
        Assert.Single(run.World.Events.OfType<ToolCallAuditEvent>(), call => call.Tool == "system.events" && call.Outcome == ToolOutcome.Success);

        var discovery = run.Discovery();
        Assert.Equal("ReadSucceeded", discovery["readStage"]!.GetValue<string>());
        var proposal = discovery["requests"]![1]!["proposals"]![0]!;
        Assert.Equal(("read_resource", "K1.R1", false), (proposal["operation"]!.GetValue<string>(), proposal["ref"]!.GetValue<string>(), proposal["byName"]!.GetValue<bool>()));
        Assert.False(discovery["reads"]![0]!["byName"]!.GetValue<bool>());
    }

    [WindowsOnlyFact]
    public async Task B_AnUnlistedRefIsRefusedAndAudited_TheTaskContinues_ReadRefused()
    {
        using var temporary = new TempDirectory();
        var unlisted = Script.KnowledgeRaw("k9", new JsonObject { ["operation"] = "read_resource", ["ref"] = "K9.R1" });
        using var run = await RunAsync(temporary, "B", [KnowledgeBenchmark.Plan, unlisted, KnowledgeBenchmark.Events, Script.Final("offline done")]);

        Assert.Equal(AgentTaskStatus.Completed, run.State.Status);
        var refused = Assert.Single(run.World.Events.OfType<KnowledgeReadAuditEvent>());
        Assert.NotEqual(KnowledgeReadResultCode.Success, refused.ResultCode);
        Assert.Equal("ReadRefused", run.Discovery()["readStage"]!.GetValue<string>());
    }

    [WindowsOnlyFact]
    public async Task A_HasNeitherIndexNorFunction_AndIsClassifiedNotBound()
    {
        using var temporary = new TempDirectory();
        using var run = await RunAsync(temporary, "A", [KnowledgeBenchmark.Plan, KnowledgeBenchmark.Events, Script.Final("offline done")]);

        Assert.Equal(AgentTaskStatus.Completed, run.State.Status);
        Assert.All(run.Probe.Requests, request =>
        {
            Assert.DoesNotContain(request.AvailableTools, tool => tool.Name == Function);
            Assert.DoesNotContain(request.History, turn => turn.Provenance?.Operation == "index");
        });
        var discovery = run.Discovery();
        Assert.Equal(("NotBound", "NotBound"), (discovery["indexStage"]!.GetValue<string>(), discovery["readStage"]!.GetValue<string>()));
    }

    [WindowsOnlyFact]
    public async Task C_DiffersFromBOnlyInTheOperatorGoal_WhichNamesNoSkillPackOrRef()
    {
        Assert.StartsWith(KnowledgeBenchmark.Goal.TrimEnd(), KnowledgeBenchmark.GoalFor("C"), StringComparison.Ordinal);
        Assert.Equal(KnowledgeBenchmark.Goal, KnowledgeBenchmark.GoalFor("B"));
        foreach (var named in new[] { Skill, KnowledgeBenchmark.Reference.PackId, "K1", Function, "interpretation" })
        {
            Assert.DoesNotContain(named, KnowledgeBenchmark.GoalFor("C"), StringComparison.OrdinalIgnoreCase);
        }

        using var temporary = new TempDirectory();
        using var b = await RunAsync(temporary, "B", [KnowledgeBenchmark.Plan, KnowledgeBenchmark.Events, Script.Final("offline done")]);
        using var c = await RunAsync(temporary, "C", [KnowledgeBenchmark.Plan, KnowledgeBenchmark.Events, Script.Final("offline done")]);

        Assert.Equal(b.Probe.Requests.Count, c.Probe.Requests.Count);
        for (var i = 0; i < b.Probe.Requests.Count; i++)
        {
            var (rb, rc) = (b.Probe.Requests[i], c.Probe.Requests[i]);
            // Evidence ids are per task; everything else in the standing prompt is identical.
            Assert.Equal(TaskNeutral(rb.SystemPrompt), TaskNeutral(rc.SystemPrompt));
            Assert.Equal(rb.AvailableTools.Select(tool => tool.Name), rc.AvailableTools.Select(tool => tool.Name));
            var goalB = Assert.Single(rb.History, turn => turn.Provenance == ContextProvenance.Task);
            var goalC = Assert.Single(rc.History, turn => turn.Provenance == ContextProvenance.Task);
            Assert.Equal(KnowledgeBenchmark.GoalFor("B"), goalB.Content);
            Assert.Equal(KnowledgeBenchmark.GoalFor("C"), goalC.Content);
        }

        // Same pin and same single authorized entry: the same index reaches both arms.
        Assert.Equal(
            b.World.Events.OfType<KnowledgeIndexAuditEvent>().Single().IndexHash,
            c.World.Events.OfType<KnowledgeIndexAuditEvent>().Single().IndexHash);
    }

    [Fact]
    public void FIX12_ArtifactsMapToNotPublishedAndOfferedNotSelected()
    {
        // FIX12 ran 44696aa: the bridge was admitted (one knowledgePackAdmission event), runtime.knowledge_read was added to every step
        // view (B step prompts are ~204 tokens larger than A's while planning prompts are identical), no index type existed yet and
        // the typed audit holds no KnowledgeReadAuditEvent, which the bridge writes for every call it handles, refusals included.
        Assert.Equal("NotPublished", BenchmarkKnowledgeDiscovery.IndexStage(bound: true, [], stepRequests: 7, presented: 0));
        Assert.Equal("OfferedNotSelected", BenchmarkKnowledgeDiscovery.ReadStage(bound: true, offered: 7, proposals: 0, []));
        // Residual case FIX12 could not observe: a knowledge call placed after an evidence read in the same reply is never served.
        Assert.Equal("SelectedNotServed", BenchmarkKnowledgeDiscovery.ReadStage(bound: true, offered: 7, proposals: 1, []));
    }

    [WindowsOnlyTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OverTheRealAdapter_TheIndexPrecedesTheGoalOnTheWire_ARefReadRoundTrips_AndTheExportCarriesNoPublisherText(bool native)
    {
        using var temporary = new TempDirectory();
        var corpus = KnowledgeBenchmark.Synthetic();
        var hash = CanaryEventsCorpus.Hash(corpus);
        var path = KnowledgeBenchmark.WriteSynthetic(temporary, corpus);
        var (raw, trust) = await KnowledgeBenchmark.LoadAsync();
        var output = temporary.Sub(native ? "native" : "fallback");
        Directory.CreateDirectory(output);
        var requests = new List<JsonObject>();
        using var transport = new RefReadTransport(requests, native);
        await KnowledgeBenchmarkExport.RunAsync(output, "B", async export =>
        {
            export.World = new KnowledgeRunnerWorld();
            export.CorpusSha256 = hash;
            using var budget = new BenchmarkHttpBudget(transport);
            using var client = new HttpClient(budget, disposeHandler: false);
            export.ModelObservation.Http = budget;
            var wire = new LlamaCppProviderPackage(new ClientFactory(client)).Create(
                new ChatModelOptions("LlamaCpp", "http://offline.invalid/v1", null, KnowledgeBenchmark.ModelId, native) { RequestTimeout = TimeSpan.FromSeconds(120) });
            var rig = KnowledgeBenchmark.ComposeReplay(export.World, wire, "B", path, hash, raw, trust,
                KnowledgePackFormat.Sha256Hex(raw.ManifestBytes.Span), KnowledgeBenchmark.GrantAuthorization, export.ModelObservation);
            export.Store = rig.Store;
            await export.ExecuteTaskAsync(() => rig.Runner.RunAsync(KnowledgeBenchmark.Goal, KnowledgeRunnerWorld.ActorA, export.RunId));
            Assert.Equal(AgentTaskStatus.Completed, export.State!.Status);
        });

        // On the wire: planning has no index; the first step request has the index as a user message right before the goal.
        Assert.DoesNotContain(UserMessages(requests[0]), content => content.Contains("[knowledge-index v1", StringComparison.Ordinal));
        var step = UserMessages(requests[1]);
        var at = Array.FindIndex(step, content => content.StartsWith("[knowledge-index v1", StringComparison.Ordinal));
        Assert.True(at >= 0, "The index reached the provider.");
        Assert.Contains(Skill, step[at], StringComparison.Ordinal);
        Assert.Equal(KnowledgeBenchmark.Goal, step[at + 1]);

        var result = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(output, "B", "result.json")))!.AsObject();
        var discovery = result["knowledgeDiscovery"]!.AsObject();
        Assert.Equal(("Presented", "ReadSucceeded"), (discovery["indexStage"]!.GetValue<string>(), discovery["readStage"]!.GetValue<string>()));
        Assert.Equal("K1", discovery["indexAudit"]![0]!["listed"]![0]!["ref"]!.GetValue<string>());
        Assert.Equal(Skill, discovery["indexAudit"]![0]!["listed"]![0]!["skill"]!.GetValue<string>());
        Assert.Equal("K1.R1", discovery["reads"]![0]!["ref"]!.GetValue<string>());
        Assert.Equal(BenchmarkKnowledgeDiscovery.Sha256(KnowledgeBenchmark.Goal), result["goalSha256"]!.GetValue<string>());

        var artifacts = result.ToJsonString() + await File.ReadAllTextAsync(Path.Combine(output, "B", "audit.jsonl"));
        foreach (var text in new[] { Description, "Kernel-Power 41 registra", KnowledgeBenchmark.Goal.Trim()[..40], "[knowledge-index", "only if useful" })
        {
            Assert.DoesNotContain(text, artifacts, StringComparison.Ordinal);
        }
    }

    private sealed record Run(KnowledgeRunnerWorld World, BenchmarkProbe Probe, TaskState State, BenchmarkModelObservation Observation) : IDisposable
    {
        internal JsonObject Discovery() => Observation.Discovery.Snapshot([.. World.Events], State.KnowledgeBinding is not null);

        public void Dispose() => World.Dispose();
    }

    private static async Task<Run> RunAsync(TempDirectory temporary, string condition, IEnumerable<ModelResponse> script)
    {
        var corpus = KnowledgeBenchmark.Synthetic();
        var hash = CanaryEventsCorpus.Hash(corpus);
        var path = Path.Combine(temporary.Sub(".bops-event-corpus"), $"{condition}-{Guid.NewGuid():N}.bops-events.snapshot.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, CanaryEventsCorpus.Encode(corpus, hash));
        var world = new KnowledgeRunnerWorld();
        var probe = new BenchmarkProbe(script);
        var observation = new BenchmarkModelObservation();
        var knowledge = KnowledgeBenchmark.UsesKnowledge(condition);
        var (raw, trust) = knowledge ? await KnowledgeBenchmark.LoadAsync() : default;
        var rig = KnowledgeBenchmark.ComposeReplay(world, probe, condition, path, hash, raw, trust,
            knowledge ? KnowledgePackFormat.Sha256Hex(raw!.ManifestBytes.Span) : null,
            knowledge ? KnowledgeBenchmark.GrantAuthorization : null, observation);
        var state = await rig.Runner.RunAsync(KnowledgeBenchmark.GoalFor(condition), KnowledgeRunnerWorld.ActorA);
        return new Run(world, probe, state, observation);
    }

    private static string TaskNeutral(string prompt) =>
        System.Text.RegularExpressions.Regex.Replace(prompt, "ev[0-9]+:[0-9a-f]{8,}", "ev:<task>", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static string[] UserMessages(JsonObject request) => request["messages"]!.AsArray()
        .Where(m => m!["role"]!.GetValue<string>() == "user").Select(m => m!["content"]!.GetValue<string>()).ToArray();

    /// <summary>Plan, then a read by the listed ref, then system.events, then a final answer; wire names read from each request.</summary>
    private sealed class RefReadTransport(List<JsonObject> requests, bool native) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            int index;
            lock (requests)
            {
                requests.Add(body);
                index = requests.Count;
            }

            return index switch
            {
                1 => Text(native ? KnowledgeBenchmark.Plan.TextResponse! : new JsonObject { ["final"] = KnowledgeBenchmark.Plan.TextResponse }.ToJsonString()),
                2 => Call(body, Function, new JsonObject { ["operation"] = "read_resource", ["ref"] = "K1.R1" }),
                3 => Call(body, "system.events", KnowledgeBenchmark.Arguments.ToJson()),
                _ => Text(native ? "offline done" : new JsonObject { ["final"] = "offline done" }.ToJsonString()),
            };
        }

        private HttpResponseMessage Call(JsonObject body, string canonical, JsonObject arguments)
        {
            if (!native)
            {
                return Text(new JsonObject { ["tool"] = canonical, ["arguments"] = arguments }.ToJsonString());
            }

            var alias = body["tools"]!.AsArray().Select(t => t!["function"]!["name"]!.GetValue<string>()).Single(name => name == canonical.Replace('.', '_'));
            return Reply(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = null,
                ["tool_calls"] = new JsonArray(new JsonObject
                {
                    ["id"] = $"call-{Guid.NewGuid():N}",
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = alias, ["arguments"] = arguments.ToJsonString() },
                }),
            }, "tool_calls");
        }

        private static HttpResponseMessage Text(string content) => Reply(new JsonObject { ["role"] = "assistant", ["content"] = content }, "stop");

        private static HttpResponseMessage Reply(JsonObject message, string finish) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(new JsonObject
            {
                ["model"] = KnowledgeBenchmark.ModelId,
                ["choices"] = new JsonArray(new JsonObject { ["message"] = message, ["finish_reason"] = finish }),
            }.ToJsonString(), Encoding.UTF8, "application/json"),
        };
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
