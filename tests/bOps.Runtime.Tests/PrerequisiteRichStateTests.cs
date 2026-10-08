// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// ADR-0049 section 1: the rich <see cref="PrerequisiteState"/> survives into component readiness. Required: Available → available;
/// Degraded → available and degraded; Unavailable/Error/Unknown → unavailable. Optional: Available → no degradation; every other
/// state → available and degraded.
/// </summary>
public sealed class PrerequisiteRichStateTests
{
    private static readonly PackageId Package = new("package.sample");

    [Theory]
    [InlineData(PrerequisiteState.Available, true, false)]
    [InlineData(PrerequisiteState.Degraded, true, true)]
    [InlineData(PrerequisiteState.Unavailable, false, false)]
    [InlineData(PrerequisiteState.Error, false, false)]
    [InlineData(PrerequisiteState.Unknown, false, false)]
    public void Required_Evaluate(PrerequisiteState state, bool available, bool degraded)
    {
        var readiness = ComponentReadiness.Evaluate(ComponentReference.Tool("sample.t"), ["sample.p"], [], _ => state);

        Assert.Equal(available, readiness.Available);
        Assert.Equal(degraded, readiness.Degraded);
        Assert.Equal(state is PrerequisiteState.Degraded ? ["sample.p"] : [], readiness.DegradedRequired);
        Assert.Equal(available ? [] : ["sample.p"], readiness.UnsatisfiedRequired);
    }

    [Theory]
    [InlineData(PrerequisiteState.Available, false)]
    [InlineData(PrerequisiteState.Degraded, true)]
    [InlineData(PrerequisiteState.Unavailable, true)]
    [InlineData(PrerequisiteState.Error, true)]
    [InlineData(PrerequisiteState.Unknown, true)]
    public void Optional_Evaluate(PrerequisiteState state, bool degraded)
    {
        var readiness = ComponentReadiness.Evaluate(ComponentReference.Tool("sample.t"), [], ["sample.p"], _ => state);

        Assert.True(readiness.Available);
        Assert.Equal(degraded, readiness.Degraded);
        Assert.Equal(degraded ? ["sample.p"] : [], readiness.UnsatisfiedOptional);
    }

    [Fact]
    public void AnUnavailableRequiredPrerequisite_NeverReportsDegraded_EvenWithADegradedOne()
    {
        var states = new Dictionary<string, PrerequisiteState> { ["sample.a"] = PrerequisiteState.Degraded, ["sample.b"] = PrerequisiteState.Unavailable };

        var readiness = ComponentReadiness.Evaluate(ComponentReference.Tool("sample.t"), ["sample.a", "sample.b"], [], id => states[id]);

        Assert.False(readiness.Available);
        Assert.False(readiness.Degraded);
    }

    [Fact]
    public void TheBooleanOverload_MapsTrueToAvailableAndFalseToUnavailable()
    {
        var up = ComponentReadiness.Evaluate(ComponentReference.Tool("sample.t"), ["sample.a"], ["sample.b"], _ => true);
        var down = ComponentReadiness.Evaluate(ComponentReference.Tool("sample.t"), ["sample.a"], ["sample.b"], _ => false);

        Assert.True(up.Available);
        Assert.False(up.Degraded);
        Assert.False(down.Available);
        Assert.Equal(["sample.b"], down.UnsatisfiedOptional);
    }

