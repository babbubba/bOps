// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Architecture.Tests;

/// <summary>
/// Rule S1 and the permanent exclusions in <c>agentic/00-project-spec.md</c>: there is no generic
/// execution tool, no <c>process.start</c>, and no unfiltered environment dump — "never, at any
/// version". Those are the two rules a process-diagnostics milestone is most likely to erode, one
/// convenient tool at a time, so they are checked mechanically rather than by review.
/// <para>
/// The check reads the source tree rather than compiled assemblies, because it has to cover every
/// package — including ones this test project does not and should not reference (rule A7). A tool
/// name only ever exists as a literal in a manifest, so a literal scan is exact enough to be
/// worth failing a build over.
/// </para>
/// </summary>
public sealed class NoGenericExecutionToolTests
{
    /// <summary>Tool names that may never exist, in any package, under any risk level or flag.</summary>
    private static readonly string[] ForbiddenToolNames =
    [
        "shell.run", "shell.exec", "shell.command",
        "system.exec", "system.run", "system.shell", "system.command",
        "powershell.invoke", "powershell.run", "cmd.run", "bash.run",
        "process.start", "process.exec", "process.run", "process.launch", "process.spawn",
        "process.env", "process.environ", "process.environment",
    ];

    /// <summary>
    /// Ways a process's whole environment block is read. <c>/proc/&lt;pid&gt;/environ</c> is the
    /// Linux one; <c>ReadProcessMemory</c> walking the PEB is the Windows one;
    /// <c>GetEnvironmentVariables()</c> (plural) dumps this host's own. Reading one named variable
    /// for configuration is a different thing and stays allowed.
    /// </summary>
    private static readonly string[] ForbiddenEnvironmentReads =
    [
        "/environ", "ReadProcessMemory", "GetEnvironmentVariables(",
    ];

    [Fact]
    public void NoSourceFile_NamesAGenericExecutionOrProcessStartTool()
    {
        var offending = Scan(ForbiddenToolNames, quoted: true).ToList();

        Assert.True(
            offending.Count == 0,
            "agentic/03-security-rules.md S1 — there is no generic execution tool and no process.start:\n" + string.Join('\n', offending));
    }

    [Fact]
    public void NoSourceFile_DumpsAProcessEnvironment()
    {
        var offending = Scan(ForbiddenEnvironmentReads, quoted: false).ToList();

        Assert.True(
            offending.Count == 0,
            "agentic/00-project-spec.md — an unfiltered environment-variable dump is permanently out of scope:\n" + string.Join('\n', offending));
    }

    [Fact]
    public void TheScan_ActuallyReachesTheSourceTree()
    {
        var files = SourceFiles().ToList();

        Assert.True(files.Count > 200, $"Only {files.Count} source files were scanned; the repository root was not found.");
        Assert.Contains(files, file => file.EndsWith("SystemToolManifests.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void TheScan_WouldSeeAForbiddenName()
    {
        // The scan is only worth anything if it matches the way a tool name is actually written.
        const string manifestLine = """    Name = "process.start",""";

        Assert.Contains(ForbiddenToolNames, name => manifestLine.Contains($"\"{name}\"", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> Scan(string[] terms, bool quoted)
    {
        foreach (var file in SourceFiles())
        {
            var text = File.ReadAllText(file);
            foreach (var term in terms)
            {
                var needle = quoted ? $"\"{term}\"" : term;
                if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    yield return $"  {term} in {file}";
                }
            }
        }
    }

    private static IEnumerable<string> SourceFiles()
    {
        var root = RepositoryRoot();
        if (root is null)
        {
            return [];
        }

        return Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
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
