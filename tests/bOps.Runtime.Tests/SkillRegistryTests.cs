// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime.Tests;

public sealed class SkillRegistryTests
{
    [Fact]
    public void Register_StampsPackageAndOrdersSkillsAndCapabilities()
    {
        var registry = new SkillRegistry();
        registry.Register(new PackageId("package.z"), PackageTrustLevel.Verified,
            new FakeSkillProvider("skill.z", new FakeCapability("cap.z"), new FakeCapability("cap.a")));
        registry.Register(new PackageId("package.a"), new FakeSkillProvider("skill.a", new FakeCapability("cap.m")));

        var skills = registry.GetAvailableSkills();

        Assert.Equal(["skill.a", "skill.z"], skills.Select(s => s.SkillId));
        Assert.Equal(["cap.a", "cap.z"], skills[1].Capabilities.Select(c => c.Name));
        Assert.All(skills[1].Capabilities, capability => Assert.Equal(new PackageId("package.z"), capability.Package));
        Assert.Equal(PackageTrustLevel.Verified, registry.GetTrust("skill.z"));
    }

    [Fact]
    public void Register_RejectsDuplicateSkillIdentity()
    {
        var registry = new SkillRegistry();
        registry.Register(new PackageId("package.a"), new FakeSkillProvider("skill.same", new FakeCapability("cap.a")));

        Assert.Throws<SkillRegistrationException>(() =>
            registry.Register(new PackageId("package.b"), new FakeSkillProvider("skill.same", new FakeCapability("cap.b"))));
    }

    [Fact]
    public void Register_RejectsDuplicateCapabilityIdentityAcrossSkills()
    {
        var registry = new SkillRegistry();
        registry.Register(new PackageId("package.a"), new FakeSkillProvider("skill.a", new FakeCapability("cap.same")));

        Assert.Throws<SkillRegistrationException>(() =>
            registry.Register(new PackageId("package.b"), new FakeSkillProvider("skill.b", new FakeCapability("cap.same"))));
    }

    [Fact]
    public void Register_RejectsBlankIdentityAndNonPositiveTimeout()
    {
        var registry = new SkillRegistry();

        Assert.Throws<SkillRegistrationException>(() =>
            registry.Register(new PackageId("package.a"), new FakeSkillProvider(" ", new FakeCapability("cap.a"))));
        Assert.Throws<SkillRegistrationException>(() =>
            registry.Register(new PackageId("package.a"),
                new FakeSkillProvider("skill.a", new FakeCapability("cap.a", TimeSpan.Zero))));
    }

    [Fact]
    public void Unregister_RemovesOnlyTheSpecifiedPackage()
    {
        var registry = new SkillRegistry();
        registry.Register(new PackageId("package.a"), new FakeSkillProvider("skill.a", new FakeCapability("cap.a")));
        registry.Register(new PackageId("package.b"), new FakeSkillProvider("skill.b", new FakeCapability("cap.b")));

        registry.Unregister(new PackageId("package.a"));

        Assert.Null(registry.Resolve("skill.a", "cap.a"));
        Assert.NotNull(registry.Resolve("skill.b", "cap.b"));
    }

    internal sealed class FakeSkillProvider(string skillId, params ICapability[] capabilities) : ISkillProvider
    {
        public string SkillId { get; } = skillId;
        public IReadOnlyList<ICapability> GetCapabilities() => capabilities;
        public IEnumerable<ITool> GetTools() => [];
    }

    internal sealed class FakeCapability(string name, TimeSpan? timeout = null) : ICapability
    {
        public CapabilityManifest Manifest { get; } = new(
            name, "1.0.0", "Test capability.", RiskLevel.Read, [], [], [],
            timeout ?? TimeSpan.FromSeconds(5), SupportsDryRun: true);

        public Task<SkillReport> PrepareAsync(
            CapabilityRequest request, IToolInvoker toolInvoker, CancellationToken ct = default) =>
            Task.FromResult(new SkillReport([], [], null));
    }
}
