// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text.Json;
using bOps.Abstractions;
using bOps.Runtime;

namespace bOps.Architecture.Tests;

/// <summary>
/// KNOW-M1-A (ADR-0057, T1 and T2): the knowledge-index contracts are closed, immutable and additive; they carry no publisher text in
/// anything that is audited; and the runtime sees them without naming the Knowledge engine or the agent framework. Nothing here builds,
/// ranks or publishes an index: those are later tasks.
/// </summary>
public sealed class KnowledgeIndexContractTests
{
    private static readonly string[] AllowedAuditStrings =
        ["TenantId", "ProviderId", "PackId", "PackVersion", "PackHash", "SelectorVersion", "IndexHash", "AggressiveIndexHash"];

    private static readonly string[] ForbiddenMemberWords =
        ["Description", "Goal", "Query", "Content", "Path", "Body", "Text", "Summary", "Title", "Instruction"];

    private static readonly Type[] ContractTypes =
    [
        typeof(KnowledgeIndexStatus), typeof(KnowledgeIndexSuppression), typeof(KnowledgeIndexListing), typeof(KnowledgeIndexAuditEvent),
        typeof(KnowledgeIndexQuery), typeof(KnowledgeIndexRendering), typeof(KnowledgeIndexView),
    ];

    [Fact]
    public void TheStatusAndSuppressionCodes_AreClosedAndNumberedAsTheDecisionRecordFixes()
    {
        Assert.Equal(
            [("Published", 0), ("Suppressed", 1)],
            Enum.GetValues<KnowledgeIndexStatus>().Select(value => (value.ToString(), (int)value)).ToArray());
        Assert.Equal(
            [
                ("AuthorityWithdrawn", 1), ("SnapshotUnavailable", 2), ("NoEntries", 3), ("ProvenanceRejected", 4), ("SessionFault", 5),
                ("LimitsUnsatisfiable", 6),
            ],
            Enum.GetValues<KnowledgeIndexSuppression>().Select(value => (value.ToString(), (int)value)).ToArray());
        Assert.Equal(11, (int)KnowledgeReadResultCode.OutOfRange);
        Assert.Equal(10, (int)KnowledgeReadResultCode.SnapshotUnavailable);
    }

