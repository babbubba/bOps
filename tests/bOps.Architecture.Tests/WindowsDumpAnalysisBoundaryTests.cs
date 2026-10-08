// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.Versioning;
using bOps.Abstractions;
using bOps.Hosting;
using bOps.Packages.Docker;
using bOps.Packages.Filesystem;
using bOps.Packages.Sys.Windows;
using bOps.Packages.Web;
using bOps.Runtime;

namespace bOps.Architecture.Tests;

/// <summary>Skips visibly — never silently — when the test host is not Windows.</summary>
internal sealed class WindowsHostFactAttribute : FactAttribute
{
    public WindowsHostFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Requires a real Windows host: the Windows system package is only composed there.";
        }
    }
}

/// <summary>ADR-0048: capability gating of <c>system.dump_analyze</c> and the source boundary of its implementation.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDumpAnalysisBoundaryTests
{
    private static readonly string[] ImplementationFiles =
    [
        "WindowsKernelDumpAnalyzeTool.cs", "WindowsDumpPathPolicy.cs", "WindowsDebuggerLocator.cs", "WindowsDebuggerCapabilities.cs",
        "DebuggerProcessRunner.cs", "KdCommandScript.cs", "KdAnalysisParser.cs",
    ];

    [WindowsHostFact]
    public async Task WithoutTheDebuggerCapability_TheToolIsAbsentFromThePlannerCatalog_AndCannotBeResolved()
    {
        var registry = new ToolRegistry(new FixedCapabilityProbe(available: false));
        registry.Register(new PackageId("bops.packages.system.windows"), new WindowsKernelDumpAnalyzeTool());

        await registry.RefreshCapabilitiesAsync();

        Assert.DoesNotContain(registry.GetAvailableManifests(), manifest => manifest.Name == "system.dump_analyze");
        Assert.Null(registry.Resolve("system.dump_analyze"));
    }

    [WindowsHostFact]
    public async Task WithTheDebuggerCapability_TheToolIsVisibleOnWindows()
    {
        var registry = new ToolRegistry(new FixedCapabilityProbe(available: true));
        registry.Register(new PackageId("bops.packages.system.windows"), new WindowsKernelDumpAnalyzeTool());

        await registry.RefreshCapabilitiesAsync();

        Assert.Contains(registry.GetAvailableManifests(), manifest => manifest.Name == "system.dump_analyze");
    }

    [Fact]
    public void CanonicalComposition_DeclaresTheDebuggerCapabilityOnTheToolOnly()
    {
        var filesystemProvider = new FilesystemToolProvider(new FilesystemPathPolicy([], []));
        using var webProvider = new WebToolProvider(new WebFetchOptions(), new WebSearchOptions());
        var registrations = FirstPartyToolComposition.Create(new FirstPartyToolCompositionOptions(
            filesystemProvider, webProvider, new DockerClientFactory(), new DockerBuildOptions(), new DockerVolumeOptions()));

        var requiring = registrations
            .Where(registration => registration.Tool.Manifest.Requires.Contains(WindowsDebuggerCapabilities.KernelDumpAnalysis))
            .Select(registration => registration.Tool.Manifest.Name)
            .ToArray();
        var optionallyUsingDumpChk = registrations
            .Where(registration => registration.Tool.Manifest.OptionalRequires.Contains(WindowsDebuggerCapabilities.DumpCheck))
            .Select(registration => registration.Tool.Manifest.Name)
            .ToArray();

        Assert.Equal(OperatingSystem.IsWindows() ? ["system.dump_analyze"] : [], requiring);
        Assert.Equal(OperatingSystem.IsWindows() ? ["system.dump_analyze"] : [], optionallyUsingDumpChk);
    }

    [Fact]
    public void UnifiedPrerequisiteComposition_OwnsDebuggerChecks_NotApiOrCli()
    {
        var root = FindRoot();
        var composition = File.ReadAllText(Path.Combine(root, "src", "core", "bOps.Hosting", "FirstPartyPrerequisiteComposition.cs"));
        Assert.Contains("WindowsDebuggerCapabilities.KernelDumpAnalysisDescriptor", composition, StringComparison.Ordinal);
        Assert.Contains("WindowsDebuggerCapabilities.DumpCheckDescriptor", composition, StringComparison.Ordinal);

        foreach (var host in new[] { Path.Combine("src", "core", "bOps.Api", "Program.cs"), Path.Combine("src", "core", "bOps.Cli", "Program.cs") })
        {
            var source = File.ReadAllText(Path.Combine(root, host));
            Assert.DoesNotContain("WindowsDebuggerCapabilities", source, StringComparison.Ordinal);
            Assert.Contains("FirstPartyPrerequisiteComposition.Register", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DebuggerPrerequisiteDescriptors_AreActionableAndTyped()
    {
        Assert.Equal(("windows.debugger.kd", PrerequisiteKind.Executable),
            (WindowsDebuggerCapabilities.KernelDumpAnalysisDescriptor.Id, WindowsDebuggerCapabilities.KernelDumpAnalysisDescriptor.Kind));
        Assert.Contains("Debugging Tools for Windows", WindowsDebuggerCapabilities.KernelDumpAnalysisDescriptor.DisplayName, StringComparison.Ordinal);
        Assert.Contains("kd.exe", WindowsDebuggerCapabilities.KernelDumpAnalysisDescriptor.Remediation, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(("windows.debugger.dumpchk", PrerequisiteKind.Executable),
            (WindowsDebuggerCapabilities.DumpCheckDescriptor.Id, WindowsDebuggerCapabilities.DumpCheckDescriptor.Kind));
        Assert.Contains("dumpchk.exe", WindowsDebuggerCapabilities.DumpCheckDescriptor.Remediation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImplementationSource_HasNoShellScriptingOrRawMemorySurface()
    {
        var root = FindRoot();
        var forbidden = new[]
        {
            "UseShellExecute = true", "cmd.exe", "powershell", "pwsh", "wscript", "cscript", "winget", "Process.Start(",
            ".Arguments =", "StartInfo.Arguments", ".shell", ".scriptload", ".load ", "-cf", "-cfr", "$$><", "-xmf",
            "\"db ", "\"dd ", "\"dq ", "\"dw ", "\"du ", "\"da ", "\"dc ", "\"s -", "DbgEng", "IDebugClient",
        };

        foreach (var file in ImplementationFiles)
        {
            var source = File.ReadAllText(Path.Combine(root, "src", "packages", "bOps.Packages.System.Windows", file));
            var hits = forbidden.Where(term => source.Contains(term, StringComparison.Ordinal)).ToArray();
            Assert.True(hits.Length == 0, $"{file} contains {string.Join(", ", hits)}");
        }
    }

    [Fact]
    public void ExistingCrashEvidenceTool_StillNeverOpensDumps()
    {
        var source = File.ReadAllText(Path.Combine(FindRoot(), "src", "packages", "bOps.Packages.System.Windows", "WindowsCrashEvidenceTool.cs"));

        Assert.DoesNotContain("WindowsKernelDumpAnalyzeTool", source, StringComparison.Ordinal);
        Assert.DoesNotContain("KdCommandScript", source, StringComparison.Ordinal);
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "bOps.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class FixedCapabilityProbe(bool available) : ICapabilityProbe
    {
        public Task<bool> IsAvailableAsync(string capability, CancellationToken ct = default) => Task.FromResult(available);
    }
}
