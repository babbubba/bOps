using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Packages.Docker.Tests;

/// <summary>
/// Integration tests against a real local Docker daemon — never mocked
/// (agentic/04-testing-rules.md: Docker is a platform package). Every test that touches the
/// daemon creates its own uniquely named container via <see cref="TestContainer"/> and removes it
/// afterwards; none of these tests ever names or assumes anything about a container that was not
/// created by the test itself.
/// </summary>
public sealed class DockerToolsTests
{
    private static readonly IDockerClientFactory ClientFactory = new DockerClientFactory();

    private static ToolArguments Args(params (string Name, object Value)[] values)
    {
        var json = new JsonObject();
        foreach (var (name, value) in values)
        {
            json[name] = JsonValue.Create(value);
        }

        return ToolArguments.FromJson(json);
    }

    private static string? ReadStatus(string? inspectOutput) =>
        inspectOutput is null ? null : JsonNode.Parse(inspectOutput)?["status"]?.GetValue<string>();

    [DockerAvailableFact]
    public async Task Containers_Start_Stop_Restart_RoundTrip_WithVerification()
    {
        await using var container = await TestContainer.CreateAsync(ClientFactory);

        var containersTool = new DockerContainersTool(ClientFactory);
        var inspectTool = new DockerInspectTool(ClientFactory);
        var startTool = new DockerStartTool(ClientFactory);
        var stopTool = new DockerStopTool(ClientFactory);
        var restartTool = new DockerRestartTool(ClientFactory);
        var logsTool = new DockerLogsTool(ClientFactory);

        // docker.containers sees the freshly created (not yet started) container.
        var listResult = await containersTool.ExecuteAsync(ToolArguments.Empty);
        Assert.True(listResult.Succeeded);
        Assert.Contains(container.Name, listResult.Output, StringComparison.Ordinal);

        // docker.inspect reports it as not running before any start.
        var beforeStart = await inspectTool.ExecuteAsync(Args(("container", container.Name)));
        Assert.True(beforeStart.Succeeded);
        Assert.NotEqual("running", ReadStatus(beforeStart.Output));

        // docker.start, verified via docker.inspect: Confirmed once the container is running.
        var startResult = await startTool.ExecuteAsync(Args(("container", container.Name)));
        Assert.True(startResult.Succeeded);
        var afterStart = await inspectTool.ExecuteAsync(Args(("container", container.Name)));
        var startVerification = await startTool.EvaluateVerificationAsync(Args(("container", container.Name)), afterStart);
        Assert.Equal(VerificationStatus.Confirmed, startVerification.Status);

        // docker.stop, verified the same way: Confirmed once it is no longer running.
        var stopResult = await stopTool.ExecuteAsync(Args(("container", container.Name)));
        Assert.True(stopResult.Succeeded);
        var afterStop = await inspectTool.ExecuteAsync(Args(("container", container.Name)));
        var stopVerification = await stopTool.EvaluateVerificationAsync(Args(("container", container.Name)), afterStop);
        Assert.Equal(VerificationStatus.Confirmed, stopVerification.Status);

        // docker.restart on a stopped container starts it — verified the same predicate as docker.start.
        var restartResult = await restartTool.ExecuteAsync(Args(("container", container.Name)));
        Assert.True(restartResult.Succeeded);
        var afterRestart = await inspectTool.ExecuteAsync(Args(("container", container.Name)));
        var restartVerification = await restartTool.EvaluateVerificationAsync(Args(("container", container.Name)), afterRestart);
        Assert.Equal(VerificationStatus.Confirmed, restartVerification.Status);

        // docker.logs works against a real, running container (alpine + `sleep` writes nothing,
        // but the call itself must succeed and report that cleanly rather than erroring).
        var logsResult = await logsTool.ExecuteAsync(Args(("container", container.Name)));
        Assert.True(logsResult.Succeeded);
    }

    [DockerAvailableFact]
    public async Task Images_And_Networks_DoNotThrow()
    {
        var imagesResult = await new DockerImagesTool(ClientFactory).ExecuteAsync(ToolArguments.Empty);
        Assert.True(imagesResult.Succeeded);

        var networksResult = await new DockerNetworksTool(ClientFactory).ExecuteAsync(ToolArguments.Empty);
        Assert.True(networksResult.Succeeded);
        // The default bridge network always exists once the daemon has run at least one container.
        Assert.NotEqual("(no networks)", networksResult.Output);
    }

    [DockerAvailableFact]
    public async Task Inspect_Fails_ForAContainerThatDoesNotExist()
    {
        var result = await new DockerInspectTool(ClientFactory).ExecuteAsync(Args(("container", $"bops-does-not-exist-{Guid.NewGuid():N}")));

        Assert.Equal(ToolOutcome.Failure, result.Outcome);
    }

    [Theory]
    [InlineData("docker.start")]
    [InlineData("docker.stop")]
    [InlineData("docker.restart")]
    public async Task NonReadTools_VerificationIsInconclusive_WhenInspectItselfFails(string toolName)
    {
        ITool tool = toolName switch
        {
            "docker.start" => new DockerStartTool(ClientFactory),
            "docker.stop" => new DockerStopTool(ClientFactory),
            "docker.restart" => new DockerRestartTool(ClientFactory),
            _ => throw new ArgumentOutOfRangeException(nameof(toolName)),
        };
        var verifiable = (IVerifiableTool)tool;
        var failedInspect = ToolCallResult.Failure("container not found");

        var outcome = await verifiable.EvaluateVerificationAsync(Args(("container", "irrelevant")), failedInspect);

        Assert.Equal(VerificationStatus.Inconclusive, outcome.Status);
    }

    [Fact]
    public void ToolProvider_ContributesExactlyTheEightDockerTools_AllRequiringTheDockerCapability()
    {
        var tools = new DockerToolProvider(ClientFactory).GetTools().ToList();

        Assert.Equal(
            ["docker.containers", "docker.images", "docker.networks", "docker.inspect", "docker.logs", "docker.start", "docker.stop", "docker.restart"],
            tools.Select(t => t.Manifest.Name));
        Assert.All(tools, t => Assert.Contains(DockerCapability.Name, t.Manifest.Requires));
    }

    [Theory]
    [InlineData("docker.start")]
    [InlineData("docker.stop")]
    [InlineData("docker.restart")]
    public void NonReadTools_AreMediumRisk_AndDeclareVerificationAgainstDockerInspect(string toolName)
    {
        var tool = new DockerToolProvider(ClientFactory).GetTools().Single(t => t.Manifest.Name == toolName);

        Assert.Equal(RiskLevel.Medium, tool.Manifest.Risk);
        Assert.NotNull(tool.Manifest.Verification);
        Assert.Equal("docker.inspect", tool.Manifest.Verification!.VerifyToolName);
        Assert.IsAssignableFrom<IVerifiableTool>(tool);
    }
}
