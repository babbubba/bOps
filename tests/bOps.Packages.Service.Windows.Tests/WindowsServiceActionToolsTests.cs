// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.ServiceProcess;
using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Packages.Service.Windows.Tests;

/// <summary>
/// Integration tests against a real, freshly-installed, uniquely-named Windows Service every test
/// creates and deletes itself (<see cref="ThrowawayWindowsService"/>) — never a real system
/// service (agentic/04-testing-rules.md, "Never mock the operating system"). Requires an elevated
/// process (<see cref="RequiresElevationFactAttribute"/>): this dev environment's own session is
/// not elevated, so these run for real only on CI's <c>windows-latest</c> runner.
/// </summary>
[Trait("Platform", "Windows")]
public sealed class WindowsServiceActionToolsTests
{
    private static ToolArguments NameArgs(string name) => ToolArguments.FromJson(new JsonObject { ["name"] = name });

    private static async Task<string?> ReadStatusAsync(string name)
    {
        var result = await new WindowsServiceStatusTool().ExecuteAsync(NameArgs(name));
        return result.Succeeded ? JsonNode.Parse(result.Output!)!["status"]!.GetValue<string>() : null;
    }

    [RequiresElevationFact]
    public async Task ServiceStart_StartsTheRealThrowawayService()
    {
        using var service = new ThrowawayWindowsService();
        var tool = new WindowsServiceStartTool();

        var result = await tool.ExecuteAsync(NameArgs(service.Name));

        Assert.True(result.Succeeded, result.ErrorMessage);
        using var controller = new ServiceController(service.Name);
        controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
        Assert.Equal("running", await ReadStatusAsync(service.Name));
    }

    [RequiresElevationFact]
    public async Task ServiceStop_StopsTheRealThrowawayService()
    {
        using var service = new ThrowawayWindowsService();
        using (var controller = new ServiceController(service.Name))
        {
            controller.Start();
            controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
        }

        var tool = new WindowsServiceStopTool();
        var result = await tool.ExecuteAsync(NameArgs(service.Name));

        Assert.True(result.Succeeded, result.ErrorMessage);
        using var refreshedController = new ServiceController(service.Name);
        refreshedController.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
        Assert.Equal("stopped", await ReadStatusAsync(service.Name));
    }

    [RequiresElevationFact]
    public async Task ServiceRestart_StartsAFreshlyCreatedStoppedService()
    {
        // A throwaway service starts life Stopped (sc.exe create's default) — this exercises
        // WindowsServiceRestartTool's tolerance of "already stopped" rather than assuming a prior
        // running state.
        using var service = new ThrowawayWindowsService();
        var tool = new WindowsServiceRestartTool();

        var result = await tool.ExecuteAsync(NameArgs(service.Name));

        Assert.True(result.Succeeded, result.ErrorMessage);
        using var controller = new ServiceController(service.Name);
        controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
        Assert.Equal("running", await ReadStatusAsync(service.Name));
    }

    [RequiresElevationFact]
    public async Task ServiceRestart_RestartsAnAlreadyRunningService()
    {
        using var service = new ThrowawayWindowsService();
        using (var controller = new ServiceController(service.Name))
        {
            controller.Start();
            controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
        }

        var tool = new WindowsServiceRestartTool();
        var result = await tool.ExecuteAsync(NameArgs(service.Name));

        Assert.True(result.Succeeded, result.ErrorMessage);
        using var refreshedController = new ServiceController(service.Name);
        refreshedController.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
        Assert.Equal("running", await ReadStatusAsync(service.Name));
    }

    [RequiresElevationFact]
    public async Task ServiceStart_Fails_ForAServiceThatDoesNotExist()
    {
        var tool = new WindowsServiceStartTool();

        var result = await tool.ExecuteAsync(NameArgs("bops-does-not-exist-" + Guid.NewGuid().ToString("N")));

        Assert.Equal(ToolOutcome.Failure, result.Outcome);
    }

    [Theory]
    [InlineData("service.start", "running")]
    [InlineData("service.stop", "stopped")]
    [InlineData("service.restart", "running")]
    public void ActionTools_DeclareVerificationAgainstServiceStatus(string toolName, string expectedInDescription)
    {
        var tool = new WindowsServiceToolProvider().GetTools().Single(t => t.Manifest.Name == toolName);

        Assert.Equal(RiskLevel.Medium, tool.Manifest.Risk);
        Assert.NotNull(tool.Manifest.Verification);
        Assert.Equal("service.status", tool.Manifest.Verification!.VerifyToolName);
        Assert.Contains(expectedInDescription, tool.Manifest.Verification.Description, StringComparison.Ordinal);
        Assert.IsAssignableFrom<IVerifiableTool>(tool);
    }
}
