// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Hosting;
using bOps.Packages.Docker;
using bOps.Packages.Filesystem;
using bOps.Packages.Providers.OpenAiCompatible;
using bOps.Packages.Providers.OpenAiCompatible.Tests;
using bOps.Packages.Web;
using bOps.Runtime;

namespace bOps.Architecture.Tests;

/// <summary>HARDEN-1 N-3: every first-party host tool is projected through the strict provider contract.</summary>
public sealed class HardenFourteenToolCatalogTests
{
    [Fact]
    [Trait("Category", "Harden14")]
    public async Task CompleteRegisteredFirstPartyCatalog_IsProviderSafe_WithValidSchemasAndHistoryPairing()
    {
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        using var webProvider = new WebToolProvider(new WebFetchOptions(), new WebSearchOptions());
        var registrations = FirstPartyToolComposition.Create(new FirstPartyToolCompositionOptions(
            new FilesystemToolProvider(new FilesystemPathPolicy([], [])),
            webProvider,
            new DockerClientFactory(),
            new DockerBuildOptions(),
            new DockerVolumeOptions()));
        FirstPartyToolComposition.Register(registry, registrations);
        await registry.RefreshCapabilitiesAsync();
        var manifests = registry.GetAvailableManifests();

        using var provider = new StrictOpenAiProvider(Response("catalog accepted"));
        using var httpClient = new HttpClient(provider);
        var model = new OpenAiCompatibleChatModel(
            new ChatModelOptions(
                "strict-test",
                "https://strict.invalid/v1",
                new SecretReference("environment", "HARDEN14_UNUSED"),
                "strict/catalog",
                true)
            {
                ResolvedApiKey = "test-only",
            },
            httpClient);
        var history = new ChatTurn[]
        {
            new() { Role = ChatRole.User, Content = "Inspect the complete catalog." },
            new()
            {
                Role = ChatRole.Assistant,
                ToolCalls =
                [
                    new ModelToolCall("catalog-a", "system.info", ToolArguments.Empty),
                    new ModelToolCall("catalog-b", "network.interfaces", ToolArguments.Empty),
                ],
            },
            new() { Role = ChatRole.Tool, ToolCallId = "catalog-a", Content = "system evidence" },
            new() { Role = ChatRole.Tool, ToolCallId = "catalog-b", Content = "network evidence" },
        };

        var response = await model.CompleteAsync(new ModelRequest("Use typed tools only.", history, manifests));

        Assert.True(response.IsFinal);
        Assert.Equal("catalog accepted", response.TextResponse);
        Assert.Equal(0, provider.Rejections);
        Assert.Equal(registrations.Count, manifests.Count);
        Assert.Equal(registrations.Count, manifests.Select(manifest => manifest.Name).Distinct(StringComparer.Ordinal).Count());

        using var request = JsonDocument.Parse(Assert.Single(provider.RequestBodies));
        var offered = request.RootElement.GetProperty("tools").EnumerateArray().ToArray();
        Assert.Equal(manifests.Count, offered.Length);
        Assert.Equal(offered.Length, offered.Select(tool => tool.GetProperty("function").GetProperty("name").GetString()).Distinct(StringComparer.Ordinal).Count());
        Assert.All(offered, tool =>
        {
            var function = tool.GetProperty("function");
            Assert.Matches("^[A-Za-z0-9_-]{1,64}$", function.GetProperty("name").GetString()!);
            var parameters = function.GetProperty("parameters");
            Assert.Equal(JsonValueKind.Object, parameters.ValueKind);
            Assert.Equal("object", parameters.GetProperty("type").GetString());
            Assert.Equal(JsonValueKind.Object, parameters.GetProperty("properties").ValueKind);
        });
        Assert.Null(StrictOpenAiProvider.Validate(request.RootElement.GetRawText()));
    }

    private static string Response(string content) => new JsonObject
    {
        ["model"] = "strict/catalog",
        ["choices"] = new JsonArray(new JsonObject
        {
            ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content },
            ["finish_reason"] = "stop",
        }),
    }.ToJsonString();

    private sealed class AlwaysAvailableCapabilityProbe : ICapabilityProbe
    {
        public Task<bool> IsAvailableAsync(string capability, CancellationToken ct = default) => Task.FromResult(true);
    }
}
