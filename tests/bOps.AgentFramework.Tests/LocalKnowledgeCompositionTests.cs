// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Knowledge;

namespace bOps.AgentFramework.Tests;

/// <summary>
/// ADR-0058: the parts of the Local Agent composition that are internal to the adapter. The end-to-end behaviour is in the CLI tests
/// (<c>KnowledgeCliCompositionTests</c>, <c>KnowledgeCliProcessTests</c>); this pins the identity, the scope and the policy.
/// </summary>
public sealed class LocalKnowledgeCompositionTests
{
    private sealed class FixedTenant(string tenant) : ITenantAuthority
    {
        public TenantId? Resolve(ActorIdentity actor) => new TenantId(tenant);
    }

    [Fact]
    public void TheOperatingSystemUserActsForTheTenantTheArchitectureIssuesForTheLocalAgent_AndNobodyElseActsForAnyTenant()
    {
        var authority = new LocalAgentTenantAuthority();
        var taskId = Guid.NewGuid();

        var scope = KnowledgeTaskScope.FromAuthority(authority, ActorIdentity.FromOperatingSystemUser("alice"), taskId);

        Assert.NotNull(scope);
        Assert.Equal(KnowledgeTaskScope.ForLocalAgent(taskId).Tenant, scope.Tenant);
        Assert.Equal(TenantId.Local, scope.Tenant);
        Assert.Null(authority.Resolve(ActorIdentity.RuntimeSystem));
        Assert.Null(authority.Resolve(new ActorIdentity("service", "tenant-b", null)));
        Assert.Null(authority.Resolve(new ActorIdentity("api-key", "alice", "alice")));
    }

    [Fact]
    public void ThePolicyBindsTheConfiguredPackOnlyForTheLocalAgentTenant_AndServesAPinnedPackOnlyToIt()
    {
        var source = new DirectoryKnowledgePackSource(Path.GetTempPath());
        var binding = new PackReference("bsoft.windows-event-runbooks", "1.0.0");
        var policy = new LocalKnowledgePolicy(source, binding);
        var local = KnowledgeTaskScope.ForLocalAgent(Guid.NewGuid());
        var foreign = KnowledgeTaskScope.FromAuthority(new FixedTenant("tenant-b"), ActorIdentity.FromOperatingSystemUser("alice"), Guid.NewGuid())!;

        var choice = policy.ChooseFor(ActorIdentity.FromOperatingSystemUser("alice"), local);

        Assert.NotNull(choice);
        Assert.Same(source, choice.Source);
        Assert.Equal(binding, choice.Reference);
        Assert.Null(policy.ChooseFor(ActorIdentity.FromOperatingSystemUser("alice"), foreign));
        Assert.Same(source, policy.SourceFor(local, new PackReference("bsoft.windows-event-runbooks", "0.9.0")));
        Assert.Null(policy.SourceFor(foreign, binding));
    }

    [Fact]
    public void TheEngineVersionIsTheProductVersion_WithoutAPreReleaseSuffixOrAFourthComponent()
    {
        var engine = LocalKnowledgeComposition.EngineVersion();

        Assert.True(engine >= new Version(1, 3, 0), engine.ToString());
        Assert.Equal(-1, engine.Revision);
        Assert.Equal(typeof(IAuditSink).Assembly.GetName().Version!.Major, engine.Major);
    }
}
