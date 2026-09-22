// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Sys.Core;
using bOps.Packages.Sys.Linux;

namespace bOps.Packages.System.Linux.Tests;

/// <summary>
/// The <c>/proc</c> parsers of V1.3-C against real kernel output captured verbatim (ADR-0034).
/// These are plain <c>[Fact]</c>s, not <c>[LinuxOnlyFact]</c>s: they parse text, they touch no
/// operating system, and running them everywhere is what lets a Windows development machine catch
/// a Linux parsing mistake before CI does. The tools that actually read <c>/proc</c> are covered
/// by <c>LinuxProcessDiagnosticsTests</c>, which does need a real Linux host.
/// </summary>
public sealed class LinuxProcParsingTests
{
    // Captured from a real /proc/<pid>/stat.
    private const string Stat =
        "1234 (dotnet) S 1200 1234 1200 0 -1 4194304 12345 0 67 0 150 40 0 0 20 0 24 0 987654 " +
        "2818572288 15000 18446744073709551615 1 1 0 0 0 0 0 0 0 0 0 0 17 3 0 0 0 0 0 0 0 0 0 0 0 0 0";

    [Fact]
    public void Stat_IsParsedFieldByField()
    {
        var stat = LinuxProcReader.ParseStat(1234, Stat);

        Assert.NotNull(stat);
        Assert.Equal(1234, stat!.Pid);
        Assert.Equal("dotnet", stat.Name);
        Assert.Equal(1200, stat.ParentPid);
        Assert.Equal(150 + 40, stat.CpuTicks);
        Assert.Equal(12345 + 67, stat.PageFaults);
        Assert.Equal(24, stat.ThreadCount);
        Assert.Equal(2_818_572_288, stat.VirtualBytes);
    }

    [Fact]
    public void Stat_SurvivesAProcessNameContainingSpacesAndParentheses()
    {
        // The kernel does not escape comm, so splitting the whole line on spaces silently misreads
        // every process whose name has one — which is why the parser reads after the *last* ')'.
        var stat = LinuxProcReader.ParseStat(9, Stat.Replace("(dotnet)", "(my (odd) name)", StringComparison.Ordinal));

        Assert.NotNull(stat);
        Assert.Equal("my (odd) name", stat!.Name);
        Assert.Equal(1200, stat.ParentPid);
        Assert.Equal(190, stat.CpuTicks);
    }

    [Fact]
    public void Stat_ReportsNullRatherThanGuessingOnAMalformedLine()
    {
        Assert.Null(LinuxProcReader.ParseStat(1, "not a stat line at all"));
    }

    [Fact]
    public void Status_IsParsedIntoBytesAndTheRealUid()
    {
        const string status = """
            Name:	dotnet
            Umask:	0022
            State:	S (sleeping)
            Tgid:	1234
            Pid:	1234
            PPid:	1200
            Uid:	1000	1000	1000	1000
            Gid:	1000	1000	1000	1000
            VmSize:	  2752512 kB
            VmRSS:	    64512 kB
            RssAnon:	    31232 kB
            Threads:	24
            """;

        var parsed = LinuxProcReader.ParseStatus(status);

        Assert.Equal(1000, parsed.Uid);
        Assert.Equal(2_752_512L * 1024, parsed.VirtualBytes);
        Assert.Equal(64_512L * 1024, parsed.ResidentBytes);
        Assert.Equal(31_232L * 1024, parsed.PrivateBytes);
        Assert.Equal(24, parsed.ThreadCount);
    }

    [Fact]
    public void Status_LeavesPrivateMemoryNullOnAKernelWithoutRssAnon()
    {
        var parsed = LinuxProcReader.ParseStatus("Uid:\t0\t0\t0\t0\nVmRSS:\t100 kB\n");

        Assert.Equal(0, parsed.Uid);
        Assert.Null(parsed.PrivateBytes);
        Assert.Equal(100L * 1024, parsed.ResidentBytes);
    }

    [Fact]
    public void Maps_CoalesceTheMappingsOfOneSharedObject()
    {
        const string maps = """
            55a4b2c00000-55a4b2c01000 r--p 00000000 08:01 1000    /usr/bin/dotnet
            55a4b2c01000-55a4b2c03000 r-xp 00001000 08:01 1000    /usr/bin/dotnet
            7f1122200000-7f1122201000 r--p 00000000 08:01 2000    /usr/lib/libc.so.6
            7f1122300000-7f1122301000 rw-p 00000000 00:00 0
            7ffd00000000-7ffd00021000 rw-p 00000000 00:00 0                          [stack]
            7ffd00100000-7ffd00101000 r-xp 00000000 00:00 0                          [vdso]
            """;

        var snapshot = LinuxProcessModulesTool.Parse(maps, 8_000, CancellationToken.None);

        Assert.True(snapshot.Exists);
        Assert.Equal(InventorySourceStatus.Available, snapshot.Status);
        Assert.Equal(2, snapshot.Modules.Count);

        var dotnet = snapshot.Modules.Single(module => module.Path == "/usr/bin/dotnet");
        Assert.Equal("dotnet", dotnet.Name);
        Assert.Equal("0x55a4b2c00000", dotnet.BaseAddress);
        Assert.Equal(0x1000 + 0x2000, dotnet.SizeBytes);
        // Linux records no version for a mapped image, and inventing one from a file name would be
        // a fact the kernel never stated.
        Assert.Null(dotnet.Version);

        Assert.DoesNotContain(snapshot.Modules, module => module.Path.Contains("stack", StringComparison.Ordinal));
    }

    [Fact]
    public void Maps_StopAtTheScanCeilingAndSaySo()
    {
        var many = string.Join('\n', Enumerable.Range(0, 50)
            .Select(index => $"{index:x8}000-{index:x8}100 r-xp 00000000 08:01 {index}    /lib/m{index}.so"));

        var snapshot = LinuxProcessModulesTool.Parse(many, 10, CancellationToken.None);

        Assert.True(snapshot.CollectionTruncated);
        Assert.Equal(10, snapshot.Modules.Count);
    }

    [Fact]
    public void Maps_ReportAnUnreadableRangeAsPartialRatherThanDroppingItSilently()
    {
        var snapshot = LinuxProcessModulesTool.Parse(
            "nonsense r-xp 00000000 08:01 1    /lib/broken.so\n", 8_000, CancellationToken.None);

        Assert.Equal(InventorySourceStatus.Partial, snapshot.Status);
        Assert.Empty(snapshot.Modules);
    }
}
