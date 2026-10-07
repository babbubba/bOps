// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace bOps.Api.Tests;

/// <summary>
/// ADR-0044 (HARDEN-11) over the real composition: <c>GET /api/delegations/readiness</c> and <c>GET /api/skills</c> (viewer, Bearer
/// or browser session, no CSRF header for a GET, an invalid Bearer never falling back to the cookie), the strict start body
/// (duplicate keys only), the typed <c>400</c> codes for an unknown Capability and invalid input with a Sensitive value never
/// echoed, the typed limitation fields of the run and approval views, and the HARDEN-3 role-task resume guard after a
/// diagnosis-only run. No authority logic is mocked: the real policy file, profile source, reducer, runner and stores are used.
/// </summary>
public sealed class DelegationOperabilityEndpointsTests
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(20);
    private static readonly PackageId Package = new("bops.tests.operability");

    private const string ReadOnlyPolicy = """
        defaults:
          read: automatic
          low: automatic
          medium: approval
          high: approval
          critical: forbidden
        delegation:
          roles:
            discovery:
              tools: ["optest.info", "optest.partial"]
              maxRisk: read
              maxBlastRadius: single
              targets: [local]
              environments: [local]
              maxSteps: 15
              maxTokens: 150000
              maxDuration: 00:30:00
            diagnostic:
              tools: ["optest.info", "optest.partial"]
              maxRisk: read
              maxBlastRadius: single
              targets: [local]
              environments: [local]
              maxSteps: 15
              maxTokens: 150000
              maxDuration: 00:30:00
        """;

    private const string FullPolicy = """
        delegation:
          roles:
            discovery:
              tools: ["optest.info", "optest.partial"]
              maxRisk: read
              maxBlastRadius: single
              targets: [local]
              environments: [local]
              maxSteps: 15
              maxTokens: 150000
              maxDuration: 00:30:00
            diagnostic:
              skills: [optest.skill]
              capabilities: [optest.configure]
              tools: ["optest.info", "optest.partial"]
              maxRisk: read
              maxBlastRadius: single
              targets: [local]
              environments: [local]
              maxSteps: 15
              maxTokens: 150000
              maxDuration: 00:30:00
            remediation:
              skills: [optest.skill]
              capabilities: [optest.configure]
              tools: [optest.restart]
              maxRisk: high
              maxBlastRadius: single
              targets: [local]
              environments: [local]
              maxSteps: 6
              maxDuration: 00:05:00
            verification:
              tools: [optest.info]
              maxRisk: read
              maxBlastRadius: single
              targets: [local]
              environments: [local]
              maxSteps: 6
              maxDuration: 00:05:00
        """;

    // ---- fixtures ----

    private sealed class ReadTool(string name, ToolCallResult result) : ITool
    {
        public ToolManifest Manifest { get; } = new()
        {
            Name = name, Description = "Reads something.", Risk = RiskLevel.Read, Platforms = [CurrentPlatform.Id], Requires = [], Parameters = [],
        };

        public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) => Task.FromResult(result);
    }

    private sealed class RestartTool : IVerifiableTool
    {
        public ToolManifest Manifest { get; } = new()
        {
            Name = "optest.restart", Description = "Restarts.", Risk = RiskLevel.High, Platforms = [CurrentPlatform.Id], Requires = [], Parameters = [],
            Verification = new VerificationSpec("optest.info", [], "Reads."),
        };

        public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) => Task.FromResult(ToolCallResult.Success("ok"));

        public Task<VerificationOutcome> EvaluateVerificationAsync(ToolArguments originalArguments, ToolCallResult verificationToolResult, CancellationToken ct = default) =>
            Task.FromResult(new VerificationOutcome(VerificationStatus.Confirmed, "ok"));
    }

    internal static readonly IReadOnlyList<ToolParameter> ConfigureSchema =
    [
        new ToolParameter("service", ToolParameterType.String, "The service.", Required: true) { MinLength = 2, MaxLength = 8 },
        new ToolParameter("password", ToolParameterType.String, "A secret.", Required: false, Sensitive: true) { MinLength = 12 },
        new ToolParameter("mode", ToolParameterType.Enum, "How.", Required: false, AllowedValues: ["fast", "safe"]),
        new ToolParameter("retries", ToolParameterType.Integer, "Retries.", Required: false) { Minimum = 0, Maximum = 5 },
    ];

    private sealed class Capability(string name, IReadOnlyList<ToolParameter> schema) : ICapability
    {
        public int Calls;

        public CapabilityManifest Manifest { get; } = new(
            name, "2.1.0", $"Capability {name}.", RiskLevel.High, ["service.manage"], schema, [], TimeSpan.FromSeconds(5),
            SupportsDryRun: true, new VerificationSpec("optest.info", [], "Reads."));

        public Task<SkillReport> PrepareAsync(CapabilityRequest request, IToolInvoker toolInvoker, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new SkillReport([], [], null));
        }
    }

    private sealed class Skill(string id, params ICapability[] capabilities) : ISkillProvider
    {
        public string SkillId => id;

        public IReadOnlyList<ICapability> GetCapabilities() => capabilities;

        public IEnumerable<ITool> GetTools() => [];
    }

    private sealed class CountingModel(params ModelResponse[] responses) : IChatModel
    {
        private readonly QueueChatModel _inner = new(responses);
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public ChatModelDescriptor Descriptor => _inner.Descriptor;

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return _inner.CompleteAsync(request, ct);
        }
    }

    private static ModelResponse Plan(string expectedTool) => new(
        new JsonObject { ["rationale"] = "A plan.", ["steps"] = new JsonArray { new JsonObject { ["description"] = "look", ["expectedTool"] = expectedTool } } }.ToJsonString(), [], false, null);

    private static ModelResponse NoToolPlan() => new(
        new JsonObject { ["rationale"] = "No tool is needed.", ["steps"] = new JsonArray() }.ToJsonString(), [], false, null);

    private static ModelResponse[] DiagnosisScript(string citedTool = "optest.info") =>
    [
        Plan(citedTool),
        new ModelResponse(null, [new ModelToolCall("c1", citedTool, ToolArguments.Empty)], false, null),
        new ModelResponse("Read it.\n\nEvidence limitations\n- one read was partial.", [], true, null),
        NoToolPlan(),
        new ModelResponse("{\"findings\":[{\"summary\":\"Something is off.\",\"evidenceIds\":[\"discovery-0\"],\"severity\":\"low\"}]}", [], true, null),
    ];

    private sealed record Host(TestAppFactory Factory, HttpClient Client, CountingModel Model, Capability Configure) : IDisposable
    {
        public void Dispose()
        {
            Client.Dispose();
            Factory.Dispose();
        }
    }

    private static Host NewHost(string? policy, IReadOnlyList<string>? roles = null, CountingModel? model = null)
    {
        var counting = model ?? new CountingModel(DiagnosisScript());
#pragma warning disable CA2000 // Ownership passes to the returned Host.
        var factory = new TestAppFactory
#pragma warning restore CA2000
        {
            ChatModel = counting,
            PolicyEngine = new FixedPolicyEngine(PolicyMode.Automatic),
            Roles = roles ?? ["viewer", "operator", "approver"],
        };
        try
        {
            if (policy is not null)
            {
                File.WriteAllText(Path.Combine(factory.TempDirectory, "policy.yaml"), policy);
            }

            var tools = factory.Services.GetRequiredService<IToolRegistry>();
            tools.Register(Package, new ReadTool("optest.info", ToolCallResult.Success("cpu 12%")));
            tools.Register(Package, new ReadTool("optest.partial", ToolCallResult.Success("some events") with { Completeness = ToolResultCompleteness.Partial }));
            tools.Register(Package, new RestartTool());
            var configure = new Capability("optest.configure", ConfigureSchema);
            var skills = factory.Services.GetRequiredService<ISkillRegistry>();
            skills.Register(Package, new Skill("optest.skill", new Capability("optest.zeta", []), configure, new Capability("optest.alpha", [])));
            skills.Register(Package, new Skill("optest.another", new Capability("optest.beta", [])));
            tools.RefreshCapabilitiesAsync().GetAwaiter().GetResult();
            return new Host(factory, factory.CreateClient(), counting, configure);
        }
        catch
        {
            factory.Dispose();
            throw;
        }
    }

    private static async Task<JsonObject> ReadinessAsync(HttpClient client, string query = "")
    {
        var response = await client.GetAsync(new Uri($"/api/delegations/readiness{query}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
    }

    private static string[] States(JsonObject readiness) =>
        [.. readiness["roles"]!.AsArray().Select(role => role!["state"]!.GetValue<string>())];

    private static async Task<HttpResponseMessage> PostRawAsync(HttpClient client, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return await client.PostAsync(new Uri("/api/delegations", UriKind.Relative), content);
    }

    private static async Task<JsonObject> WaitForRunAsync(HttpClient client, Guid id, string status)
    {
        using var timeout = new CancellationTokenSource(PollTimeout);
        while (true)
        {
            var response = await client.GetAsync(new Uri($"/api/delegations/{id}", UriKind.Relative), timeout.Token);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var run = JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token))!.AsObject();
                if (run["status"]!.GetValue<string>() == status)
                {
                    return run;
                }
            }

            await Task.Delay(25, timeout.Token);
        }
    }

    private static async Task<Guid> StartDiagnosisAsync(HttpClient client)
    {
        var response = await PostRawAsync(client, """{"objective":"Why is it slow?"}""");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["delegationId"]!.GetValue<Guid>();
    }

    // ---- readiness: the matrix over HTTP ----

    [Fact]
    public async Task Readiness_OnAFreshInstall_DiscoveryAndDiagnosticMissing_TheOthersNotRequired_FourRolesInOrder()
    {
        using var host = NewHost(policy: null);

        var readiness = await ReadinessAsync(host.Client);

        Assert.False(readiness["remediation"]!.GetValue<bool>());
        Assert.False(readiness["ready"]!.GetValue<bool>());
        Assert.Equal("noFile", readiness["policy"]!.GetValue<string>());
        Assert.Equal(["Discovery", "Diagnostic", "Remediation", "Verification"], readiness["roles"]!.AsArray().Select(r => r!["role"]!.GetValue<string>()));
        Assert.Equal(["missing", "missing", "notRequired", "notRequired"], States(readiness));
        var discovery = readiness["roles"]![0]!;
        Assert.Equal("profile_missing", discovery["reasonCode"]!.GetValue<string>());
        Assert.Equal("Profile", discovery["dimension"]!.GetValue<string>());
        Assert.Equal("Discovery role: no usable profile is configured.", discovery["reason"]!.GetValue<string>());
        var remediation = readiness["roles"]![2]!.AsObject();
        Assert.Equal("not_required", remediation["reasonCode"]!.GetValue<string>());
        Assert.True(remediation.ContainsKey("dimension") && remediation["dimension"] is null);
        Assert.True(remediation.ContainsKey("reason") && remediation["reason"] is null);
        Assert.Equal(0, readiness["profileDriftCount"]!.GetValue<int>());
        Assert.True(readiness["evaluatedAtUtc"]!.GetValue<DateTimeOffset>() > DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public async Task Readiness_WithTheReadOnlyProfiles_IsReadyForADiagnosis_AndNotForARemediation()
    {
        using var host = NewHost(ReadOnlyPolicy);

        var diagnosis = await ReadinessAsync(host.Client, "?remediation=false");
        var remediation = await ReadinessAsync(host.Client, "?remediation=TRUE");

        Assert.True(diagnosis["ready"]!.GetValue<bool>());
        Assert.Equal("loaded", diagnosis["policy"]!.GetValue<string>());
        Assert.Equal(["ready", "ready", "notRequired", "notRequired"], States(diagnosis));
        Assert.False(remediation["ready"]!.GetValue<bool>());
        Assert.True(remediation["remediation"]!.GetValue<bool>());
        Assert.Equal(["ready", "ready", "missing", "missing"], States(remediation));
    }

    [Fact]
    public async Task Readiness_WithAllFourProfiles_IsReadyForBothShapes()
    {
        using var host = NewHost(FullPolicy);

        Assert.True((await ReadinessAsync(host.Client, "?remediation=true"))["ready"]!.GetValue<bool>());
        Assert.True((await ReadinessAsync(host.Client))["ready"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Readiness_WithAPolicyThatFailsToLoad_SaysSo_WithAFixedReason_AndNeverTheLoadersMessage()
    {
        using var host = NewHost("delegation:\n  roles:\n    discovery:\n      tools: [optest.info]\n      maxRisk: notarisk-zz9\n");

        var response = await host.Client.GetAsync(new Uri("/api/delegations/readiness?remediation=true", UriKind.Relative));
        var text = await response.Content.ReadAsStringAsync();
        var readiness = JsonNode.Parse(text)!.AsObject();

        Assert.Equal("loadFailed", readiness["policy"]!.GetValue<string>());
        Assert.Equal(["malformed", "malformed", "malformed", "malformed"], States(readiness));
        Assert.All(readiness["roles"]!.AsArray(), role => Assert.Equal("policy_load_failed", role!["reasonCode"]!.GetValue<string>()));
        Assert.Equal(DelegationReadinessEvaluator.PolicyLoadFailedReason, readiness["roles"]![0]!["reason"]!.GetValue<string>());
        Assert.DoesNotContain("notarisk-zz9", text, StringComparison.Ordinal);
        Assert.DoesNotContain("line", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("policy.yaml (", text, StringComparison.Ordinal);
        Assert.Equal(1, readiness["profileDriftCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task Readiness_CountsDrift_ButDriftNeverChangesReady()
    {
        using var host = NewHost(ReadOnlyPolicy.Replace("tools: [\"optest.info\", \"optest.partial\"]", "tools: [\"optest.info\", \"removed.tool\"]", StringComparison.Ordinal));

        var readiness = await ReadinessAsync(host.Client);

        Assert.True(readiness["ready"]!.GetValue<bool>());
        Assert.True(readiness["profileDriftCount"]!.GetValue<int>() >= 4, readiness.ToJsonString());
    }

    [Theory]
    [InlineData("?remediation=yes")]
    [InlineData("?remediation=1")]
    [InlineData("?remediation=")]
    [InlineData("?remediation=true&remediation=false")]
    public async Task Readiness_WithAValueThatIsNotTrueOrFalse_Is400(string query)
    {
        using var host = NewHost(ReadOnlyPolicy);

        var response = await host.Client.GetAsync(new Uri($"/api/delegations/readiness{query}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("'remediation' is true or false.", JsonNode.Parse(await response.Content.ReadAsStringAsync())!["message"]!.GetValue<string>());
    }

    // ---- authorization (ADR-0043 schemes) ----

    [Fact]
    public async Task ReadinessAndSkills_NeedTheViewerRole_AndAnonymousIsRefused()
    {
        using var host = NewHost(ReadOnlyPolicy, roles: ["viewer"]);
        using var anonymous = host.Factory.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync(new Uri("/api/delegations/readiness", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync(new Uri("/api/skills", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(new Uri("/api/delegations/readiness", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(new Uri("/api/skills", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task ReadinessAndSkills_WithoutTheViewerRole_Are403()
    {
        using var host = NewHost(ReadOnlyPolicy, roles: ["operator"]);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.GetAsync(new Uri("/api/delegations/readiness", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.GetAsync(new Uri("/api/skills", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task ReadinessAndSkills_WorkWithABrowserSession_WithoutTheCsrfHeader_AndAnInvalidBearerNeverFallsBackToTheCookie()
    {
        using var host = NewHost(ReadOnlyPolicy);
        using var client = host.Factory.CreateSessionClient();
        var cookie = await BrowserSession.SignInAsync(client, host.Factory.ApiKey);

        foreach (var uri in new[] { "/api/delegations/readiness?remediation=false", "/api/skills" })
        {
            using var withCookie = await BrowserSession.SendAsync(client, HttpMethod.Get, uri, cookie);
            using var badBearer = await BrowserSession.SendAsync(client, HttpMethod.Get, uri, cookie, bearer: "Bearer not-the-key");

            Assert.Equal(HttpStatusCode.OK, withCookie.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, badBearer.StatusCode);
        }
    }

    // ---- the skills catalog ----

    [Fact]
    public async Task Skills_ListsTheActivatedCatalog_InOrdinalOrder_WithExactlyTheContractsFields()
    {
        using var host = NewHost(ReadOnlyPolicy);

        var response = await host.Client.GetAsync(new Uri("/api/skills", UriKind.Relative));
        var text = await response.Content.ReadAsStringAsync();
        var skills = JsonNode.Parse(text)!["skills"]!.AsArray();

        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(["optest.another", "optest.skill"], skills.Select(s => s!["skillId"]!.GetValue<string>()));
        var skill = skills[1]!.AsObject();
        Assert.Equal(["skillId", "package", "trust", "capabilities"], skill.Select(p => p.Key));
        Assert.Equal("bops.tests.operability", skill["package"]!.GetValue<string>());
        Assert.Equal("Official", skill["trust"]!.GetValue<string>());
        Assert.Equal(["optest.alpha", "optest.configure", "optest.zeta"], skill["capabilities"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()));
        var configure = skill["capabilities"]![1]!.AsObject();
        Assert.Equal(["name", "version", "description", "risk", "supportsDryRun", "inputSchema"], configure.Select(p => p.Key));
        Assert.Equal("2.1.0", configure["version"]!.GetValue<string>());
        Assert.Equal("High", configure["risk"]!.GetValue<string>());
        Assert.True(configure["supportsDryRun"]!.GetValue<bool>());
        Assert.DoesNotContain("service.manage", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\\\\", text, StringComparison.Ordinal);
        Assert.DoesNotContain(".dll", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Skills_InputSchema_RoundTripsFieldForField_InDeclarationOrder_WithNullsWhereTheManifestHasNone()
    {
        using var host = NewHost(ReadOnlyPolicy);

        var skills = JsonNode.Parse(await host.Client.GetStringAsync(new Uri("/api/skills", UriKind.Relative)))!["skills"]!;
        var schema = skills[1]!["capabilities"]![1]!["inputSchema"]!.AsArray();

        Assert.Equal(ConfigureSchema.Select(p => p.Name), schema.Select(p => p!["name"]!.GetValue<string>()));
        var service = schema[0]!.AsObject();
        Assert.Equal(
            ["name", "type", "description", "required", "sensitive", "allowedValues", "minimum", "maximum", "minLength", "maxLength", "minItems", "maxItems"],
            service.Select(p => p.Key));
        Assert.Equal("String", service["type"]!.GetValue<string>());
        Assert.True(service["required"]!.GetValue<bool>());
        Assert.Equal(2, service["minLength"]!.GetValue<int>());
        Assert.Equal(8, service["maxLength"]!.GetValue<int>());
        Assert.Null(service["allowedValues"]);
        Assert.Null(service["minimum"]);
        Assert.True(schema[1]!["sensitive"]!.GetValue<bool>());
        Assert.Equal(["fast", "safe"], schema[2]!["allowedValues"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Equal("Enum", schema[2]!["type"]!.GetValue<string>());
        Assert.Equal(5, schema[3]!["maximum"]!.GetValue<double>());
    }

    [Fact]
    public async Task Skills_ShowsOnlyWhatIsActivated()
    {
        using var host = NewHost(ReadOnlyPolicy);
        host.Factory.Services.GetRequiredService<ISkillRegistry>().Unregister(Package);

        var skills = JsonNode.Parse(await host.Client.GetStringAsync(new Uri("/api/skills", UriKind.Relative)))!["skills"]!.AsArray();

        Assert.Empty(skills);
    }

    // ---- POST /api/delegations: strict body, typed codes ----

    [Theory]
    [InlineData("""{"objective":"a","objective":"b"}""")]
    [InlineData("""{"objective":"a","remediation":{"skillId":"optest.skill","capabilityName":"optest.configure","target":"local","environment":"local","input":{"service":"web","service":"db"}}}""")]
    [InlineData("""{"objective":"a","remediation":{"skillId":"optest.skill","skillId":"optest.another","capabilityName":"optest.configure","target":"local","environment":"local"}}""")]
    [InlineData("""{"objective":"a",}""")]
    [InlineData("""{"objective":"a" /* comment */}""")]
    public async Task S28_ADuplicateProperty_OrNonStrictJson_Is400MalformedJson_AndNoRunIsCreated(string body)
    {
        using var host = NewHost(FullPolicy);

        var response = await PostRawAsync(host.Client, body);
        var error = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("malformed_json", error["code"]!.GetValue<string>());
        Assert.Empty(JsonNode.Parse(await host.Client.GetStringAsync(new Uri("/api/delegations", UriKind.Relative)))!.AsArray());
        Assert.Equal(0, host.Model.Calls);
    }

    [Fact]
    public async Task TheStrictBody_ChangesNothingElse_CasingAndUnknownMembersBindAsBefore()
    {
        using var host = NewHost(ReadOnlyPolicy);

        var response = await PostRawAsync(host.Client, """{"Objective":"Why is it slow?","somethingElse":42,"MaxSteps":5}""");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task S11_AnUnknownCapability_Is400UnknownCapability_BeforeAnyRunOrModelCall()
    {
        using var host = NewHost(FullPolicy);

        var response = await PostRawAsync(host.Client, """{"objective":"fix","remediation":{"skillId":"optest.skill","capabilityName":"optest.nope","target":"local","environment":"local"}}""");
        var error = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("unknown_capability", error["code"]!.GetValue<string>());
        Assert.Equal(0, host.Model.Calls);
        Assert.Empty(JsonNode.Parse(await host.Client.GetStringAsync(new Uri("/api/delegations", UriKind.Relative)))!.AsArray());
    }

    [Theory]
    [InlineData("""{"service":"w"}""", "service")]
    [InlineData("""{"service":"web","unexpected":1}""", "unexpected")]
    [InlineData("""{"service":"web","mode":"slow"}""", "mode")]
    [InlineData("""{"service":"web","retries":9}""", "retries")]
    [InlineData("""{}""", "service")]
    public async Task S12_InvalidCapabilityInput_Is400CapabilityInputInvalid_NamingTheParameter_AndNoCapabilityCodeRuns(string input, string parameter)
    {
        using var host = NewHost(FullPolicy);

        var response = await PostRawAsync(host.Client, """{"objective":"fix","remediation":{"skillId":"optest.skill","capabilityName":"optest.configure","target":"local","environment":"local","input":""" + input + "}}");
        var error = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("capability_input_invalid", error["code"]!.GetValue<string>());
        Assert.Equal(parameter, error["parameter"]!.GetValue<string>());
        Assert.True(error["message"]!.GetValue<string>().Length <= 500);
        Assert.Equal(0, host.Configure.Calls);
        Assert.Equal(0, host.Model.Calls);
    }

    [Fact]
    public async Task S27_ASensitiveValue_IsNeverEchoedInTheError()
    {
        using var host = NewHost(FullPolicy);

        var response = await PostRawAsync(host.Client, """{"objective":"fix","remediation":{"skillId":"optest.skill","capabilityName":"optest.configure","target":"local","environment":"local","input":{"service":"web","password":"hunter2-x"}}}""");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("password", text, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
        Assert.DoesNotContain(" 9 ", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task S23_ReadinessGreen_ThenAChangeOutsideTheRemediationProfile_IsDeniedNamingTheDimension_BeforeAnyModelCall()
    {
        using var host = NewHost(FullPolicy);
        Assert.True((await ReadinessAsync(host.Client, "?remediation=true"))["ready"]!.GetValue<bool>());

        var response = await PostRawAsync(host.Client, """{"objective":"fix","remediation":{"skillId":"optest.skill","capabilityName":"optest.configure","target":"elsewhere","environment":"local","input":{"service":"web"}}}""");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var id = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["delegationId"]!.GetValue<Guid>();
        var run = await WaitForRunAsync(host.Client, id, nameof(DelegationStatus.Denied));

        Assert.Equal("Targets", run["denial"]!["dimension"]!.GetValue<string>());
        Assert.Equal(0, host.Model.Calls);
        Assert.True((await ReadinessAsync(host.Client, "?remediation=true"))["ready"]!.GetValue<bool>());
    }

    [Fact]
    public async Task S5_ARemediation_WithOnlyReadOnlyProfiles_IsDeniedNamingRemediation()
    {
        using var host = NewHost(ReadOnlyPolicy);

        var response = await PostRawAsync(host.Client, """{"objective":"fix","remediation":{"skillId":"optest.skill","capabilityName":"optest.configure","target":"local","environment":"local","input":{"service":"web"}}}""");
        var id = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["delegationId"]!.GetValue<Guid>();
        var run = await WaitForRunAsync(host.Client, id, nameof(DelegationStatus.Denied));

        Assert.Equal("Profile", run["denial"]!["dimension"]!.GetValue<string>());
        Assert.Equal("Remediation role: no usable profile is configured.", run["denial"]!["reason"]!.GetValue<string>());
        Assert.Equal(0, host.Model.Calls);
    }

    // ---- a diagnosis with the read-only profiles, and its typed limitations ----

    [Fact]
    public async Task ADiagnosisWithTheReadOnlyProfiles_CompletesAsADiagnosis_AndTheViewCarriesTypedLimitations()
    {
        using var host = NewHost(ReadOnlyPolicy, model: new CountingModel(DiagnosisScript("optest.partial")));

        var run = await WaitForRunAsync(host.Client, await StartDiagnosisAsync(host.Client), nameof(DelegationStatus.DiagnosisCompleted));

        var roles = run["roles"]!.AsArray();
        Assert.Equal(["Discovery", "Diagnostic"], roles.Select(r => r!["role"]!.GetValue<string>()));
        var limitation = roles[0]!["evidenceLimitations"]!.AsArray().Single()!;
        Assert.Equal("Partial", limitation["completeness"]!.GetValue<string>());
        Assert.Equal("discovery-0", limitation["evidenceId"]!.GetValue<string>());
        Assert.Equal("optest.partial", limitation["toolName"]!.GetValue<string>());
        Assert.Empty(roles[1]!["evidenceLimitations"]!.AsArray());
        Assert.Equal("Valid", roles[1]!["findingsReply"]!["status"]!.GetValue<string>());
        Assert.True(roles[1]!["findings"]![0]!["restsOnLimitedEvidence"]!.GetValue<bool>());
        Assert.DoesNotContain("some events", run.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFindingThatCitesNoLimitedEvidence_IsMarkedFalse_NotNull()
    {
        using var host = NewHost(ReadOnlyPolicy);

        var run = await WaitForRunAsync(host.Client, await StartDiagnosisAsync(host.Client), nameof(DelegationStatus.DiagnosisCompleted));

        Assert.False(run["roles"]![1]!["findings"]![0]!["restsOnLimitedEvidence"]!.GetValue<bool>());
    }

    [Fact]
    public async Task AHistoricalRunWithoutLimitations_IsShownAsNotRecorded_NeverAsNone()
    {
        using var host = NewHost(ReadOnlyPolicy);
        var actor = new ActorIdentity("api-user", "test-user", "Test User");
        var envelope = new AuthorityEnvelope(actor, 1, [], [], ["optest.info"], RiskLevel.Read, BlastRadius.Single, ["local"], ["local"], new DelegationBudget(5, 5, DateTimeOffset.UtcNow.AddHours(1)));
        var stored = new DelegationRun
        {
            Id = Guid.NewGuid(), Node = NodeId.Local, Actor = actor, Objective = "old", Status = DelegationStatus.DiagnosisCompleted,
            RootEnvelope = envelope with { Depth = 0 },
            Roles =
            [
                new DelegationRoleRun
                {
                    Agent = new AgentIdentity(AgentId.New(), AgentRoleKind.Diagnostic), Envelope = envelope, Status = DelegationRoleStatus.Completed, Consumed = BudgetConsumption.Empty,
                    Report = new SkillReport([new Evidence("discovery-0", EvidenceKind.Fact, "Read.", "x", "optest.info", DateTimeOffset.UtcNow)], [new Finding("finding-0", "s", ["discovery-0"], null)], null),
                },
            ],
            Journal = [], CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        await host.Factory.Services.GetRequiredService<IDelegationStore>().StartAsync(stored);

        var view = JsonNode.Parse(await host.Client.GetStringAsync(new Uri($"/api/delegations/{stored.Id}", UriKind.Relative)))!;

        var role = view["roles"]![0]!.AsObject();
        Assert.True(role.ContainsKey("evidenceLimitations") && role["evidenceLimitations"] is null);
        Assert.True(role.ContainsKey("findingsReply") && role["findingsReply"] is null);
        Assert.Null(role["findings"]![0]!["restsOnLimitedEvidence"]);
    }

    [Fact]
    public void TheApprovalView_ShowsLimitationsUnavailable_WhenTheRunCannotBeLoaded_NeverNone()
    {
        var plan = new PendingPlanApproval(
            Guid.NewGuid(), "hash", DateTimeOffset.UtcNow, "optest.skill", "optest.configure", "local", "local", "Single", "r", [],
            [new PendingPlanFinding("f", "s", null, ["discovery-0"])], null);

        var view = DelegationViews.WithLimitations(plan, run: null);

        Assert.False(view.Limitations!.Available);
        Assert.Empty(view.Limitations.Roles);
        Assert.Null(Assert.Single(view.Findings).RestsOnLimitedEvidence);
    }

    // ---- HARDEN-3 guard: a role task is resumed only through its delegation ----

    [Fact]
    public async Task AfterADiagnosis_OrdinaryResumeOfItsRoleTasks_Is409TaskDelegated()
    {
        using var host = NewHost(ReadOnlyPolicy);
        var id = await StartDiagnosisAsync(host.Client);
        await WaitForRunAsync(host.Client, id, nameof(DelegationStatus.DiagnosisCompleted));
        var tasks = await host.Factory.Services.GetRequiredService<ITaskStore>().ListByStatusAsync(AgentTaskStatus.Completed);
        var roleTasks = tasks.Where(task => task.DelegationId == id).ToList();

        Assert.Equal(2, roleTasks.Count);
        foreach (var task in roleTasks)
        {
            var response = await host.Client.PostAsync(new Uri($"/api/agents/tasks/{task.Id}/resume", UriKind.Relative), null);
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("task_delegated", body["code"]!.GetValue<string>());
        }

        var delegationResume = await host.Client.PostAsync(new Uri($"/api/delegations/{id}/resume", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.Conflict, delegationResume.StatusCode);
    }

    // ---- approvals carry the persisted limitations ----

    [Fact]
    public async Task ThePendingPlan_CarriesItsRunsPersistedLimitations()
    {
        var model = new CountingModel(
        [
            .. DiagnosisScript("optest.partial"),
        ]);
        using var host = NewHost(FullPolicy, model: model);
        host.Factory.Services.GetRequiredService<ISkillRegistry>().Unregister(Package);
        host.Factory.Services.GetRequiredService<ISkillRegistry>().Register(Package, new Skill("optest.skill", new PlanningCapability()));

        var response = await PostRawAsync(host.Client, """{"objective":"fix","remediation":{"skillId":"optest.skill","capabilityName":"optest.configure","target":"local","environment":"local","input":{"service":"web"}}}""");
        var id = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["delegationId"]!.GetValue<Guid>();

        JsonNode? pending = null;
        using var timeout = new CancellationTokenSource(PollTimeout);
        while (pending is null)
        {
            pending = JsonNode.Parse(await host.Client.GetStringAsync(new Uri("/api/delegations/approvals", UriKind.Relative), timeout.Token))!.AsArray()
                .FirstOrDefault(p => p!["delegationId"]!.GetValue<Guid>() == id);
            await Task.Delay(25, timeout.Token);
        }

        var limitations = pending["limitations"]!;
        Assert.True(limitations["available"]!.GetValue<bool>());
        Assert.Equal(["Discovery", "Diagnostic"], limitations["roles"]!.AsArray().Select(r => r!["role"]!.GetValue<string>()));
        Assert.True(limitations["roles"]![0]!["recorded"]!.GetValue<bool>());
        Assert.Equal("Partial", limitations["roles"]![0]!["evidenceLimitations"]![0]!["completeness"]!.GetValue<string>());
        Assert.Equal("Valid", limitations["roles"]![1]!["findingsReply"]!["status"]!.GetValue<string>());
        Assert.Contains(pending["findings"]!.AsArray(), f => f!["restsOnLimitedEvidence"]?.GetValue<bool>() == true);

        await host.Client.PostAsJsonAsync($"/api/delegations/{id}/approval", new RespondToPlanApprovalRequest(pending["planHash"]!.GetValue<string>(), false, "no"));
    }

    private sealed class PlanningCapability : ICapability
    {
        public CapabilityManifest Manifest { get; } = new(
            "optest.configure", "1.0.0", "Configures.", RiskLevel.High, [], ConfigureSchema, [], TimeSpan.FromSeconds(5),
            SupportsDryRun: true, new VerificationSpec("optest.info", [], "Reads."));

        public Task<SkillReport> PrepareAsync(CapabilityRequest request, IToolInvoker toolInvoker, CancellationToken ct = default) =>
            Task.FromResult(new SkillReport(
                [],
                [],
                new ExecutionPlan("optest.configure", "1.0.0", "Restart.", [new ExecutionPlanStep(0, "optest.restart", ToolArguments.Empty, "Restart.")])));
    }
}
