// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Service.Conformance;
using bOps.Packages.Service.Windows;

namespace bOps.Packages.Service.Windows.Tests;

/// <summary>
/// Integration tests against the real Windows Service Control Manager (never mocked —
/// agentic/04-testing-rules.md, "Never mock the operating system"), run through the shared
/// conformance suite so a difference in output shape from the Linux package would be caught here.
/// </summary>
[Trait("Platform", "Windows")]
public sealed class WindowsServiceToolsTests
{
    // The Windows Event Log service: present and installed on every Windows machine this suite
    // runs on, whether or not it happens to be running right now.
    private const string KnownServiceName = "EventLog";

    [WindowsOnlyFact]
    public Task List_Conforms() =>
        ServiceToolConformance.AssertServiceListConformsAsync(new WindowsServiceListTool(), "windows");

    [WindowsOnlyFact]
    public Task Status_ReportsExisting_ForEventLog() =>
        ServiceToolConformance.AssertServiceStatusReportsExistingAsync(new WindowsServiceStatusTool(), "windows", KnownServiceName);

    [WindowsOnlyFact]
    public Task Status_ReportsMissing_ForAnUnlikelyServiceName() =>
        ServiceToolConformance.AssertServiceStatusReportsMissingAsync(
            new WindowsServiceStatusTool(), "windows", "bops-this-service-definitely-does-not-exist");

    [Fact]
    public void ToolProvider_ContributesExactlyTheTwoServiceTools()
    {
        var names = new WindowsServiceToolProvider().GetTools().Select(t => t.Manifest.Name).ToList();

        Assert.Equal(["service.list", "service.status"], names);
    }
}
