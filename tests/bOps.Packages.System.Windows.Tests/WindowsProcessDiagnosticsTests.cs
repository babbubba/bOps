// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Sys.Conformance;
using bOps.Packages.Sys.Windows;

namespace bOps.Packages.System.Windows.Tests;

/// <summary>
/// V1.3-C process diagnostics against the real Windows OS — the actual test process, a real child
/// process this test spawns, and a real protected system process. Never mocked
/// (agentic/04-testing-rules.md, "Never mock the operating system").
/// </summary>
[Trait("Platform", "Windows")]
public sealed class WindowsProcessDiagnosticsTests
{
    private const int SystemProcessId = 4;

    [WindowsOnlyFact]
    public Task ProcessInspect_ReportsTheAddedFields() =>
        ProcessDiagnosticsConformance.AssertProcessInspectReportsTheAddedFieldsAsync(new WindowsProcessInspectTool());

    [WindowsOnlyFact]
    public Task ProcessInspect_ReportsMissing_WithEveryAddedFieldNull() =>
        ProcessDiagnosticsConformance.AssertProcessInspectReportsMissingWithTheAddedFieldsAsync(new WindowsProcessInspectTool());

    [WindowsOnlyFact]
    public Task ProcessMetrics_Conforms() =>
        ProcessDiagnosticsConformance.AssertProcessMetricsConformsAsync(new WindowsProcessMetricsTool(), "windows");

    [WindowsOnlyFact]
    public Task ProcessMetrics_DefaultsToFiveHundredMilliseconds() =>
        ProcessDiagnosticsConformance.AssertProcessMetricsDefaultsToFiveHundredMillisecondsAsync(new WindowsProcessMetricsTool());

    [WindowsOnlyFact]
    public Task ProcessMetrics_ReportsMissing() =>
        ProcessDiagnosticsConformance.AssertProcessMetricsReportsMissingAsync(new WindowsProcessMetricsTool());

    [WindowsOnlyFact]
    public Task ProcessMetrics_RejectsOutOfRangeSamples() =>
        ProcessDiagnosticsConformance.AssertProcessMetricsRejectsOutOfRangeSamplesAsync(new WindowsProcessMetricsTool());

    [WindowsOnlyFact]
    public Task ProcessTree_Conforms() =>
        ProcessDiagnosticsConformance.AssertProcessTreeConformsAsync(new WindowsProcessTreeTool(), "windows");

    [WindowsOnlyFact]
    public Task ProcessTree_ReportsAMissingRoot() =>
        ProcessDiagnosticsConformance.AssertProcessTreeReportsAMissingRootAsync(new WindowsProcessTreeTool());

    [WindowsOnlyFact]
    public Task ProcessTree_RespectsBounds() =>
        ProcessDiagnosticsConformance.AssertProcessTreeRespectsBoundsAsync(new WindowsProcessTreeTool());

    [WindowsOnlyFact]
    public Task ProcessTree_RejectsOutOfRangeBounds() =>
        ProcessDiagnosticsConformance.AssertProcessTreeRejectsOutOfRangeBoundsAsync(new WindowsProcessTreeTool());

    [WindowsOnlyFact]
    public async Task ProcessTree_ShowsARealChildOfThisProcess()
    {
        using var child = StartChild();

        await ProcessDiagnosticsConformance.AssertProcessTreeShowsTheParentChildRelationAsync(
            new WindowsProcessTreeTool(), Environment.ProcessId, child.Id);
    }

    [WindowsOnlyFact]
    public async Task ProcessInspect_ReportsThisProcessAsTheParentOfARealChild()
    {
        using var child = StartChild();

        var result = await new WindowsProcessInspectTool().ExecuteAsync(
            ToolArguments.FromJson(new JsonObject { ["pid"] = child.Id }));

        Assert.True(result.Succeeded, result.ErrorMessage);
        var json = JsonNode.Parse(result.Output!)!;
        Assert.True(json["exists"]!.GetValue<bool>());
        Assert.Equal(Environment.ProcessId, json["parentPid"]!.GetValue<int>());
    }

    [WindowsOnlyFact]
    public Task ProcessModules_Conform() =>
        ProcessDiagnosticsConformance.AssertProcessModulesConformAsync(new WindowsProcessModulesTool(), "windows");

