// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Runtime;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace bOps.Packages.Docker.Tests;

/// <summary>
/// The Docker package's public contract (ADR-0033): the exact tool set, the risk and approval each tool declares, that every
/// non-Read tool is verifiable and its verification is wired by argument name, and that the registry accepts the set. None of it
/// needs a daemon.
/// </summary>
public sealed class DockerContractTests
{
    private static readonly IDockerClientFactory Factory = new DockerClientFactory();

    private static readonly string[] Existing =
    [
        "docker.containers", "docker.images", "docker.networks", "docker.inspect", "docker.logs", "docker.start", "docker.stop", "docker.restart",
    ];

    private static readonly string[] Added =
    [
        "docker.image.inspect", "docker.image.pull", "docker.image.tag", "docker.image.remove", "docker.build",
        "docker.volumes", "docker.volume.inspect", "docker.volume.create", "docker.volume.remove",
    ];

    private static ITool[] Tools() => [.. new DockerToolProvider(Factory).GetTools()];

    private static ITool Tool(string name) => Tools().Single(tool => tool.Manifest.Name == name);

    private static ToolArguments Args(params (string Name, object Value)[] values)
    {
        var json = new JsonObject();
        foreach (var (name, value) in values)
        {
            json[name] = JsonValue.Create(value);
        }

        return ToolArguments.FromJson(json);
    }

    [Fact]
    public void TheProviderContributesTheEightExistingToolsAndTheNineAdded_AndNothingElse()
    {
        var names = Tools().Select(tool => tool.Manifest.Name).ToArray();

        Assert.Equal(17, names.Length);
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal([.. Existing, .. Added], names);
    }

    [Fact]
    public void TheExistingEightKeepTheirNamesRisksAndParameters()
    {
        var expected = new (string Name, RiskLevel Risk, string[] Parameters)[]
        {
            ("docker.containers", RiskLevel.Read, []),
            ("docker.images", RiskLevel.Read, []),
            ("docker.networks", RiskLevel.Read, []),
            ("docker.inspect", RiskLevel.Read, ["container"]),
            ("docker.logs", RiskLevel.Read, ["container", "tail"]),
            ("docker.start", RiskLevel.Medium, ["container"]),
            ("docker.stop", RiskLevel.Medium, ["container"]),
            ("docker.restart", RiskLevel.Medium, ["container"]),
        };

        foreach (var (name, risk, parameters) in expected)
        {
            var manifest = Tool(name).Manifest;
            Assert.Equal(risk, manifest.Risk);
            Assert.Equal(parameters, manifest.Parameters.Select(parameter => parameter.Name).ToArray());
        }
    }

    [Fact]
    public void ThereIsNoGenericExecutionOrPruneOrContainerCreationTool()
    {
        var names = Tools().Select(tool => tool.Manifest.Name).ToArray();

        Assert.DoesNotContain(names, name =>
            name.Contains("exec", StringComparison.OrdinalIgnoreCase)
            || name.Contains("prune", StringComparison.OrdinalIgnoreCase)
            || name.Contains("shell", StringComparison.OrdinalIgnoreCase)
            || name.Contains("compose", StringComparison.OrdinalIgnoreCase)
            || name.Contains("push", StringComparison.OrdinalIgnoreCase)
            || name is "docker.run" or "docker.create" or "docker.rm" or "docker.remove");
    }

