// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Service.Conformance;
using bOps.Packages.Service.Core;
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
    public void ToolProvider_ContributesExactlyTheFiveServiceTools()
    {
        var names = new LinuxServiceToolProvider().GetTools().Select(t => t.Manifest.Name).ToList();

        Assert.Equal(["service.list", "service.status", "service.start", "service.stop", "service.restart", "service.config", "service.dependencies", "service.enable", "service.disable"], names);
    }

    [Theory]
    [InlineData("enabled", true, "enabled", "automatic")]
    [InlineData("enabled-runtime", true, "enabled-runtime", "automatic")]
    [InlineData("disabled", false, "disabled", "disabled")]
    [InlineData("static", null, "static", "static")]
    [InlineData("indirect", null, "indirect", "unknown")]
    [InlineData("linked", null, "linked", "unknown")]
    [InlineData("linked-runtime", null, "linked-runtime", "unknown")]
    [InlineData("alias", null, "alias", "unknown")]
    [InlineData("generated", null, "generated", "unknown")]
    [InlineData("transient", null, "transient", "unknown")]
    [InlineData("masked", false, "masked", "disabled")]
    [InlineData("masked-runtime", false, "masked-runtime", "disabled")]
    [InlineData("unknown", null, "unknown", "unknown")]
    public void UnitFileState_IsMappedWithoutLosingNativeValue(string state, bool? enabled, string native, string startupType)
    {
        var result = LinuxServiceConfiguration.NormalizeUnitFileState(state);
        Assert.Equal(enabled, result.Enabled);
        Assert.Equal(native, result.EnablementState);
        Assert.Equal(startupType, result.StartupType);
    }

    [Fact]
    public void UnitName_RejectsOptionInjection()
    {
        Assert.False(ServiceUnitName.IsPlausible("--help"));
        Assert.False(ServiceUnitName.IsPlausible("-evil.service"));
    }

    [Fact]
    public void G3Commands_AreFixedAndNeverUseNow()
    {
        Assert.Equal(["show", $"--property={LinuxServiceConfiguration.Properties}", "--no-pager", "--", "demo.service"], LinuxServiceConfiguration.ShowCommand("demo.service"));
        Assert.Equal(["enable", "--", "demo.service"], LinuxServiceConfiguration.EnableCommand("demo.service"));
        Assert.Equal(["disable", "--", "demo.service"], LinuxServiceConfiguration.DisableCommand("demo.service"));
        Assert.DoesNotContain("--now", LinuxServiceConfiguration.EnableCommand("demo.service"));
        Assert.DoesNotContain("--now", LinuxServiceConfiguration.DisableCommand("demo.service"));
    }

    [Theory]
    [InlineData("{ path=/usr/bin/demo ; argv[]=/usr/bin/demo ; ignore_errors=no ; }", "/usr/bin/demo", null, true)]
    [InlineData("{ path=/usr/bin/demo ; argv[]=/usr/bin/demo --flag value ; ignore_errors=no ; }", "/usr/bin/demo", "--flag value", true)]
    [InlineData("{ path=/usr/bin/one ; argv[]=/usr/bin/one ; }{ path=/usr/bin/two ; argv[]=/usr/bin/two ; }", null, null, false)]
    [InlineData("not a structured ExecStart", null, null, false)]
    public void ExecStart_IsParsedConservatively(string value, string? executable, string? arguments, bool complete)
    {
        var result = ExecStartParser.Parse(value);
        Assert.Equal(executable, result.Executable);
        Assert.Equal(arguments, result.Arguments);
        Assert.Equal(complete, result.Complete);
    }

    [Fact]
    public void StructuredProperties_PreserveDependencyKindsAndOrdering()
    {
        var properties = SystemctlProperties.Parse("Requires=z.service a.service\nWants=b.service\nRequiredBy=y.service\nWantedBy=x.service\n");
        var relations = new List<ServiceDependencyRelation>();
        foreach (var item in new[] { ("Requires", "requires"), ("Wants", "wants"), ("RequiredBy", "requiredBy"), ("WantedBy", "wantedBy") })
            LinuxServiceConfiguration.AddRelations(relations, properties, item.Item1, item.Item2);
        var bounded = ServiceDependencyResults.Bound(new(relations, relations.Count, false, true, "systemd/systemctl"), 100);
        Assert.Equal(["requiredBy", "requires", "requires", "wantedBy", "wants"], bounded.Relations.Select(r => r.Relation));
        Assert.Equal(["a.service", "z.service"], bounded.Relations.Where(r => r.Relation == "requires").Select(r => r.ServiceName));
    }

    [Theory]
    [InlineData(1, 1, false, true)]
    [InlineData(100, 100, false, true)]
    [InlineData(1000, 1000, false, true)]
    [InlineData(1000, 1, true, false)]
    public void DependencyBounds_AreDeterministic(int sourceCount, int limit, bool truncated, bool complete)
    {
        var relations = Enumerable.Range(0, sourceCount).Select(i => new ServiceDependencyRelation("requires", $"unit-{i:D4}.service", null)).ToArray();
        var result = ServiceDependencyResults.Bound(new(relations, sourceCount, false, true, "systemd/systemctl"), limit);
        Assert.Equal(limit, result.Count);
        Assert.Equal(truncated, result.Truncated);
        Assert.Equal(complete, result.Complete);
    }
}
