// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// ADR-0044 section 9.2: one runtime validator for tool arguments and Capability input, the input schema checked when the Skill
/// registers (review N-7, S26), input validated before Capability code on every invocation — delegated and not (operator decision
/// D) — and a Sensitive value never in a message (review N-12, S27).
/// </summary>
public sealed class CapabilityInputValidationTests
{
    private static readonly PackageId Package = new("sample.package");
    private static readonly ActorIdentity Operator = ActorIdentity.FromOperatingSystemUser("operator");

    private static ToolParameter P(string name, ToolParameterType type, bool required = false, bool sensitive = false, IReadOnlyList<string>? allowed = null) =>
        new(name, type, $"{name}.", required, sensitive, allowed);

    private static readonly IReadOnlyList<ToolParameter> Schema =
    [
        P("name", ToolParameterType.String, required: true) with { MinLength = 2, MaxLength = 5 },
        P("count", ToolParameterType.Integer) with { Minimum = 1, Maximum = 10 },
        P("ratio", ToolParameterType.Number) with { Minimum = 0.5, Maximum = 1.5 },
        P("force", ToolParameterType.Boolean),
        P("path", ToolParameterType.Path) with { MaxLength = 8 },
        P("wait", ToolParameterType.Duration),
        P("mode", ToolParameterType.Enum, allowed: ["fast", "safe"]),
        P("paths", ToolParameterType.PathList) with { MinItems = 1, MaxItems = 2 },
        P("color", ToolParameterType.String, allowed: ["red", "blue"]),
    ];

    private static ArgumentSchema.Violation? Validate(JsonObject input, IReadOnlyList<ToolParameter>? schema = null) =>
        ArgumentSchema.Validate(schema ?? Schema, ToolArguments.FromJson(input));

    private static JsonObject Minimal() => new() { ["name"] = "abc" };

    // ---- the shared validator: every type, every constraint, no coercion ----

    [Fact]
    public void AConformingInputOfEveryType_IsAccepted()
    {
        var input = new JsonObject
        {
            ["name"] = "abcde",
            ["count"] = 10,
            ["ratio"] = 0.5,
            ["force"] = false,
            ["path"] = "/var/log",
            ["wait"] = "00:05:00",
            ["mode"] = "safe",
            ["paths"] = new JsonArray("/a", "/b"),
            ["color"] = "red",
        };

        Assert.Null(Validate(input));
    }

    public static TheoryData<string, JsonNode?> WrongTypes() => new()
    {
        { "count", "3" }, { "count", 1.5 }, { "count", 3_000_000_000L }, { "ratio", "0.7" }, { "force", "true" }, { "force", 1 },
        { "path", 7 }, { "wait", 300 }, { "mode", 1 }, { "paths", "/a" }, { "paths", new JsonArray("/a", 3) }, { "paths", new JsonArray("/a", null) },
        { "name", new JsonArray("x") }, { "name", new JsonObject() },
    };

