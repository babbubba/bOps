// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// ADR-0049 section 4 / rule A10: the <see cref="ICapabilityProbe"/> a package receives is a read-only view, never the host's mutable
/// registry and never something that can be cast to the host-only registrar.
/// </summary>
public sealed class PrerequisiteFacadeSecurityTests
{
    [Fact]
    public void ThePackageFacingProbe_IsADistinctReadOnlyObject()
    {
        var registry = new PrerequisiteRegistry(TimeProvider.System, TimeSpan.Zero);

        var probe = registry.AsCapabilityProbe();

        Assert.IsNotType<PrerequisiteRegistry>(probe);
        Assert.IsNotAssignableFrom<IPrerequisiteRegistrar>(probe);
        Assert.IsNotAssignableFrom<IPrerequisiteStateSource>(probe);
        Assert.Equal([typeof(ICapabilityProbe)], probe.GetType().GetInterfaces());
        Assert.Equal(["IsAvailableAsync"], probe.GetType().GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly).Select(m => m.Name).Order());
    }

    [Fact]
    public void TheRegistryItself_IsNotAnICapabilityProbe_SoItCannotBeHandedToAPackageByMistake() =>
        Assert.False(typeof(ICapabilityProbe).IsAssignableFrom(typeof(PrerequisiteRegistry)));

    [Fact]
    public async Task TheFacade_ObservesTheRegistry_WithTheBooleanCompatibilityMapping()
    {
        var registry = new PrerequisiteRegistry(TimeProvider.System, TimeSpan.Zero);
        registry.Register(new PackageId("package.a"), new PrerequisiteRegistryTests.FakeCheck("sample.up", PrerequisiteState.Available));
        registry.Register(new PackageId("package.a"), new PrerequisiteRegistryTests.FakeCheck("sample.degraded", PrerequisiteState.Degraded));
        registry.Register(new PackageId("package.a"), new PrerequisiteRegistryTests.FakeCheck("sample.down", PrerequisiteState.Unavailable));
        var probe = registry.AsCapabilityProbe();

        Assert.True(await probe.IsAvailableAsync("sample.up"));
        Assert.True(await probe.IsAvailableAsync("sample.degraded"));
        Assert.False(await probe.IsAvailableAsync("sample.down"));
        Assert.False(await probe.IsAvailableAsync("sample.unregistered"));
    }
}
