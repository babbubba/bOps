// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Sys.Conformance;
using bOps.Packages.Sys.Linux;

namespace bOps.Packages.System.Linux.Tests;

/// <summary>
/// V1.3-C process diagnostics against a real Linux <c>/proc</c> — the actual test process, a real
/// child process this test spawns, and PID 1. Never mocked (agentic/04-testing-rules.md).
/// </summary>
[Trait("Platform", "Linux")]
public sealed class LinuxProcessDiagnosticsTests
{
    private const int InitProcessId = 1;

    [LinuxOnlyFact]
    public Task ProcessInspect_ReportsTheAddedFields() =>
        ProcessDiagnosticsConformance.AssertProcessInspectReportsTheAddedFieldsAsync(new LinuxProcessInspectTool());

    [LinuxOnlyFact]
    public Task ProcessInspect_ReportsMissing_WithEveryAddedFieldNull() =>
        ProcessDiagnosticsConformance.AssertProcessInspectReportsMissingWithTheAddedFieldsAsync(new LinuxProcessInspectTool());

    [LinuxOnlyFact]
    public Task ProcessMetrics_Conforms() =>
        ProcessDiagnosticsConformance.AssertProcessMetricsConformsAsync(new LinuxProcessMetricsTool(), "linux");

    [LinuxOnlyFact]
    public Task ProcessMetrics_DefaultsToFiveHundredMilliseconds() =>
        ProcessDiagnosticsConformance.AssertProcessMetricsDefaultsToFiveHundredMillisecondsAsync(new LinuxProcessMetricsTool());

    [LinuxOnlyFact]
    public Task ProcessMetrics_ReportsMissing() =>
        ProcessDiagnosticsConformance.AssertProcessMetricsReportsMissingAsync(new LinuxProcessMetricsTool());

    [LinuxOnlyFact]
    public Task ProcessMetrics_RejectsOutOfRangeSamples() =>
        ProcessDiagnosticsConformance.AssertProcessMetricsRejectsOutOfRangeSamplesAsync(new LinuxProcessMetricsTool());

    [LinuxOnlyFact]
    public Task ProcessTree_Conforms() =>
        ProcessDiagnosticsConformance.AssertProcessTreeConformsAsync(new LinuxProcessTreeTool(), "linux");

    [LinuxOnlyFact]
    public Task ProcessTree_ReportsAMissingRoot() =>
        ProcessDiagnosticsConformance.AssertProcessTreeReportsAMissingRootAsync(new LinuxProcessTreeTool());

    [LinuxOnlyFact]
    public Task ProcessTree_RespectsBounds() =>
        ProcessDiagnosticsConformance.AssertProcessTreeRespectsBoundsAsync(new LinuxProcessTreeTool());

    [LinuxOnlyFact]
    public Task ProcessTree_RejectsOutOfRangeBounds() =>
        ProcessDiagnosticsConformance.AssertProcessTreeRejectsOutOfRangeBoundsAsync(new LinuxProcessTreeTool());

    [LinuxOnlyFact]
    public async Task ProcessTree_ShowsARealChildOfThisProcess()
    {
        using var child = StartChild();

        await ProcessDiagnosticsConformance.AssertProcessTreeShowsTheParentChildRelationAsync(
            new LinuxProcessTreeTool(), Environment.ProcessId, child.Id);
    }

