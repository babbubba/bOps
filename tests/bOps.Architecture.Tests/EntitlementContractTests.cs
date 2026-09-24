// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using bOps.Abstractions;

namespace bOps.Architecture.Tests;

public sealed class EntitlementContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    [Fact]
    public void AuthorizationKind_ValuesAreFrozenAndEntitlementDeniedIsAppended()
    {
        Assert.Equal(0, (int)AuthorizationKind.Automatic);
        Assert.Equal(1, (int)AuthorizationKind.UserApproved);
        Assert.Equal(2, (int)AuthorizationKind.UserRejected);
        Assert.Equal(3, (int)AuthorizationKind.PolicyDenied);
        Assert.Equal(4, (int)AuthorizationKind.UnknownTool);
        Assert.Equal(5, (int)AuthorizationKind.EntitlementDenied);
    }

    [Fact]
    public void GovernedRequestAndDecision_RoundTripWithIdenticalNeutralBindingAndAllDimensions()
    {
        var binding = new RequestBinding("attempt-opaque-123");
        var request = new EntitlementRequest(binding, "subject-1", "install-1", "feature-1", "skill-1", "2.3", "capability-1",
            new EntitlementResourceRequest("resource-1", 4, "unit"), new NodeId("node-1"), "target-1",
            new EntitlementValidityRequest(DateTimeOffset.Parse("2026-09-24T10:00:00Z"), TimeSpan.FromMinutes(2)),
            new EntitlementConstraints(new Dictionary<string, string> { ["region"] = "north" }));
        var roundTripRequest = RoundTrip(request);
        var decision = new EntitlementDecision(binding, EntitlementDecisionKind.Allowed, EntitlementReasonCode.Permitted,
            EntitlementSourceCategory.Local, "authority-42", DateTimeOffset.Parse("2026-09-24T10:00:00Z"),
            DateTimeOffset.Parse("2026-09-24T10:05:00Z"), new EntitlementConstraints(new Dictionary<string, string> { ["remaining"] = "8" }));
        var roundTripDecision = RoundTrip(decision);

        Assert.Equal(request.Binding, roundTripRequest.Binding);
        Assert.Equal(request.Subject, roundTripRequest.Subject);
        Assert.Equal(request.Installation, roundTripRequest.Installation);
        Assert.Equal(request.Feature, roundTripRequest.Feature);
        Assert.Equal(request.SkillId, roundTripRequest.SkillId);
        Assert.Equal(request.SkillVersion, roundTripRequest.SkillVersion);
        Assert.Equal(request.Capability, roundTripRequest.Capability);
        Assert.Equal(request.Resource, roundTripRequest.Resource);
        Assert.Equal(request.Node, roundTripRequest.Node);
        Assert.Equal(request.Target, roundTripRequest.Target);
        Assert.Equal(request.Validity, roundTripRequest.Validity);
        Assert.Equal(request.Constraints!.Values["region"], roundTripRequest.Constraints!.Values["region"]);
        Assert.Equal(decision.Binding, roundTripDecision.Binding);
        Assert.Equal(decision.Result, roundTripDecision.Result);
        Assert.Equal(decision.Reason, roundTripDecision.Reason);
        Assert.Equal(decision.Source, roundTripDecision.Source);
        Assert.Equal(decision.AuthorityId, roundTripDecision.AuthorityId);
        Assert.Equal(decision.ValidFrom, roundTripDecision.ValidFrom);
        Assert.Equal(decision.ValidUntil, roundTripDecision.ValidUntil);
        Assert.Equal(decision.Constraints!.Values["remaining"], roundTripDecision.Constraints!.Values["remaining"]);
        Assert.Equal(roundTripRequest.Binding, roundTripDecision.Binding);
        Assert.Equal(EntitlementApplicability.Governed, RoundTrip(new EntitlementRequirement(EntitlementApplicability.Governed)).Applicability);
        Assert.Equal(EntitlementApplicability.NotGoverned, RoundTrip(new EntitlementRequirement(EntitlementApplicability.NotGoverned)).Applicability);
    }

    [Fact]
    public void DeniedDecision_RoundTripsAsDeniedWithNeutralReasonAndSource()
    {
        var decision = new EntitlementDecision(new RequestBinding("attempt-2"), EntitlementDecisionKind.Denied,
            EntitlementReasonCode.LimitExceeded, EntitlementSourceCategory.Remote, "authority-43",
            DateTimeOffset.Parse("2026-09-24T10:00:00Z"), DateTimeOffset.Parse("2026-09-24T10:05:00Z"), null);

        var roundTrip = RoundTrip(decision);
        Assert.Equal(decision.Binding, roundTrip.Binding);
        Assert.Equal(decision.Result, roundTrip.Result);
        Assert.Equal(decision.Reason, roundTrip.Reason);
        Assert.Equal(decision.Source, roundTrip.Source);
        Assert.Equal(decision.AuthorityId, roundTrip.AuthorityId);
        Assert.Equal(decision.ValidFrom, roundTrip.ValidFrom);
        Assert.Equal(decision.ValidUntil, roundTrip.ValidUntil);
    }

    [Fact]
    public void PublicContracts_ExposeNoRawSecretOrCommercialProperties()
    {
        var types = new[] { typeof(EntitlementRequirement), typeof(EntitlementRequest), typeof(EntitlementDecision),
            typeof(RequestBinding), typeof(EntitlementConstraints), typeof(EntitlementResourceRequest), typeof(EntitlementValidityRequest) };
        var forbidden = new[] { "licenseToken", "rawToken", "secret", "credential", "payment", "sku", "price", "vendorPayload" };
        var exposed = types.SelectMany(type => type.GetProperties().Select(property => $"{type.Name}.{property.Name}"))
            .Where(member => forbidden.Any(term => member.Contains(term, StringComparison.OrdinalIgnoreCase))).ToArray();

        Assert.Empty(exposed);
    }

    [Fact]
    public void EntitlementDecisionAuditEvent_RoundTripsNeutralEvidenceAndBaseCorrelation()
    {
        var value = new EntitlementDecisionAuditEvent
        {
            TimestampUtc = DateTimeOffset.Parse("2026-09-24T10:00:00Z"),
            Node = new NodeId("node-1"), TaskId = Guid.Parse("a604b480-4282-4438-b74a-8daeed857e3b"), StepIndex = 7,
            Actor = new ActorIdentity("operator", "operator-1", "Operator"),
            Delegation = new DelegationCorrelation(Guid.Parse("c889111a-c035-4c08-96cd-7991cfad5644"), "envelope-hash",
                new AgentIdentity(new AgentId(Guid.Parse("6b50273b-8379-4e0f-b911-f0b7485dbdc7")), AgentRoleKind.Verification)),
            Package = new PackageId("package-1"), Tool = "sample.write",
            Applicability = EntitlementApplicability.Governed,
            Result = EntitlementDecisionKind.Denied, Source = EntitlementSourceCategory.Remote,
            Reason = EntitlementReasonCode.Unavailable, Binding = new RequestBinding("opaque-attempt-1"),
            AuthorityId = "authority-1", ValidFrom = DateTimeOffset.Parse("2026-09-24T09:59:00Z"),
            ValidUntil = DateTimeOffset.Parse("2026-09-24T10:04:00Z"),
            Constraints = new EntitlementConstraints(new Dictionary<string, string> { ["limit"] = "denied" }),
        };

        var roundTrip = Assert.IsType<EntitlementDecisionAuditEvent>(JsonSerializer.Deserialize<AuditEvent>(JsonSerializer.Serialize<AuditEvent>(value), JsonOptions));

        Assert.Equal(value.Node, roundTrip.Node);
        Assert.Equal(value.TaskId, roundTrip.TaskId);
        Assert.Equal(value.StepIndex, roundTrip.StepIndex);
        Assert.Equal(value.Actor, roundTrip.Actor);
        Assert.Equal(value.Delegation, roundTrip.Delegation);
        Assert.Equal(value.Package, roundTrip.Package);
        Assert.Equal(value.Tool, roundTrip.Tool);
        Assert.Equal(value.Applicability, roundTrip.Applicability);
        Assert.Equal(value.Result, roundTrip.Result);
        Assert.Equal(value.Source, roundTrip.Source);
        Assert.Equal(value.Reason, roundTrip.Reason);
        Assert.Equal(value.Binding, roundTrip.Binding);
        Assert.Equal(value.ValidFrom, roundTrip.ValidFrom);
        Assert.Equal(value.ValidUntil, roundTrip.ValidUntil);
        Assert.Equal("denied", roundTrip.Constraints!.Values["limit"]);
    }

    [Fact]
    public void NotGovernedEntitlementAuditEvent_RecordsHostApplicabilityWithoutDecision()
    {
        var value = new EntitlementDecisionAuditEvent
        {
            TimestampUtc = DateTimeOffset.UtcNow, Node = new NodeId("node-1"), TaskId = Guid.NewGuid(), StepIndex = 0,
            Actor = new ActorIdentity("operator", "operator-1", "Operator"), Package = new PackageId("package-1"), Tool = "sample.read",
            Applicability = EntitlementApplicability.NotGoverned,
        };
        var roundTrip = Assert.IsType<EntitlementDecisionAuditEvent>(JsonSerializer.Deserialize<AuditEvent>(JsonSerializer.Serialize<AuditEvent>(value), JsonOptions));
        Assert.Equal(EntitlementApplicability.NotGoverned, roundTrip.Applicability);
        Assert.Null(roundTrip.Result);
    }

    [Fact]
    public void EntitlementDecisionAuditEvent_ExposesNoRawOrCommercialEscapeHatches()
    {
        var properties = typeof(EntitlementDecisionAuditEvent).GetProperties();
        var forbidden = new[] { "rawtoken", "licensetoken", "secret", "credential", "providerpayload", "vendorpayload", "payment", "price", "sku", "subscription" };
        Assert.DoesNotContain(properties, property => forbidden.Any(term => property.Name.Contains(term.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(properties, property => typeof(System.Collections.IDictionary).IsAssignableFrom(property.PropertyType));
        Assert.DoesNotContain(properties, property => property.PropertyType == typeof(Exception) || property.PropertyType.FullName == "System.Text.Json.Nodes.JsonObject");
    }

    [Fact]
    public void PublicSurface_ContainsTheAdditiveEntitlementContractsAndService()
    {
        var surface = PublicSurface.Describe(typeof(NodeId).Assembly);

        Assert.Contains("bOps.Abstractions.AuthorizationKind | enum-value EntitlementDenied = 5", surface);
        Assert.Contains(surface, line => line.StartsWith("bOps.Abstractions.IEntitlementService |", StringComparison.Ordinal));
        foreach (var name in new[] { "EntitlementApplicability", "EntitlementRequirement", "EntitlementRequest", "EntitlementDecision", "EntitlementDecisionAuditEvent",
                     "RequestBinding", "EntitlementValidityRequest", "EntitlementConstraints", "EntitlementResourceRequest", "ToolExecutionRegistration" })
            Assert.Contains(surface, line => line.StartsWith($"bOps.Abstractions.{name} |", StringComparison.Ordinal));

        Assert.Contains(surface, line => line.StartsWith("bOps.Abstractions.IToolRegistry | method fixed bOps.Abstractions.ToolExecutionRegistration ResolveForExecution(System.String)", StringComparison.Ordinal));

        var snapshot = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Snapshots", "entitlement-contracts.txt"));
        Assert.All(snapshot, line => Assert.Contains(line, surface));
    }

    [Fact]
    public void UnknownNumericDecisionValue_DoesNotDeserializeAsAllowed()
    {
        var decision = JsonSerializer.Deserialize<EntitlementDecisionKind>("99", JsonOptions);

        Assert.NotEqual(EntitlementDecisionKind.Allowed, decision);
        Assert.Equal(99, (int)decision);
    }

    [Fact]
    public void Applicability_IsHostRequirementAndNotProviderRequestOrDecision()
    {
        Assert.DoesNotContain(typeof(EntitlementRequest).GetProperties(), property => property.Name == "Applicability");
        Assert.DoesNotContain(typeof(EntitlementDecision).GetProperties(), property => property.Name == "Applicability");
        Assert.DoesNotContain(typeof(ToolManifest).GetProperties(), property => property.Name.Contains("Entitlement", StringComparison.Ordinal));
        Assert.Equal(EntitlementApplicability.Governed, default(EntitlementApplicability));
        Assert.Equal(EntitlementDecisionKind.Denied, default(EntitlementDecisionKind));
    }

    [Fact]
    public void AbstractionsVersion_AdvancesAdditivelyForV13Contracts()
    {
        var projectFile = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/core/bOps.Abstractions/bOps.Abstractions.csproj"));
        var project = System.Xml.Linq.XDocument.Load(projectFile);

        Assert.Equal("1.3.0-preview.1", project.Descendants("Version").Single().Value);
    }

    private static T RoundTrip<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions)!;
}
