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
        var value = new ToolParameter("paths", ToolParameterType.PathList, "Filesystem paths.", Required: false, Sensitive: true, AllowedValues: ["a", "b"]);

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
            RequiresExplicitApproval = true,
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
        Assert.True(result.RequiresExplicitApproval);
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
            Summary = new JsonObject { ["observedCount"] = 5 },
            Verification = null,
        };

        var json = JsonSerializer.Serialize(value, Options);
        var result = JsonSerializer.Deserialize<AuditEvent>(json, Options);

        var typed = Assert.IsType<ToolCallAuditEvent>(result);
        Assert.Equal(((ToolCallAuditEvent)value).Tool, typed.Tool);
        Assert.Equal(((ToolCallAuditEvent)value).Outcome, typed.Outcome);
        Assert.Equal(5, typed.Summary!["observedCount"]!.GetValue<int>());
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

    [Fact]
    public void SettingsChangedAuditEvent_RoundTrips_AsItsBaseType_WithSentinelTaskCoordinates()
    {
        AuditEvent value = new SettingsChangedAuditEvent
        {
            TimestampUtc = DateTimeOffset.UnixEpoch,
            Node = SampleNode,
            TaskId = Guid.Empty,
            StepIndex = -1,
            Actor = SampleActor,
            SettingName = "provider.apiKey",
            Operation = SettingsChangeOperation.Set,
            ProviderId = "openai",
            Outcome = SettingsChangeOutcome.Success,
        };

        var json = JsonSerializer.Serialize(value, Options);
        var result = JsonSerializer.Deserialize<AuditEvent>(json, Options);

        var typed = Assert.IsType<SettingsChangedAuditEvent>(result);
        Assert.Equal(Guid.Empty, typed.TaskId);
        Assert.Equal(-1, typed.StepIndex);
        Assert.Equal("provider.apiKey", typed.SettingName);
        Assert.Equal(SettingsChangeOperation.Set, typed.Operation);
        Assert.Equal("openai", typed.ProviderId);
        Assert.Equal(SettingsChangeOutcome.Success, typed.Outcome);
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

    // ---- Evidence.cs ----

    [Fact]
    public void Evidence_RoundTrips()
    {
        var value = new Evidence("evidence-1", EvidenceKind.Fact, "Table bloat ratio.", "42%", "postgres.list_bloated_tables", DateTimeOffset.UtcNow);

        var result = RoundTrip(value);

        Assert.Equal(value, result);
    }

    [Fact]
    public void Finding_RoundTrips()
    {
        var value = new Finding("finding-1", "Bloat detected.", ["evidence-1", "evidence-2"], RiskLevel.Medium);

        var result = RoundTrip(value);

        Assert.Equal(value.Id, result!.Id);
        Assert.Equal(value.Summary, result.Summary);
        Assert.Equal(value.EvidenceIds, result.EvidenceIds);
        Assert.Equal(value.Severity, result.Severity);
    }

    [Fact]
    public void SkillReport_RoundTrips()
    {
        var value = new SkillReport(
            [new Evidence("evidence-1", EvidenceKind.Fact, "CPU usage.", "42%", "system.cpu", DateTimeOffset.UtcNow)],
            [new Finding("finding-1", "High CPU.", ["evidence-1"], RiskLevel.Low)],
            Plan: null);

        var result = RoundTrip(value);

        Assert.Single(result!.Evidence);
        Assert.Single(result.Findings);
        Assert.Null(result.Plan);
    }

    // ---- ExecutionPlan.cs ----

    [Fact]
    public void ExecutionPlanStep_RoundTrips()
    {
        var value = new ExecutionPlanStep(0, "system.cpu", ToolArguments.FromJson(new JsonObject { ["limit"] = 5 }), "Read CPU usage.");

        var result = RoundTrip(value);

        Assert.Equal(value.Index, result!.Index);
        Assert.Equal(value.ToolName, result.ToolName);
        Assert.Equal(value.Arguments.ToJson().ToJsonString(), result.Arguments.ToJson().ToJsonString());
        Assert.Equal(value.Description, result.Description);
    }

    [Fact]
    public void ExecutionPlan_RoundTrips()
    {
        var value = new ExecutionPlan("system.diagnose", "1.0.0", "Check CPU usage.",
            [new ExecutionPlanStep(0, "system.cpu", ToolArguments.Empty, null)]);

        var result = RoundTrip(value);

        Assert.Equal(value.CapabilityName, result!.CapabilityName);
        Assert.Equal(value.CapabilityVersion, result.CapabilityVersion);
        Assert.Equal(value.Rationale, result.Rationale);
        Assert.Single(result.Steps);
        Assert.Equal(value.Steps[0].ToolName, result.Steps[0].ToolName);
    }

    [Fact]
    public void ExecutionPlanApproval_RoundTrips()
    {
        var value = new ExecutionPlanApproval("abc123", new ApprovalDecision(true, SampleActor, "Looks fine."));

        var result = RoundTrip(value);

        Assert.Equal(value, result);
    }

    // ---- Capabilities.cs ----

    [Fact]
    public void CapabilityManifest_RoundTrips()
    {
        var value = new CapabilityManifest(
            "postgres.diagnose_bloat", "1.0.0", "Diagnoses table bloat.", RiskLevel.Read,
            RequiredPermissions: ["db.read"],
            InputSchema: [new ToolParameter("schema", ToolParameterType.String, "Schema name.")],
            OutputSchema: [new ToolParameter("bloatRatio", ToolParameterType.Number, "Bloat ratio.")],
            Timeout: TimeSpan.FromMinutes(5),
            SupportsDryRun: true,
            Verification: new VerificationSpec("postgres.list_bloated_tables", ["schema"], "Confirms the bloat figures."),
            RollbackDescription: null)
        {
            Package = SamplePackage,
        };

        var result = RoundTrip(value);

        Assert.Equal(value.Name, result!.Name);
        Assert.Equal(value.Version, result.Version);
        Assert.Equal(value.Description, result.Description);
        Assert.Equal(value.Risk, result.Risk);
        Assert.Equal(value.RequiredPermissions, result.RequiredPermissions);
        Assert.Equal(value.InputSchema.Count, result.InputSchema.Count);
        Assert.Equal(value.OutputSchema.Count, result.OutputSchema.Count);
        Assert.Equal(value.Timeout, result.Timeout);
        Assert.Equal(value.SupportsDryRun, result.SupportsDryRun);
        Assert.Equal(value.Verification!.VerifyToolName, result.Verification!.VerifyToolName);
        Assert.Equal(value.Package, result.Package);
    }

    [Fact]
    public void CapabilityRequest_RoundTrips()
    {
        var value = new CapabilityRequest(
            ToolArguments.FromJson(new JsonObject { ["name"] = "sample" }),
            "node-1", "test", BlastRadius.Single, DryRun: true);

        var result = RoundTrip(value);

        Assert.Equal("sample", result!.Input.GetRequired<string>("name"));
        Assert.Equal(value.Target, result.Target);
        Assert.Equal(value.Environment, result.Environment);
        Assert.Equal(value.BlastRadius, result.BlastRadius);
        Assert.True(result.DryRun);
    }

    [Fact]
    public void SkillDescriptor_RoundTrips()
    {
        var capability = new CapabilityManifest(
            "sample.inspect", "1.0.0", "Inspects a sample.", RiskLevel.Read,
            [], [], [], TimeSpan.FromSeconds(5), SupportsDryRun: true);
        var value = new SkillDescriptor("sample.skill", SamplePackage, PackageTrustLevel.Verified, [capability]);

        var result = RoundTrip(value);

        Assert.Equal(value.SkillId, result!.SkillId);
        Assert.Equal(value.Package, result.Package);
        Assert.Equal(value.Trust, result.Trust);
        Assert.Equal("sample.inspect", Assert.Single(result.Capabilities).Name);
    }

    [Fact]
    public void PreparedSkillRun_RoundTrips()
    {
        var request = new CapabilityRequest(ToolArguments.Empty, "local", "test", BlastRadius.Single);
        var report = new SkillReport(
            [new Evidence("e-1", EvidenceKind.Fact, "Observed.", "ok", "sample.read", DateTimeOffset.UnixEpoch)],
            [new Finding("f-1", "Healthy.", ["e-1"])],
            null);
        var value = new PreparedSkillRun(
            Guid.NewGuid(), "sample.skill", "sample.inspect", request,
            SkillPreparationStatus.Prepared, report, null, null);

        var result = RoundTrip(value);

        Assert.Equal(value.RunId, result!.RunId);
        Assert.Equal(value.SkillId, result.SkillId);
        Assert.Equal(value.Status, result.Status);
        Assert.Equal("e-1", Assert.Single(result.Report.Evidence).Id);
    }

    [Fact]
    public void SkillRunAuditEvent_RoundTripsPolymorphically()
    {
        AuditEvent value = new SkillRunAuditEvent
        {
            TimestampUtc = DateTimeOffset.UnixEpoch,
            Node = SampleNode,
            TaskId = Guid.NewGuid(),
            StepIndex = -1,
            Actor = SampleActor,
            RunId = Guid.NewGuid(),
            Package = SamplePackage,
            SkillId = "sample.skill",
            CapabilityName = "sample.inspect",
            Stage = SkillRunStage.Preparation,
            Outcome = SkillRunOutcome.Success,
            PlanHash = "abc",
            EvidenceCount = 1,
            FindingCount = 1,
        };

        var result = RoundTrip(value);

        var skillEvent = Assert.IsType<SkillRunAuditEvent>(result);
        Assert.Equal("sample.skill", skillEvent.SkillId);
        Assert.Equal("abc", skillEvent.PlanHash);
    }

    // ---- Policy.cs (ADR-0023 additions) ----

    [Fact]
    public void PolicyContext_RoundTrips_WithSkillFieldsAbsent()
    {
        var manifest = new ToolManifest
        {
            Name = "system.cpu",
            Description = "Reads CPU usage.",
            Risk = RiskLevel.Read,
            Platforms = ["windows"],
            Requires = [],
            Parameters = [],
        };
        var value = new PolicyContext(SampleNode, SamplePackage, PackageTrustLevel.Official, manifest, ToolArguments.Empty, SampleActor);

        var json = JsonSerializer.Serialize(value, Options);
        var result = JsonSerializer.Deserialize<PolicyContext>(json, Options);

        Assert.Null(result!.SkillId);
        Assert.Null(result.CapabilityName);
        Assert.Null(result.Target);
        Assert.Null(result.Environment);
        Assert.Null(result.BlastRadius);
    }

    [Fact]
    public void PolicyContext_RoundTrips_WithSkillFieldsPresent()
    {
        var manifest = new ToolManifest
        {
            Name = "postgres.list_bloated_tables",
            Description = "Lists bloated tables.",
            Risk = RiskLevel.Read,
            Platforms = ["windows", "linux"],
            Requires = [],
            Parameters = [],
        };
        var value = new PolicyContext(SampleNode, SamplePackage, PackageTrustLevel.Community, manifest, ToolArguments.Empty, SampleActor)
        {
            SkillId = "postgres-dba",
            CapabilityName = "postgres.diagnose_bloat",
            Target = "db-primary",
            Environment = "production",
            BlastRadius = BlastRadius.Single,
        };

        var json = JsonSerializer.Serialize(value, Options);
        var result = JsonSerializer.Deserialize<PolicyContext>(json, Options);

        Assert.Equal(value.SkillId, result!.SkillId);
        Assert.Equal(value.CapabilityName, result.CapabilityName);
        Assert.Equal(value.Target, result.Target);
        Assert.Equal(value.Environment, result.Environment);
        Assert.Equal(value.BlastRadius, result.BlastRadius);
    }

    private static T? RoundTrip<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, Options);
        return JsonSerializer.Deserialize<T>(json, Options);
    }
}
