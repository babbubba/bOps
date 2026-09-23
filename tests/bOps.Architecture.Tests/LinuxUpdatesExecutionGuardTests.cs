// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Architecture.Tests;

public sealed class LinuxUpdatesExecutionGuardTests
{
    [Fact]
    public void LinuxUpdateCollectorUsesOnlyFixedReadOnlyCommandsWithoutShell()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "bOps.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var file = Path.Combine(root!.FullName, "src", "packages", "bOps.Packages.System.Linux", "LinuxUpdatesTool.cs");
        var source = File.ReadAllText(file);
        Assert.Contains("UseShellExecute = false", source, StringComparison.Ordinal);
        Assert.Contains("ArgumentList.Add", source, StringComparison.Ordinal);
        Assert.Contains("\"--just-print\", \"upgrade\"", source, StringComparison.Ordinal);
        Assert.Contains("\"check-update\", \"--cacheonly\"", source, StringComparison.Ordinal);
        Assert.Contains("\"--xmlout\", \"list-updates\"", source, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "sh -c", "bash -c", "apt update", "apt-get update", "dnf upgrade", "dnf update", "dnf install", "zypper refresh", "zypper update", "zypper install", "UseShellExecute = true" })
            Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("new ProcessStartInfo(arguments", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ArgumentList.Add(arguments", source, StringComparison.Ordinal);
    }
}
