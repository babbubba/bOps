// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using bOps.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// Rule A2: everything crossing the tool boundary must round-trip through
/// <see cref="System.Text.Json"/>. One test per contract record in <c>Tools.cs</c>,
/// <c>Audit.cs</c>, <c>Model.cs</c>, <c>Policy.cs</c> and <c>TaskState.cs</c>.
/// </summary>
public sealed class JsonRoundTripTests
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    private static readonly ActorIdentity SampleActor = new("os-user", "alice", "Alice");
    private static readonly NodeId SampleNode = new("node-1");
    private static readonly PackageId SamplePackage = new("bops.packages.system.windows");

    // ---- Tools.cs ----

    [Fact]
    public void ToolParameter_RoundTrips()
    {
        var value = new ToolParameter("path", ToolParameterType.Path, "A file path.", Required: false, Sensitive: true, AllowedValues: ["a", "b"]);

        var result = RoundTrip(value);

        // Record equality does not deep-compare IReadOnlyList<T> members (no value equality on
        // the interface), so AllowedValues is asserted separately via SequenceEqual.
        Assert.Equal(value.Name, result!.Name);
        Assert.Equal(value.Type, result.Type);
        Assert.Equal(value.Description, result.Description);
        Assert.Equal(value.Required, result.Required);
        Assert.Equal(value.Sensitive, result.Sensitive);
        Assert.Equal(value.AllowedValues, result.AllowedValues);
    }

    [Fact]
    public void VerificationSpec_RoundTrips()
    {
        var value = new VerificationSpec("service.status", ["serviceName"], "Confirms the service is running.");

        var result = RoundTrip(value);

        Assert.Equal(value.VerifyToolName, result!.VerifyToolName);
        Assert.Equal(value.ArgumentsFrom, result.ArgumentsFrom);
        Assert.Equal(value.Description, result.Description);
    }

    [Fact]
    public void ToolManifest_RoundTrips()
    {
        var value = new ToolManifest
        {
            Name = "service.restart",
            Description = "Restarts a service.",
            Risk = RiskLevel.High,
            Platforms = ["windows"],
            Requires = ["service-control"],
            Parameters = [new ToolParameter("name", ToolParameterType.String, "Service name.")],
            Verification = new VerificationSpec("service.status", ["name"], "Confirms the service is running."),
        };

        var result = RoundTrip(value);

        Assert.Equal(value.Name, result!.Name);
        Assert.Equal(value.Description, result.Description);
        Assert.Equal(value.Risk, result.Risk);
        Assert.Equal(value.Platforms, result.Platforms);
        Assert.Equal(value.Requires, result.Requires);
        Assert.Equal(value.Parameters.Count, result.Parameters.Count);
        Assert.Equal(value.Parameters[0].Name, result.Parameters[0].Name);
        Assert.Equal(value.Verification!.VerifyToolName, result.Verification!.VerifyToolName);
        Assert.Equal(value.Verification.ArgumentsFrom, result.Verification.ArgumentsFrom);
        Assert.Equal(value.Verification.Description, result.Verification.Description);
    }

    [Fact]
    public void ToolCallRequest_RoundTrips()
    {
        var value = new ToolCallRequest("system.cpu", ToolArguments.FromJson(new JsonObject { ["limit"] = 5 }));

        var result = RoundTrip(value);

        Assert.Equal(value.ToolName, result!.ToolName);
        Assert.Equal(value.Arguments.ToJson().ToJsonString(), result.Arguments.ToJson().ToJsonString());
    }

    [Fact]
    public void ToolCallResult_RoundTrips()
    {
        var value = ToolCallResult.Success("42% CPU");

        var result = RoundTrip(value);

        Assert.Equal(value, result);
    }

    // ---- Audit.cs ----

    [Fact]
    public void ToolCallAuditEvent_RoundTrips_AsItsBaseType()
    {
        AuditEvent value = new ToolCallAuditEvent
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Node = SampleNode,
            TaskId = Guid.NewGuid(),
            StepIndex = 0,
            Actor = SampleActor,
            Package = SamplePackage,
            Tool = "system.cpu",
            Arguments = new JsonObject { ["limit"] = 5 },
            Risk = RiskLevel.Read,
            Authorization = AuthorizationKind.Automatic,
            Outcome = ToolOutcome.Success,
            Duration = TimeSpan.FromMilliseconds(120),
            Verification = null,
        };

        var json = JsonSerializer.Serialize(value, Options);
        var result = JsonSerializer.Deserialize<AuditEvent>(json, Options);

        var typed = Assert.IsType<ToolCallAuditEvent>(result);
        Assert.Equal(((ToolCallAuditEvent)value).Tool, typed.Tool);
        Assert.Equal(((ToolCallAuditEvent)value).Outcome, typed.Outcome);
        Assert.Equal(value.TaskId, typed.TaskId);
        Assert.Equal(value.Actor, typed.Actor);
    }

    [Fact]
    public void ModelCallAuditEvent_RoundTrips_AsItsBaseType()
    {
        AuditEvent value = new ModelCallAuditEvent
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Node = SampleNode,
            TaskId = Guid.NewGuid(),
            StepIndex = 1,
            Actor = SampleActor,
            Provider = "OpenRouter",
            Model = "anthropic/claude-sonnet-4.5",
            Outcome = ModelCallOutcome.Success,
            Usage = new ModelUsage(120, 45, 0.002m),
        };

        var json = JsonSerializer.Serialize(value, Options);
        var result = JsonSerializer.Deserialize<AuditEvent>(json, Options);

        var typed = Assert.IsType<ModelCallAuditEvent>(result);
        Assert.Equal(((ModelCallAuditEvent)value).Provider, typed.Provider);
        Assert.Equal(((ModelCallAuditEvent)value).Model, typed.Model);
        Assert.Equal(((ModelCallAuditEvent)value).Outcome, typed.Outcome);
        Assert.Equal(((ModelCallAuditEvent)value).Usage, typed.Usage);
    }

    [Fact]
    public void PolicyDecisionAuditEvent_RoundTrips_AsItsBaseType()
    {
        AuditEvent value = new PolicyDecisionAuditEvent
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Node = SampleNode,
            TaskId = Guid.NewGuid(),
            StepIndex = 2,
            Actor = SampleActor,
            Package = SamplePackage,
            Tool = "service.restart",
            Mode = PolicyMode.Forbidden,
            Reason = "no policy engine wired yet",
        };

        var json = JsonSerializer.Serialize(value, Options);
        var result = JsonSerializer.Deserialize<AuditEvent>(json, Options);

        var typed = Assert.IsType<PolicyDecisionAuditEvent>(result);
        Assert.Equal(((PolicyDecisionAuditEvent)value).Mode, typed.Mode);
        Assert.Equal(((PolicyDecisionAuditEvent)value).Reason, typed.Reason);
    }

    [Fact]
    public void ApprovalAuditEvent_RoundTrips_AsItsBaseType()
    {
        AuditEvent value = new ApprovalAuditEvent
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Node = SampleNode,
            TaskId = Guid.NewGuid(),
            StepIndex = 3,
            Actor = SampleActor,
            Package = SamplePackage,
            Tool = "service.restart",
            Approved = true,
            Approver = new ActorIdentity("os-user", "bob", "Bob"),
            Note = "confirmed with the on-call.",
        };

        var json = JsonSerializer.Serialize(value, Options);
        var result = JsonSerializer.Deserialize<AuditEvent>(json, Options);

        var typed = Assert.IsType<ApprovalAuditEvent>(result);
        Assert.Equal(((ApprovalAuditEvent)value).Approved, typed.Approved);
        Assert.Equal(((ApprovalAuditEvent)value).Approver, typed.Approver);
        Assert.Equal(((ApprovalAuditEvent)value).Note, typed.Note);
    }

    // ---- Model.cs ----

    [Fact]
    public void ModelToolCall_RoundTrips()
    {
        var value = new ModelToolCall("call-1", "system.cpu", ToolArguments.FromJson(new JsonObject { ["limit"] = 3 }));

        var result = RoundTrip(value);

        Assert.Equal(value.Id, result!.Id);
        Assert.Equal(value.ToolName, result.ToolName);
        Assert.Equal(value.Arguments.ToJson().ToJsonString(), result.Arguments.ToJson().ToJsonString());
    }

    [Fact]
    public void ChatTurn_RoundTrips_WithToolCalls()
    {
        var value = ChatTurn.FromAssistantToolCalls([new ModelToolCall("call-1", "system.cpu", ToolArguments.Empty)]);

        var result = RoundTrip(value);

        Assert.Equal(value.Role, result!.Role);
        Assert.Equal(value.ToolCalls!.Count, result.ToolCalls!.Count);
        Assert.Equal(value.ToolCalls[0].ToolName, result.ToolCalls[0].ToolName);
    }

    [Fact]
    public void ChatTurn_RoundTrips_AsToolResult()
    {
        var value = ChatTurn.FromToolResult("call-1", "42% CPU");

        var result = RoundTrip(value);

        Assert.Equal(value, result);
    }

    [Fact]
    public void ModelUsage_RoundTrips()
    {
        var value = new ModelUsage(100, 50, 0.0015m);

        var result = RoundTrip(value);

        Assert.Equal(value, result);
    }

    [Fact]
    public void ChatModelDescriptor_RoundTrips()
    {
        var value = new ChatModelDescriptor("OpenRouter", "anthropic/claude-sonnet-4.5");

        var result = RoundTrip(value);

        Assert.Equal(value, result);
    }

    [Fact]
    public void ModelResponse_RoundTrips()
    {
        var value = new ModelResponse("done", [new ModelToolCall("call-1", "system.cpu", ToolArguments.Empty)], true, new ModelUsage(10, 5, null));

        var result = RoundTrip(value);

        Assert.Equal(value.TextResponse, result!.TextResponse);
        Assert.Equal(value.IsFinal, result.IsFinal);
        Assert.Equal(value.ToolCalls.Count, result.ToolCalls.Count);
        Assert.Equal(value.Usage, result.Usage);
    }

    // ---- Policy.cs ----

    [Fact]
    public void PolicyDecision_RoundTrips()
    {
        var value = new PolicyDecision(PolicyMode.Approval, "High risk requires human approval.");

        var result = RoundTrip(value);

        Assert.Equal(value, result);
    }

    [Fact]
    public void ApprovalDecision_RoundTrips()
    {
        var value = new ApprovalDecision(true, SampleActor, "Looks fine.");

        var result = RoundTrip(value);

        Assert.Equal(value, result);
    }

    // ---- TaskState.cs ----

    [Fact]
    public void PlanStep_RoundTrips()
    {
        var value = new PlanStep(
            0,
            "system.cpu",
            new ModelToolCall("call-1", "system.cpu", ToolArguments.Empty),
            ToolCallResult.Success("42% CPU"),
            "42% CPU",
            PlanRevision: 0);

        var result = RoundTrip(value);

        Assert.Equal(value.Index, result!.Index);
        Assert.Equal(value.Description, result.Description);
        Assert.Equal(value.ToolCall!.ToolName, result.ToolCall!.ToolName);
        Assert.Equal(value.Result, result.Result);
        Assert.Equal(value.Observation, result.Observation);
        Assert.Equal(value.PlanRevision, result.PlanRevision);
    }

    [Fact]
    public void TaskState_RoundTrips()
    {
        var value = new TaskState(
            Guid.NewGuid(),
            SampleNode,
            "check cpu usage",
            AgentTaskStatus.Completed,
            [new PlanStep(0, "Final response", null, null, "All good.", PlanRevision: 0)],
            [new AgentPlan(0, "Check CPU usage and report it.", [new PlannedStep(0, "Read CPU usage", "system.cpu")])],
            DateTimeOffset.UtcNow);

        var result = RoundTrip(value);

        Assert.Equal(value.Id, result!.Id);
        Assert.Equal(value.Node, result.Node);
        Assert.Equal(value.Goal, result.Goal);
        Assert.Equal(value.Status, result.Status);
        Assert.Single(result.Steps);
        Assert.Equal(0, result.Steps[0].PlanRevision);
        Assert.Single(result.Plans);
        Assert.Equal(value.Plans[0].Rationale, result.Plans[0].Rationale);
        Assert.Single(result.Plans[0].Steps);
        Assert.Equal(value.Plans[0].Steps[0].Description, result.Plans[0].Steps[0].Description);
        Assert.Equal(value.CreatedAtUtc, result.CreatedAtUtc);
    }

    // ---- Planning.cs ----

    [Fact]
    public void PlannedStep_RoundTrips()
    {
        var value = new PlannedStep(0, "Read CPU usage", "system.cpu");

        var result = RoundTrip(value);

        Assert.Equal(value, result);
    }

    [Fact]
    public void AgentPlan_RoundTrips()
    {
        var value = new AgentPlan(1, "Reconsidering after a timeout.", [new PlannedStep(0, "Retry with a longer timeout", "system.cpu")]);

        var result = RoundTrip(value);

        Assert.Equal(value.Revision, result!.Revision);
        Assert.Equal(value.Rationale, result.Rationale);
        Assert.Equal(value.Steps.Count, result.Steps.Count);
        Assert.Equal(value.Steps[0], result.Steps[0]);
    }

    // ---- Identity.cs ----

    [Fact]
    public void ActorIdentity_RoundTrips()
    {
        var value = new ActorIdentity("os-user", "alice", "Alice");

        var result = RoundTrip(value);

        Assert.Equal(value, result);
    }

    [Fact]
    public void NodeId_RoundTrips_AsABareJsonString()
    {
        var value = new NodeId("node-1");

        var json = JsonSerializer.Serialize(value, Options);
        var result = JsonSerializer.Deserialize<NodeId>(json, Options);

        Assert.Equal("\"node-1\"", json);
        Assert.Equal(value, result);
    }

    [Fact]
    public void PackageId_RoundTrips_AsABareJsonString()
    {
        var value = new PackageId("bops.packages.system.windows");

        var json = JsonSerializer.Serialize(value, Options);
        var result = JsonSerializer.Deserialize<PackageId>(json, Options);

        Assert.Equal("\"bops.packages.system.windows\"", json);
        Assert.Equal(value, result);
    }

    // ---- Providers.cs ----

    [Fact]
    public void SecretReference_RoundTrips_WithoutASecretValue()
    {
        var value = new SecretReference("environment", "BOPS_OPENAI_API_KEY");

        var result = RoundTrip(value);

        Assert.Equal(value, result);
    }

    [Fact]
    public void ChatModelOptions_RoundTrips()
    {
        var value = new ChatModelOptions("OpenRouter", "https://openrouter.ai/api/v1",
            new SecretReference("environment", "BOPS_MODEL_API_KEY"), "anthropic/claude-sonnet-4.5",
            SupportsNativeToolCalling: false)
        {
            ResolvedApiKey = "must-not-round-trip",
        };

        var result = RoundTrip(value);

        Assert.Equal(value.Provider, result!.Provider);
        Assert.Equal(value.BaseUrl, result.BaseUrl);
        Assert.Equal(value.ApiKeySecret, result.ApiKeySecret);
        Assert.Equal(value.Model, result.Model);
        Assert.Equal(value.SupportsNativeToolCalling, result.SupportsNativeToolCalling);
        Assert.Null(result.ResolvedApiKey);
        Assert.DoesNotContain("must-not-round-trip", JsonSerializer.Serialize(value, Options), StringComparison.Ordinal);
        Assert.DoesNotContain("must-not-round-trip", value.ToString(), StringComparison.Ordinal);
    }

    // ---- Plugins.cs ----

    [Fact]
    public void PluginDependency_RoundTrips()
    {
        var value = new PluginDependency("Newtonsoft.Json", "13.0.3");

        var result = RoundTrip(value);

        Assert.Equal(value, result);
    }

    [Fact]
    public void PluginManifest_RoundTrips()
    {
        var value = new PluginManifest(
            SchemaVersion: 1,
            Id: "acme.sample-plugin",
            Publisher: "Acme",
            Version: "1.0.0",
            MinHostAbstractionsVersion: "0.10.0",
            EntryAssembly: "AcmeSamplePlugin.dll",
            EntryType: "Acme.SamplePlugin.SampleToolProvider",
            DeclaredCapabilities: ["sample"],
            Dependencies: [new PluginDependency("Newtonsoft.Json", "13.0.3")],
            MaxDeclaredRisk: RiskLevel.Low);

        var result = RoundTrip(value);

        Assert.Equal(value.SchemaVersion, result!.SchemaVersion);
        Assert.Equal(value.Id, result.Id);
        Assert.Equal(value.Publisher, result.Publisher);
        Assert.Equal(value.Version, result.Version);
        Assert.Equal(value.MinHostAbstractionsVersion, result.MinHostAbstractionsVersion);
        Assert.Equal(value.EntryAssembly, result.EntryAssembly);
        Assert.Equal(value.EntryType, result.EntryType);
        Assert.Equal(value.DeclaredCapabilities, result.DeclaredCapabilities);
        Assert.Equal(value.Dependencies, result.Dependencies);
        Assert.Equal(value.MaxDeclaredRisk, result.MaxDeclaredRisk);
    }

    [Fact]
    public void PluginManifest_RoundTrips_WithNoDeclaredMaxRisk()
    {
        var value = new PluginManifest(1, "acme.sample-plugin", "Acme", "1.0.0", "0.10.0",
            "AcmeSamplePlugin.dll", "Acme.SamplePlugin.SampleToolProvider", [], [], MaxDeclaredRisk: null);

        var result = RoundTrip(value);

        Assert.Null(result!.MaxDeclaredRisk);
    }

    private static T? RoundTrip<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, Options);
        return JsonSerializer.Deserialize<T>(json, Options);
    }
}
