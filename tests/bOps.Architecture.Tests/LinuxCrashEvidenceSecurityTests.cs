// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Architecture.Tests;

public sealed class LinuxCrashEvidenceSecurityTests
{
    [Fact]
    public void K6ProductionSourceRemainsMetadataOnlyAndUsesFixedDirectCommand()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "bOps.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var directory = Path.Combine(root!.FullName, "src", "packages", "bOps.Packages.System.Linux");
        var source = File.ReadAllText(Path.Combine(directory, "LinuxCrashEvidenceTool.cs"));
        var runner = File.ReadAllText(Path.Combine(directory, "LinuxUpdatesProcessRunner.cs"));
        Assert.Contains("--json=short", source, StringComparison.Ordinal);
        Assert.Contains("--since", source, StringComparison.Ordinal);
        Assert.Contains("ArgumentList", runner, StringComparison.Ordinal);
        Assert.Contains("UseShellExecute = false", runner, StringComparison.Ordinal);
        Assert.Contains("Kill(entireProcessTree: true)", runner, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "coredumpctl dump", "coredumpctl debug", "coredumpctl gdb", "--output", "sh -c", "bash -c", "Directory.EnumerateFiles", "EnumerateFiles(\"/\"", "ProcessStartInfo(arguments", "ArgumentList.Add(arguments", "File.Delete", "File.ReadAllBytes" })
            Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase);
    }
}