    /// <summary>The same matrix through the real registries fed by a real <see cref="PrerequisiteRegistry"/>, for Tools and Capabilities.</summary>
    [Theory]
    [InlineData(PrerequisiteState.Available, true, false, false)]
    [InlineData(PrerequisiteState.Degraded, true, true, true)]
    [InlineData(PrerequisiteState.Unavailable, false, false, true)]
    [InlineData(PrerequisiteState.Error, false, false, true)]
    public async Task Registries_KeepTheRichState_ForToolsAndCapabilities(
        PrerequisiteState state, bool requiredAvailable, bool requiredDegraded, bool optionalDegraded)
    {
        var prerequisites = new PrerequisiteRegistry(TimeProvider.System, TimeSpan.Zero);
        prerequisites.Register(Package, new PrerequisiteRegistryTests.FakeCheck("sample.p", state));
        var tools = new ToolRegistry(prerequisites);
        tools.Register(Package, new RichTool("sample.required", requires: ["sample.p"]));
        tools.Register(Package, new RichTool("sample.optional", optional: ["sample.p"]));
        var skills = new SkillRegistry(prerequisites);
        skills.Register(Package, new SkillRegistryTests.FakeSkillProvider("sample.skill",
            new PrerequisiteReadinessTests.PrerequisiteCapability("sample.required", requires: ["sample.p"]),
            new PrerequisiteReadinessTests.PrerequisiteCapability("sample.optional", optional: ["sample.p"])));

        await prerequisites.RefreshAsync();
        await tools.RefreshCapabilitiesAsync();
        await skills.RefreshPrerequisitesAsync();

        foreach (var readiness in tools.GetReadiness().Concat(skills.GetReadiness()))
        {
            if (readiness.Component.Id.EndsWith("sample.required", StringComparison.Ordinal))
            {
                Assert.Equal(requiredAvailable, readiness.Available);
                Assert.Equal(requiredDegraded, readiness.Degraded);
            }
            else
            {
                Assert.True(readiness.Available);
                Assert.Equal(optionalDegraded, readiness.Degraded);
            }
        }

        Assert.Equal(requiredAvailable, tools.Resolve("sample.required") is not null);
        Assert.NotNull(tools.Resolve("sample.optional"));
        Assert.Equal(requiredAvailable, skills.Resolve("sample.skill", "sample.required") is not null);
        Assert.NotNull(skills.Resolve("sample.skill", "sample.optional"));
    }

    [Fact]
    public async Task APrerequisiteNobodyRegistered_IsUnknown_SoARequiredComponentIsUnavailableAndAnOptionalOneDegraded()
    {
        var prerequisites = new PrerequisiteRegistry(TimeProvider.System, TimeSpan.Zero);
        var tools = new ToolRegistry(prerequisites);
        tools.Register(Package, new RichTool("sample.required", requires: ["sample.ghost"]));
        tools.Register(Package, new RichTool("sample.optional", optional: ["sample.ghost"]));

        await prerequisites.RefreshAsync();
        await tools.RefreshCapabilitiesAsync();

        Assert.Null(tools.Resolve("sample.required"));
        Assert.NotNull(tools.Resolve("sample.optional"));
        Assert.True(tools.GetReadiness().Single(r => r.Component.Id == "sample.optional").Degraded);
    }

    [Fact]
    public async Task TheOldBooleanProbePath_StillWorks_TrueIsAvailableFalseIsUnavailable()
    {
        var tools = new ToolRegistry(new FixedProbe(true));
        tools.Register(Package, new RichTool("sample.required", requires: ["sample.p"]));
        tools.Register(Package, new RichTool("sample.optional", optional: ["sample.q"]));
        await tools.RefreshCapabilitiesAsync();
        Assert.NotNull(tools.Resolve("sample.required"));
        Assert.All(tools.GetReadiness(), readiness => Assert.False(readiness.Degraded));

        var down = new ToolRegistry(new FixedProbe(false));
        down.Register(Package, new RichTool("sample.required", requires: ["sample.p"]));
        await down.RefreshCapabilitiesAsync();
        Assert.Null(down.Resolve("sample.required"));
    }

    private sealed class FixedProbe(bool value) : ICapabilityProbe
    {
        public Task<bool> IsAvailableAsync(string capability, CancellationToken ct = default) => Task.FromResult(value);
    }

    private sealed class RichTool(string name, IReadOnlyList<string>? requires = null, IReadOnlyList<string>? optional = null) : ITool
    {
        public ToolManifest Manifest { get; } = new()
        {
            Name = name,
            Description = "A test tool.",
            Risk = RiskLevel.Read,
            Platforms = [CurrentPlatform.Id],
            Requires = requires ?? [],
            OptionalRequires = optional ?? [],
            Parameters = [],
        };

        public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) =>
            Task.FromResult(ToolCallResult.Success(null));
    }
}
