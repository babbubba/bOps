using bOps.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// Rule B3: a non-<see cref="RiskLevel.Read"/> tool must declare a <see cref="VerificationSpec"/>
/// and implement <see cref="IVerifiableTool"/>, or registration must fail. This is what makes
/// "every side-effecting action is verified" structural rather than a convention a package can skip.
/// </summary>
public sealed class ToolRegistryTests
{
    private static ToolRegistry CreateRegistry() => new(new AlwaysAvailableCapabilityProbe());

    [Fact]
    public void Register_AcceptsAReadTool()
    {
        var registry = CreateRegistry();

        registry.Register(new PackageId("test.package"), new FakeReadTool());

        Assert.NotNull(registry.Resolve("test.read"));
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
    }

    [Fact]
    public void SetEnabled_HidesEveryToolFromThatPackage_WithoutUnregistering()
    {
        var registry = CreateRegistry();
        var package = new PackageId("test.package");
        registry.Register(package, new FakeReadTool());

        registry.SetEnabled(package, false);

        Assert.Null(registry.Resolve("test.read"));
    }

    private sealed class ManifestOverrideTool(ToolManifest manifest) : ITool
    {
        public ToolManifest Manifest { get; } = manifest;

        public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) =>
            Task.FromResult(ToolCallResult.Success(null));
    }
}
