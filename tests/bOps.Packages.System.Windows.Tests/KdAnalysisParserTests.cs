// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Sys.Windows;

namespace bOps.Packages.System.Windows.Tests;

/// <summary>Loads the synthetic kd output fixtures (see Fixtures/KernelDump/README.md for provenance).</summary>
internal static class KernelDumpFixtures
{
    internal const string Nonce = "0123456789ABCDEF";

    internal static string Load(string name, string nonce = Nonce) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "KernelDump", name + ".txt")).Replace("{NONCE}", nonce, StringComparison.Ordinal);
}

/// <summary>ADR-0048 §6-§7: the text-first parser reads only marker-delimited sections and only allowlisted fields.</summary>
public sealed class KdAnalysisParserTests
{
    [Fact]
    public void NormalAnalysis_IsParsedIntoBugcheckAttributionStackModulesSymbolsAndBlackboxes()
    {
        var result = KdAnalysisParser.Parse(KernelDumpFixtures.Load("normal-complete"), KernelDumpFixtures.Nonce);

        Assert.True(result.AllSectionsComplete);
        Assert.Equal("0x9f", result.Bugcheck!.Code);
        Assert.Equal("DRIVER_POWER_STATE_FAILURE", result.Bugcheck.Name);
        Assert.Equal(["0x3", "0xffffa00111111111", "0xffffb80122222222", "0xffffa00133333333"], result.Bugcheck.Parameters);
        Assert.Equal("USBXHCI", result.Attribution!.ModuleName);
        Assert.Equal("USBXHCI.SYS", result.Attribution.ImageName);
        Assert.Equal("USBXHCI!Controller_WaitForPowerIrp+4a", result.Attribution.SymbolName);
        Assert.Equal("0x9F_3_POWER_DOWN_USBXHCI!Controller_WaitForPowerIrp", result.Attribution.FailureBucketId);
        Assert.Equal("{11111111-2222-3333-4444-555555555555}", result.Attribution.FailureIdHash);
        Assert.Equal("System", result.Attribution.ProcessName);
        Assert.Equal("loaded", result.SymbolsStatus);
        Assert.Equal(4, result.Stack.Count);
        Assert.Equal(("nt", "KeBugCheckEx", (string?)null, "0xfffff8074a3c8e2e"), (result.Stack[0].Module, result.Stack[0].Symbol, result.Stack[0].Offset, result.Stack[0].Address));
        Assert.Equal(("USBXHCI", "Controller_WaitForPowerIrp", "0x4a"), (result.Stack[2].Module, result.Stack[2].Symbol, result.Stack[2].Offset));
        Assert.Equal(["USBXHCI", "nt"], result.Modules.Select(module => module.Name));
        Assert.True(result.Modules[0].Implicated);
        Assert.Equal("pdb symbols", result.Modules[0].SymbolState);
        Assert.True(result.Pnp.Available);
        Assert.Equal([@"PCI\VEN_0000&DEV_0000&SUBSYS_00000000&REV_00\SYNTHETIC0001"], result.Pnp.DeviceIds);
        Assert.True(result.Bsd.Available);
        Assert.Empty(result.Warnings);
        Assert.Null(result.RawAnalysisExcerpt);
    }

    [Fact]
    public void Bugcheck0x1E_WithAnUnsymbolizedThirdPartyDriver_IsPartialSymbolsAndAbsentBlackboxes()
    {
        var result = KdAnalysisParser.Parse(KernelDumpFixtures.Load("bugcheck-1e-third-party"), KernelDumpFixtures.Nonce);

        Assert.Equal("0x1e", result.Bugcheck!.Code);
        Assert.Equal("KMODE_EXCEPTION_NOT_HANDLED", result.Bugcheck.Name);
        Assert.Equal("0xffffffffc0000005", result.Bugcheck.Parameters[0]);
        Assert.Equal("c0000005", result.Attribution!.ExceptionCode);
        Assert.Equal("synthdrv", result.Attribution.ModuleName);
        Assert.Equal(("synthdrv", (string?)null, "0x1042"), (result.Stack[2].Module, result.Stack[2].Symbol, result.Stack[2].Offset));
        Assert.Equal("partial", result.SymbolsStatus);
        Assert.Contains(KdAnalysisParser.WarningSymbolsPartial, result.Warnings);
        Assert.Equal("no symbols", result.Modules.Single(module => module.Name == "synthdrv").SymbolState);
        Assert.False(result.Pnp.Available);
        Assert.Empty(result.Pnp.DeviceIds);
        Assert.False(result.Bsd.Available);
        Assert.Contains(KdAnalysisParser.WarningPnpUnavailable, result.Warnings);
        Assert.Contains(KdAnalysisParser.WarningBsdUnavailable, result.Warnings);
    }