    [Theory]
    [InlineData("docker.image.inspect", RiskLevel.Read, false, null, new[] { "image" })]
    [InlineData("docker.volumes", RiskLevel.Read, false, null, new[] { "limit", "maxOutputBytes" })]
    [InlineData("docker.volume.inspect", RiskLevel.Read, false, null, new[] { "volume" })]
    [InlineData("docker.image.pull", RiskLevel.Medium, false, "docker.image.inspect", new[] { "image", "platform" })]
    [InlineData("docker.image.tag", RiskLevel.Low, false, "docker.image.inspect", new[] { "source", "image" })]
    [InlineData("docker.image.remove", RiskLevel.High, true, "docker.image.inspect", new[] { "image" })]
    [InlineData("docker.build", RiskLevel.High, true, "docker.image.inspect", new[] { "context", "image", "dockerfile" })]
    [InlineData("docker.volume.create", RiskLevel.Low, false, "docker.volume.inspect", new[] { "volume", "driver" })]
    [InlineData("docker.volume.remove", RiskLevel.High, true, "docker.volume.inspect", new[] { "volume" })]
    public void EachAddedToolDeclaresItsRiskApprovalVerificationAndParameters(
        string name, RiskLevel risk, bool approval, string? verifier, string[] parameters)
    {
        var manifest = Tool(name).Manifest;

        Assert.Equal(risk, manifest.Risk);
        Assert.Equal(approval, manifest.RequiresExplicitApproval);
        Assert.Equal(verifier, manifest.Verification?.VerifyToolName);
        Assert.Equal(parameters, manifest.Parameters.Select(parameter => parameter.Name).ToArray());
        Assert.Equal(["windows", "linux"], manifest.Platforms);
        Assert.Contains(DockerCapability.Name, manifest.Requires);
        Assert.False(string.IsNullOrWhiteSpace(manifest.Description));
    }

    [Fact]
    public void OnlyBuildRequiresTheBuildContextsCapability()
    {
        foreach (var tool in Tools())
        {
            var needs = tool.Manifest.Requires.Contains(DockerCapability.BuildContexts, StringComparer.Ordinal);
            Assert.Equal(tool.Manifest.Name == "docker.build", needs);
        }

        Assert.Equal("docker.build-contexts", DockerCapability.BuildContexts);
    }

    [Fact]
    public void EveryNonReadToolIsVerifiable_AndItsVerificationCanBeBuiltFromItsOwnArguments()
    {
        var tools = Tools();
        var byName = tools.ToDictionary(tool => tool.Manifest.Name, StringComparer.Ordinal);

        foreach (var tool in tools.Where(tool => tool.Manifest.Risk != RiskLevel.Read))
        {
            var manifest = tool.Manifest;
            Assert.IsAssignableFrom<IVerifiableTool>(tool);
            var spec = Assert.IsType<VerificationSpec>(manifest.Verification);

            var verifier = byName[spec.VerifyToolName];
            Assert.Equal(RiskLevel.Read, verifier.Manifest.Risk);

            var ownParameters = manifest.Parameters.Select(parameter => parameter.Name).ToHashSet(StringComparer.Ordinal);
            var verifierParameters = verifier.Manifest.Parameters.Select(parameter => parameter.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var carried in spec.ArgumentsFrom)
            {
                Assert.Contains(carried, ownParameters);
                Assert.Contains(carried, verifierParameters);
            }

            foreach (var required in verifier.Manifest.Parameters.Where(parameter => parameter.Required))
            {
                Assert.Contains(required.Name, spec.ArgumentsFrom);
                Assert.True(manifest.Parameters.Single(parameter => parameter.Name == required.Name).Required);
            }
        }
    }

    [Fact]
    public async Task TheRegistryAcceptsEveryTool_AndHidesBuildUntilAContextIsConfigured()
    {
        var withoutContexts = await Register(buildConfigured: false);
        var withContexts = await Register(buildConfigured: true);

        Assert.Equal(16, withoutContexts.Count);
        Assert.DoesNotContain("docker.build", withoutContexts);
        Assert.Equal(17, withContexts.Count);
        Assert.Contains("docker.build", withContexts);
    }

    [Fact]
    public async Task TheRegistryHidesEveryDockerToolWhenTheDaemonIsNotAvailable()
    {
        var registry = new ToolRegistry(new Probe(dockerAvailable: false, buildConfigured: true));
        foreach (var tool in Tools())
        {
            registry.Register(new PackageId("bops.packages.docker"), tool);
        }

        await registry.RefreshCapabilitiesAsync();

        Assert.Empty(registry.GetAvailableManifests());
    }

