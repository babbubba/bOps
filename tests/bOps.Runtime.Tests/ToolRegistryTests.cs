// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// Rule B3: a non-<see cref="RiskLevel.Read"/> tool must declare a <see cref="VerificationSpec"/>
/// and implement <see cref="IVerifiableTool"/>, or registration must fail. This is what makes
/// "every side-effecting action is verified" structural rather than a convention a package can skip.
/// </summary>
public sealed class ToolRegistryTests
{
    [Fact]
    public void GetTrust_ReturnsTheHostAssignedLevel_AndFailsClosedForUnknownPackages()
    {
        var registry = CreateRegistry();
        var package = new PackageId("community.package");
        registry.Register(package, PackageTrustLevel.Community, new FakeReadTool());

        Assert.Equal(PackageTrustLevel.Community, registry.GetTrust(package));
        Assert.Equal(PackageTrustLevel.Unverified, registry.GetTrust(new PackageId("unknown.package")));
    }

    private static ToolRegistry CreateRegistry() => new(new AlwaysAvailableCapabilityProbe());

    [Fact]
    public void Register_AcceptsAReadTool()
    {
        var registry = CreateRegistry();

        registry.Register(new PackageId("test.package"), new FakeReadTool());

        Assert.NotNull(registry.Resolve("test.read"));
    }

    [Fact]
    public void LegacyRegisterOverloads_StampExplicitNotGovernedRequirements()
    {
        var registry = CreateRegistry();
        registry.Register(new PackageId("test.default"), new FakeReadTool("test.default"));
        registry.Register(new PackageId("test.trusted"), PackageTrustLevel.Community, new FakeReadTool("test.trusted"));

        Assert.Equal(EntitlementApplicability.NotGoverned, registry.ResolveForExecution("test.default")!.Entitlement.Applicability);
        Assert.Equal(EntitlementApplicability.NotGoverned, registry.ResolveForExecution("test.trusted")!.Entitlement.Applicability);
    }

    [Fact]
    public void ResolveForExecution_ReturnsTheHostStampedExecutionSnapshot()
    {
        var registry = CreateRegistry();
        var package = new PackageId("host.governed");
        var tool = new FakeReadTool();
        var requirement = new EntitlementRequirement(EntitlementApplicability.Governed);

        registry.Register(package, PackageTrustLevel.Community, requirement, tool);

        var resolved = Assert.IsType<ToolExecutionRegistration>(registry.ResolveForExecution("test.read"));
        Assert.Same(tool, resolved.Tool);
        Assert.Equal(package, resolved.Package);
        Assert.Equal(PackageTrustLevel.Community, resolved.Trust);
        Assert.Equal(requirement, resolved.Entitlement);
        Assert.Same(tool, registry.Resolve("test.read"));
    }

    [Fact]
    public void ResolveForExecution_KeepsRequirementsScopedToEachHostRegistration()
    {
        var registry = CreateRegistry();
        registry.Register(new PackageId("host.governed"), PackageTrustLevel.Official,
            new EntitlementRequirement(EntitlementApplicability.Governed), new FakeReadTool("test.governed"));
        registry.Register(new PackageId("host.oss"), PackageTrustLevel.Community,
            new EntitlementRequirement(EntitlementApplicability.NotGoverned), new FakeReadTool("test.oss"));

        Assert.Equal(EntitlementApplicability.Governed, registry.ResolveForExecution("test.governed")!.Entitlement.Applicability);
        Assert.Equal(EntitlementApplicability.NotGoverned, registry.ResolveForExecution("test.oss")!.Entitlement.Applicability);
    }

    [Fact]
    public void ResolveForExecution_ReturnsImmutableMetadataSnapshot()
    {
        var registry = CreateRegistry();
        var package = new PackageId("host.governed");
        registry.Register(package, PackageTrustLevel.Community,
            new EntitlementRequirement(EntitlementApplicability.Governed), new FakeReadTool());

        var changedCopy = registry.ResolveForExecution("test.read")! with
        {
            Entitlement = new EntitlementRequirement(EntitlementApplicability.NotGoverned),
            Trust = PackageTrustLevel.Unverified,
        };

        Assert.Equal(EntitlementApplicability.NotGoverned, changedCopy.Entitlement.Applicability);
        var resolvedAgain = registry.ResolveForExecution("test.read")!;
        Assert.Equal(EntitlementApplicability.Governed, resolvedAgain.Entitlement.Applicability);
        Assert.Equal(PackageTrustLevel.Community, resolvedAgain.Trust);
    }

    [Fact]
    public void Register_AcceptsANonReadTool_WithVerificationSpecAndIVerifiableTool()
    {
        var registry = CreateRegistry();

        registry.Register(new PackageId("test.package"), new FakeHighRiskTool());

        Assert.NotNull(registry.Resolve("test.highrisk"));
    }