    [LinuxOnlyFact]
    public async Task ProcessInspect_ReportsThisProcessAsTheParentOfARealChild()
    {
        using var child = StartChild();

        var result = await new LinuxProcessInspectTool().ExecuteAsync(
            ToolArguments.FromJson(new JsonObject { ["pid"] = child.Id }));

        Assert.True(result.Succeeded, result.ErrorMessage);
        var json = JsonNode.Parse(result.Output!)!;
        Assert.True(json["exists"]!.GetValue<bool>());
        Assert.Equal(Environment.ProcessId, json["parentPid"]!.GetValue<int>());
        Assert.Contains("sleep", json["commandLine"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [LinuxOnlyFact]
    public Task ProcessModules_Conform() =>
        ProcessDiagnosticsConformance.AssertProcessModulesConformAsync(new LinuxProcessModulesTool(), "linux");

    [LinuxOnlyFact]
    public Task ProcessModules_RespectBounds() =>
        ProcessDiagnosticsConformance.AssertProcessModulesRespectBoundsAsync(new LinuxProcessModulesTool());

    [LinuxOnlyFact]
    public Task ProcessModules_RejectOutOfRangeBounds() =>
        ProcessDiagnosticsConformance.AssertProcessModulesRejectOutOfRangeBoundsAsync(new LinuxProcessModulesTool());

    [LinuxOnlyFact]
    public Task ProcessModules_ReportMissing() =>
        ProcessDiagnosticsConformance.AssertProcessModulesReportMissingAsync(new LinuxProcessModulesTool());

    [LinuxOnlyFact]
    public async Task ProcessModules_ReportPidOneAsAGapOrAsModules_NeverAsAFailure()
    {
        // PID 1 belongs to root. An unprivileged run cannot read its memory map and must say so; a
        // container that runs as root can read it and "available" is equally correct. What must
        // never happen is a failed tool call or a silent empty success.
        var result = await new LinuxProcessModulesTool().ExecuteAsync(
            ToolArguments.FromJson(new JsonObject { ["pid"] = InitProcessId }));

        Assert.True(result.Succeeded, result.ErrorMessage);
        var json = JsonNode.Parse(result.Output!)!;
        Assert.True(json["exists"]!.GetValue<bool>());
        if (json["status"]!.GetValue<string>() == "unavailable")
        {
            Assert.False(json["complete"]!.GetValue<bool>());
            Assert.Empty(json["modules"]!.AsArray());
        }
    }

    [LinuxOnlyFact]
    public async Task ProcessInspect_DegradesFieldByField_ForPidOne()
    {
        var result = await new LinuxProcessInspectTool().ExecuteAsync(
            ToolArguments.FromJson(new JsonObject { ["pid"] = InitProcessId }));

        Assert.True(result.Succeeded, result.ErrorMessage);
        var json = JsonNode.Parse(result.Output!)!.AsObject();
        Assert.True(json["exists"]!.GetValue<bool>());
        // /proc/1/stat and /proc/1/status are world-readable, so ancestry and ownership are known
        // even when /proc/1/io and /proc/1/exe are refused.
        Assert.Equal(0, json["parentPid"]!.GetValue<int>());
        Assert.False(string.IsNullOrWhiteSpace(json["user"]?.GetValue<string>()));
        foreach (var field in new[] { "executablePath", "ioReadBytes", "ioWriteBytes", "handleOrFdCount" })
        {
            Assert.True(json.ContainsKey(field));
        }
    }

    [LinuxOnlyFact]
    public Task NoProcessTool_EverReturnsAnEnvironmentVariable()
    {
        const string name = "BOPS_V13C_MARKER";
        const string value = "a9f3c1e7-never-in-tool-output";
        Environment.SetEnvironmentVariable(name, value);
        try
        {
            return ProcessDiagnosticsConformance.AssertNoEnvironmentVariableIsEverReturnedAsync(
                new LinuxProcessInspectTool(),
                new LinuxProcessMetricsTool(),
                new LinuxProcessTreeTool(),
                new LinuxProcessModulesTool(),
                name,
                value);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [LinuxOnlyFact]
    public Task EveryProcessTool_HonoursCancellation() =>
        ProcessDiagnosticsConformance.AssertCancellationIsHonouredAsync(
            new LinuxProcessInspectTool(),
            new LinuxProcessMetricsTool(),
            new LinuxProcessTreeTool(),
            new LinuxProcessModulesTool());

    private static ChildProcess StartChild()
    {
        var child = Process.Start(new ProcessStartInfo("/bin/sleep", "60") { UseShellExecute = false });

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
