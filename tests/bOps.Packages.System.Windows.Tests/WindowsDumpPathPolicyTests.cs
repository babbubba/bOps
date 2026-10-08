// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Sys.Windows;

namespace bOps.Packages.System.Windows.Tests;

/// <summary>A fake <c>%SystemRoot%</c> under the test output directory (whose path uses only the allowed characters).</summary>
internal sealed class FakeSystemRoot : IDisposable
{
    internal FakeSystemRoot()
    {
        Root = Path.Combine(AppContext.BaseDirectory, "dump-roots", Guid.NewGuid().ToString("N"), "Windows");
        Directory.CreateDirectory(Path.Combine(Root, "Minidump"));
        Directory.CreateDirectory(Path.Combine(Root, "LiveKernelReports", "WATCHDOG"));
        Outside = Path.Combine(Path.GetDirectoryName(Root)!, "Outside");
        Directory.CreateDirectory(Outside);
        Policy = new WindowsDumpPathPolicy(Root);
    }

    internal string Root { get; }

    internal string Outside { get; }

    internal WindowsDumpPathPolicy Policy { get; }

    internal string Create(params string[] relative)
    {
        var path = Path.Combine([Root, .. relative]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0x50, 0x41, 0x47, 0x45]);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(Root)!, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Skips visibly when this identity cannot create directory symbolic links (Developer Mode or the privilege is off).</summary>
internal sealed class SymbolicLinkFactAttribute : FactAttribute
{
    public SymbolicLinkFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Requires a real Windows host.";
            return;
        }

        var probe = Path.Combine(Path.GetTempPath(), "bops-link-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateSymbolicLink(probe, Path.GetTempPath());
            Directory.Delete(probe);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Skip = "This identity cannot create symbolic links (enable Developer Mode or run elevated) — the reparse-point escape test did not run.";
        }
    }
}

/// <summary>ADR-0048 §5: default-deny path authorization for kernel dumps.</summary>
public sealed class WindowsDumpPathPolicyTests : IDisposable
{
    private readonly FakeSystemRoot roots = new();

    public void Dispose() => roots.Dispose();

    [Fact]
    public void ApprovedLocations_AreAuthorizedWithTheirKind()
    {
        var minidump = roots.Create("Minidump", "100626-20984-01.dmp");
        var nested = roots.Create("LiveKernelReports", "WATCHDOG", "WD-20261006-1403.dmp");
        var memory = roots.Create("MEMORY.DMP");
        var mdmp = roots.Create("Minidump", "small.mdmp");

        Assert.Equal((DumpPathVerdict.Authorized, WindowsDumpPathPolicy.KindSmall), Verdict(minidump));
        Assert.Equal((DumpPathVerdict.Authorized, WindowsDumpPathPolicy.KindLiveKernelReport), Verdict(nested));
        Assert.Equal((DumpPathVerdict.Authorized, WindowsDumpPathPolicy.KindFull), Verdict(memory));
        Assert.Equal((DumpPathVerdict.Authorized, WindowsDumpPathPolicy.KindSmall), Verdict(mdmp));
        Assert.Equal((DumpPathVerdict.Authorized, WindowsDumpPathPolicy.KindSmall), Verdict(minidump.ToUpperInvariant()));
    }

