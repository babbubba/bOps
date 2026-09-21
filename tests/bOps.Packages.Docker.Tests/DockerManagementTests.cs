// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace bOps.Packages.Docker.Tests;

/// <summary>
/// The V1.3-B tools (ADR-0033) against a real local Docker daemon, never mocked (agentic/04-testing-rules.md): every image, tag,
/// volume and container a test touches is created by the test under a unique <c>bops-test-</c> name and removed afterwards, and no
/// test names or assumes anything it did not create. Images are built from a repository-owned <c>FROM scratch</c> context, so no
/// external mutable source is involved except the one <c>alpine</c> tag the existing suite already depends on.
/// </summary>
public sealed class DockerManagementTests
{
    private const string Alpine = "alpine:3.20";

    private static readonly DockerClientFactory Factory = new();

    private static ToolArguments Args(params (string Name, object Value)[] values)
    {
        var json = new JsonObject();
        foreach (var (name, value) in values)
        {
            json[name] = JsonValue.Create(value);
        }

        return ToolArguments.FromJson(json);
    }

    // Synchronous on purpose: fixture setup, and the file-system calls are not what these async tests measure.
    private static void Write(string path, string content) => File.WriteAllText(path, content);

    private static void WriteBytes(string path, byte[] content) => File.WriteAllBytes(path, content);

    private static JsonObject Json(ToolCallResult result)
    {
        Assert.True(result.Succeeded, result.ErrorMessage);
        return JsonNode.Parse(result.Output!)!.AsObject();
    }

    private static async Task<ToolCallResult> RunAsync(ITool tool, params (string Name, object Value)[] values) =>
        await tool.ExecuteAsync(Args(values));

    private static async Task<VerificationOutcome> VerifyAsync(IVerifiableTool tool, ITool verifier, params (string Name, object Value)[] values)
    {
        var arguments = Args(values);
        var spec = tool.Manifest.Verification!;
        var carried = new JsonObject();
        foreach (var name in spec.ArgumentsFrom)
        {
            carried[name] = arguments.ToJson()[name]?.DeepClone();
        }

        var observed = await verifier.ExecuteAsync(ToolArguments.FromJson(carried));
        return await tool.EvaluateVerificationAsync(arguments, observed);
    }

    /// <summary>The disposable things one test made. Disposed in a <c>finally</c>, never through xunit lifetime (see <see cref="TestContainer"/>).</summary>
    private sealed class Scope : IAsyncDisposable
    {
        private readonly List<string> _images = [];
        private readonly List<string> _volumes = [];
        private readonly List<string> _containers = [];

        public Scope()
        {
            Root = Directory.CreateTempSubdirectory("bops-docker-e2e-");
            Options = new DockerBuildOptions { Contexts = [Root.FullName], MaximumContextFiles = 50, MaximumContextBytes = 1_000_000 };
            Build = new DockerBuildTool(Factory, Options);
        }

        public DirectoryInfo Root { get; }

        public DockerBuildOptions Options { get; }

        public DockerBuildTool Build { get; }

        public string NewImageName() => Track(_images, $"bops-test-{Guid.NewGuid():N}:1");

        public string NewVolumeName() => Track(_volumes, $"bops-test-{Guid.NewGuid():N}");

        public void TrackImage(string reference) => _images.Add(reference);

        public void TrackContainer(string id) => _containers.Add(id);

        public string NewContext(string content = "hello", string? dockerfile = null)
        {
            var context = Directory.CreateDirectory(Path.Combine(Root.FullName, Guid.NewGuid().ToString("N"))).FullName;
            File.WriteAllText(Path.Combine(context, "Dockerfile"), dockerfile ?? "FROM scratch\nCOPY hello.txt /hello.txt\n");
            File.WriteAllText(Path.Combine(context, "hello.txt"), content);
            return context;
        }

        /// <summary>Builds a tiny image from a context this scope owns and returns its new reference.</summary>
        public async Task<string> BuildAsync(string content = "hello")
        {
            var image = NewImageName();
            var built = await Build.ExecuteAsync(Args(("context", NewContext(content)), ("image", image)));
            Assert.True(built.Succeeded, built.ErrorMessage);
            return image;
        }