    [Fact]
    public void Register_RejectsANonReadTool_WithNoVerificationSpec()
    {
        var registry = CreateRegistry();

        var ex = Assert.Throws<ToolRegistrationException>(
            () => registry.Register(new PackageId("test.package"), new UnverifiedHighRiskTool()));

        Assert.Equal("test.unverified", ex.ToolName);
        Assert.Null(registry.Resolve("test.unverified"));
    }

    [Fact]
    public void Register_RejectsANonReadTool_ThatDeclaresVerificationButDoesNotImplementIVerifiableTool()
    {
        var registry = CreateRegistry();

        var ex = Assert.Throws<ToolRegistrationException>(
            () => registry.Register(new PackageId("test.package"), new DeclaredButNotVerifiableTool()));

        Assert.Equal("test.declared-not-verifiable", ex.ToolName);
        Assert.Null(registry.Resolve("test.declared-not-verifiable"));
    }

    [Fact]
    public void Register_RejectsADuplicateToolName()
    {
        var registry = CreateRegistry();
        registry.Register(new PackageId("test.package"), new FakeReadTool());

        Assert.Throws<ToolRegistrationException>(
            () => registry.Register(new PackageId("test.package"), new FakeReadTool()));
    }

    [Fact]
    public void Register_StampsThePackageIdOntoTheManifest_RegardlessOfWhatThePackageClaims()
    {
        var registry = CreateRegistry();
        var tool = new FakeReadTool();

        registry.Register(new PackageId("real.package"), tool);

        Assert.Equal("real.package", tool.Manifest.Package.Value);
    }

    [Fact]
    public void Resolve_ReturnsNull_ForATotallyUnknownTool()
    {
        var registry = CreateRegistry();

        Assert.Null(registry.Resolve("nonexistent.tool"));
    }

    [Fact]
    public void GetAvailableManifests_ExcludesToolsForOtherPlatforms()
    {
        var registry = CreateRegistry();
        var otherPlatform = CurrentPlatform.Id == "windows" ? "linux" : "windows";
        var tool = new FakeReadTool();
        // Overwrite the manifest's platform list to something this test is not running on.
        var manifestForOtherPlatform = tool.Manifest with { Platforms = [otherPlatform] };
        registry.Register(new PackageId("test.package"), new ManifestOverrideTool(manifestForOtherPlatform));

        Assert.DoesNotContain(registry.GetAvailableManifests(), m => m.Name == tool.Manifest.Name);
        Assert.Null(registry.ResolveForExecution(tool.Manifest.Name));
    }

    [Fact]
    public async Task ResolveForExecution_ExcludesCapabilityInvisibleTools()
    {
        var registry = new ToolRegistry(new UnavailableCapabilityProbe());
        var tool = new FakeReadTool();
        var manifest = tool.Manifest with { Requires = ["test.capability"] };
        registry.Register(new PackageId("test.package"), new ManifestOverrideTool(manifest));

        await registry.RefreshCapabilitiesAsync();

        Assert.Null(registry.ResolveForExecution("test.read"));
    }

    [Fact]
    public void SetEnabled_HidesEveryToolFromThatPackage_WithoutUnregistering()
    {
        var registry = CreateRegistry();
        var package = new PackageId("test.package");
        registry.Register(package, new FakeReadTool());

        registry.SetEnabled(package, false);

        Assert.Null(registry.Resolve("test.read"));
        Assert.Null(registry.ResolveForExecution("test.read"));
    }

    [Fact]
    public void Unregister_RemovesTheToolEntirely_UnlikeSetEnabled()
    {
        var registry = CreateRegistry();
        var package = new PackageId("test.package");
        registry.Register(package, new FakeReadTool());

        registry.Unregister(package);

        Assert.Null(registry.Resolve("test.read"));
        Assert.Null(registry.ResolveForExecution("test.read"));
        Assert.DoesNotContain(registry.GetAvailableManifests(), m => m.Name == "test.read");
    }

    [Fact]
    public void Unregister_ThenReRegisteringTheSameToolName_Succeeds()
    {
        var registry = CreateRegistry();
        var package = new PackageId("test.package");
        registry.Register(package, new FakeReadTool());
        registry.Unregister(package);

        registry.Register(package, new FakeReadTool());

        Assert.NotNull(registry.Resolve("test.read"));
    }

    [Fact]
    public void Unregister_OnlyAffectsToolsFromThatPackage()
    {
        var registry = CreateRegistry();
        registry.Register(new PackageId("package.a"), new FakeReadTool());
        registry.Register(new PackageId("package.b"), new FakeHighRiskTool());

        registry.Unregister(new PackageId("package.a"));

        Assert.Null(registry.Resolve("test.read"));
        Assert.NotNull(registry.Resolve("test.highrisk"));
    }

    private sealed class ManifestOverrideTool(ToolManifest manifest) : ITool
    {
        public ToolManifest Manifest { get; } = manifest;

        public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) =>
            Task.FromResult(ToolCallResult.Success(null));
    }

    private sealed class UnavailableCapabilityProbe : ICapabilityProbe
    {
        public Task<bool> IsAvailableAsync(string capability, CancellationToken ct = default) => Task.FromResult(false);
    }
}
