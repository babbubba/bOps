// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>ADR-0049 sections 1 and 3: registered is not available; required hides, optional degrades — for Tools and Skill Capabilities.</summary>
public sealed class PrerequisiteReadinessTests
{
    private static readonly PackageId Package = new("package.sample");

    [Fact]
    public async Task Tool_WithAnUnavailableRequiredPrerequisite_IsRegisteredButNeitherOfferedNorExecutable()
    {
        var (prerequisites, check) = Prerequisites("sample.debugger", PrerequisiteState.Unavailable);
        var tools = new ToolRegistry(prerequisites);
        tools.Register(Package, new ManifestTool(Manifest("sample.analyze", requires: ["sample.debugger"])));

        await tools.RefreshCapabilitiesAsync();

        var readiness = Assert.Single(tools.GetReadiness());
        Assert.True(readiness.Registered);
        Assert.False(readiness.Available);
        Assert.False(readiness.Degraded);
        Assert.Equal(["sample.debugger"], readiness.UnsatisfiedRequired);
        Assert.Empty(tools.GetAvailableManifests());
        Assert.Null(tools.Resolve("sample.analyze"));
        Assert.Null(tools.ResolveForExecution("sample.analyze"));

        check.State = PrerequisiteState.Available;
        await tools.RefreshCapabilitiesAsync();
        Assert.NotNull(tools.ResolveForExecution("sample.analyze"));
    }

    [Fact]
    public async Task Tool_WithAnUnavailableOptionalPrerequisite_IsAvailableAndDegraded()
    {
        var (prerequisites, _) = Prerequisites("sample.checker", PrerequisiteState.Unavailable);
        prerequisites.Register(new PackageId("package.debugger"), new PrerequisiteRegistryTests.FakeCheck("sample.debugger", PrerequisiteState.Available));
        var tools = new ToolRegistry(prerequisites);
        tools.Register(Package, new ManifestTool(Manifest("sample.analyze", requires: ["sample.debugger"], optional: ["sample.checker"])));

        await tools.RefreshCapabilitiesAsync();

        var readiness = Assert.Single(tools.GetReadiness());
        Assert.True(readiness.Available);
        Assert.True(readiness.Degraded);
        Assert.Equal(["sample.checker"], readiness.UnsatisfiedOptional);
        Assert.Single(tools.GetAvailableManifests());
        Assert.NotNull(tools.ResolveForExecution("sample.analyze"));
    }

    [Fact]
    public async Task Tool_WithADegradedRequiredPrerequisite_StaysAvailable()
    {
        var (prerequisites, _) = Prerequisites("sample.daemon", PrerequisiteState.Degraded);
        var tools = new ToolRegistry(prerequisites);
        tools.Register(Package, new ManifestTool(Manifest("sample.read", requires: ["sample.daemon"])));

        await tools.RefreshCapabilitiesAsync();

        Assert.NotNull(tools.Resolve("sample.read"));
    }

    [Fact]
    public async Task Tool_WithoutPrerequisites_IsUnchanged()
    {
        var tools = new ToolRegistry(new PrerequisiteRegistry(TimeProvider.System, TimeSpan.Zero));
        tools.Register(Package, new ManifestTool(Manifest("sample.read")));

        await tools.RefreshCapabilitiesAsync();

        var readiness = Assert.Single(tools.GetReadiness());
        Assert.True(readiness.Available);
        Assert.False(readiness.Degraded);
        Assert.NotNull(tools.Resolve("sample.read"));
    }

    [Fact]
    public void Tool_DeclaringAnIdBothRequiredAndOptional_OrABlankOptional_IsRefused()
    {
        var tools = new ToolRegistry(new AlwaysAvailableCapabilityProbe());

        Assert.Throws<ToolRegistrationException>(() =>
            tools.Register(Package, new ManifestTool(Manifest("sample.a", requires: ["sample.x"], optional: ["SAMPLE.X"]))));
        Assert.Throws<ToolRegistrationException>(() =>
            tools.Register(Package, new ManifestTool(Manifest("sample.b", optional: [" "]))));
        Assert.Empty(tools.GetReadiness());
    }

    [Fact]
    public async Task Capability_WithAnUnavailableRequiredPrerequisite_IsRegisteredButNotListedOrResolvable()
    {
        var (prerequisites, check) = Prerequisites("sample.client", PrerequisiteState.Unavailable);
        var skills = new SkillRegistry(prerequisites);
        skills.Register(Package, new SkillRegistryTests.FakeSkillProvider("sample.skill",
            new PrerequisiteCapability("sample.diagnose", requires: ["sample.client"]),
            new PrerequisiteCapability("sample.inspect")));

        await skills.RefreshPrerequisitesAsync();

        Assert.Equal(["sample.inspect"], Assert.Single(skills.GetAvailableSkills()).Capabilities.Select(capability => capability.Name));
        Assert.Null(skills.Resolve("sample.skill", "sample.diagnose"));
        Assert.NotNull(skills.Resolve("sample.skill", "sample.inspect"));
        var readiness = skills.GetReadiness();
        Assert.Equal(["sample.skill/sample.diagnose", "sample.skill/sample.inspect"], readiness.Select(item => item.Component.Id));
        Assert.All(readiness, item => Assert.True(item.Registered));
        Assert.False(readiness[0].Available);
        Assert.Equal(SystemComponentType.SkillCapability, readiness[0].Component.Type);

        check.State = PrerequisiteState.Available;
        await skills.RefreshPrerequisitesAsync();
        Assert.NotNull(skills.Resolve("sample.skill", "sample.diagnose"));
    }