    [Fact]
    public void KernelWithoutPdbSymbols_IsSymbolsUnavailable_NotACorruptDump()
    {
        var result = KdAnalysisParser.Parse(KernelDumpFixtures.Load("symbols-unavailable"), KernelDumpFixtures.Nonce);

        Assert.Equal("unavailable", result.SymbolsStatus);
        Assert.Contains(KdAnalysisParser.WarningSymbolsUnavailable, result.Warnings);
        Assert.Equal("0xa", result.Bugcheck!.Code);
        Assert.True(result.HasEvidence);
        Assert.Equal(("nt", (string?)null, "0x3f6e58"), (result.Stack[0].Module, result.Stack[0].Symbol, result.Stack[0].Offset));
    }

    [Fact]
    public void UnknownModule_IsReportedAsUnresolved_AndNotListedAsImplicated()
    {
        var result = KdAnalysisParser.Parse(KernelDumpFixtures.Load("module-unresolved"), KernelDumpFixtures.Nonce);

        Assert.Contains(KdAnalysisParser.WarningModuleUnresolved, result.Warnings);
        Assert.DoesNotContain(result.Modules, module => module.Implicated);
        Assert.Null(result.Stack[1].Module);
        Assert.Equal("0x50", result.Bugcheck!.Code);
    }

    [Fact]
    public void MissingEndMarker_LeavesTheSectionIncomplete_AndAForgedOrForeignMarkerNeverClosesIt()
    {
        var result = KdAnalysisParser.Parse(KernelDumpFixtures.Load("malformed-incomplete"), KernelDumpFixtures.Nonce);

        Assert.False(result.AllSectionsComplete);
        Assert.False(result.Sections["ANALYZE"].Complete);
        Assert.Contains("section-incomplete-analyze", result.Warnings);
        Assert.Contains("section-missing-modules", result.Warnings);
        Assert.Contains("section-missing-pnp", result.Warnings);
        Assert.Equal("error", result.SymbolsStatus);
        Assert.Equal("0x1e", result.Bugcheck!.Code);
    }

    [Fact]
    public void EchoedCommandLines_AndMarkersOfAnotherNonce_DoNotStartSections()
    {
        var output = KernelDumpFixtures.Load("normal-complete", "FFFFFFFFFFFFFFFF");

        var result = KdAnalysisParser.Parse(output, KernelDumpFixtures.Nonce);

        Assert.Empty(result.Sections);
        Assert.False(result.HasEvidence);
    }

    [Fact]
    public void NoBugcheckInStructuredText_FallsBackToABoundedExcerptOfTheFixedAnalysisOnly()
    {
        var nonce = KernelDumpFixtures.Nonce;
        var output = string.Join('\n',
            "garbage before the sections that must never be excerpted",
            KdCommandScriptTestsSupport.Begin(nonce, "ANALYZE"),
            "The debugger printed something unstructured here.",
            new string('x', 10_000),
            KdCommandScriptTestsSupport.End(nonce, "ANALYZE"));

        var result = KdAnalysisParser.Parse(output, nonce);

        Assert.Null(result.Bugcheck);
        Assert.Contains(KdAnalysisParser.WarningNoBugcheck, result.Warnings);
        Assert.NotNull(result.RawAnalysisExcerpt);
        Assert.StartsWith("The debugger printed something unstructured here.", result.RawAnalysisExcerpt, StringComparison.Ordinal);
        Assert.DoesNotContain("garbage", result.RawAnalysisExcerpt, StringComparison.Ordinal);
        Assert.True(result.RawAnalysisExcerpt!.Length <= Sys.Core.DumpAnalysisLimits.RawExcerptCharacters);
    }

    [Theory]
    [InlineData("ffffffff`c0000005", "0xffffffffc0000005")]
    [InlineData("0000001E", "0x1e")]
    [InlineData("0", "0x0")]
    [InlineData("0x00ABC", "0xabc")]
    [InlineData("not-hex", null)]
    [InlineData("11112222333344445", null)]
    public void Hex_NormalizesDebuggerValues(string input, string? expected) => Assert.Equal(expected, KdAnalysisParser.Hex(input));
}

internal static class KdCommandScriptTestsSupport
{
    internal static string Begin(string nonce, string section) => KdCommandScript.BeginMarker(nonce, section);

    internal static string End(string nonce, string section) => KdCommandScript.EndMarker(nonce, section);
}
