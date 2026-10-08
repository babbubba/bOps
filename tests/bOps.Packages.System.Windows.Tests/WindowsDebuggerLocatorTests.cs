// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using bOps.Packages.Sys.Windows;

namespace bOps.Packages.System.Windows.Tests;

/// <summary>Skips visibly when Debugging Tools for Windows (kd.exe) is not installed on this host.</summary>
internal sealed class RequiresKernelDebuggerFactAttribute : FactAttribute
{
    public RequiresKernelDebuggerFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Requires a real Windows host.";
        }
        else if (WindowsDebuggerLocator.Locate() is null)
        {
            Skip = "Debugging Tools for Windows (kd.exe) is not installed on this host — real-debugger detection was not exercised.";
        }
    }
}

/// <summary>ADR-0048 §4: deterministic, bounded kd.exe discovery; nothing is launched or installed.</summary>
public sealed class WindowsDebuggerLocatorTests : IDisposable
{
    private readonly string directory = Path.Combine(AppContext.BaseDirectory, "locator", Guid.NewGuid().ToString("N"));

    public WindowsDebuggerLocatorTests() => Directory.CreateDirectory(directory);

    public void Dispose()
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void Candidates_ArePathEntriesInOrder_ThenTheStandardKitsDirectoriesForThisArchitecture()
    {
        var candidates = WindowsDebuggerLocator.Candidates(
            @"C:\Tools;relative\dir;\\server\share;""C:\Quoted Dir"";;C:\Tools",
            @"C:\Program Files (x86)", @"C:\Program Files", Architecture.X64);

        Assert.Equal(
            [
                @"C:\Tools", @"C:\Quoted Dir",
                @"C:\Program Files (x86)\Windows Kits\10\Debuggers\x64", @"C:\Program Files\Windows Kits\10\Debuggers\x64",
            ],
            candidates);
    }

    [Fact]
    public void Candidates_UseTheArchitectureFolder_AndNeverSearchRecursively()
    {
        var arm = WindowsDebuggerLocator.Candidates(null, @"C:\PF86", @"C:\PF", Architecture.Arm64);
        var unknown = WindowsDebuggerLocator.Candidates(null, @"C:\PF86", @"C:\PF", Architecture.Wasm);

        Assert.Equal([@"C:\PF86\Windows Kits\10\Debuggers\arm64", @"C:\PF\Windows Kits\10\Debuggers\arm64"], arm);
        Assert.Empty(unknown);
    }

    [Fact]
    public void Candidates_AreBoundedInPathEntries()
    {
        var path = string.Join(';', Enumerable.Range(0, 500).Select(index => @"C:\p" + index));

        Assert.Equal(64, WindowsDebuggerLocator.Candidates(path, null, null, Architecture.X64).Count);
    }

    [Fact]
    public void Locate_IgnoresDirectoriesWithoutKd_AndAFileThatIsNotAMicrosoftImage()
    {
        File.WriteAllText(Path.Combine(directory, "kd.exe"), "not a debugger");

        Assert.Null(WindowsDebuggerLocator.Locate([Path.Combine(directory, "missing"), directory]));
    }

    [WindowsOnlyFact]
    public void Locate_AcceptsTheFirstMicrosoftKd_AndPairsDumpChkFromTheSameDirectoryOnly()
    {
        var first = Path.Combine(directory, "first");
        var second = Path.Combine(directory, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        var microsoftImage = Path.Combine(Environment.SystemDirectory, "where.exe");
        File.Copy(microsoftImage, Path.Combine(first, "kd.exe"));
        File.Copy(microsoftImage, Path.Combine(second, "kd.exe"));
        File.Copy(microsoftImage, Path.Combine(second, "dumpchk.exe"));

        var found = WindowsDebuggerLocator.Locate([first, second])!;

        Assert.Equal(Path.Combine(first, "kd.exe"), found.KdPath);
        Assert.Null(found.DumpChkPath);
        Assert.NotNull(found.Version);
        Assert.Equal(Path.Combine(second, "dumpchk.exe"), WindowsDebuggerLocator.Locate([second])!.DumpChkPath);
    }

    [WindowsOnlyFact]
    public async Task CapabilityCheck_AgreesWithTheLocator_OnThisHost()
    {
        var available = await WindowsDebuggerCapabilities.IsKernelDumpAnalysisAvailableAsync(CancellationToken.None);

        Assert.Equal(WindowsDebuggerLocator.Locate() is not null, available);
    }

    [RequiresKernelDebuggerFact]
    public void RealInstallation_IsAMicrosoftKdInAnExpectedLocation()
    {
        var installation = WindowsDebuggerLocator.Locate()!;

        Assert.EndsWith(@"\kd.exe", installation.KdPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(installation.KdPath));
        Assert.NotNull(installation.Version);
    }
}
