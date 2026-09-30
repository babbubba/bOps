// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text.Json;
using bOps.Abstractions;

namespace bOps.Packages.Providers.OpenAiCompatible.Tests;

/// <summary>
/// HARDEN-6 / ADR-0022: the schema this adapter sends is a projection of the typed manifest. A bound the manifest declares is
/// emitted as native JSON Schema, a bound it does not declare is never invented, and the closed argument object carries
/// <c>additionalProperties: false</c> exactly where the runtime already rejects unknown arguments.
/// </summary>
public sealed class ToolSchemaConstraintTests
{
    // Mirrors SystemToolManifests.Crashes; the architecture test pins the real manifest's constraints to the same values.
    private static readonly ToolManifest Crashes = new()
    {
        Name = "system.crashes",
        Description = "Reports recent crashes.",
        Risk = RiskLevel.Read,
        Platforms = ["linux", "windows"],
        Requires = [],
        Parameters =
        [
            new ToolParameter("sinceMinutes", ToolParameterType.Integer, "Crash window in minutes (1-10080, default 1440).", Required: false) { Minimum = 1, Maximum = 10080 },
            new ToolParameter("limit", ToolParameterType.Integer, "Maximum rows.", Required: false),
        ],
    };

    private static readonly ToolManifest EveryKind = new()
    {
        Name = "test.everything",
        Description = "One parameter per constraint kind.",
        Risk = RiskLevel.Read,
        Platforms = ["linux", "windows"],
        Requires = [],
        Parameters =
        [
            new ToolParameter("bounded", ToolParameterType.Number, "n") { Minimum = 0.5, Maximum = 2.5 },
            new ToolParameter("floorOnly", ToolParameterType.Integer, "n") { Minimum = 1 },
            new ToolParameter("ceilingOnly", ToolParameterType.Integer, "n") { Maximum = 9 },
            new ToolParameter("plainNumber", ToolParameterType.Integer, "n"),
            new ToolParameter("name", ToolParameterType.String, "s") { MinLength = 2, MaxLength = 8 },
            new ToolParameter("file", ToolParameterType.Path, "p") { MaxLength = 260 },
            new ToolParameter("free", ToolParameterType.String, "s"),
            new ToolParameter("files", ToolParameterType.PathList, "l") { MinItems = 1, MaxItems = 4 },
            new ToolParameter("anyFiles", ToolParameterType.PathList, "l"),
            new ToolParameter("mode", ToolParameterType.Enum, "m", AllowedValues: ["a", "b"]),
            new ToolParameter("flag", ToolParameterType.Boolean, "b"),
        ],
    };

    private const string NativeReply = """{"id":"gen-1","model":"m","choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""";

    private const string FallbackReply = """{"id":"gen-1","model":"m","choices":[{"message":{"role":"assistant","content":"{\"final\":\"ok\"}"},"finish_reason":"stop"}]}""";

    private static readonly string[] BoundKeys = ["minimum", "maximum", "minLength", "maxLength", "minItems", "maxItems"];

#pragma warning disable CA2000 // The handler/HttpClient pair lives for the test method; the handler is returned so tests can inspect what was sent.
    private static async Task<string> RequestBodyAsync(ToolManifest manifest, bool nativeToolCalling, string reply)
    {
        var handler = new StubHttpMessageHandler((HttpStatusCode.OK, reply));
        var model = new OpenAiCompatibleChatModel(
            new ChatModelOptions("OpenRouter", "https://openrouter.ai/api/v1", new SecretReference("environment", "TEST_KEY"), "openrouter/free", nativeToolCalling) { ResolvedApiKey = "test-key" },
            new HttpClient(handler));

        await model.CompleteAsync(new ModelRequest("system", [ChatTurn.FromUser("hello")], [manifest]));
        return handler.RequestBodies[0];
    }
#pragma warning restore CA2000

    private static async Task<JsonElement> SchemaOfAsync(ToolManifest manifest)
    {
        using var body = JsonDocument.Parse(await RequestBodyAsync(manifest, nativeToolCalling: true, NativeReply));
        return body.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("parameters").Clone();
    }

    [Fact]
    public async Task SystemCrashes_SinceMinutes_EmitsTypeMinimumAndMaximum()
    {
        var schema = await SchemaOfAsync(Crashes);

        var sinceMinutes = schema.GetProperty("properties").GetProperty("sinceMinutes");
        Assert.Equal("integer", sinceMinutes.GetProperty("type").GetString());
        Assert.Equal(1, sinceMinutes.GetProperty("minimum").GetInt32());
        Assert.Equal(10080, sinceMinutes.GetProperty("maximum").GetInt32());
        Assert.False(sinceMinutes.TryGetProperty("minLength", out _));
        Assert.False(sinceMinutes.TryGetProperty("maxItems", out _));
    }

    [Fact]
    public async Task SystemCrashes_ParameterWithoutConstraints_EmitsNoBounds()
    {
        var schema = await SchemaOfAsync(Crashes);

        var limit = schema.GetProperty("properties").GetProperty("limit");
        Assert.Equal("integer", limit.GetProperty("type").GetString());
        Assert.All(BoundKeys, key => Assert.False(limit.TryGetProperty(key, out _), key));
    }