    private static async Task<List<string>> Register(bool buildConfigured)
    {
        var registry = new ToolRegistry(new Probe(dockerAvailable: true, buildConfigured));
        foreach (var tool in new DockerToolProvider(Factory).GetTools())
        {
            registry.Register(new PackageId("bops.packages.docker"), tool);
        }

        await registry.RefreshCapabilitiesAsync();
        return [.. registry.GetAvailableManifests().Select(manifest => manifest.Name)];
    }

    private sealed class Probe(bool dockerAvailable, bool buildConfigured) : ICapabilityProbe
    {
        public Task<bool> IsAvailableAsync(string capability, CancellationToken ct = default) => Task.FromResult(capability switch
        {
            DockerCapability.Name => dockerAvailable,
            DockerCapability.BuildContexts => buildConfigured,
            _ => false,
        });
    }

    [Fact]
    public async Task TheBuildCapability_FollowsTheConfiguration()
    {
        Assert.False(await DockerCapability.IsBuildConfiguredAsync(new DockerBuildOptions()));
        Assert.False(await DockerCapability.IsBuildConfiguredAsync(new DockerBuildOptions { Contexts = ["", "  "] }));
        Assert.True(await DockerCapability.IsBuildConfiguredAsync(new DockerBuildOptions { Contexts = [Path.GetTempPath()] }));
    }

    [Fact]
    public void TheOptionsHaveTheDocumentedDefaults()
    {
        var build = new DockerBuildOptions();

        Assert.Empty(build.Contexts);
        Assert.False(build.IsConfigured);
        Assert.Equal(10_000, build.MaximumContextFiles);
        Assert.Equal(64L * 1024 * 1024, build.MaximumContextBytes);
        Assert.Equal(["local"], new DockerVolumeOptions().AllowedDrivers);
        Assert.Equal(["local"], new DockerVolumeOptions { Drivers = ["", " "] }.AllowedDrivers);
        Assert.Equal(["local", "custom"], new DockerVolumeOptions { Drivers = ["local", "custom", "local"] }.AllowedDrivers);
    }

    // ---- verification, decided from what docker.image.inspect / docker.volume.inspect said ----

    private const string ImageAbsent = """{"schemaVersion":1,"exists":false,"reference":"x:1"}""";

    private static string ImagePresent(params string[] tags) =>
        new JsonObject
        {
            ["schemaVersion"] = 1,
            ["exists"] = true,
            ["id"] = "sha256:abc",
            ["tags"] = new JsonArray(tags.Select(tag => (JsonNode)JsonValue.Create(tag)!).ToArray()),
            ["digests"] = new JsonArray(),
        }.ToJsonString();

    public static TheoryData<string, string, string?, VerificationStatus> ImageVerdicts => new()
    {
        // tool, verification output, image argument, expected verdict
        { "docker.image.pull", ImagePresent("alpine:3.20"), "alpine:3.20", VerificationStatus.Confirmed },
        { "docker.image.pull", ImageAbsent, "alpine:3.20", VerificationStatus.Refuted },
        { "docker.image.remove", ImageAbsent, "alpine:3.20", VerificationStatus.Confirmed },
        { "docker.image.remove", ImagePresent("alpine:3.20"), "alpine:3.20", VerificationStatus.Refuted },
        { "docker.image.tag", ImagePresent("alpine:3.20", "team/app:v2"), "team/app:v2", VerificationStatus.Confirmed },
        { "docker.image.tag", ImagePresent("alpine:3.20"), "team/app:v2", VerificationStatus.Refuted },
        { "docker.image.tag", ImageAbsent, "team/app:v2", VerificationStatus.Refuted },
        { "docker.image.tag", ImagePresent("app:latest"), "docker.io/library/app", VerificationStatus.Confirmed },
        { "docker.build", ImagePresent("bops/app:1"), "bops/app:1", VerificationStatus.Confirmed },
        { "docker.build", ImagePresent("bops/other:1"), "bops/app:1", VerificationStatus.Refuted },
        { "docker.build", ImageAbsent, "bops/app:1", VerificationStatus.Refuted },
    };

