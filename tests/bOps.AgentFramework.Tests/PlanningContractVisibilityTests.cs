// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Knowledge;
using bOps.Runtime;

namespace bOps.AgentFramework.Tests;

public sealed class PlanningContractVisibilityTests
{
    [Fact]
    public void ScopedCatalog_ExposesExactlyTheNonSensitiveSchemaAndConstraints()
    {
        var manifest = new ToolManifest
        {
            Name = "test.schema", Description = "Inspect the scoped resource.", Risk = RiskLevel.Read,
            Platforms = ["private-platform"], Requires = ["private-configuration"],
            Parameters =
            [
                new("mode", ToolParameterType.Enum, "mode description", AllowedValues: ["raw", "summary"]),
                new("count", ToolParameterType.Integer, "count description", Required: false) { Minimum = 1, Maximum = 20 },
                new("ratio", ToolParameterType.Number, "ratio description") { Minimum = 0.5, Maximum = 1.5 },
                new("topic", ToolParameterType.String, "topic description") { MinLength = 1, MaxLength = 80 },
                new("path", ToolParameterType.Path, "path description", Required: false) { MinLength = 2, MaxLength = 100 },
                new("paths", ToolParameterType.PathList, "paths description") { MinItems = 1, MaxItems = 3 },
                new("enabled", ToolParameterType.Boolean, "enabled description", Required: false),
                new("timeout", ToolParameterType.Duration, "timeout description", Required: false),
                new("secret", ToolParameterType.Enum, "private secret description", Sensitive: true,
                    AllowedValues: ["private-secret-value"]),
            ],
        };

        // Project the supplied task view directly: no model response, global lookup or execution.
        var catalog = AgentRunner.DescribeToolCatalog([manifest]);
        Assert.Equal(
            "- test.schema [Read]: Inspect the scoped resource. Parameters: mode:Enum required allowed=[\"raw\",\"summary\"], "
            + "count:Integer optional minimum=1 maximum=20, ratio:Number required minimum=0.5 maximum=1.5, "
            + "topic:String required minLength=1 maxLength=80, path:Path optional minLength=2 maxLength=100, "
            + "paths:PathList required minItems=1 maxItems=3, enabled:Boolean optional, timeout:Duration optional",
            Assert.Single(catalog.Split('\n'), line => line.StartsWith("- ", StringComparison.Ordinal)));
        Assert.DoesNotContain("private", catalog, StringComparison.Ordinal);
        Assert.DoesNotContain("secret:Enum", catalog, StringComparison.Ordinal);
        Assert.DoesNotContain("runtime.knowledge_read", catalog, StringComparison.Ordinal);
        Assert.Contains("unknown names, sensitive parameters and null values are forbidden", catalog, StringComparison.Ordinal);
        Assert.Contains($"at most {SemanticExpectedArguments.MaximumConstraintCount} properties and {SemanticExpectedArguments.MaximumSerializedBytes} serialized UTF-8 bytes", catalog, StringComparison.Ordinal);

        var sensitiveOnly = AgentRunner.DescribeToolCatalog([manifest with { Parameters = [manifest.Parameters[^1]] }]);
        Assert.Contains("Parameters: none permitted in planning. Omit expectedArguments or use {}.", sensitiveOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("private", sensitiveOnly, StringComparison.Ordinal);
    }

    [Fact]
    public void ScopedCatalog_CarriesBoundedPlanningGuidanceForSystemEventsMode()
    {
        var events = bOps.Packages.Sys.Core.SystemToolManifests.Events("windows");
        var withoutGuidance = events with
        {
            Parameters = [.. events.Parameters.Select(parameter => parameter with { PlanningGuidance = null })],
        };

        var line = Assert.Single(AgentRunner.DescribeToolCatalog([events]).Split('\n'), l => l.StartsWith("- system.events", StringComparison.Ordinal));
        var before = Assert.Single(AgentRunner.DescribeToolCatalog([withoutGuidance]).Split('\n'), l => l.StartsWith("- system.events", StringComparison.Ordinal));

        Assert.Contains("mode:Enum optional allowed=[\"raw\",\"aggregate\"] (Prefer aggregate for overviews, counts, distributions", line, StringComparison.Ordinal);
        Assert.Contains("use raw to inspect individual events and full messages", line, StringComparison.Ordinal);
        Assert.Contains("raw may return only a subset (limit or output cap) even when the time window is fully covered", line, StringComparison.Ordinal);
        Assert.Contains("complete=false or truncated=true is not complete evidence", line, StringComparison.Ordinal);
        Assert.DoesNotContain("must use aggregate", line, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(line.Length - before.Length, 1, 330);
    }

    [Fact]
    public async Task CanaryPlanning_KeepsKnowledgeAuxiliaryAndDescribesArgumentlessTools()
    {
        using var canary = KnowledgeHostCanary.Create(KnowledgeCanaryActivation.Authorized(KnowledgeRunnerWorld.TenantA));
        var model = new CapturePlanningModel();
        var rig = canary.Compose(model);

        var state = await rig.Runner.RunAsync(KnowledgeHostCanaryLiveTests.Goal, KnowledgeRunnerWorld.ActorA);

        var request = Assert.Single(model.Requests);
        Assert.Empty(request.AvailableTools);
        Assert.Equal(
            ["- test.other [Read]: Records whether validation allowed execution. Parameters: none (no execution arguments). Omit expectedArguments or use {}.",
             "- test.read [Read]: Records whether validation allowed execution. Parameters: none (no execution arguments). Omit expectedArguments or use {}."],
            request.SystemPrompt.Split("Tools available in later steps.", StringSplitOptions.None)[1]
                .Split('\n').Where(line => line.StartsWith("- ", StringComparison.Ordinal)).ToArray());
        Assert.DoesNotContain("runtime.knowledge_read", request.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains(request.History, turn => turn.Content?.Contains(
            "Knowledge is an auxiliary capability available during execution, not a plannable operational step", StringComparison.Ordinal) == true);
        Assert.DoesNotContain("nginx -t; ss -ltnp :80", request.SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain(request.History, turn => turn.Content?.Contains("nginx -t; ss -ltnp :80", StringComparison.Ordinal) == true);
        Assert.Empty(state.Plans);
        Assert.Equal(AgentTaskStatus.Failed, state.Status);
        Assert.Equal(0, rig.Read.ExecutionCount);
        Assert.Equal(0, rig.Other.ExecutionCount);
        Assert.Empty(canary.AuditEvents.OfType<KnowledgeReadAuditEvent>());
    }

    // Stop at the real governed planning boundary; never synthesize a plan or bypass its validators.
    private sealed class CapturePlanningModel : IChatModel
    {
        public ChatModelDescriptor Descriptor { get; } = new("offline", "capture-planning");
        internal List<ModelRequest> Requests { get; } = [];

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            throw new ModelProtocolException("Offline capture completed") { FailureKind = ModelFailureKind.Authentication };
        }
    }
}
