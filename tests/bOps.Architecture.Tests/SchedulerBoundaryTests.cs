// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Architecture.Tests;

public sealed class SchedulerBoundaryTests
{
    private static readonly string[] AllowedLinuxExecutables = ["systemctl", "journalctl"];

    [Fact]
    public void SchedulerCore_DoesNotInvokeProcessesOrOsBoundaries()
    {
        var text = ReadSource("src/packages/bOps.Packages.Scheduler.Core");
        Assert.DoesNotContain("Process.Start", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ProcessStartInfo", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("systemctl", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("journalctl", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WindowsScheduler_DoesNotUseForbiddenExecutionSurfaces()
    {
        var text = ReadSource("src/packages/bOps.Packages.Scheduler.Windows");
        foreach (var forbidden in new[] { "schtasks.exe", "powershell", "System.Management", "WMI", "Process.Start", "ProcessStartInfo" })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void LinuxScheduler_UsesOnlyFixedSystemBoundariesAndNeverCronMutation()
    {
        var text = ReadSource("src/packages/bOps.Packages.Scheduler.Linux");
        Assert.DoesNotContain("schtasks", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("powershell", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bash", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sh -c", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Process.Start(\"", text, StringComparison.OrdinalIgnoreCase);
        var invocations = System.Text.RegularExpressions.Regex.Matches(text, "RunAsync\\(\\\"([^\\\"]+)\\\"");
        Assert.All(invocations.Cast<System.Text.RegularExpressions.Match>(), match =>
            Assert.Contains(match.Groups[1].Value, AllowedLinuxExecutables));
        Assert.Contains("systemctl", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("journalctl", text, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadSource(string relativePath)
    {
        var root = RepositoryRoot();
        Assert.NotNull(root);
        return string.Join('\n', Directory.EnumerateFiles(Path.Combine(root!, relativePath), "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText));
    }

    private static string? RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "bOps.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName;
    }
}