    [Theory]
    [MemberData(nameof(WrongTypes))]
    public void AValueOfAnotherJsonType_IsRefused_NeverCoerced(string parameter, JsonNode? value)
    {
        var input = Minimal();
        input[parameter] = value;

        var violation = Validate(input);

        Assert.NotNull(violation);
        Assert.Equal(parameter, violation.Parameter);
        Assert.Contains($"'{parameter}' is not a valid", violation.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string, JsonNode, bool> Boundaries() => new()
    {
        { "count", 1, true }, { "count", 10, true }, { "count", 0, false }, { "count", 11, false },
        { "ratio", 0.5, true }, { "ratio", 1.5, true }, { "ratio", 0.49, false }, { "ratio", 1.51, false },
        { "name", "ab", true }, { "name", "abcde", true }, { "name", "a", false }, { "name", "abcdef", false },
        { "path", "12345678", true }, { "path", "123456789", false },
        { "paths", new JsonArray("/a"), true }, { "paths", new JsonArray("/a", "/b"), true }, { "paths", new JsonArray(), false }, { "paths", new JsonArray("/a", "/b", "/c"), false },
        { "mode", "fast", true }, { "mode", "FAST", false }, { "mode", "other", false },
        { "color", "blue", true }, { "color", "green", false },
    };

    [Theory]
    [MemberData(nameof(Boundaries))]
    public void Constraints_AreInclusiveBounds_ExactMatches_AndNeverClamped(string parameter, JsonNode value, bool accepted)
    {
        ArgumentNullException.ThrowIfNull(value);
        var input = Minimal();
        input[parameter] = value.DeepClone();
        var before = input.ToJsonString();

        var violation = Validate(input);

        Assert.Equal(accepted, violation is null);
        Assert.Equal(before, input.ToJsonString());
    }

    [Fact]
    public void AnUnknownProperty_IsRefused_NeverDropped()
    {
        var input = Minimal();
        input["unexpected"] = "x";

        var violation = Validate(input)!;

        Assert.Equal("unexpected", violation.Parameter);
        Assert.Equal("Unknown argument 'unexpected'.", violation.Message);
    }

    [Fact]
    public void ARequiredParameter_MustBePresentAndNotNull_AnOptionalOneMayBeOmittedOrNull()
    {
        Assert.Equal("Missing required argument 'name'.", Validate([])!.Message);
        Assert.Equal("Missing required argument 'name'.", Validate(new JsonObject { ["name"] = null })!.Message);
        Assert.Null(Validate(new JsonObject { ["name"] = "abc", ["count"] = null }));
    }

    [Fact]
    public void AnEmptySchema_AcceptsOnlyAnEmptyInput()
    {
        Assert.Null(Validate([], []));
        Assert.NotNull(Validate(new JsonObject { ["x"] = 1 }, []));
    }

    [Fact]
    public void ASchemaThatDeclaresANameTwice_IsAViolation_NotAnException()
    {
        IReadOnlyList<ToolParameter> broken = [P("a", ToolParameterType.String), P("a", ToolParameterType.Integer)];

        var violation = Validate(new JsonObject { ["a"] = "x" }, broken);

        Assert.NotNull(violation);
        Assert.Null(violation.Parameter);
    }

    // ---- Sensitive values never appear in a message (S27) ----

    public static TheoryData<JsonNode> SensitiveValues() => [9_876_543, 0.000_017, "sekret-value-123", "x"];

    [Theory]
    [MemberData(nameof(SensitiveValues))]
    public void AViolationOnASensitiveParameter_NamesTheParameterAndTheConstraint_NeverTheValue(JsonNode value)
    {
        IReadOnlyList<ToolParameter> schema =
        [
            P("pin", ToolParameterType.Integer, sensitive: true) with { Minimum = 1, Maximum = 99 },
            P("rate", ToolParameterType.Number, sensitive: true) with { Minimum = 1, Maximum = 2 },
            P("token", ToolParameterType.String, sensitive: true) with { MinLength = 4, MaxLength = 8 },
            P("choice", ToolParameterType.String, sensitive: true, allowed: ["a"]),
        ];
        ArgumentNullException.ThrowIfNull(value);
        var parameter = value.GetValueKind() == System.Text.Json.JsonValueKind.String ? (value.ToString().Length > 1 ? "token" : "choice") : (value.ToString().Contains('.', StringComparison.Ordinal) ? "rate" : "pin");
        var text = value.ToJsonString().Trim('"');

        var violation = Validate(new JsonObject { [parameter] = value.DeepClone() }, schema)!;

        Assert.Equal(parameter, violation.Parameter);
        Assert.Contains(parameter, violation.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(text, violation.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + " ", violation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANonSensitiveViolation_StillSaysWhatItFound_AsToolsAlwaysHave()
    {
        IReadOnlyList<ToolParameter> schema = [P("count", ToolParameterType.Integer) with { Maximum = 3 }];

        Assert.Equal("Argument 'count' = 9 exceeds maximum 3.", Validate(new JsonObject { ["count"] = 9 }, schema)!.Message);
    }

    // ---- registration: the input schema must be internally valid (N-7, S26) ----

    public static TheoryData<string, ToolParameter[]?> InvalidSchemas() => new()
    {
        { "no schema", null },
        { "blank name", [P(" ", ToolParameterType.String)] },
        { "empty name", [P("", ToolParameterType.String)] },
        { "duplicate", [P("a", ToolParameterType.String), P("a", ToolParameterType.String)] },
        { "enum without values", [P("mode", ToolParameterType.Enum)] },
        { "enum with empty values", [P("mode", ToolParameterType.Enum, allowed: [])] },
        { "bound on a string", [P("a", ToolParameterType.String) with { Minimum = 1 }] },
        { "length on an integer", [P("a", ToolParameterType.Integer) with { MaxLength = 3 }] },
        { "items on a path", [P("a", ToolParameterType.Path) with { MinItems = 1 }] },
        { "non-finite bound", [P("a", ToolParameterType.Number) with { Maximum = double.PositiveInfinity }] },
        { "NaN bound", [P("a", ToolParameterType.Number) with { Minimum = double.NaN }] },
        { "negative length", [P("a", ToolParameterType.String) with { MinLength = -1 }] },
        { "minimum above maximum", [P("a", ToolParameterType.Integer) with { Minimum = 5, Maximum = 1 }] },
        { "undefined type", [P("a", (ToolParameterType)42)] },
    };

    [Theory]
    [MemberData(nameof(InvalidSchemas))]
    public void S26_ASkillWhoseCapabilityHasAnInvalidInputSchema_IsRefusedAtRegistration_AndNeverBecomesActive(string why, ToolParameter[]? schema)
    {
        var registry = new SkillRegistry();
        var capability = new SchemaCapability("sample.broken", schema!);

        var refused = Assert.Throws<SkillRegistrationException>(() => registry.Register(Package, new TestSkillProvider([capability])));

        Assert.Contains("invalid input schema", refused.Message, StringComparison.Ordinal);
        Assert.True(refused.Message.Length < 600, why);
        Assert.Empty(registry.GetAvailableSkills());
        Assert.Null(registry.Resolve("sample.skill", "sample.broken"));
    }

    [Fact]
    public void AValidSchemaOfEveryType_Registers()
    {
        var registry = new SkillRegistry();

        registry.Register(Package, new TestSkillProvider([new SchemaCapability("sample.ok", [.. Schema])]));

        Assert.Equal(Schema.Select(p => p.Name), Assert.Single(Assert.Single(registry.GetAvailableSkills()).Capabilities).InputSchema.Select(p => p.Name));
    }

    // ---- validation before Capability code, on the non-delegated path too (operator decision D) ----

    [Fact]
    public async Task NonDelegatedPreparation_RefusesInvalidInput_BeforeCapabilityCode_AndAuditsTheRefusal()
    {
        var (runner, audit, capability) = Runner();
        var request = new CapabilityRequest(ToolArguments.FromJson(new JsonObject { ["name"] = "a" }), "local", "test", BlastRadius.Single);

        var prepared = await runner.PrepareSkillAsync(Guid.NewGuid(), Operator, "sample.skill", "sample.ok", request);

        Assert.Equal(SkillPreparationStatus.Failed, prepared.Status);
        Assert.Contains("input is not valid", prepared.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(0, capability.Calls);
        var refusal = Assert.Single(audit.Events.OfType<SkillRunAuditEvent>(), e => e.Stage == SkillRunStage.Preparation);
        Assert.Equal(SkillRunOutcome.Failure, refusal.Outcome);
    }

    [Fact]
    public async Task NonDelegatedPreparation_WithConformingInput_StillReachesTheCapability()
    {
        var (runner, _, capability) = Runner();
        var request = new CapabilityRequest(ToolArguments.FromJson(Minimal()), "local", "test", BlastRadius.Single);

        var prepared = await runner.PrepareSkillAsync(Guid.NewGuid(), Operator, "sample.skill", "sample.ok", request);

        Assert.Equal(SkillPreparationStatus.Prepared, prepared.Status);
        Assert.Equal(1, capability.Calls);
    }

    [Fact]
    public async Task NonDelegatedPreparation_OfASensitiveViolation_NeverCarriesTheValueIntoTheResultOrTheAudit()
    {
        var registry = new SkillRegistry();
        var capability = new SchemaCapability("sample.secret", [P("password", ToolParameterType.String, required: true, sensitive: true) with { MinLength = 20 }]);
        registry.Register(Package, new TestSkillProvider([capability]));
        var audit = new RecordingAuditSink();
        var runner = new AgentRunner(
            new FakeChatModel(), new ToolRegistry(new AlwaysAvailableCapabilityProbe()), new StubPolicyEngine(PolicyMode.Automatic),
            new NeverCalledApprovalProvider(), audit, new InMemoryTaskStore(), TimeProvider.System, NullLogger<AgentRunner>.Instance,
            new AgentRunnerOptions(), registry);
        var request = new CapabilityRequest(ToolArguments.FromJson(new JsonObject { ["password"] = "hunter2-secret" }), "local", "test", BlastRadius.Single);

        var prepared = await runner.PrepareSkillAsync(Guid.NewGuid(), Operator, "sample.skill", "sample.secret", request);

        Assert.DoesNotContain("hunter2", prepared.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(audit.Events, e => System.Text.Json.JsonSerializer.Serialize<AuditEvent>(e).Contains("hunter2", StringComparison.Ordinal));
        Assert.Equal(0, capability.Calls);
    }

    // ---- the start check the API, the CLI and the orchestrator share ----

    [Fact]
    public void CapabilityRequestValidator_SaysUnknownCapability_InvalidInput_OrNothing()
    {
        var registry = new SkillRegistry();
        registry.Register(Package, new TestSkillProvider([new SchemaCapability("sample.ok", [.. Schema])]));

        var unknown = CapabilityRequestValidator.Check(registry, "sample.skill", "sample.missing", ToolArguments.Empty)!;
        var noSkill = CapabilityRequestValidator.Check(registry, "other.skill", "sample.ok", ToolArguments.Empty)!;
        var noRegistry = CapabilityRequestValidator.Check(null, "sample.skill", "sample.ok", ToolArguments.Empty)!;
        var invalid = CapabilityRequestValidator.Check(registry, "sample.skill", "sample.ok", ToolArguments.FromJson(new JsonObject { ["name"] = "a" }))!;
        var valid = CapabilityRequestValidator.Check(registry, "sample.skill", "sample.ok", ToolArguments.FromJson(Minimal()));

        Assert.All([unknown, noSkill, noRegistry], refusal => Assert.Equal("unknown_capability", refusal.Code));
        Assert.Equal("capability_input_invalid", invalid.Code);
        Assert.Equal("name", invalid.Parameter);
        Assert.True(invalid.Message.Length <= 500);
        Assert.Null(valid);
    }

    // ---- fixtures ----

    private static (AgentRunner Runner, RecordingAuditSink Audit, SchemaCapability Capability) Runner()
    {
        var registry = new SkillRegistry();
        var capability = new SchemaCapability("sample.ok", [.. Schema]);
        registry.Register(Package, new TestSkillProvider([capability]));
        var audit = new RecordingAuditSink();
        var runner = new AgentRunner(
            new FakeChatModel(), new ToolRegistry(new AlwaysAvailableCapabilityProbe()), new StubPolicyEngine(PolicyMode.Automatic),
            new NeverCalledApprovalProvider(), audit, new InMemoryTaskStore(), TimeProvider.System, NullLogger<AgentRunner>.Instance,
            new AgentRunnerOptions(), registry);
        return (runner, audit, capability);
    }

    private sealed class SchemaCapability(string name, IReadOnlyList<ToolParameter> schema) : ICapability
    {
        public int Calls { get; private set; }

        public CapabilityManifest Manifest { get; } = new(name, "1.0.0", "A test Capability.", RiskLevel.Read, [], schema, [], TimeSpan.FromSeconds(5), SupportsDryRun: true);

        public Task<SkillReport> PrepareAsync(CapabilityRequest request, IToolInvoker toolInvoker, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new SkillReport([], [], null));
        }
    }
}