    [Fact]
    public async Task Capability_WithAnUnavailableOptionalPrerequisite_IsAvailableAndDegraded()
    {
        var (prerequisites, _) = Prerequisites("sample.extension", PrerequisiteState.Unavailable);
        var skills = new SkillRegistry(prerequisites);
        skills.Register(Package, new SkillRegistryTests.FakeSkillProvider("sample.skill",
            new PrerequisiteCapability("sample.diagnose", optional: ["sample.extension"])));

        await skills.RefreshPrerequisitesAsync();

        var readiness = Assert.Single(skills.GetReadiness());
        Assert.True(readiness.Available);
        Assert.True(readiness.Degraded);
        Assert.NotNull(skills.Resolve("sample.skill", "sample.diagnose"));
    }

    [Fact]
    public async Task SkillRegistryWithoutAProbe_FailsClosedOnlyForCapabilitiesDeclaringRequiredPrerequisites()
    {
        var skills = new SkillRegistry();
        skills.Register(Package, new SkillRegistryTests.FakeSkillProvider("sample.skill",
            new PrerequisiteCapability("sample.diagnose", requires: ["sample.client"]),
            new SkillRegistryTests.FakeCapability("sample.legacy")));

        await skills.RefreshPrerequisitesAsync();

        Assert.Null(skills.Resolve("sample.skill", "sample.diagnose"));
        Assert.NotNull(skills.Resolve("sample.skill", "sample.legacy"));
    }

    [Fact]
    public void Capability_DeclaringAnIdBothRequiredAndOptional_IsRefused()
    {
        var skills = new SkillRegistry();

        Assert.Throws<SkillRegistrationException>(() => skills.Register(Package, new SkillRegistryTests.FakeSkillProvider("sample.skill",
            new PrerequisiteCapability("sample.diagnose", requires: ["sample.x"], optional: ["sample.x"]))));
    }

    [Fact]
    public void Usage_CollectsRequiredAndOptionalComponentsAcrossToolsAndCapabilities()
    {
        var (prerequisites, _) = Prerequisites("sample.shared", PrerequisiteState.Unavailable);
        var tools = new ToolRegistry(prerequisites);
        tools.Register(Package, new ManifestTool(Manifest("sample.b", requires: ["sample.shared"])));
        tools.Register(Package, new ManifestTool(Manifest("sample.a", requires: ["sample.shared"])));
        tools.Register(Package, new ManifestTool(Manifest("sample.c", optional: ["sample.shared"])));
        var skills = new SkillRegistry(prerequisites);
        skills.Register(Package, new SkillRegistryTests.FakeSkillProvider("sample.skill",
            new PrerequisiteCapability("sample.diagnose", requires: ["sample.shared"])));

        var usage = PrerequisiteUsage.For("sample.shared", tools.GetReadiness().Concat(skills.GetReadiness()));

        Assert.Equal(["skill-capability:sample.skill/sample.diagnose", "tool:sample.a", "tool:sample.b"], usage.RequiredBy.Select(component => component.ToString()));
        Assert.Equal(["tool:sample.c"], usage.OptionalBy.Select(component => component.ToString()));
        Assert.Empty(PrerequisiteUsage.For("sample.other", tools.GetReadiness()).RequiredBy);
    }

    private static (PrerequisiteRegistry Registry, PrerequisiteRegistryTests.FakeCheck Check) Prerequisites(string id, PrerequisiteState state)
    {
        var registry = new PrerequisiteRegistry(TimeProvider.System, TimeSpan.Zero);
        var check = new PrerequisiteRegistryTests.FakeCheck(id, state);
        registry.Register(new PackageId($"package.{id}"), check);
        return (registry, check);
    }

    private static ToolManifest Manifest(string name, IReadOnlyList<string>? requires = null, IReadOnlyList<string>? optional = null) => new()
    {
        Name = name,
        Description = "A test tool.",
        Risk = RiskLevel.Read,
        Platforms = [CurrentPlatform.Id],
        Requires = requires ?? [],
        OptionalRequires = optional ?? [],
        Parameters = [],
    };

    private sealed class ManifestTool(ToolManifest manifest) : ITool
    {
        public ToolManifest Manifest { get; } = manifest;

        public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) =>
            Task.FromResult(ToolCallResult.Success(null));
    }

    private sealed class PrerequisiteCapability(string name, IReadOnlyList<string>? requires = null, IReadOnlyList<string>? optional = null) : ICapability
    {
        public CapabilityManifest Manifest { get; } = new(name, "1.0.0", "Test capability.", RiskLevel.Read, [], [], [], TimeSpan.FromSeconds(5), SupportsDryRun: true)
        {
            Requires = requires ?? [],
            OptionalRequires = optional ?? [],
        };

        public Task<SkillReport> PrepareAsync(CapabilityRequest request, IToolInvoker toolInvoker, CancellationToken ct = default) =>
            Task.FromResult(new SkillReport([], [], null));
    }
}