    [Theory]
    [MemberData(nameof(ImageVerdicts))]
    public async Task AnImageMutationIsVerifiedFromWhatInspectReports(string tool, string output, string? image, VerificationStatus expected)
    {
        var verifiable = (IVerifiableTool)Tool(tool);

        var outcome = await verifiable.EvaluateVerificationAsync(Args(("image", image!)), ToolCallResult.Success(output));

        Assert.Equal(expected, outcome.Status);
        Assert.Equal(expected == VerificationStatus.Confirmed, outcome.Detail is null);
    }

    [Theory]
    [InlineData("docker.image.pull")]
    [InlineData("docker.image.tag")]
    [InlineData("docker.image.remove")]
    [InlineData("docker.build")]
    public async Task AnImageVerificationThatCouldNotReadIsInconclusive_NeverConfirmed(string tool)
    {
        var verifiable = (IVerifiableTool)Tool(tool);
        var arguments = Args(("image", "alpine:3.20"));

        var failed = await verifiable.EvaluateVerificationAsync(arguments, ToolCallResult.Failure("daemon gone"));
        var unreadable = await verifiable.EvaluateVerificationAsync(arguments, ToolCallResult.Success("not json"));
        var wrongShape = await verifiable.EvaluateVerificationAsync(arguments, ToolCallResult.Success("""{"exists":"maybe"}"""));
        var missing = await verifiable.EvaluateVerificationAsync(arguments, ToolCallResult.Success(null));
        var empty = await verifiable.EvaluateVerificationAsync(arguments, ToolCallResult.Success(string.Empty));

        Assert.Equal(VerificationStatus.Inconclusive, failed.Status);
        Assert.Contains("daemon gone", failed.Detail, StringComparison.Ordinal);
        Assert.All([unreadable, wrongShape, missing, empty], outcome => Assert.Equal(VerificationStatus.Inconclusive, outcome.Status));
        Assert.All([unreadable, wrongShape, missing, empty], outcome => Assert.NotNull(outcome.Detail));
    }

    [Theory]
    [InlineData("docker.volume.create", true, VerificationStatus.Confirmed)]
    [InlineData("docker.volume.create", false, VerificationStatus.Refuted)]
    [InlineData("docker.volume.remove", false, VerificationStatus.Confirmed)]
    [InlineData("docker.volume.remove", true, VerificationStatus.Refuted)]
    public async Task AVolumeMutationIsVerifiedFromWhatInspectReports(string tool, bool exists, VerificationStatus expected)
    {
        var output = exists
            ? """{"schemaVersion":1,"exists":true,"name":"data","driver":"local"}"""
            : """{"schemaVersion":1,"exists":false,"name":"data"}""";

        var outcome = await ((IVerifiableTool)Tool(tool)).EvaluateVerificationAsync(Args(("volume", "data")), ToolCallResult.Success(output));

        Assert.Equal(expected, outcome.Status);
    }

    [Theory]
    [InlineData("docker.volume.create")]
    [InlineData("docker.volume.remove")]
    public async Task AVolumeVerificationThatCouldNotReadIsInconclusive(string tool)
    {
        var verifiable = (IVerifiableTool)Tool(tool);
        var arguments = Args(("volume", "data"));

        var failed = await verifiable.EvaluateVerificationAsync(arguments, ToolCallResult.Failure("daemon gone"));
        var unreadable = await verifiable.EvaluateVerificationAsync(arguments, ToolCallResult.Success("not json"));
        var wrongShape = await verifiable.EvaluateVerificationAsync(arguments, ToolCallResult.Success("""{"exists":1}"""));

        Assert.Equal(VerificationStatus.Inconclusive, failed.Status);
        Assert.Contains("daemon gone", failed.Detail, StringComparison.Ordinal);
        Assert.Equal(VerificationStatus.Inconclusive, unreadable.Status);
        Assert.Equal(VerificationStatus.Inconclusive, wrongShape.Status);
    }

    // ---- what the volume tools return ----

    private static VolumeResponse Volume(string name, string driver = "local") => new()
    {
        Name = name,
        Driver = driver,
        Scope = "local",
        CreatedAt = "2026-09-21T10:00:00Z",
        Mountpoint = "/var/lib/docker/volumes/secret-location/_data",
        Options = new Dictionary<string, string> { ["device"] = "/srv/private", ["o"] = "bind", ["type"] = "none" },
        Labels = new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" },
        UsageData = new VolumeUsageData { Size = 4096, RefCount = 2 },
    };

