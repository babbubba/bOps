using bOps.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// <see cref="ChatModelRegistry.RegisteredProviderIds"/> backs <c>GET /api/providers</c>
/// (ADR-0019) — an operator-facing "what providers could I configure" list, distinct from
/// <see cref="ChatModelRegistry.Create"/>'s resolve-and-construct path.
/// </summary>
public sealed class ChatModelRegistryTests
{
    [Fact]
    public void RegisteredProviderIds_IsEmpty_BeforeAnyPackageRegisters()
    {
        var registry = new ChatModelRegistry();

        Assert.Empty(registry.RegisteredProviderIds);
    }

    [Fact]
    public void RegisteredProviderIds_ListsEveryProviderId_ARegisteredPackageSupports()
    {
        var registry = new ChatModelRegistry();

        registry.Register(new PackageId("test.package"), new FakeModelProviderPackage(["OpenRouter", "Ollama"]));

        Assert.Equal(["Ollama", "OpenRouter"], registry.RegisteredProviderIds.OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void RegisteredProviderIds_DoesNotDuplicate_WhenTwoPackagesRegisterTheSameProviderId()
    {
        var registry = new ChatModelRegistry();

        registry.Register(new PackageId("test.package.a"), new FakeModelProviderPackage(["OpenRouter"]));
        registry.Register(new PackageId("test.package.b"), new FakeModelProviderPackage(["OpenRouter"]));

        Assert.Single(registry.RegisteredProviderIds);
    }

    private sealed class FakeModelProviderPackage(IReadOnlyList<string> supportedProviderIds) : IModelProviderPackage
    {
        public IReadOnlyList<string> SupportedProviderIds { get; } = supportedProviderIds;

        public IChatModel Create(ChatModelOptions options) => throw new NotSupportedException("Not needed by this test.");
    }
}