    [Theory]
    [InlineData(@"Minidump\a.dmp", "not-absolute")]
    [InlineData(@"\Windows\Minidump\a.dmp", "not-absolute")]
    [InlineData(@"C:Windows\Minidump\a.dmp", "not-absolute")]
    [InlineData(@"\\server\share\Minidump\a.dmp", "unc-or-device-path")]
    [InlineData(@"\\?\C:\Windows\Minidump\a.dmp", "unc-or-device-path")]
    [InlineData(@"\\.\C:\Windows\Minidump\a.dmp", "unc-or-device-path")]
    [InlineData(@"\??\C:\Windows\Minidump\a.dmp", "unc-or-device-path")]
    [InlineData(@"//server/share/a.dmp", "unc-or-device-path")]
    [InlineData(@"C:/Windows/Minidump/a.dmp", "forward-slash")]
    [InlineData(@"C:\Windows\Minidump\a.dmp:hidden", "alternate-data-stream")]
    [InlineData(@"C:\Windows\Minidump\a.dmp::$DATA", "alternate-data-stream")]
    [InlineData("C:\\Windows\\Minidump\\a\u0000.dmp", "control-characters")]
    [InlineData("C:\\Windows\\Minidump\\a\n.dmp", "control-characters")]
    [InlineData(@"C:\Windows\Minidump\a"".dmp", "characters")]
    [InlineData(@"C:\Windows\Minidump\a.dmp;.echo pwned", "characters")]
    [InlineData(@"C:\Windows\Minidump\a.dmp"" -c ""!analyze", "characters")]
    [InlineData(@"C:\Windows\Minidump\%TEMP%.dmp", "characters")]
    [InlineData(@"C:\Windows\Minidump\a&calc.dmp", "characters")]
    [InlineData(@"C:\Windows\Minidump\a|b.dmp", "characters")]
    [InlineData(@"C:\Windows\Minidump\$(x).dmp", "characters")]
    [InlineData(@"C:\Windows\Minidump\FCAVAL~1.dmp", "characters")]
    public void LexicallyInvalidPaths_AreRejectedBeforeAnyFilesystemAccess(string path, string reason)
    {
        var decision = roots.Policy.Decide(path);

        Assert.Equal(DumpPathVerdict.Rejected, decision.Verdict);
        Assert.Equal(reason, decision.Reason);
        Assert.Null(decision.Path);
    }

    [Fact]
    public void NonDumpExtensions_AreRejected()
    {
        foreach (var name in new[] { "a.txt", "a.dmp.txt", "a.dll", "a.evtx", "a", "a.dmpx" })
        {
            Assert.Equal("extension", roots.Policy.Decide(Path.Combine(roots.Root, "Minidump", name)).Reason);
        }
    }

    [Fact]
    public void PathsOutsideTheApprovedRoots_AreRejected_IncludingSiblingPrefixesAndTheRootsThemselves()
    {
        var outside = Path.Combine(roots.Outside, "a.dmp");
        File.WriteAllBytes(outside, [0]);

        foreach (var path in new[]
        {
            outside,
            Path.Combine(roots.Root, "a.dmp"),
            Path.Combine(roots.Root, "MinidumpX", "a.dmp"),
            Path.Combine(roots.Root, "Minidump.dmp"),
            Path.Combine(roots.Root, "LiveKernelReports.dmp"),
            Path.Combine(roots.Root, "System32", "MEMORY.DMP"),
            Path.Combine(roots.Root, "MEMORY2.DMP"),
        })
        {
            var decision = roots.Policy.Decide(path);
            Assert.Equal((DumpPathVerdict.Rejected, "outside-approved-roots"), (decision.Verdict, decision.Reason));
        }
    }

    [Fact]
    public void DotDotSegments_AreCanonicalizedBeforeTheRootCheck_AndCannotEscape()
    {
        var outside = Path.Combine(roots.Outside, "a.dmp");
        File.WriteAllBytes(outside, [0]);
        var escaping = Path.Combine(roots.Root, "Minidump", "..", "..", "Outside", "a.dmp");
        var staying = Path.Combine(roots.Root, "Minidump", "sub", "..", "x.dmp");
        roots.Create("Minidump", "x.dmp");

        Assert.Equal("outside-approved-roots", roots.Policy.Decide(escaping).Reason);
        var decision = roots.Policy.Decide(staying);
        Assert.Equal(DumpPathVerdict.Authorized, decision.Verdict);
        Assert.Equal(Path.Combine(roots.Root, "Minidump", "x.dmp"), decision.Path);
    }

    [Fact]
    public void MissingFile_IsNotFound_WithTheCanonicalPath()
    {
        var decision = roots.Policy.Decide(Path.Combine(roots.Root, "Minidump", "absent.dmp"));

        Assert.Equal((DumpPathVerdict.NotFound, "file-not-found"), (decision.Verdict, decision.Reason));
        Assert.Equal(WindowsDumpPathPolicy.KindSmall, decision.Kind);
    }

