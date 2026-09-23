// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Architecture.Tests;

/// <summary>Focused K5 guard: crash evidence is metadata only, never a dump or command surface.</summary>
public sealed class WindowsCrashEvidenceSecurityTests
{
    private static readonly string[] Forbidden =
    [
        "MiniDumpReadDumpStream", "DbgHelp", "SearchOption.AllDirectories", "*.dmp",
        "PowerShell", "wevtutil", "Process.Start", "Directory.EnumerateFiles",
        "crash.delete", "crash.remove", "TerminateProcess",
    ];

    [Fact]
    public void K5ProductionSource_HasNoDumpParserRecursiveScanOrMutationSurface()
    {
        var root = FindRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!, "src", "packages", "bOps.Packages.System.Windows", "WindowsCrashEvidenceTool.cs");
        var source = File.ReadAllText(path);
        var hits = Forbidden.Where(term => source.Contains(term, StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.True(hits.Length == 0, "K5 crash evidence must remain metadata-only: " + string.Join(", ", hits));
    }

    private static string? FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "bOps.slnx"))) return directory.FullName;
        return null;
    }
}