    [Fact]
    public void AVolumeIsDescribedWithoutItsMountPointOrItsDriverOptions()
    {
        var json = JsonNode.Parse(DockerVolumeOutput.Present(Volume("data")))!.AsObject();

        Assert.Equal(1, json["schemaVersion"]!.GetValue<int>());
        Assert.True(json["exists"]!.GetValue<bool>());
        Assert.Equal("data", json["name"]!.GetValue<string>());
        Assert.Equal("local", json["driver"]!.GetValue<string>());
        Assert.Equal("local", json["scope"]!.GetValue<string>());
        Assert.Equal("2026-09-21T10:00:00Z", json["createdAt"]!.GetValue<string>());
        Assert.Equal(3, json["optionCount"]!.GetValue<int>());
        Assert.Equal(4096, json["usageBytes"]!.GetValue<long>());
        Assert.Equal(2, json["referenceCount"]!.GetValue<long>());
        Assert.Equal(["a", "b"], json["labels"]!.AsObject().Select(pair => pair.Key).ToArray());

        var text = json.ToJsonString();
        Assert.DoesNotContain("secret-location", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/srv/private", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ountpoint", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AVolumeWithoutUsageDataHasNullUsage_AndNegativeUsageIsNotReported()
    {
        var withoutUsage = Volume("data");
        withoutUsage.UsageData = null;
        var unknownUsage = Volume("data");
        unknownUsage.UsageData = new VolumeUsageData { Size = -1, RefCount = -1 };

        foreach (var volume in new[] { withoutUsage, unknownUsage })
        {
            var json = JsonNode.Parse(DockerVolumeOutput.Present(volume))!.AsObject();
            Assert.Null(json["usageBytes"]);
            Assert.Null(json["referenceCount"]);
        }
    }

    [Fact]
    public void AVolumeWithZeroUsageReportsZero_NotUnknown()
    {
        var volume = Volume("data");
        volume.UsageData = new VolumeUsageData { Size = 0, RefCount = 0 };

        var json = JsonNode.Parse(DockerVolumeOutput.Present(volume))!.AsObject();

        Assert.Equal(0, json["usageBytes"]!.GetValue<long>());
        Assert.Equal(0, json["referenceCount"]!.GetValue<long>());
    }

    [Fact]
    public void LabelsAreBoundedInCountAndLength()
    {
        var volume = Volume("data");
        volume.Labels = Enumerable.Range(0, 25).ToDictionary(i => $"k{i:00}", _ => new string('v', 300));

        var json = JsonNode.Parse(DockerVolumeOutput.Present(volume))!.AsObject();
        var labels = json["labels"]!.AsObject();

        Assert.Equal(20, labels.Count);
        Assert.True(json["labelsTruncated"]!.GetValue<bool>());
        Assert.All(labels, pair => Assert.Equal(257, pair.Value!.GetValue<string>().Length));
        Assert.Equal("k00", labels.First().Key);
    }

    [Fact]
    public void AVolumeWithNoLabelsOrOptions_IsDescribedEmpty()
    {
        var volume = new VolumeResponse { Name = "plain", Driver = "local", Scope = "local" };

        var json = JsonNode.Parse(DockerVolumeOutput.Present(volume))!.AsObject();

        Assert.Empty(json["labels"]!.AsObject());
        Assert.False(json["labelsTruncated"]!.GetValue<bool>());
        Assert.Equal(0, json["optionCount"]!.GetValue<int>());
    }

    [Fact]
    public void AMissingVolumeIsExistsFalse()
    {
        var json = JsonNode.Parse(DockerVolumeOutput.Missing("gone"))!.AsObject();

        Assert.False(json["exists"]!.GetValue<bool>());
        Assert.Equal("gone", json["name"]!.GetValue<string>());
        Assert.Equal(1, json["schemaVersion"]!.GetValue<int>());
    }

    [Fact]
    public void TheVolumeListIsSortedByName_AndBoundedByTheLimit()
    {
        var volumes = new[] { Volume("zeta"), Volume("alpha"), Volume("Mid"), Volume("beta") };

        var json = JsonNode.Parse(DockerVolumeOutput.List(volumes, limit: 3, maximumBytes: 65_536))!.AsObject();

        Assert.Equal(["Mid", "alpha", "beta"], json["volumes"]!.AsArray().Select(v => v!["name"]!.GetValue<string>()).ToArray());
        Assert.Equal(4, json["observedVolumes"]!.GetValue<int>());
        Assert.Equal(3, json["returnedVolumes"]!.GetValue<int>());
        Assert.True(json["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public void AListThatFitsIsNotTruncated()
    {
        var json = JsonNode.Parse(DockerVolumeOutput.List([Volume("b"), Volume("a")], limit: 2, maximumBytes: 65_536))!.AsObject();

        Assert.Equal(2, json["returnedVolumes"]!.GetValue<int>());
        Assert.False(json["truncated"]!.GetValue<bool>());
        Assert.Empty(JsonNode.Parse(DockerVolumeOutput.List([], 100, 4096))!["volumes"]!.AsArray());
    }

    [Fact]
    public void TheListIsCutToTheByteBudget_AndSaysSo()
    {
        var volumes = Enumerable.Range(0, 60).Select(i => Volume($"volume-{i:000}")).ToArray();

        var output = DockerVolumeOutput.List(volumes, limit: 500, maximumBytes: 4_096);
        var json = JsonNode.Parse(output)!.AsObject();

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(output) <= 4_096);
        Assert.True(json["truncated"]!.GetValue<bool>());
        Assert.Equal(json["returnedVolumes"]!.GetValue<int>(), json["volumes"]!.AsArray().Count);
        Assert.InRange(json["returnedVolumes"]!.GetValue<int>(), 1, 59);
        Assert.Equal("volume-000", json["volumes"]!.AsArray()[0]!["name"]!.GetValue<string>());
        Assert.Equal(60, json["observedVolumes"]!.GetValue<int>());
    }

    [Fact]
    public void AListThatExactlyFitsTheByteBudgetIsNotCut()
    {
        var volumes = Enumerable.Range(0, 12).Select(i => Volume($"volume-{i:000}")).ToArray();
        var full = DockerVolumeOutput.List(volumes, limit: 500, maximumBytes: 65_536);
        var size = System.Text.Encoding.UTF8.GetByteCount(full);

        var exact = JsonNode.Parse(DockerVolumeOutput.List(volumes, 500, size))!.AsObject();
        var tighter = JsonNode.Parse(DockerVolumeOutput.List(volumes, 500, size - 1))!.AsObject();

        Assert.False(exact["truncated"]!.GetValue<bool>());
        Assert.Equal(12, exact["returnedVolumes"]!.GetValue<int>());
        Assert.True(tighter["truncated"]!.GetValue<bool>());
        Assert.Equal(11, tighter["returnedVolumes"]!.GetValue<int>());
    }

    // ---- what the image tools return ----

    [Fact]
    public void AnImageIsDescribedWithBoundedSortedTagsAndDigests()
    {
        var image = new ImageInspectResponse
        {
            ID = "sha256:0123",
            RepoTags = [.. Enumerable.Range(0, 60).Select(i => $"app:v{i:00}").Reverse()],
            RepoDigests = [.. Enumerable.Range(0, 25).Select(i => $"app@sha256:{i:000}")],
            Size = 1234,
            Created = new DateTime(2026, 9, 1, 12, 30, 15, DateTimeKind.Utc),
            Os = "linux",
            Architecture = "arm64",
            Variant = "v8",
        };

        var json = JsonNode.Parse(DockerImageOutput.Present("app:v00", image))!.AsObject();

        Assert.True(json["exists"]!.GetValue<bool>());
        Assert.Equal("app:v00", json["reference"]!.GetValue<string>());
        Assert.Equal("sha256:0123", json["id"]!.GetValue<string>());
        Assert.Equal(50, json["tags"]!.AsArray().Count);
        Assert.Equal("app:v00", json["tags"]![0]!.GetValue<string>());
        Assert.Equal(20, json["digests"]!.AsArray().Count);
        Assert.Equal(1234, json["sizeBytes"]!.GetValue<long>());
        Assert.Equal("2026-09-01T12:30:15.0000000Z", json["createdUtc"]!.GetValue<string>());
        Assert.Equal("linux", json["os"]!.GetValue<string>());
        Assert.Equal("arm64", json["architecture"]!.GetValue<string>());
        Assert.Equal("v8", json["variant"]!.GetValue<string>());
    }

    [Fact]
    public void AnImageWithoutAVariantOrTagsHasNullVariantAndEmptyLists()
    {
        var image = new ImageInspectResponse { ID = "sha256:0123", Os = "linux", Architecture = "amd64", Created = DateTime.UnixEpoch };

        var json = JsonNode.Parse(DockerImageOutput.Present("x", image))!.AsObject();

        Assert.Null(json["variant"]);
        Assert.Empty(json["tags"]!.AsArray());
        Assert.Empty(json["digests"]!.AsArray());
    }

    [Fact]
    public void AMissingImageIsExistsFalse_AndTheStateReadsBack()
    {
        var missing = DockerImageOutput.TryRead(DockerImageOutput.Missing("gone:1"));
        var present = DockerImageOutput.TryRead(DockerImageOutput.Present("a:1", new ImageInspectResponse { ID = "sha256:9", RepoTags = ["a:1"], Created = DateTime.UnixEpoch }));

        Assert.False(missing!.Exists);
        Assert.True(present!.Exists);
        Assert.Equal("sha256:9", present.Id);
        Assert.Equal(["a:1"], present.Tags);
        Assert.Empty(present.Digests);
        Assert.Null(DockerImageOutput.TryRead("[]"));
        Assert.Null(DockerImageOutput.TryRead("""{"exists":"yes"}"""));
        Assert.Null(DockerImageOutput.TryRead("{"));
    }

    [Fact]
    public void ALongReferenceOrErrorIsBoundedToOneLine()
    {
        var bounded = DockerFailure.Bound(new string('x', 400) + "\nsecond line");

        Assert.Equal(301, bounded.Length);
        Assert.EndsWith("…", bounded, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', bounded);
        Assert.Equal("a b", DockerFailure.Bound("a\nb"));
        Assert.Equal("short", DockerFailure.Bound("  short  "));
    }

    [Fact]
    public void ADaemonThatCannotBeReachedIsAFailedResultText_NotAFault()
    {
        Assert.True(DockerFailure.IsUnreachable(new HttpRequestException("no")));
        Assert.True(DockerFailure.IsUnreachable(new IOException("no")));
        Assert.True(DockerFailure.IsUnreachable(new TimeoutException("no")));
        Assert.False(DockerFailure.IsUnreachable(new InvalidOperationException("bug")));
        Assert.False(DockerFailure.IsUnreachable(new OperationCanceledException()));
        Assert.Equal("The Docker daemon could not be reached: pipe gone", DockerFailure.Unreachable(new IOException("pipe gone")));
    }

    [Fact]
    public void TheDaemonsOwnMessageIsExtractedFromItsJsonBody_AndBounded()
    {
        var withBody = new DockerApiException(System.Net.HttpStatusCode.Conflict, """{"message":"image is being used by container abc"}""");
        var plain = new DockerApiException(System.Net.HttpStatusCode.InternalServerError, "not json at all");
        var notFound = new DockerApiException(System.Net.HttpStatusCode.NotFound, """{"message":"No such image: x"}""");

        Assert.Equal("409 Conflict: image is being used by container abc", DockerFailure.Describe(withBody));
        Assert.StartsWith("500 InternalServerError:", DockerFailure.Describe(plain), StringComparison.Ordinal);
        Assert.Contains("not json at all", DockerFailure.Describe(plain), StringComparison.Ordinal);
        Assert.True(DockerFailure.IsNotFound(notFound));
        Assert.False(DockerFailure.IsNotFound(withBody));
    }
}