    [WindowsOnlyFact]
    public Task ProcessModules_RespectBounds() =>
        ProcessDiagnosticsConformance.AssertProcessModulesRespectBoundsAsync(new WindowsProcessModulesTool());

    [WindowsOnlyFact]
    public Task ProcessModules_RejectOutOfRangeBounds() =>
        ProcessDiagnosticsConformance.AssertProcessModulesRejectOutOfRangeBoundsAsync(new WindowsProcessModulesTool());

    [WindowsOnlyFact]
    public Task ProcessModules_ReportMissing() =>
        ProcessDiagnosticsConformance.AssertProcessModulesReportMissingAsync(new WindowsProcessModulesTool());

    [WindowsOnlyFact]
    public async Task ProcessModules_ReportAProtectedProcessAsAGap_NotAsAnEmptyList()
    {
        // PID 4 is the Windows "System" process. Its module list is not readable without
        // SeDebugPrivilege, which bOps never asks for — so the honest answer is a status, not an
        // empty list. If a run ever does have the privilege, "available" is equally correct; what
        // must never happen is a failure or a silent empty success.
        var result = await new WindowsProcessModulesTool().ExecuteAsync(
            ToolArguments.FromJson(new JsonObject { ["pid"] = SystemProcessId }));

        Assert.True(result.Succeeded, result.ErrorMessage);
        var json = JsonNode.Parse(result.Output!)!;
        Assert.True(json["exists"]!.GetValue<bool>());
        if (json["status"]!.GetValue<string>() == "unavailable")
        {
            Assert.False(json["complete"]!.GetValue<bool>());
            Assert.Empty(json["modules"]!.AsArray());
        }
    }

    [WindowsOnlyFact]
    public async Task ProcessInspect_DegradesFieldByField_ForAProtectedProcess()
    {
        var result = await new WindowsProcessInspectTool().ExecuteAsync(
            ToolArguments.FromJson(new JsonObject { ["pid"] = SystemProcessId }));

        Assert.True(result.Succeeded, result.ErrorMessage);
        var json = JsonNode.Parse(result.Output!)!.AsObject();
        Assert.True(json["exists"]!.GetValue<bool>());
        // Whatever this identity cannot read is null; the observation itself still stands, and the
        // name comes from a different source than the counters that are refused.
        Assert.False(string.IsNullOrWhiteSpace(json["name"]?.GetValue<string>()));
        foreach (var field in new[] { "commandLine", "handleOrFdCount", "ioReadBytes", "cpuTotalMs" })
        {
            Assert.True(json.ContainsKey(field));
        }
    }

    [WindowsOnlyFact]
    public Task NoProcessTool_EverReturnsAnEnvironmentVariable()
    {
        const string name = "BOPS_V13C_MARKER";
        const string value = "a9f3c1e7-never-in-tool-output";
        Environment.SetEnvironmentVariable(name, value);
        try
        {
            return ProcessDiagnosticsConformance.AssertNoEnvironmentVariableIsEverReturnedAsync(
                new WindowsProcessInspectTool(),
                new WindowsProcessMetricsTool(),
                new WindowsProcessTreeTool(),
                new WindowsProcessModulesTool(),
                name,
                value);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [WindowsOnlyFact]
    public Task EveryProcessTool_HonoursCancellation() =>
        ProcessDiagnosticsConformance.AssertCancellationIsHonouredAsync(
            new WindowsProcessInspectTool(),
            new WindowsProcessMetricsTool(),
            new WindowsProcessTreeTool(),
            new WindowsProcessModulesTool());

    // A real child process owned by this test: ping loops for a minute without a console window and
    // exits on its own if a test ever fails before disposing it.
    private static ChildProcess StartChild()
    {
        var child = Process.Start(new ProcessStartInfo("ping.exe", "-n 60 127.0.0.1")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        });

        Assert.NotNull(child);
        return new ChildProcess(child!);
    }

    private sealed class ChildProcess(Process process) : IDisposable
    {
        public int Id { get; } = process.Id;

        public void Dispose()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (InvalidOperationException)
            {
            }

            process.Dispose();
        }
    }
}
