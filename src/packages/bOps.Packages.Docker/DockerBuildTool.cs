// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bOps.Abstractions;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace bOps.Packages.Docker;

/// <summary>
/// Builds one local directory into one tagged image (ADR-0033). <see cref="RiskLevel.High"/> and always approved by a human: a
/// Dockerfile executes instructions and can use network, disk and CPU. The context must be inside a directory the operator listed
/// in <c>Docker:Build:Contexts</c> and is checked, and limited in files and bytes, before anything reaches the daemon; there are
/// no build arguments, secrets, network or privilege options, only the archive, the Dockerfile name and one tag. The result carries
/// the image id and a short bounded tail of the daemon's output, never the context or the whole log. Verified via
/// <c>docker.image.inspect</c>, which must list the tag. The argument is called <c>image</c> because the runtime carries
/// verification arguments by name.
/// </summary>
public sealed partial class DockerBuildTool(IDockerClientFactory clientFactory, DockerBuildOptions? options = null) : IVerifiableTool
{
    private const int MaximumLogLines = 20;
    private const int MaximumLogLineCharacters = 200;

    private readonly DockerBuildOptions _options = options ?? new DockerBuildOptions();

    public ToolManifest Manifest { get; } = new()
    {
        Name = "docker.build",
        Description = "Builds an image from a local directory the operator has allowed (Docker:Build:Contexts) and a Dockerfile inside it, and tags it. " +
            "No build arguments, secrets or network options. Contexts with symbolic links or a .dockerignore are refused.",
        Risk = RiskLevel.High,
        RequiresExplicitApproval = true,
        Platforms = ["windows", "linux"],
        Requires = [DockerCapability.Name, DockerCapability.BuildContexts],
        Parameters =
        [
            new ToolParameter("context", ToolParameterType.String, "The absolute path of the build context directory."),
            new ToolParameter("image", ToolParameterType.String, "The repository:tag to give the built image."),
            new ToolParameter("dockerfile", ToolParameterType.String, "The Dockerfile's path inside the context. Defaults to Dockerfile.", Required: false),
        ],
        Verification = new VerificationSpec("docker.image.inspect", ["image"], "Confirms the tagged image exists afterwards."),
    };

    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var contextPath = arguments.GetRequired<string>("context");
        var image = arguments.GetRequired<string>("image");
        var dockerfile = arguments.TryGet<string>("dockerfile", out var requested) ? requested : null;

        if (!DockerImageReference.TryParseName(image, out var reference, out var referenceError))
        {
            return ToolCallResult.Failure($"image: {referenceError}");
        }

        if (reference!.Digest is not null)
        {
            return ToolCallResult.Failure("image: a build is tagged repository:tag, not a digest.");
        }

        await using var context = await DockerBuildContext.PrepareAsync(_options, contextPath, dockerfile, temporaryDirectory: null, ct);
        if (context.Error is not null)
        {
            return ToolCallResult.Failure($"Refusing to build: {context.Error}");
        }

        using var client = clientFactory.Create();
        try
        {
            var log = new BuildLog();
            var progress = new InlineProgress<JSONMessage>(log.Add);

            await client.Images.BuildImageFromDockerfileAsync(
                new ImageBuildParameters
                {
                    Dockerfile = context.Dockerfile,
                    Tags = [reference.Familiar],
                    Remove = true,
                    ForceRemove = true,
                    NoCache = false,
                    Pull = null,
                    SuppressOutput = false,
                },
                context.Archive,
                authConfigs: null,
                headers: null,
                progress,
                ct);

            if (log.Error is not null)
            {
                return ToolCallResult.Failure($"The build of '{DockerFailure.Bound(image)}' failed: {DockerFailure.Bound(log.Error)}{log.TailSuffix()}");
            }

            var built = await client.Images.InspectImageAsync(reference.Familiar, ct);
            return ToolCallResult.Success(new JsonObject
            {
                ["schemaVersion"] = 1,
                ["image"] = reference.Familiar,
                ["id"] = built.ID,
                ["context"] = new JsonObject { ["files"] = context.Files, ["bytes"] = context.Bytes },
                ["steps"] = log.Steps,
                ["log"] = new JsonArray(log.Tail.Select(line => (JsonNode)JsonValue.Create(line)!).ToArray()),
            }.ToJsonString());
        }
        catch (DockerApiException ex)
        {
            return ToolCallResult.Failure($"The build of '{DockerFailure.Bound(image)}' failed: {DockerFailure.Describe(ex)}");
        }
        catch (Exception ex) when (DockerFailure.IsUnreachable(ex))
        {
            return ToolCallResult.Failure(DockerFailure.Unreachable(ex));
        }
    }

    public Task<VerificationOutcome> EvaluateVerificationAsync(
        ToolArguments originalArguments, ToolCallResult verificationToolResult, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(originalArguments);
        var wanted = originalArguments.TryGet<string>("image", out var image)
            && DockerImageReference.TryParseName(image, out var reference, out _)
                ? reference!.Familiar
                : null;

        return Task.FromResult(DockerImageOutput.Verify(
            verificationToolResult,
            "build",
            state =>
            {
                if (!state.Exists)
                {
                    return "docker.image.inspect reports the built image does not exist.";
                }

                return wanted is null || state.Tags.Contains(wanted, StringComparer.Ordinal)
                    ? null
                    : $"docker.image.inspect does not list '{wanted}' among the image's tags.";
            }));
    }

    [GeneratedRegex(@"^Step (?<step>\d+)/\d+", RegexOptions.CultureInvariant)]
    private static partial Regex StepPattern();

    /// <summary>What the build's progress stream said: the last few lines, the highest step number, and any error.</summary>
    private sealed class BuildLog
    {
        private readonly Queue<string> _tail = new();

        public IReadOnlyCollection<string> Tail => _tail;

        public int Steps { get; private set; }

        public string? Error { get; private set; }

        public void Add(JSONMessage message)
        {
            if (message.Error is not null || !string.IsNullOrEmpty(message.ErrorMessage))
            {
                Error = message.Error?.Message ?? message.ErrorMessage;
            }

            if (string.IsNullOrEmpty(message.Stream))
            {
                return;
            }

            foreach (var raw in message.Stream.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var line = raw.Length <= MaximumLogLineCharacters ? raw : raw[..MaximumLogLineCharacters] + "…";
                if (StepPattern().Match(line) is { Success: true } step
                    && int.TryParse(step.Groups["step"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                {
                    Steps = Math.Max(Steps, number);
                }

                _tail.Enqueue(line);
                if (_tail.Count > MaximumLogLines)
                {
                    _tail.Dequeue();
                }
            }
        }

        public string TailSuffix() => _tail.Count == 0 ? string.Empty : $" Last output: {string.Join(" | ", _tail.TakeLast(5))}";
    }
}