        public async ValueTask DisposeAsync()
        {
            using var client = Factory.Create();
            foreach (var container in _containers)
            {
                await TryAsync(() => client.Containers.RemoveContainerAsync(container, new ContainerRemoveParameters { Force = true }));
            }

            foreach (var volume in _volumes)
            {
                await TryAsync(() => client.Volumes.RemoveAsync(volume, force: true, CancellationToken.None));
            }

            foreach (var image in _images)
            {
                await TryAsync(() => client.Images.DeleteImageAsync(image, new ImageDeleteParameters { Force = true }, CancellationToken.None));
            }

            Root.Delete(recursive: true);
        }

        private static string Track(List<string> list, string name)
        {
            list.Add(name);
            return name;
        }

        private static async Task TryAsync(Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (DockerApiException)
            {
                // Best-effort cleanup: the resource may already be gone (a test that removed it itself).
            }
        }
    }

    // ---- build ----

    [DockerAvailableFact]
    public async Task Build_ProducesAnImage_TheInspectToolSees_AndVerificationConfirms()
    {
        await using var scope = new Scope();
        var image = scope.NewImageName();
        var context = scope.NewContext();

        var built = await RunAsync(scope.Build, ("context", context), ("image", image));
        var result = Json(built);

        Assert.Equal(1, result["schemaVersion"]!.GetValue<int>());
        Assert.Equal(image, result["image"]!.GetValue<string>());
        Assert.StartsWith("sha256:", result["id"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(2, result["context"]!["files"]!.GetValue<int>());
        Assert.Equal(5 + "FROM scratch\nCOPY hello.txt /hello.txt\n".Length, result["context"]!["bytes"]!.GetValue<long>());
        Assert.True(result["steps"]!.GetValue<int>() >= 2);
        Assert.InRange(result["log"]!.AsArray().Count, 1, 20);

        var inspect = new DockerImageInspectTool(Factory);
        var seen = Json(await RunAsync(inspect, ("image", image)));
        Assert.True(seen["exists"]!.GetValue<bool>());
        Assert.Equal(result["id"]!.GetValue<string>(), seen["id"]!.GetValue<string>());
        Assert.Contains(image, seen["tags"]!.AsArray().Select(tag => tag!.GetValue<string>()));
        Assert.Equal("linux", seen["os"]!.GetValue<string>());

        var verdict = await VerifyAsync(scope.Build, inspect, ("image", image));
        Assert.Equal(VerificationStatus.Confirmed, verdict.Status);
    }

    [DockerAvailableFact]
    public async Task Build_OfADifferentContext_GivesADifferentImage()
    {
        await using var scope = new Scope();

        var first = await scope.BuildAsync("one");
        var second = await scope.BuildAsync("two");
        var inspect = new DockerImageInspectTool(Factory);

        Assert.NotEqual(
            Json(await RunAsync(inspect, ("image", first)))["id"]!.GetValue<string>(),
            Json(await RunAsync(inspect, ("image", second)))["id"]!.GetValue<string>());
    }

    [DockerAvailableFact]
    public async Task Build_UsesTheDockerfileThatWasNamed()
    {
        await using var scope = new Scope();
        var context = scope.NewContext();
        Write(Path.Combine(context, "Other.Dockerfile"), "FROM scratch\nCOPY hello.txt /other.txt\n");
        var image = scope.NewImageName();

        var built = await RunAsync(scope.Build, ("context", context), ("image", image), ("dockerfile", "Other.Dockerfile"));

        Assert.True(built.Succeeded, built.ErrorMessage);
    }

    [DockerAvailableFact]
    public async Task Build_ReportsADaemonSideFailureAsAFailedResult_AndLeavesNoImage()
    {
        await using var scope = new Scope();
        var image = scope.NewImageName();
        var context = scope.NewContext(dockerfile: "FROM scratch\nNOPE this is not an instruction\n");

        var built = await RunAsync(scope.Build, ("context", context), ("image", image));

        Assert.False(built.Succeeded);
        Assert.Equal(ToolOutcome.Failure, built.Outcome);
        Assert.Contains("failed", built.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("nope", built.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(built.ErrorMessage!.Length < 2_000);
        Assert.False(Json(await RunAsync(new DockerImageInspectTool(Factory), ("image", image)))["exists"]!.GetValue<bool>());
    }

    [DockerAvailableFact]
    public async Task Build_RefusesWithoutTouchingTheDaemon_WhatTheContextChecksRefuse()
    {
        await using var scope = new Scope();
        var image = scope.NewImageName();
        var inspect = new DockerImageInspectTool(Factory);

        var outside = Directory.CreateTempSubdirectory("bops-docker-outside-");
        try
        {
            Write(Path.Combine(outside.FullName, "Dockerfile"), "FROM scratch\n");
            var notAllowed = await RunAsync(scope.Build, ("context", outside.FullName), ("image", image));
            Assert.False(notAllowed.Succeeded);
            Assert.Contains("Refusing to build", notAllowed.ErrorMessage, StringComparison.Ordinal);
            Assert.Contains("not inside a directory allowed", notAllowed.ErrorMessage, StringComparison.Ordinal);
        }
        finally
        {
            outside.Delete(recursive: true);
        }

        var traversal = await RunAsync(scope.Build, ("context", scope.NewContext()), ("image", image), ("dockerfile", "../Dockerfile"));
        Assert.False(traversal.Succeeded);

        var ignoredContext = scope.NewContext();
        Write(Path.Combine(ignoredContext, ".dockerignore"), "hello.txt\n");
        var ignored = await RunAsync(scope.Build, ("context", ignoredContext), ("image", image));
        Assert.False(ignored.Succeeded);
        Assert.Contains(".dockerignore", ignored.ErrorMessage, StringComparison.Ordinal);

        var oversizedContext = scope.NewContext();
        WriteBytes(Path.Combine(oversizedContext, "big.bin"), new byte[1_100_000]);
        var oversized = await RunAsync(scope.Build, ("context", oversizedContext), ("image", image));
        Assert.False(oversized.Succeeded);
        Assert.Contains("bytes", oversized.ErrorMessage, StringComparison.Ordinal);

        var notATag = await RunAsync(scope.Build, ("context", scope.NewContext()), ("image", "Not A Tag"));
        Assert.False(notATag.Succeeded);
        var digestTag = await RunAsync(scope.Build, ("context", scope.NewContext()), ("image", "app@sha256:" + new string('a', 64)));
        Assert.False(digestTag.Succeeded);

        Assert.False(Json(await RunAsync(inspect, ("image", image)))["exists"]!.GetValue<bool>());
    }

    [DockerAvailableFact]
    public async Task Build_WithNoConfiguredContext_RefusesEverything()
    {
        await using var scope = new Scope();
        var tool = new DockerBuildTool(Factory);

        var result = await RunAsync(tool, ("context", scope.NewContext()), ("image", scope.NewImageName()));

        Assert.False(result.Succeeded);
        Assert.Contains("Docker:Build:Contexts", result.ErrorMessage, StringComparison.Ordinal);
    }

    [DockerAvailableFact]
    public async Task Build_ATimedOutOrCancelledBuild_ThrowsCancellationForTheRuntimeToReport()
    {
        await using var scope = new Scope();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            scope.Build.ExecuteAsync(Args(("context", scope.NewContext()), ("image", scope.NewImageName())), cancelled.Token));
    }

    // ---- images ----

    [DockerAvailableFact]
    public async Task ImageInspect_ReportsAMissingImageAsExistsFalse_NotAsAnError()
    {
        var tool = new DockerImageInspectTool(Factory);

        var missing = await RunAsync(tool, ("image", $"bops-test-{Guid.NewGuid():N}:never"));
        var json = Json(missing);

        Assert.False(json["exists"]!.GetValue<bool>());
        Assert.Equal(1, json["schemaVersion"]!.GetValue<int>());
        Assert.Null(json["id"]);
    }

    [DockerAvailableFact]
    public async Task ImageInspect_FindsAnImageByTagAndById_AndRefusesAMalformedReference()
    {
        await using var scope = new Scope();
        var image = await scope.BuildAsync();
        var tool = new DockerImageInspectTool(Factory);

        var byTag = Json(await RunAsync(tool, ("image", image)));
        var id = byTag["id"]!.GetValue<string>();
        var byId = Json(await RunAsync(tool, ("image", id)));
        var byShortId = Json(await RunAsync(tool, ("image", id["sha256:".Length..][..12])));

        Assert.True(byId["exists"]!.GetValue<bool>());
        Assert.Equal(id, byId["id"]!.GetValue<string>());
        Assert.Equal(id, byShortId["id"]!.GetValue<string>());
        Assert.True(byTag["sizeBytes"]!.GetValue<long>() > 0);
        Assert.EndsWith("Z", byTag["createdUtc"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(byTag["architecture"]!.GetValue<string>()));

        var malformed = await RunAsync(tool, ("image", "Not;An Image"));
        Assert.False(malformed.Succeeded);
    }

    [DockerAvailableFact]
    public async Task Pull_FetchesAnImage_ReturnsItsIdAndDigests_AndVerificationConfirms()
    {
        var pull = new DockerImagePullTool(Factory);
        var inspect = new DockerImageInspectTool(Factory);

        var pulled = await RunAsync(pull, ("image", Alpine));
        var json = Json(pulled);

        Assert.Equal(Alpine, json["image"]!.GetValue<string>());
        Assert.StartsWith("sha256:", json["id"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.NotEmpty(json["digests"]!.AsArray());
        Assert.Null(json["platform"]);

        var verdict = await VerifyAsync(pull, inspect, ("image", Alpine));
        Assert.Equal(VerificationStatus.Confirmed, verdict.Status);
    }

    [DockerAvailableFact]
    public async Task Pull_OfAnImageThatDoesNotExist_IsAFailedResultWithTheDaemonsReason()
    {
        var pull = new DockerImagePullTool(Factory);
        var name = $"bops-test-{Guid.NewGuid():N}/does-not-exist:never";

        var result = await RunAsync(pull, ("image", name));

        Assert.False(result.Succeeded);
        Assert.Equal(ToolOutcome.Failure, result.Outcome);
        Assert.Contains("Could not pull", result.ErrorMessage, StringComparison.Ordinal);
        Assert.True(result.ErrorMessage!.Length < 1_000);
    }

    [DockerAvailableFact]
    public async Task Pull_RefusesAMalformedReferenceOrPlatform_BeforeTheDaemon()
    {
        var pull = new DockerImagePullTool(Factory);

        Assert.False((await RunAsync(pull, ("image", "Not Valid"))).Succeeded);
        Assert.False((await RunAsync(pull, ("image", "0123456789ab"))).Succeeded);
        Assert.False((await RunAsync(pull, ("image", Alpine), ("platform", "not a platform"))).Succeeded);
    }

    [DockerAvailableFact]
    public async Task Pull_ACancelledPull_ThrowsCancellationForTheRuntimeToReport()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DockerImagePullTool(Factory).ExecuteAsync(Args(("image", Alpine)), cancelled.Token));
    }

    [DockerAvailableFact]
    public async Task Tag_AddsAReference_IsIdempotent_AndVerificationConfirms()
    {
        await using var scope = new Scope();
        var source = await scope.BuildAsync();
        var target = scope.NewImageName();
        var tag = new DockerImageTagTool(Factory);
        var inspect = new DockerImageInspectTool(Factory);

        var tagged = Json(await RunAsync(tag, ("source", source), ("image", target)));

        Assert.True(tagged["created"]!.GetValue<bool>());
        Assert.Equal(source, tagged["source"]!.GetValue<string>());
        Assert.Equal(target, tagged["image"]!.GetValue<string>());
        var seen = Json(await RunAsync(inspect, ("image", target)));
        Assert.Equal(tagged["id"]!.GetValue<string>(), seen["id"]!.GetValue<string>());
        Assert.Contains(source, seen["tags"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.Contains(target, seen["tags"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.Equal(VerificationStatus.Confirmed, (await VerifyAsync(tag, inspect, ("image", target), ("source", source))).Status);

        var again = Json(await RunAsync(tag, ("source", source), ("image", target)));
        Assert.False(again["created"]!.GetValue<bool>());
    }

    [DockerAvailableFact]
    public async Task Tag_AcceptsAnImageIdAsTheSource()
    {
        await using var scope = new Scope();
        var source = await scope.BuildAsync();
        var id = Json(await RunAsync(new DockerImageInspectTool(Factory), ("image", source)))["id"]!.GetValue<string>();
        var target = scope.NewImageName();

        var tagged = await RunAsync(new DockerImageTagTool(Factory), ("source", id), ("image", target));

        Assert.True(tagged.Succeeded, tagged.ErrorMessage);
    }

    [DockerAvailableFact]
    public async Task Tag_NeverMovesATagThatNamesADifferentImage()
    {
        await using var scope = new Scope();
        var first = await scope.BuildAsync("first");
        var second = await scope.BuildAsync("second");
        var tag = new DockerImageTagTool(Factory);
        var inspect = new DockerImageInspectTool(Factory);
        var firstId = Json(await RunAsync(inspect, ("image", first)))["id"]!.GetValue<string>();

        var refused = await RunAsync(tag, ("source", second), ("image", first));

        Assert.False(refused.Succeeded);
        Assert.Contains("already names a different image", refused.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(firstId, Json(await RunAsync(inspect, ("image", first)))["id"]!.GetValue<string>());
    }

    [DockerAvailableFact]
    public async Task Tag_FailsForAMissingSource_AndRefusesBadReferences()
    {
        await using var scope = new Scope();
        var tag = new DockerImageTagTool(Factory);

        var missing = await RunAsync(tag, ("source", $"bops-test-{Guid.NewGuid():N}:never"), ("image", scope.NewImageName()));
        var badTarget = await RunAsync(tag, ("source", Alpine), ("image", "Not Valid"));
        var digestTarget = await RunAsync(tag, ("source", Alpine), ("image", "app@sha256:" + new string('a', 64)));
        var idTarget = await RunAsync(tag, ("source", Alpine), ("image", "0123456789ab"));
        var badSource = await RunAsync(tag, ("source", "Not Valid"), ("image", scope.NewImageName()));

        Assert.Contains("No such image", missing.ErrorMessage, StringComparison.Ordinal);
        Assert.All([badTarget, digestTarget, idTarget, badSource], result => Assert.False(result.Succeeded));
        Assert.Contains("image:", badTarget.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("source:", badSource.ErrorMessage, StringComparison.Ordinal);
    }

    [DockerAvailableFact]
    public async Task Remove_RemovesOneReference_LeavesTheOthers_AndVerificationConfirms()
    {
        await using var scope = new Scope();
        var source = await scope.BuildAsync();
        var extra = scope.NewImageName();
        await RunAsync(new DockerImageTagTool(Factory), ("source", source), ("image", extra));
        var remove = new DockerImageRemoveTool(Factory);
        var inspect = new DockerImageInspectTool(Factory);

        var removed = Json(await RunAsync(remove, ("image", extra)));

        Assert.Equal(extra, removed["image"]!.GetValue<string>());
        Assert.Contains(extra, removed["untagged"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.Empty(removed["deleted"]!.AsArray());
        Assert.False(removed["truncated"]!.GetValue<bool>());
        Assert.Equal(VerificationStatus.Confirmed, (await VerifyAsync(remove, inspect, ("image", extra))).Status);
        Assert.True(Json(await RunAsync(inspect, ("image", source)))["exists"]!.GetValue<bool>());
    }

    [DockerAvailableFact]
    public async Task Remove_OfTheLastReference_DeletesTheImage_AndAMissingImageIsAFailure()
    {
        await using var scope = new Scope();
        var image = await scope.BuildAsync();
        var remove = new DockerImageRemoveTool(Factory);
        var inspect = new DockerImageInspectTool(Factory);

        var removed = Json(await RunAsync(remove, ("image", image)));

        Assert.NotEmpty(removed["untagged"]!.AsArray());
        Assert.NotEmpty(removed["deleted"]!.AsArray());
        Assert.Equal(VerificationStatus.Confirmed, (await VerifyAsync(remove, inspect, ("image", image))).Status);

        var again = await RunAsync(remove, ("image", image));
        Assert.False(again.Succeeded);
        Assert.Contains("No such image", again.ErrorMessage, StringComparison.Ordinal);
        Assert.False((await RunAsync(remove, ("image", "Not Valid"))).Succeeded);
    }

    [DockerAvailableFact]
    public async Task Remove_ByIdIsRefused_WhileTheImageHasMoreThanOneTag_AndNothingIsForced()
    {
        await using var scope = new Scope();
        var source = await scope.BuildAsync();
        var extra = scope.NewImageName();
        await RunAsync(new DockerImageTagTool(Factory), ("source", source), ("image", extra));
        var inspect = new DockerImageInspectTool(Factory);
        var id = Json(await RunAsync(inspect, ("image", source)))["id"]!.GetValue<string>();

        var refused = await RunAsync(new DockerImageRemoveTool(Factory), ("image", id));

        Assert.False(refused.Succeeded);
        Assert.Contains("409", refused.ErrorMessage, StringComparison.Ordinal);
        Assert.True(Json(await RunAsync(inspect, ("image", source)))["exists"]!.GetValue<bool>());
        Assert.True(Json(await RunAsync(inspect, ("image", extra)))["exists"]!.GetValue<bool>());
    }

    [DockerAvailableFact]
    public async Task Remove_IsRefusedForAnImageAContainerUses_UntilTheContainerIsGone()
    {
        await using var scope = new Scope();
        var image = await scope.BuildAsync();
        using var client = Factory.Create();
        var container = await client.Containers.CreateContainerAsync(new CreateContainerParameters { Image = image, Cmd = ["/nonexistent"] });
        scope.TrackContainer(container.ID);
        var remove = new DockerImageRemoveTool(Factory);
        var inspect = new DockerImageInspectTool(Factory);

        var refused = await RunAsync(remove, ("image", image));

        Assert.False(refused.Succeeded);
        Assert.Equal(ToolOutcome.Failure, refused.Outcome);
        Assert.Contains("409", refused.ErrorMessage, StringComparison.Ordinal);
        Assert.True(Json(await RunAsync(inspect, ("image", image)))["exists"]!.GetValue<bool>());

        await client.Containers.RemoveContainerAsync(container.ID, new ContainerRemoveParameters { Force = true });
        var removed = await RunAsync(remove, ("image", image));

        Assert.True(removed.Succeeded, removed.ErrorMessage);
        Assert.Equal(VerificationStatus.Confirmed, (await VerifyAsync(remove, inspect, ("image", image))).Status);
    }

    [DockerAvailableFact]
    public async Task ImageMutations_CancelledBeforeTheyStart_ThrowCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var arguments = Args(("source", Alpine), ("image", $"bops-test-{Guid.NewGuid():N}:1"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DockerImageTagTool(Factory).ExecuteAsync(arguments, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DockerImageRemoveTool(Factory).ExecuteAsync(arguments, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DockerImageInspectTool(Factory).ExecuteAsync(arguments, cancelled.Token));
    }

    // ---- volumes ----

    [DockerAvailableFact]
    public async Task Volume_CreateInspectListRemove_RoundTrip_WithVerification()
    {
        await using var scope = new Scope();
        var name = scope.NewVolumeName();
        var create = new DockerVolumeCreateTool(Factory);
        var inspect = new DockerVolumeInspectTool(Factory);
        var remove = new DockerVolumeRemoveTool(Factory);
        var list = new DockerVolumesTool(Factory);

        var created = Json(await RunAsync(create, ("volume", name)));
        Assert.True(created["created"]!.GetValue<bool>());
        Assert.Equal("local", created["driver"]!.GetValue<string>());
        Assert.Equal(VerificationStatus.Confirmed, (await VerifyAsync(create, inspect, ("volume", name))).Status);

        var again = Json(await RunAsync(create, ("volume", name), ("driver", "local")));
        Assert.False(again["created"]!.GetValue<bool>());

        var seen = Json(await RunAsync(inspect, ("volume", name)));
        Assert.True(seen["exists"]!.GetValue<bool>());
        Assert.Equal(name, seen["name"]!.GetValue<string>());
        Assert.Equal("local", seen["driver"]!.GetValue<string>());
        Assert.DoesNotContain("ountpoint", seen.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("/var/lib/docker", seen.ToJsonString(), StringComparison.Ordinal);

        var listed = Json(await RunAsync(list, ("limit", 500), ("maxOutputBytes", 65_536)));
        Assert.Contains(name, listed["volumes"]!.AsArray().Select(v => v!["name"]!.GetValue<string>()));
        var names = listed["volumes"]!.AsArray().Select(v => v!["name"]!.GetValue<string>()).ToArray();
        Assert.Equal(names.Order(StringComparer.Ordinal).ToArray(), names);

        var removed = Json(await RunAsync(remove, ("volume", name)));
        Assert.True(removed["removed"]!.GetValue<bool>());
        Assert.Equal(VerificationStatus.Confirmed, (await VerifyAsync(remove, inspect, ("volume", name))).Status);
        Assert.False(Json(await RunAsync(inspect, ("volume", name)))["exists"]!.GetValue<bool>());

        var missing = await RunAsync(remove, ("volume", name));
        Assert.False(missing.Succeeded);
        Assert.Contains("No such volume", missing.ErrorMessage, StringComparison.Ordinal);
    }

    [DockerAvailableFact]
    public async Task VolumeList_IsBoundedByTheLimit_AndSaysWhenItWasCut()
    {
        await using var scope = new Scope();
        var create = new DockerVolumeCreateTool(Factory);
        await RunAsync(create, ("volume", scope.NewVolumeName()));
        await RunAsync(create, ("volume", scope.NewVolumeName()));

        var cut = Json(await RunAsync(new DockerVolumesTool(Factory), ("limit", 1)));

        Assert.Equal(1, cut["returnedVolumes"]!.GetValue<int>());
        Assert.True(cut["observedVolumes"]!.GetValue<int>() >= 2);
        Assert.True(cut["truncated"]!.GetValue<bool>());
    }

    [DockerAvailableFact]
    public async Task VolumeTools_RefuseWhatTheContractForbids_BeforeTheDaemon()
    {
        await using var scope = new Scope();
        var create = new DockerVolumeCreateTool(Factory);
        var name = scope.NewVolumeName();

        var driver = await RunAsync(create, ("volume", name), ("driver", "nfs"));
        var badName = await RunAsync(create, ("volume", "bad name/../x"));
        var shortName = await RunAsync(create, ("volume", "x"));
        var inspect = await RunAsync(new DockerVolumeInspectTool(Factory), ("volume", "bad name"));
        var remove = await RunAsync(new DockerVolumeRemoveTool(Factory), ("volume", "bad name"));
        var limit = await RunAsync(new DockerVolumesTool(Factory), ("limit", 0));
        var limitHigh = await RunAsync(new DockerVolumesTool(Factory), ("limit", 501));
        var bytes = await RunAsync(new DockerVolumesTool(Factory), ("maxOutputBytes", 100));

        Assert.Contains("not allowed", driver.ErrorMessage, StringComparison.Ordinal);
        Assert.All([driver, badName, shortName, inspect, remove, limit, limitHigh, bytes], result => Assert.False(result.Succeeded));
        Assert.False(Json(await RunAsync(new DockerVolumeInspectTool(Factory), ("volume", name)))["exists"]!.GetValue<bool>());
    }

    [DockerAvailableFact]
    public async Task VolumeCreate_HonoursTheOperatorsDriverAllowList()
    {
        await using var scope = new Scope();
        var name = scope.NewVolumeName();
        var onlyCustom = new DockerVolumeCreateTool(Factory, new DockerVolumeOptions { Drivers = ["some-plugin"] });

        var refused = await RunAsync(onlyCustom, ("volume", name), ("driver", "local"));

        Assert.False(refused.Succeeded);
        Assert.Contains("some-plugin", refused.ErrorMessage, StringComparison.Ordinal);
    }

    [DockerAvailableFact]
    public async Task VolumeRemove_IsRefusedForAVolumeAContainerUses_UntilTheContainerIsGone()
    {
        await using var scope = new Scope();
        var name = scope.NewVolumeName();
        var create = new DockerVolumeCreateTool(Factory);
        var remove = new DockerVolumeRemoveTool(Factory);
        var inspect = new DockerVolumeInspectTool(Factory);
        Assert.True((await RunAsync(create, ("volume", name))).Succeeded);
        var image = await scope.BuildAsync();

        using var client = Factory.Create();
        var container = await client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = image,
            Cmd = ["/nonexistent"],
            HostConfig = new HostConfig { Binds = [$"{name}:/data"] },
        });
        scope.TrackContainer(container.ID);

        var refused = await RunAsync(remove, ("volume", name));

        Assert.False(refused.Succeeded);
        Assert.Equal(ToolOutcome.Failure, refused.Outcome);
        Assert.Contains("409", refused.ErrorMessage, StringComparison.Ordinal);
        Assert.True(Json(await RunAsync(inspect, ("volume", name)))["exists"]!.GetValue<bool>());

        await client.Containers.RemoveContainerAsync(container.ID, new ContainerRemoveParameters { Force = true });
        Assert.True((await RunAsync(remove, ("volume", name))).Succeeded);
        Assert.Equal(VerificationStatus.Confirmed, (await VerifyAsync(remove, inspect, ("volume", name))).Status);
    }

    [DockerAvailableFact]
    public async Task VolumeTools_CancelledBeforeTheyStart_ThrowCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var arguments = Args(("volume", "bops-test-cancelled"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DockerVolumeCreateTool(Factory).ExecuteAsync(arguments, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DockerVolumeRemoveTool(Factory).ExecuteAsync(arguments, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DockerVolumeInspectTool(Factory).ExecuteAsync(arguments, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DockerVolumesTool(Factory).ExecuteAsync(ToolArguments.Empty, cancelled.Token));
    }

    // ---- the existing container lifecycle: what the daemon says about things that are not there ----

    [DockerAvailableFact]
    public async Task TheContainerLifecycleTools_ReportANonexistentContainerAsAFailedResult()
    {
        var missing = $"bops-test-{Guid.NewGuid():N}";

        foreach (var tool in new ITool[]
        {
            new DockerInspectTool(Factory), new DockerLogsTool(Factory), new DockerStartTool(Factory), new DockerStopTool(Factory), new DockerRestartTool(Factory),
        })
        {
            var result = await tool.ExecuteAsync(Args(("container", missing)));

            Assert.False(result.Succeeded, tool.Manifest.Name);
            Assert.Equal(ToolOutcome.Failure, result.Outcome);
            Assert.Contains(missing, result.ErrorMessage, StringComparison.Ordinal);
        }
    }

    [DockerAvailableFact]
    public async Task TheContainerTools_CancelledBeforeTheyStart_ThrowCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var arguments = Args(("container", "bops-test-cancelled"));

        foreach (var tool in new ITool[]
        {
            new DockerInspectTool(Factory), new DockerLogsTool(Factory), new DockerStartTool(Factory), new DockerStopTool(Factory), new DockerRestartTool(Factory),
        })
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.ExecuteAsync(arguments, cancelled.Token));
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DockerContainersTool(Factory).ExecuteAsync(ToolArguments.Empty, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DockerImagesTool(Factory).ExecuteAsync(ToolArguments.Empty, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DockerNetworksTool(Factory).ExecuteAsync(ToolArguments.Empty, cancelled.Token));
    }

    [DockerAvailableFact]
    public async Task ARestartOfAStoppedContainer_IsVerifiedRunning_AndAnUnrelatedStatusIsRefuted()
    {
        await using var container = await TestContainer.CreateAsync(Factory);
        var restart = new DockerRestartTool(Factory);
        var inspect = new DockerInspectTool(Factory);
        Assert.True((await RunAsync(restart, ("container", container.Name))).Succeeded);

        Assert.Equal(VerificationStatus.Confirmed, (await VerifyAsync(restart, inspect, ("container", container.Name))).Status);
        Assert.True((await RunAsync(new DockerStopTool(Factory), ("container", container.Name))).Succeeded);
        Assert.Equal(VerificationStatus.Refuted, (await VerifyAsync(restart, inspect, ("container", container.Name))).Status);
    }
}
