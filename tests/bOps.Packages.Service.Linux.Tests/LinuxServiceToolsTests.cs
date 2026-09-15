// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Service.Conformance;
using bOps.Packages.Service.Linux;

namespace bOps.Packages.Service.Linux.Tests;

/// <summary>
/// Integration tests against the real Linux systemd (never mocked — agentic/04-testing-rules.md,
/// "Never mock the operating system"), run through the shared conformance suite so a difference
/// in output shape from the Windows package would be caught here. Marked
/// <c>[Trait("Platform","Linux")]</c>; there is no Linux host in this dev environment, so this
/// project is scaffolded but not exercised here — it runs on CI's real ubuntu-latest VM, which
/// really does run systemd as PID 1 (ADR-0021).
/// </summary>
[Trait("Platform", "Linux")]
public sealed class LinuxServiceToolsTests
{
    // systemd-journald is a core systemd component, present and running on every systemd-based
    // distro this suite runs on, including GitHub Actions' ubuntu-latest.
    private const string KnownServiceName = "systemd-journald.service";

    [LinuxOnlyFact]
    public Task List_Conforms() =>
        ServiceToolConformance.AssertServiceListConformsAsync(new LinuxServiceListTool(), "linux");

    [LinuxOnlyFact]
    public Task Status_ReportsExisting_ForSystemdJournald() =>
        ServiceToolConformance.AssertServiceStatusReportsExistingAsync(new LinuxServiceStatusTool(), "linux", KnownServiceName);

    [LinuxOnlyFact]
    public Task Status_ReportsMissing_ForAnUnlikelyServiceName() =>
        ServiceToolConformance.AssertServiceStatusReportsMissingAsync(
            new LinuxServiceStatusTool(), "linux", "bops-this-service-definitely-does-not-exist.service");

    [Fact]
    public void ToolProvider_ContributesExactlyTheTwoServiceTools()
    {
        var names = new LinuxServiceToolProvider().GetTools().Select(t => t.Manifest.Name).ToList();

        Assert.Equal(["service.list", "service.status"], names);
    }
}
