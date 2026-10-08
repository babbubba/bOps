// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Architecture.Tests;

/// <summary>ADR-0049: the prerequisite, manifest and system-message contracts are additive, round-trip and stay bounded.</summary>
public sealed class PrerequisiteContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new();
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset Checked = new(2026, 10, 8, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Enums_HaveFrozenNumericValues()
    {
        Assert.Equal([0, 1, 2, 3, 4], Enum.GetValues<PrerequisiteState>().Select(value => (int)value));
        Assert.Equal(PrerequisiteState.Unknown, default(PrerequisiteState));
        Assert.Equal(0, (int)PrerequisiteRequirement.Required);
        Assert.Equal(1, (int)PrerequisiteRequirement.Optional);
        Assert.Equal([0, 1, 2, 3], Enum.GetValues<SystemMessageSeverity>().Select(value => (int)value));
        Assert.Equal(["Information", "Warning", "Error", "Critical"], Enum.GetNames<SystemMessageSeverity>());
        Assert.Equal(6, (int)SystemComponentType.Prerequisite);
    }

    [Theory]
    [InlineData(PrerequisiteState.Available, true)]
    [InlineData(PrerequisiteState.Degraded, true)]
    [InlineData(PrerequisiteState.Unavailable, false)]
    [InlineData(PrerequisiteState.Error, false)]
    [InlineData(PrerequisiteState.Unknown, false)]
    public void IsSatisfied_IsTheBooleanCompatibilityView(PrerequisiteState state, bool expected) =>
        Assert.Equal(expected, state.IsSatisfied());

    [Fact]
    public void OldCapabilityManifestConstructor_StillWorks_AndPrerequisitesDefaultToEmpty()
    {
        var manifest = new CapabilityManifest("sample.inspect", "1.0.0", "Inspects.", RiskLevel.Read, [], [], [], TimeSpan.FromSeconds(30), SupportsDryRun: false);

        Assert.Empty(manifest.Requires);
        Assert.Empty(manifest.OptionalRequires);
        Assert.Null(manifest.Verification);
        Assert.Null(manifest.RollbackDescription);
    }

    [Fact]
    public void OldToolManifestInitializer_StillWorks_RequiresKeepsItsValue_AndOptionalDefaultsToEmpty()
    {
        var manifest = new ToolManifest
        {
            Name = "sample.read",
            Description = "Reads.",
            Risk = RiskLevel.Read,
            Platforms = ["linux", "windows"],
            Requires = ["sample.daemon"],
            Parameters = [],
        };

        Assert.Equal(["sample.daemon"], manifest.Requires);
        Assert.Empty(manifest.OptionalRequires);
    }

    [Fact]
    public void LegacyManifestJson_WithoutOptionalRequires_DeserializesToEmptyLists()
    {
        const string toolJson = """{"Name":"sample.read","Description":"Reads.","Risk":0,"Platforms":["linux"],"Requires":["sample.daemon"],"Parameters":[]}""";
        const string capabilityJson = """{"Name":"sample.inspect","Version":"1.0.0","Description":"Inspects.","Risk":0,"RequiredPermissions":[],"InputSchema":[],"OutputSchema":[],"Timeout":"00:00:30","SupportsDryRun":false}""";

        var tool = JsonSerializer.Deserialize<ToolManifest>(toolJson, JsonOptions)!;
        var capability = JsonSerializer.Deserialize<CapabilityManifest>(capabilityJson, JsonOptions)!;

        Assert.Equal(["sample.daemon"], tool.Requires);
        Assert.Empty(tool.OptionalRequires);
        Assert.Empty(capability.Requires);
        Assert.Empty(capability.OptionalRequires);
    }

    [Fact]
    public void Manifests_RoundTripRequiredAndOptionalPrerequisites()
    {
        var tool = new ToolManifest
        {
            Name = "sample.analyze",
            Description = "Analyzes.",
            Risk = RiskLevel.Read,
            Platforms = ["windows"],
            Requires = ["sample.debugger"],
            OptionalRequires = ["sample.checker"],
            Parameters = [],
        };
        var capability = new CapabilityManifest("sample.diagnose", "1.0.0", "Diagnoses.", RiskLevel.Read, [], [], [], TimeSpan.FromSeconds(30), false)
        {
            Requires = ["sample.client"],
            OptionalRequires = ["sample.extension"],
        };

        var toolRoundTrip = RoundTrip(tool);
        var capabilityRoundTrip = RoundTrip(capability);

        Assert.Equal(tool.Requires, toolRoundTrip.Requires);
        Assert.Equal(tool.OptionalRequires, toolRoundTrip.OptionalRequires);
        Assert.Equal(capability.Requires, capabilityRoundTrip.Requires);
        Assert.Equal(capability.OptionalRequires, capabilityRoundTrip.OptionalRequires);
        Assert.Equal(capability.Timeout, capabilityRoundTrip.Timeout);
    }

    [Fact]
    public void RichCheckResult_RoundTripsWithMetadata()
    {
        var result = new PrerequisiteCheckResult
        {
            Id = "windows.debugger.kd",
            State = PrerequisiteState.Unavailable,
            Code = "executable-not-found",
            Message = "Microsoft Debugging Tools for Windows is required.",
            CheckedAtUtc = Checked,
            Metadata = OperationalMetadata.From(new JsonObject
            {
                ["requiredExecutable"] = "kd.exe",
                ["installComponent"] = "Debugging Tools for Windows",
                ["searchedLocations"] = new JsonArray("C:\\Program Files (x86)\\Windows Kits\\10\\Debuggers\\x64"),
                ["searchedLocationCount"] = 1,
                ["onPath"] = false,
            }),
        };

        var json = JsonSerializer.Serialize(result, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<PrerequisiteCheckResult>(json, JsonOptions)!;

        Assert.Equal(result, roundTrip);
        Assert.False(roundTrip.IsSatisfied);
        Assert.DoesNotContain("IsSatisfied", json, StringComparison.Ordinal);
        Assert.True(roundTrip.Metadata.TryGetString("requiredExecutable", out var executable));
        Assert.Equal("kd.exe", executable);
        Assert.Contains("\"metadata\":{", JsonSerializer.Serialize(result, WebOptions), StringComparison.Ordinal);
    }

    [Fact]
    public void DescriptorOutcomeStateRecordAndMessage_RoundTrip()
    {
        var descriptor = new PrerequisiteDescriptor("sample.debugger", "Sample debugger", "Reads crash dumps.", PrerequisiteKind.Executable)
        {
            Remediation = "Install the sample debugger.",
            CheckTimeout = TimeSpan.FromSeconds(5),
            Metadata = OperationalMetadata.From(new JsonObject { ["requiredExecutable"] = "sample.exe" }),
        };
        var outcome = new PrerequisiteCheckOutcome(PrerequisiteState.Degraded, "version-old", "An older version was found.");
        var state = new PrerequisiteStateRecord
        {
            Node = NodeId.Local,
            PrerequisiteId = "sample.debugger",
            State = PrerequisiteState.Available,
            Code = "available",
            Message = "Found.",
            CheckedAtUtc = Checked,
            ChangedAtUtc = Checked.AddMinutes(-5),
        };
        var message = SampleMessage() with { TaskId = Guid.NewGuid(), ComponentType = SystemComponentType.Prerequisite, ComponentId = "sample.debugger" };

        Assert.Equal(descriptor, RoundTrip(descriptor));
        Assert.Equal(outcome, RoundTrip(outcome));
        Assert.Equal(state, RoundTrip(state));
        Assert.Equal("Available|available", RoundTrip(state).Fingerprint);
        Assert.Equal(message, RoundTrip(message));
        var query = new SystemMessageQuery { FromUtc = Checked, ToUtc = Checked.AddDays(1), Severity = SystemMessageSeverity.Warning, Text = "docker", PageSize = 20, Cursor = "opaque" };
        Assert.Equal(query, RoundTrip(query));
    }

    [Fact]
    public void Metadata_IsBounded()
    {
        var tooMany = new JsonObject();
        for (var i = 0; i <= OperationalMetadata.MaxEntries; i++)
        {
            tooMany[$"key{i}"] = i;
        }

        Assert.False(OperationalMetadata.TryFrom(tooMany, out _, out _));
        Assert.False(OperationalMetadata.TryFrom(new JsonObject { [new string('k', OperationalMetadata.MaxKeyLength + 1)] = 1 }, out _, out _));
        Assert.False(OperationalMetadata.TryFrom(new JsonObject { ["1key"] = 1 }, out _, out _));
        Assert.False(OperationalMetadata.TryFrom(new JsonObject { ["key with space"] = 1 }, out _, out _));
        Assert.False(OperationalMetadata.TryFrom(new JsonObject { ["text"] = new string('x', OperationalMetadata.MaxStringLength + 1) }, out _, out _));
        Assert.False(OperationalMetadata.TryFrom(new JsonObject { ["nested"] = new JsonObject { ["a"] = 1 } }, out _, out _));
        Assert.False(OperationalMetadata.TryFrom(new JsonObject { ["numbers"] = new JsonArray(1, 2) }, out _, out _));
        Assert.False(OperationalMetadata.TryFrom(new JsonObject { ["items"] = new JsonArray(Enumerable.Range(0, OperationalMetadata.MaxArrayItems + 1).Select(i => (JsonNode?)$"i{i}").ToArray()) }, out _, out _));

        var large = new JsonObject();
        for (var i = 0; i < 10; i++)
        {
            large[$"part{i}"] = new string('x', OperationalMetadata.MaxStringLength);
        }

        Assert.False(OperationalMetadata.TryFrom(large, out _, out var problem));
        Assert.Contains("bytes", problem, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => OperationalMetadata.From(large));
    }

    [Fact]
    public void Metadata_AcceptsJsonNativeScalarsAndStringArrays_AndIsACopy()
    {
        var source = new JsonObject { ["name"] = "kd.exe", ["count"] = 3, ["ratio"] = 0.5, ["found"] = true, ["missing"] = null, ["components"] = new JsonArray("tool:a", "tool:b") };

        var metadata = OperationalMetadata.From(source);
        source["name"] = "changed";
        var copy = metadata.ToJson();
        copy["name"] = "changed again";

        Assert.Equal(6, metadata.Count);
        Assert.True(metadata.TryGetString("name", out var name));
        Assert.Equal("kd.exe", name);
        Assert.False(metadata.TryGetString("count", out _));
        Assert.Same(OperationalMetadata.Empty, OperationalMetadata.From([]));
    }

    [Theory]
    [InlineData("password")]
    [InlineData("dbPassword")]
    [InlineData("client_secret")]
    [InlineData("apiKey")]
    [InlineData("API-KEY")]
    [InlineData("accessToken")]
    [InlineData("token")]
    [InlineData("connectionString")]
    [InlineData("Authorization")]
    [InlineData("privateKey")]
    [InlineData("credentials")]
    [InlineData("session.cookie")]
    public void Metadata_RefusesKeysThatNameASecret(string key)
    {
        Assert.False(OperationalMetadata.TryFrom(new JsonObject { [key] = "x" }, out _, out var problem));
        Assert.Contains("secret", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("promptTokens")]
    [InlineData("tokenCount")]
    [InlineData("requiredExecutable")]
    [InlineData("endpointHost")]
    public void Metadata_AllowsOrdinaryKeys(string key) =>
        Assert.True(OperationalMetadata.TryFrom(new JsonObject { [key] = "x" }, out _, out _));

    [Theory]
    [InlineData("https://admin:hunter2@db.example.test:5432/app")]
    [InlineData("postgres://user@db.example.test/app")]
    [InlineData("Bearer eyJhbGciOiJIUzI1NiJ9")]
    public void Metadata_RefusesValuesCarryingCredentials(string value)
    {
        Assert.False(OperationalMetadata.TryFrom(new JsonObject { ["endpoint"] = value }, out _, out var problem));
        Assert.Contains("secret", problem, StringComparison.Ordinal);
        Assert.DoesNotContain(value, problem, StringComparison.Ordinal);
        Assert.False(OperationalMetadata.TryFrom(new JsonObject { ["endpoints"] = new JsonArray(value) }, out _, out _));
    }

    [Fact]
    public void Metadata_AllowsAPlainUrl() =>
        Assert.True(OperationalMetadata.TryFrom(new JsonObject { ["endpoint"] = "https://search.example.test/search?q=a@b" }, out _, out _));

    [Fact]
    public void MetadataJson_IsRevalidatedOnRead()
    {
        const string json = """{"Id":"sample.x","State":3,"Code":"missing","Message":"Missing.","CheckedAtUtc":"2026-10-08T09:30:00+00:00","Metadata":{"password":"hunter2"}}""";

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PrerequisiteCheckResult>(json, JsonOptions));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<OperationalMetadata>("[1]", JsonOptions));
    }

    [Theory]
    [InlineData("docker", true)]
    [InlineData("docker.build-contexts", true)]
    [InlineData("web.searxng", true)]
    [InlineData("windows.debugger.kd", true)]
    [InlineData("Docker", false)]
    [InlineData(".docker", false)]
    [InlineData("docker daemon", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void PrerequisiteIds_FollowTheLowercaseDottedFormat_IncludingExistingCapabilityIds(string? id, bool valid) =>
        Assert.Equal(valid, OperationalIdentifier.IsValidPrerequisiteId(id));

    [Theory]
    [InlineData("prerequisite.missing", true)]
    [InlineData("prerequisite.check-failed", true)]
    [InlineData("executable-not-found", true)]
    [InlineData("agent.replan.threshold", true)]
    [InlineData("Prerequisite.Missing", false)]
    [InlineData("prerequisite..missing", false)]
    [InlineData("prerequisite.", false)]
    [InlineData("-missing", false)]
    [InlineData("missing_thing", false)]
    [InlineData(null, false)]
    public void Codes_AreStableLowercaseValues(string? code, bool valid) =>
        Assert.Equal(valid, OperationalIdentifier.IsValidCode(code));

    [Theory]
    [InlineData("prerequisite/docker.daemon", true)]
    [InlineData("runtime/agent", true)]
    [InlineData("provider/llamacpp", true)]
    [InlineData("plugin/acme.postgres", true)]
    [InlineData("runtime", false)]
    [InlineData("/agent", false)]
    [InlineData("Runtime/agent", false)]
    [InlineData("runtime/agent/extra", false)]
    public void Sources_AreKindSlashName(string source, bool valid) =>
        Assert.Equal(valid, OperationalIdentifier.IsValidSource(source));

    [Fact]
    public void Descriptor_RejectsInvalidIdAndUnboundedText()
    {
        Assert.Throws<ArgumentException>(() => new PrerequisiteDescriptor("Not Valid", "Name", "Description."));
        Assert.ThrowsAny<ArgumentException>(() => new PrerequisiteDescriptor("sample.x", "", "Description."));
        Assert.ThrowsAny<ArgumentException>(() => new PrerequisiteDescriptor("sample.x", "Name", new string('d', PrerequisiteDescriptor.MaxTextLength + 1)));
        Assert.Equal(PrerequisiteDescriptor.DefaultCheckTimeout, new PrerequisiteDescriptor("sample.x", "Name", "Description.").CheckTimeout);
    }

    [Fact]
    public void SystemMessage_ValidatesEveryBoundedField()
    {
        SampleMessage().Validate();
        (SampleMessage() with { ComponentType = SystemComponentType.Tool, ComponentId = "sample.read" }).Validate();

        Assert.Throws<ArgumentException>(() => (SampleMessage() with { Id = Guid.Empty }).Validate());
        Assert.Throws<ArgumentException>(() => (SampleMessage() with { Source = "nokind" }).Validate());
        Assert.Throws<ArgumentException>(() => (SampleMessage() with { Code = "Not A Code" }).Validate());
        Assert.Throws<ArgumentException>(() => (SampleMessage() with { Message = " " }).Validate());
        Assert.Throws<ArgumentException>(() => (SampleMessage() with { Message = new string('m', SystemMessage.MaxMessageLength + 1) }).Validate());
        Assert.Throws<ArgumentException>(() => (SampleMessage() with { Severity = (SystemMessageSeverity)9 }).Validate());
        Assert.Throws<ArgumentException>(() => (SampleMessage() with { ComponentId = "orphan" }).Validate());
    }

    [Fact]
    public void SystemMessageQuery_DefaultsTo50_AndBoundsPageSizeAndText()
    {
        var query = new SystemMessageQuery();

        Assert.Equal(50, query.PageSize);
        Assert.Equal(200, SystemMessageQuery.MaxPageSize);
        query.Validate();
        (query with { PageSize = 1 }).Validate();
        (query with { PageSize = 200 }).Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() => (query with { PageSize = 0 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (query with { PageSize = 201 }).Validate());
        Assert.Throws<ArgumentException>(() => (query with { Text = "" }).Validate());
        Assert.Throws<ArgumentException>(() => (query with { Text = new string('t', SystemMessageQuery.MaxTextLength + 1) }).Validate());
        Assert.Throws<ArgumentException>(() => (query with { FromUtc = Checked, ToUtc = Checked.AddTicks(-1) }).Validate());
        (query with { FromUtc = Checked, ToUtc = Checked }).Validate();
    }

    [Fact]
    public void Retention_DefaultsTo90Days() => Assert.Equal(TimeSpan.FromDays(90), SystemMessageRetention.Default);

    private static SystemMessage SampleMessage() => new()
    {
        Id = Guid.CreateVersion7(Checked),
        TimestampUtc = Checked,
        Node = NodeId.Local,
        Source = "prerequisite/sample.debugger",
        Severity = SystemMessageSeverity.Warning,
        Code = "prerequisite.missing",
        Message = "The sample debugger is required.",
        Metadata = OperationalMetadata.From(new JsonObject { ["affectedComponents"] = new JsonArray("tool:sample.analyze") }),
    };

    private static T RoundTrip<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions)!;
}