    [Fact]
    public void ADirectoryNamedLikeADump_IsNotTheDump()
    {
        Directory.CreateDirectory(Path.Combine(roots.Root, "Minidump", "dir.dmp"));

        Assert.Equal(DumpPathVerdict.NotFound, roots.Policy.Decide(Path.Combine(roots.Root, "Minidump", "dir.dmp")).Verdict);
    }

    [WindowsOnlyFact]
    public void AJunctionInsideAnApprovedRoot_CannotRedirectOutsideIt()
    {
        File.WriteAllBytes(Path.Combine(roots.Outside, "secret.dmp"), [0]);
        var junction = Path.Combine(roots.Root, "Minidump", "escape");
        TestJunction.Create(junction, roots.Outside);
        Assert.True(File.Exists(Path.Combine(junction, "secret.dmp")), "the junction must really resolve outside the root");

        var decision = roots.Policy.Decide(Path.Combine(junction, "secret.dmp"));

        Assert.Equal((DumpPathVerdict.Rejected, "reparse-point"), (decision.Verdict, decision.Reason));
        Assert.Null(decision.Path);
    }

    [WindowsOnlyFact]
    public void AnApprovedRootThatIsItselfAJunction_IsRejected()
    {
        var root = Path.Combine(roots.Root, "LiveKernelReports");
        Directory.Delete(root, recursive: true);
        TestJunction.Create(root, roots.Outside);
        File.WriteAllBytes(Path.Combine(roots.Outside, "x.dmp"), [0]);

        var decision = roots.Policy.Decide(Path.Combine(root, "x.dmp"));

        Assert.Equal((DumpPathVerdict.Rejected, "approved-root-is-a-link"), (decision.Verdict, decision.Reason));
    }

    [SymbolicLinkFact]
    public void ALinkInsideAnApprovedRoot_CannotRedirectOutsideIt()
    {
        File.WriteAllBytes(Path.Combine(roots.Outside, "secret.dmp"), [0]);
        Directory.CreateSymbolicLink(Path.Combine(roots.Root, "Minidump", "escape"), roots.Outside);
        File.CreateSymbolicLink(Path.Combine(roots.Root, "Minidump", "file-link.dmp"), Path.Combine(roots.Outside, "secret.dmp"));

        var throughDirectory = roots.Policy.Decide(Path.Combine(roots.Root, "Minidump", "escape", "secret.dmp"));
        var throughFile = roots.Policy.Decide(Path.Combine(roots.Root, "Minidump", "file-link.dmp"));

        Assert.Equal((DumpPathVerdict.Rejected, "reparse-point"), (throughDirectory.Verdict, throughDirectory.Reason));
        Assert.Equal((DumpPathVerdict.Rejected, "reparse-point"), (throughFile.Verdict, throughFile.Reason));
    }

    [SymbolicLinkFact]
    public void AnApprovedRootThatIsItselfALink_IsRejected()
    {
        var root = Path.Combine(roots.Root, "LiveKernelReports");
        Directory.Delete(root, recursive: true);
        Directory.CreateSymbolicLink(root, roots.Outside);
        File.WriteAllBytes(Path.Combine(roots.Outside, "x.dmp"), [0]);

        var decision = roots.Policy.Decide(Path.Combine(root, "x.dmp"));

        Assert.Equal((DumpPathVerdict.Rejected, "approved-root-is-a-link"), (decision.Verdict, decision.Reason));
    }

    [WindowsOnlyFact]
    public void ThisMachinesPolicy_UsesTheRealSystemRoot()
    {
        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var decision = WindowsDumpPathPolicy.ForThisMachine().Decide(Path.Combine(systemRoot, "System32", "kernel32.dll"));

        Assert.Equal(DumpPathVerdict.Rejected, decision.Verdict);
    }

    private (DumpPathVerdict, string?) Verdict(string path)
    {
        var decision = roots.Policy.Decide(path);
        return (decision.Verdict, decision.Kind);
    }
}