    [Fact]
    public void TheNewContracts_AreImmutable_NoPublicSetterAndNoMutableCollection()
    {
        foreach (var type in ContractTypes.Where(type => !type.IsEnum))
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var setter = property.SetMethod;
                Assert.True(
                    setter is null || setter.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(System.Runtime.CompilerServices.IsExternalInit)),
                    $"{type.Name}.{property.Name} has a mutable setter.");
                Assert.False(
                    property.PropertyType.IsArray || (property.PropertyType.IsGenericType
                        && property.PropertyType.GetGenericTypeDefinition() is var generic
                        && (generic == typeof(List<>) || generic == typeof(Dictionary<,>) || generic == typeof(HashSet<>))),
                    $"{type.Name}.{property.Name} exposes a mutable collection.");
            }
        }
    }

    [Fact]
    public void TheIndexAuditEvent_CarriesOnlyHostGeneratedOrShapeCheckedStrings_AndNeverPublisherText()
    {
        var declared = typeof(KnowledgeIndexAuditEvent).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        Assert.Equal(
            AllowedAuditStrings.Order(StringComparer.Ordinal),
            declared.Where(property => property.PropertyType == typeof(string)).Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["Ref", "Skill"], typeof(KnowledgeIndexListing).GetProperties().Where(p => p.PropertyType == typeof(string)).Select(p => p.Name).Order(StringComparer.Ordinal));

        foreach (var name in declared.Select(property => property.Name).Concat(typeof(KnowledgeIndexListing).GetProperties().Select(p => p.Name)))
        {
            Assert.DoesNotContain(ForbiddenMemberWords, word => name.Contains(word, StringComparison.Ordinal));
        }

        // The read event gained a handle and a flag, nothing that could carry an argument or a path.
        Assert.Equal(typeof(string), typeof(KnowledgeReadAuditEvent).GetProperty(nameof(KnowledgeReadAuditEvent.Ref))!.PropertyType);
        Assert.Equal(typeof(bool?), typeof(KnowledgeReadAuditEvent).GetProperty(nameof(KnowledgeReadAuditEvent.Repeat))!.PropertyType);
    }

    [Fact]
    public void TheIndexAuditEvent_RoundTripsDeterministically_AsAPolymorphicAuditEvent_AndOmitsWhatIsAbsent()
    {
        var published = new KnowledgeIndexAuditEvent
        {
            TimestampUtc = new DateTimeOffset(2026, 10, 14, 8, 0, 0, TimeSpan.Zero), Node = NodeId.Local, TaskId = Guid.Empty, StepIndex = 2,
            Actor = new ActorIdentity("os-user", "alice", "Alice"), ExecutionAttempt = 1, Status = KnowledgeIndexStatus.Published,
            TenantId = "tenant-a", ProviderId = "kp.files.1", PackId = "acme.runbooks", PackVersion = "1.0.0", PackHash = new string('a', 64),
            SelectorVersion = "m1-lexical", Listed = [new KnowledgeIndexListing("K1", "windows-event-triage", 1, 3)], AuthorizedTotal = 1,
            IndexHash = new string('b', 64), AggressiveIndexHash = new string('c', 64),
        };
        var suppressed = new KnowledgeIndexAuditEvent
        {
            TimestampUtc = published.TimestampUtc, Node = NodeId.Local, TaskId = Guid.Empty, StepIndex = 2, Actor = published.Actor,
            ExecutionAttempt = 1, Status = KnowledgeIndexStatus.Suppressed, Suppression = KnowledgeIndexSuppression.AuthorityWithdrawn,
            CauseCode = KnowledgeAdmissionCode.ProviderNotAdmitted,
        };

        var json = JsonSerializer.Serialize<AuditEvent>(published);
        Assert.Equal(json, JsonSerializer.Serialize<AuditEvent>(published));
        Assert.Contains("\"eventType\":\"knowledgeIndex\"", json, StringComparison.Ordinal);
        var restored = Assert.IsType<KnowledgeIndexAuditEvent>(JsonSerializer.Deserialize<AuditEvent>(json));
        Assert.Equal(published.Listed, restored.Listed);
        Assert.Equal((published.StepIndex, published.IndexHash, published.AggressiveIndexHash), (restored.StepIndex, restored.IndexHash, restored.AggressiveIndexHash));

        var bare = JsonSerializer.Serialize<AuditEvent>(suppressed);
        foreach (var absent in new[] { "TenantId", "PackId", "IndexHash", "SelectorVersion" })
        {
            Assert.DoesNotContain(absent, bare, StringComparison.Ordinal);
        }

        var back = Assert.IsType<KnowledgeIndexAuditEvent>(JsonSerializer.Deserialize<AuditEvent>(bare));
        Assert.Equal((KnowledgeIndexSuppression.AuthorityWithdrawn, KnowledgeAdmissionCode.ProviderNotAdmitted), (back.Suppression!.Value, back.CauseCode!.Value));
        Assert.Empty(back.Listed);
    }

    [Fact]
    public void TheExistingReadEvent_SerializesExactlyAsBefore_WhenTheNewMembersAreAbsent()
    {
        var read = new KnowledgeReadAuditEvent
        {
            TimestampUtc = new DateTimeOffset(2026, 10, 14, 8, 0, 0, TimeSpan.Zero), Node = NodeId.Local, TaskId = Guid.Empty, StepIndex = 1,
            Actor = new ActorIdentity("os-user", "alice", "Alice"), Operation = "load", ReturnedLength = 0,
            ResultCode = KnowledgeReadResultCode.Malformed,
        };

        var json = JsonSerializer.Serialize<AuditEvent>(read);
        Assert.DoesNotContain("Ref", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Repeat", json, StringComparison.Ordinal);

        var withHandle = JsonSerializer.Serialize<AuditEvent>(read with { Ref = "K1.R2", Repeat = true, ResultCode = KnowledgeReadResultCode.OutOfRange });
        var restored = Assert.IsType<KnowledgeReadAuditEvent>(JsonSerializer.Deserialize<AuditEvent>(withHandle));
        Assert.Equal(("K1.R2", true, KnowledgeReadResultCode.OutOfRange), (restored.Ref, restored.Repeat, restored.ResultCode));
    }

    [Fact]
    public void ASessionThatImplementsOnlyTheOlderMembers_StillCompiles_AndSupportsNeitherTheIndexNorABudget()
    {
        using var legacy = new LegacySession();
        IKnowledgeReadSession session = legacy;

        Assert.Null(session.DescribeIndex(new KnowledgeIndexQuery("diagnose the service")));
        Assert.Null(session.ReadsRemaining);
        Assert.False(typeof(IKnowledgeReadSession).GetMethod(nameof(IKnowledgeReadSession.DescribeIndex))!.IsAbstract);
        Assert.False(typeof(IKnowledgeReadSession).GetProperty(nameof(IKnowledgeReadSession.ReadsRemaining))!.GetMethod!.IsAbstract);
    }

    [Fact]
    public void AViewDistinguishesAnAvailableIndexFromASuppressedOne_AndTheHostListingFromThePublisherData()
    {
        var turn = new ChatTurn { Role = ChatRole.User, Content = "data", Provenance = new ContextProvenance { Origin = ContextOrigin.Knowledge, Operation = "index" } };
        var rendering = new KnowledgeIndexRendering(turn, ["K1"], 0, new string('d', 64));
        var published = new KnowledgeIndexView(
            KnowledgeIndexStatus.Published, null, null, rendering, rendering, [new KnowledgeIndexListing("K1", "triage", 1, 0)], 1, "m1-lexical");
        var suppressed = new KnowledgeIndexView(
            KnowledgeIndexStatus.Suppressed, KnowledgeIndexSuppression.SnapshotUnavailable, KnowledgeAdmissionCode.SnapshotTampered,
            null, null, [], 0, "m1-lexical");

        // Publisher-authored text exists only inside the data turn; everything else on the view is typed or host-generated.
        Assert.Equal(ChatRole.User, published.Normal!.DataTurn.Role);
        Assert.Equal(["K1"], published.Normal.ListedRefs);
        Assert.All(
            typeof(KnowledgeIndexView).GetProperties().Where(property => property.PropertyType == typeof(string)),
            property => Assert.Equal(nameof(KnowledgeIndexView.SelectorVersion), property.Name));
        Assert.Null(suppressed.Normal);
        Assert.Null(suppressed.Aggressive);
        Assert.Equal(KnowledgeIndexSuppression.SnapshotUnavailable, suppressed.Suppression);
    }

    [Fact]
    public void TheNewRuntimeTypes_ReferenceNoKnowledgeEngineNoAgentFrameworkAndNoAuthorityType()
    {
        var forbidden = new[] { "bOps.Knowledge", "bOps.AgentFramework", "Microsoft.Agents", "Microsoft.Extensions.AI", "KnowledgeGrants", "KnowledgeTrustStore", "KnowledgePin" };
        foreach (var type in new[] { typeof(KnowledgeIndexQuery), typeof(KnowledgeIndexRendering), typeof(KnowledgeIndexView) })
        {
            var used = type.GetProperties().Select(property => property.PropertyType)
                .Concat(type.GetConstructors().SelectMany(ctor => ctor.GetParameters().Select(parameter => parameter.ParameterType)));
            foreach (var name in used.Select(candidate => candidate.ToString()))
            {
                Assert.DoesNotContain(forbidden, word => name.Contains(word, StringComparison.Ordinal));
            }
        }

        Assert.DoesNotContain(
            typeof(IKnowledgeReadSession).Assembly.GetReferencedAssemblies().Select(assembly => assembly.Name!),
            name => name.StartsWith("bOps.Knowledge", StringComparison.Ordinal) || name.StartsWith("bOps.AgentFramework", StringComparison.Ordinal)
                || name.StartsWith("Microsoft.Agents", StringComparison.Ordinal));
        Assert.DoesNotContain(
            typeof(KnowledgeIndexAuditEvent).Assembly.GetReferencedAssemblies().Select(assembly => assembly.Name!),
            name => name.StartsWith("bOps.", StringComparison.Ordinal) || name.StartsWith("Microsoft.", StringComparison.Ordinal));
    }

    private sealed class LegacySession : IKnowledgeReadSession
    {
        public ToolManifest Manifest => throw new NotSupportedException();

        public Task<KnowledgeReadOutcome> HandleAsync(
            ModelResponse response, KnowledgeReadAuthority authority, ModelInvocationContext context, CancellationToken ct = default) =>
            Task.FromResult(KnowledgeReadOutcome.NotHandled);

        public void Dispose()
        {
        }
    }
}