    [Fact]
    public async Task ArgumentObject_IsClosed_AndNoPropertySchemaIsClosedOrInvented()
    {
        var schema = await SchemaOfAsync(Crashes);

        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.False, schema.GetProperty("additionalProperties").ValueKind);
        foreach (var property in schema.GetProperty("properties").EnumerateObject())
        {
            Assert.False(property.Value.TryGetProperty("additionalProperties", out _), property.Name);
        }
    }

    [Fact]
    public async Task WithoutNativeToolCalling_ThePromptCarriesTheSameConstraints()
    {
        using var body = JsonDocument.Parse(await RequestBodyAsync(Crashes, nativeToolCalling: false, FallbackReply));
        var prompt = string.Join(" ", Strings(body.RootElement));
        Assert.Contains("\"minimum\":1", prompt, StringComparison.Ordinal);
        Assert.Contains("\"maximum\":10080", prompt, StringComparison.Ordinal);
        Assert.Contains("\"additionalProperties\":false", prompt, StringComparison.Ordinal);
    }

    private static IEnumerable<string> Strings(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => [element.GetString()!],
        JsonValueKind.Array => element.EnumerateArray().SelectMany(Strings),
        JsonValueKind.Object => element.EnumerateObject().SelectMany(property => Strings(property.Value)),
        _ => [],
    };

    [Fact]
    public async Task ParameterlessTool_StillGetsAClosedEmptyObject()
    {
        var schema = await SchemaOfAsync(Crashes with { Parameters = [] });

        Assert.Equal(JsonValueKind.False, schema.GetProperty("additionalProperties").ValueKind);
        Assert.Empty(schema.GetProperty("properties").EnumerateObject());
    }

    [Fact]
    public async Task OnlyTheDeclaredBoundsAreEmitted_ForEveryParameterKind()
    {
        var properties = (await SchemaOfAsync(EveryKind)).GetProperty("properties");

        Assert.Equal((0.5, 2.5), (properties.GetProperty("bounded").GetProperty("minimum").GetDouble(), properties.GetProperty("bounded").GetProperty("maximum").GetDouble()));
        Assert.Equal(1, properties.GetProperty("floorOnly").GetProperty("minimum").GetInt32());
        Assert.False(properties.GetProperty("floorOnly").TryGetProperty("maximum", out _));
        Assert.Equal(9, properties.GetProperty("ceilingOnly").GetProperty("maximum").GetInt32());
        Assert.False(properties.GetProperty("ceilingOnly").TryGetProperty("minimum", out _));
        Assert.Equal((2, 8), (properties.GetProperty("name").GetProperty("minLength").GetInt32(), properties.GetProperty("name").GetProperty("maxLength").GetInt32()));
        Assert.Equal(260, properties.GetProperty("file").GetProperty("maxLength").GetInt32());
        Assert.False(properties.GetProperty("file").TryGetProperty("minLength", out _));
        Assert.Equal("array", properties.GetProperty("files").GetProperty("type").GetString());
        Assert.Equal("string", properties.GetProperty("files").GetProperty("items").GetProperty("type").GetString());
        Assert.Equal((1, 4), (properties.GetProperty("files").GetProperty("minItems").GetInt32(), properties.GetProperty("files").GetProperty("maxItems").GetInt32()));
        Assert.Equal(["a", "b"], properties.GetProperty("mode").GetProperty("enum").EnumerateArray().Select(value => value.GetString()));

        foreach (var name in new[] { "plainNumber", "free", "anyFiles", "mode", "flag" })
        {
            Assert.All(BoundKeys, key => Assert.False(properties.GetProperty(name).TryGetProperty(key, out _), $"{name}.{key}"));
        }
    }

    [Fact]
    public async Task ManifestToSchemaParity_EveryDeclaredBoundAppearsExactlyOnce_AndNothingElseDoes()
    {
        var properties = (await SchemaOfAsync(EveryKind)).GetProperty("properties");

        foreach (var parameter in EveryKind.Parameters)
        {
            var emitted = properties.GetProperty(parameter.Name).EnumerateObject()
                .Where(property => BoundKeys.Contains(property.Name, StringComparer.Ordinal))
                .ToDictionary(property => property.Name, property => property.Value.GetDouble(), StringComparer.Ordinal);

            var declared = new Dictionary<string, double>(StringComparer.Ordinal);
            AddIfPresent(declared, "minimum", parameter.Minimum);
            AddIfPresent(declared, "maximum", parameter.Maximum);
            AddIfPresent(declared, "minLength", parameter.MinLength);
            AddIfPresent(declared, "maxLength", parameter.MaxLength);
            AddIfPresent(declared, "minItems", parameter.MinItems);
            AddIfPresent(declared, "maxItems", parameter.MaxItems);

            Assert.Equal(declared.OrderBy(pair => pair.Key, StringComparer.Ordinal), emitted.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        }
    }

    [Fact]
    public async Task InapplicableConstraints_AreNeverProjected_EvenOnAnUnregisteredManifest()
    {
        var manifest = Crashes with
        {
            Parameters =
            [
                new ToolParameter("flag", ToolParameterType.Boolean, "b") { Minimum = 1, MaxLength = 3, MinItems = 1 },
                new ToolParameter("text", ToolParameterType.String, "s") { Maximum = 4, MaxItems = 2 },
            ],
        };

        var properties = (await SchemaOfAsync(manifest)).GetProperty("properties");

        Assert.All(BoundKeys, key => Assert.False(properties.GetProperty("flag").TryGetProperty(key, out _), key));
        Assert.All(BoundKeys, key => Assert.False(properties.GetProperty("text").TryGetProperty(key, out _), key));
    }

    private static void AddIfPresent(Dictionary<string, double> target, string key, double? value)
    {
        if (value is { } bound)
        {
            target[key] = bound;
        }
    }
}
