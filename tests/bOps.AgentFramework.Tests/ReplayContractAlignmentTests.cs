// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
#pragma warning disable CA2000 // Enclosing scopes own worlds.
using System.Reflection;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.AgentFramework;
using bOps.Knowledge;
using bOps.Knowledge.Tests;
using bOps.Packages.Providers.OpenAiCompatible;
using bOps.Packages.Sys.Core;
using bOps.Runtime;

namespace bOps.AgentFramework.Tests;

public sealed class ReplayContractAlignmentTests
{
    [Fact]
    public async Task RestrictedContract_PreservesABIdentity_AndAdmitsOneConcurrentRead()
    {
        using var capture = Source();
        using var captureWorld = new KnowledgeRunnerWorld();
        await capture.CaptureAsync(captureWorld);
        var (raw, trust) = await KnowledgeBenchmark.LoadAsync();
        string? firstOutput = null;
        foreach (var condition in new[] { "A", "B" })
        {
            using var world = new KnowledgeRunnerWorld();
            var task = Guid.NewGuid();
            var rig = capture.Compose(world, new BenchmarkProbe([]), condition, task,
                condition == "B" ? raw : null, condition == "B" ? trust : null,
                condition == "B" ? KnowledgePackFormat.Sha256Hex(raw.ManifestBytes.Span) : null,
                condition == "B" ? KnowledgeBenchmark.GrantAuthorization : null);
            var tool = (IContextualTool)rig.Registry.Resolve("system.events")!;
            var native = SystemToolManifests.Events("windows");
            Assert.Equal(new PackageId("bops.packages.system.windows"), tool.Manifest.Package);
            Assert.Equal(native.Risk, tool.Manifest.Risk);
            Assert.Equal(native.Requires, tool.Manifest.Requires);
            Assert.Equal(5, tool.Manifest.Parameters.Count);
            Assert.All(tool.Manifest.Parameters, parameter => Assert.True(parameter.Required));
            Assert.Null(ArgumentSchema.Validate(tool.Manifest.Parameters, KnowledgeBenchmark.Arguments));
            var catalog = AgentRunner.DescribeToolCatalog([tool.Manifest]);
            Assert.Contains("allowed=[\"raw\"]", catalog, StringComparison.Ordinal);
            Assert.Contains("minimum=1440 maximum=1440", catalog, StringComparison.Ordinal);
            var method = typeof(OpenAiCompatibleChatModel).GetMethod("BuildParameterSchema",
                BindingFlags.Static | BindingFlags.NonPublic, [typeof(ToolManifest)])!;
            var schema = (JsonObject)method.Invoke(null, [tool.Manifest])!;
            Assert.False(schema["additionalProperties"]!.GetValue<bool>());
            Assert.Equal(5, schema["required"]!.AsArray().Count);
            foreach (var parameter in tool.Manifest.Parameters)
            {
                var property = schema["properties"]![parameter.Name]!;
                if (parameter.AllowedValues is { } allowed)
                    Assert.Equal(allowed.Single(), property["enum"]!.AsArray().Single()!.GetValue<string>());
                else
                {
                    Assert.Equal(parameter.Minimum, property["minimum"]!.GetValue<double>());
                    Assert.Equal(parameter.Maximum, property["maximum"]!.GetValue<double>());
                }
                var wrong = KnowledgeBenchmark.Arguments.ToJson(); wrong[parameter.Name] = parameter.AllowedValues is null ? JsonValue.Create(1) : JsonValue.Create("wrong");
                Assert.NotNull(ArgumentSchema.Validate(tool.Manifest.Parameters, ToolArguments.FromJson(wrong)));
            }
            var authorized = new ModelRequest("offline", [], [tool.Manifest]);
            var options = MafGovernedTurn.BuildOptions(authorized).ChatOptions;
            var gate = ContextGate.Evaluate([], options, authorized, new ChatModelDescriptor("offline", "offline"));
            Assert.Same(tool.Manifest, Assert.Single(gate.Request!.AvailableTools));
            var fallbackMethod = typeof(OpenAiCompatibleChatModel).GetMethod("BuildFallbackSystemPrompt", BindingFlags.Static | BindingFlags.NonPublic)!;
            var fallback = (string)fallbackMethod.Invoke(null, [authorized])!;
            Assert.Contains(schema.ToJsonString(), fallback, StringComparison.Ordinal);
            var wireType = typeof(OpenAiCompatibleChatModel).Assembly.GetType("bOps.Packages.Providers.Wire.ToolWireNames")!;
            var map = wireType.GetMethod("Create")!.Invoke(null, [new[] { tool.Manifest.Name }])!;
            Assert.Equal("system_events", wireType.GetMethod("ToWire")!.Invoke(map, ["system.events"]));
            var context = new ToolExecutionContext(NodeId.Local, task, KnowledgeRunnerWorld.ActorA);
            // Start simultaneous callers on separate workers; the CAS admission must have exactly one winner.
            var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => tool.ExecuteAsync(KnowledgeBenchmark.Arguments, context))));
            var accepted = Assert.Single(results, result => result.Succeeded);
            Assert.All(results.Where(result => !result.Succeeded), result => Assert.Equal("AlreadyConsumed", Code(result)));
            Assert.Equal(capture.Hash, accepted.Facts.Single(f => f.Key == "corpusSha256").Value.GetValue<string>());
            Assert.Equal(CanaryEventsCorpus.Canonical(capture.Provenance), accepted.Facts.Single(f => f.Key == "corpusProvenance").Value.GetValue<string>());
            if (firstOutput is null) firstOutput = accepted.Output;
            else Assert.Equal(firstOutput, accepted.Output);
            Assert.Equal("AlreadyConsumed", Code(await tool.ExecuteAsync(KnowledgeBenchmark.Arguments, context)));
            Assert.Empty(world.Events.OfType<ModelCallAuditEvent>());
        }
    }

    [Fact]
    public async Task RejectedRequests_DoNotConsume_AndExportOnlyAllowlistedDiagnostics()
    {
        const string sensitive = "Artificial private prompt and Windows event";
        using var capture = Source();
        using var captureWorld = new KnowledgeRunnerWorld();
        await capture.CaptureAsync(captureWorld);
        using var temporary = new TempDirectory();
        var root = temporary.Sub("exports"); Directory.CreateDirectory(root);
        await Assert.ThrowsAsync<IOException>(() => KnowledgeBenchmarkExport.RunAsync(root, "A", async export =>
        {
            var world = new KnowledgeRunnerWorld(); export.World = world;
            var rig = capture.Compose(world, new BenchmarkProbe([]), "A", export.RunId);
            var tool = (IContextualTool)rig.Registry.Resolve("system.events")!;
            var summary = (IToolAuditSummaryProvider)tool;
            var context = new ToolExecutionContext(NodeId.Local, export.RunId, KnowledgeRunnerWorld.ActorA);
            var wrong = KnowledgeBenchmark.Arguments.ToJson(); wrong["mode"] = sensitive;
            var bad = ToolArguments.FromJson(wrong);
            Assert.NotNull(ArgumentSchema.Validate(tool.Manifest.Parameters, bad));
            await Refusal(bad, context, "IncompatibleArguments");
            await Refusal(KnowledgeBenchmark.Arguments, context with { TaskId = Guid.NewGuid() }, "IdentityMismatch");
            await Refusal(KnowledgeBenchmark.Arguments, context with { Actor = KnowledgeRunnerWorld.ActorB }, "IdentityMismatch");
            await Refusal(KnowledgeBenchmark.Arguments, context with { Actor = new ActorIdentity("os-user", "tenant-a", "wrong-role") }, "IdentityMismatch");
            var field = typeof(TrustedEventsCapture).GetField("provenance", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var original = field.GetValue(capture);
            field.SetValue(capture, "{}");
            await Refusal(KnowledgeBenchmark.Arguments, context, "IntegrityOrProvenance");
            field.SetValue(capture, original);
            var report = await rig.Runner.ExecuteExecutionPlanAsync(export.RunId, KnowledgeRunnerWorld.ActorA,
                new ExecutionPlan("offline", "1", "offline", [new ExecutionPlanStep(0, "system.events", KnowledgeBenchmark.Arguments, "offline")]), null);
            Assert.DoesNotContain("ERROR", Assert.Single(report.Evidence).Data!, StringComparison.Ordinal);
            await Refusal(KnowledgeBenchmark.Arguments, context, "AlreadyConsumed");
            Assert.Empty(world.Events.OfType<ModelCallAuditEvent>());
            // Exercise failure-safe publication with sensitive free text present only in source data.
            throw new IOException(sensitive);

            async Task Refusal(ToolArguments arguments, ToolExecutionContext supplied, string expected)
            {
                var result = await tool.ExecuteAsync(arguments, supplied);
                Assert.False(result.Succeeded); Assert.Null(result.Output); Assert.Equal(expected, Code(result));
                await world.Audit.WriteAsync(new ToolCallAuditEvent
                {
                    TimestampUtc = DateTimeOffset.UtcNow, Node = NodeId.Local, TaskId = export.RunId, Actor = supplied.Actor,
                    Package = tool.Manifest.Package, Tool = "system.events", Arguments = arguments.ToJson(), Risk = RiskLevel.Read,
                    Authorization = AuthorizationKind.PolicyDenied, Outcome = result.Outcome, Duration = TimeSpan.Zero,
                    Summary = summary.CreateAuditSummary(arguments, result),
                });
            }
        }));
        var json = await File.ReadAllTextAsync(Path.Combine(root, "A", "result.json"));
        var audit = await File.ReadAllTextAsync(Path.Combine(root, "A", "audit.jsonl"));
        Assert.Equal(2, JsonNode.Parse(json)!["schemaVersion"]!.GetValue<int>());
        foreach (var code in new[] { "IncompatibleArguments", "IdentityMismatch", "AlreadyConsumed", "IntegrityOrProvenance" })
        {
            Assert.Contains(code, json, StringComparison.Ordinal); Assert.Contains(code, audit, StringComparison.Ordinal);
        }
        Assert.DoesNotContain(sensitive, json + audit, StringComparison.Ordinal);
        Assert.DoesNotContain("\"arguments\"", json + audit, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("corpusProvenance", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("output", audit, StringComparison.OrdinalIgnoreCase);
    }

    private static TrustedEventsCapture Source() => TrustedEventsCapture.Synthetic(_ =>
        ToolCallResult.Success(KnowledgeBenchmark.Synthetic()["output"]!.GetValue<string>()));
    private static string Code(ToolCallResult result) => result.Facts.Single(f => f.Key == "replayRefusalCode").Value.GetValue<string>();
}
