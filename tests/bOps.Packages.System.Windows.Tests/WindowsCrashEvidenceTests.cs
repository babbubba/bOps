// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Sys.Core;
using bOps.Packages.Sys.Windows;

namespace bOps.Packages.System.Windows.Tests;

/// <summary>
/// HARDEN-7 Windows crash evidence: WER 1001 and Application Error 1000 normalization on golden records (C-16), exact-identity
/// correlation (ADR-0041 §7, review note R10), honest time (§6), the bounded Report.wer key scan (H-7), dump references (R11), the
/// call budget (R7) and the schema-2 envelope of the real tool (R12).
/// </summary>
public sealed class WindowsCrashEvidenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private const string ProbeReport = "d25f62a3-ce7f-4702-8453-9bd1d0e36482";
    private const string ProbeArchive = "279c417a-ebf9-4cdb-97d1-3f8c5ec3001d";

    // ---- WER 1001 golden normalization ----

    [Fact]
    public void Wer1001_BlueScreenP1_50_IsAKernelBugcheck_WithCanonicalCode0x50_AndTheMinidumpReference()
    {
        var native = WindowsCrashEvidenceTool.FromWer(Item(WerGoldenFixtures.Wer1001BlueScreen50));

        Assert.Equal(CrashKinds.KernelBugcheck, native.Kind);
        Assert.Equal("0x50", native.BugcheckCode);
        Assert.Null(native.LiveDumpCode);
        Assert.Null(native.ExceptionCode);
        Assert.Equal("BlueScreen", native.EventName);
        Assert.Null(native.Process);
        Assert.Equal(@"C:\WINDOWS\Minidump\090426-53171-01.dmp", native.DumpPath);
        Assert.Equal("07e3c759-e445-4da7-8efa-2694d7dea04c", native.PrimaryReportId);
        Assert.Equal(EvidenceTimestampKind.Reported, native.TimestampKind);
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 8, 43, 34, TimeSpan.Zero).AddTicks(3323853), native.TimeUtc);

        var crash = Assert.Single(WindowsCrashCorrelator.Merge([native]));
        Assert.Equal("0x50", crash.Code);
        Assert.Equal("BlueScreen; 0x50", crash.Summary);
    }

    [Theory]
    [InlineData("193", "0x193")]
    [InlineData("1a1", "0x1a1")]
    [InlineData("117", "0x117")]
    [InlineData("141", "0x141")]
    public void Wer1001_LiveKernelEvent_IsAKernelLiveDump_WithItsCanonicalCode(string p1, string code)
    {
        var xml = p1 == "1a1" ? WerGoldenFixtures.Wer1001LiveKernel1a1 : WerGoldenFixtures.Wer1001LiveKernel(p1, Guid.NewGuid().ToString(), Now.AddHours(-1));
        var native = WindowsCrashEvidenceTool.FromWer(Item(xml));

        Assert.Equal(CrashKinds.KernelLiveDump, native.Kind);
        Assert.Equal(code, native.LiveDumpCode);
        Assert.Null(native.BugcheckCode);
        Assert.Equal(code, WindowsCrashCorrelator.Merge([native]).Single().Code);
        Assert.EndsWith(".dmp", native.DumpPath, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\\?\", native.DumpPath, StringComparison.Ordinal);
    }

    [Fact]
    public void Wer1001_AppCrash_ReadsApplicationP1_ModuleP4_ExceptionP7_Bucket_AndTheDumpReference()
    {
        var native = WindowsCrashEvidenceTool.FromWer(Item(WerGoldenFixtures.Wer1001AppCrash));

        Assert.Equal(CrashKinds.ApplicationCrash, native.Kind);
        Assert.Equal("testhost.exe", native.Process);
        Assert.Equal("coreclr.dll", native.FaultModule);
        Assert.Equal("0xc000001d", native.ExceptionCode);
        Assert.Equal("2242631497190538527", native.Bucket);
        Assert.Equal(@"C:\ProgramData\Microsoft\Windows\WER\Temp\WER.978b2804-c6bb-468f-a801-6eb84f5f52d4.tmp.dmp", native.DumpPath);
        Assert.Equal("e28f09dd-6af6-4e72-b597-eb72d502a361", native.PrimaryReportId);
        Assert.Equal("APPCRASH; 0xc000001d; coreclr.dll", WindowsCrashCorrelator.Merge([native]).Single().Summary);
    }

    [Fact]
    public void Wer1001_AppHangB1_IsAnApplicationHang_WithoutAModule()
    {
        var native = WindowsCrashEvidenceTool.FromWer(Item(WerGoldenFixtures.Wer1001AppHangB1));

        Assert.Equal(CrashKinds.ApplicationHang, native.Kind);
        Assert.Equal("explorer.exe", native.Process);
        Assert.Null(native.FaultModule);
        Assert.Null(native.ExceptionCode);
        Assert.Null(native.DumpPath); // only WERInternalMetadata.xml is attached
    }

    [Fact]
    public void Wer1001_Clr20r3_IsAnApplicationCrash_WhoseModuleIsTheAssembly_AndWhoseCodeIsUnknown()
    {
        var native = WindowsCrashEvidenceTool.FromWer(Item(WerGoldenFixtures.Wer1001Clr20r3));

        Assert.Equal(CrashKinds.ApplicationCrash, native.Kind);
        Assert.Equal("CLR20r3", native.EventName);
        Assert.Equal("probe.exe", native.Process);
        Assert.Equal("System.Management", native.FaultModule);
        Assert.Null(native.ExceptionCode);
    }

    [Fact]
    public void Wer1001_AnUnmappedEventName_StaysWer_WithTheActualNameKept()
    {
        var native = WindowsCrashEvidenceTool.FromWer(Item(WerGoldenFixtures.Wer1001Bex64));

        Assert.Equal(CrashKinds.Wer, native.Kind);
        Assert.Equal("BEX64", native.EventName);
        Assert.Null(native.Process);
        Assert.Null(native.ExceptionCode);
    }

    [Fact]
    public void Wer1001_UnnamedLayouts_FallBackToTheDocumentedPositions_AndGiveTheSameMeaning()
    {
        var named = WindowsCrashEvidenceTool.FromWer(Item(WerGoldenFixtures.Wer1001BlueScreen50));
        var unnamedItem = Item(WerGoldenFixtures.Wer1001BlueScreen50Unnamed);
        var unnamed = WindowsCrashEvidenceTool.FromWer(unnamedItem);

        Assert.True(unnamedItem.IsUnnamed);
        Assert.Equal((named.Kind, named.BugcheckCode, named.DumpPath, named.PrimaryReportId), (unnamed.Kind, unnamed.BugcheckCode, unnamed.DumpPath, unnamed.PrimaryReportId));

        var crash = WindowsCrashEvidenceTool.FromWer(Item(WerGoldenFixtures.Wer1001AppCrashUnnamed));
        Assert.Equal((CrashKinds.ApplicationCrash, "testhost.exe", "coreclr.dll", "0xc000001d", "e28f09dd-6af6-4e72-b597-eb72d502a361"),
            (crash.Kind, crash.Process, crash.FaultModule, crash.ExceptionCode, crash.PrimaryReportId));
    }

    [Fact]
    public void Wer1001_ANonexistentEventTypeField_IsNeverRead()
    {
        var signature = WindowsWerNormalizer.FromWer1001(new Dictionary<string, string> { ["EventType"] = "APPHANG", ["EventName"] = "BlueScreen", ["P1"] = "7f" }, unnamed: false);
        Assert.Equal(CrashKinds.KernelBugcheck, signature.Kind);
        Assert.Equal("0x7f", signature.BugcheckCode);

        var withoutName = WindowsWerNormalizer.FromWer1001(new Dictionary<string, string> { ["EventType"] = "APPHANG", ["P1"] = "app.exe" }, unnamed: false);
        Assert.Equal(CrashKinds.Wer, withoutName.Kind);
        Assert.Null(withoutName.EventName);
        Assert.Null(withoutName.Application);
    }

    // ---- Application Error 1000 ----

    [Fact]
    public void ApplicationError1000_Named_ReadsApplicationModuleExceptionHexProcessIdAndIntegratorReportId()
    {
        var native = WindowsCrashEvidenceTool.FromApplicationError(Item(WerGoldenFixtures.ApplicationError1000Named));

        Assert.Equal(CrashKinds.ApplicationCrash, native.Kind);
        Assert.Equal("probe.exe", native.Process);
        Assert.Equal("KERNELBASE.dll", native.FaultModule);
        Assert.Equal("0xe0434352", native.ExceptionCode);
        Assert.Equal(3844, native.Pid);
        Assert.Equal([ProbeReport], native.Identities);
        Assert.Equal(EvidenceTimestampKind.Occurred, native.TimestampKind);
    }

    [Fact]
    public void ApplicationError1000_Unnamed_UsesIndices0_3_6_8_12()
    {
        var item = Item(WerGoldenFixtures.ApplicationError1000Unnamed);
        Assert.True(item.IsUnnamed);
        Assert.Equal("probe.exe", item.Values[0]);
        Assert.Equal("KERNELBASE.dll", item.Values[3]);
        Assert.Equal("e0434352", item.Values[6]);
        Assert.Equal("0xf04", item.Values[8]);
        Assert.Equal(ProbeReport, item.Values[12]);

        var native = WindowsCrashEvidenceTool.FromApplicationError(item);
        Assert.Equal(("probe.exe", "KERNELBASE.dll", "0xe0434352", (int?)3844), (native.Process, native.FaultModule, native.ExceptionCode, native.Pid));
        Assert.Equal([ProbeReport], native.Identities);
    }

    [Theory]
    [InlineData("0xf04", 3844)]
    [InlineData("0XF04", 3844)]
    [InlineData("3844", null)]
    [InlineData("0x0", null)]
    [InlineData("0xZZ", null)]
    [InlineData("", null)]
    public void ApplicationError1000_ProcessId_IsParsedOnlyAsPrefixedHexadecimal(string value, int? expected) =>
        Assert.Equal(expected, WindowsWerNormalizer.HexProcessId(value));

    [Theory]
    [InlineData("50", "0x50")]
    [InlineData("193", "0x193")]
    [InlineData("1a1", "0x1a1")]
    [InlineData("1A1", "0x1a1")]
    [InlineData("0x00000050", "0x50")]
    [InlineData("c0000005", "0xc0000005")]
    [InlineData("0", "0x0")]
    [InlineData("zz", null)]
    [InlineData("", null)]
    [InlineData("12345678901234567", null)]
    public void Codes_AreCanonicalLowerCaseHexadecimal(string value, string? expected) =>
        Assert.Equal(expected, EvidenceCodes.CanonicalHex(value));

    [Theory]
    [InlineData("D25F62A3-CE7F-4702-8453-9BD1D0E36482", ProbeReport)]
    [InlineData("{d25f62a3-ce7f-4702-8453-9bd1d0e36482}", ProbeReport)]
    [InlineData("00000000-0000-0000-0000-000000000000", null)]
    [InlineData("0", null)]
    [InlineData("report-1", null)]
    public void ReportGuids_AreNormalized_AndTheAllZeroGuidIsNotAnIdentity(string value, string? expected) =>
        Assert.Equal(expected, WindowsWerNormalizer.NormalizeGuid(value));

    // ---- dump references (review note R11) ----

    [Theory]
    [InlineData("\n\\\\?\\C:\\WINDOWS\\Minidump\\a-1-01.dmp\n\\\\?\\C:\\x.xml", @"C:\WINDOWS\Minidump\a-1-01.dmp")]
    [InlineData("\\\\?\\C:\\x.xml\n\\\\?\\C:\\dumps\\b.DMP", @"C:\dumps\b.DMP")]
    [InlineData("\\\\?\\C:\\x.xml\n\\\\?\\C:\\y.txt", null)]
    [InlineData("\\\\?\\UNC\\server\\share\\c.dmp", null)]
    [InlineData("\\\\server\\share\\c.dmp", null)]
    [InlineData("relative\\c.dmp", null)]
    [InlineData("C:\\dumps\\bad\u0007name.dmp", null)]
    [InlineData("", null)]
    public void DumpReferences_AreTheFirstSafeDriveAbsoluteDmpPath_PathStringsOnly(string attachedFiles, string? expected) =>
        Assert.Equal(expected, WindowsWerNormalizer.DumpReference(attachedFiles));

    [Fact]
    public void ADumpReferenceLongerThanTheBound_IsDropped_NeverCut()
    {
        var path = @"C:\" + new string('d', WindowsWerNormalizer.MaximumDumpPathCharacters) + ".dmp";

        Assert.Null(WindowsWerNormalizer.DumpReference(path));
    }

    // ---- correlation (ADR-0041 §7, review note R10), on the recorded 1000 / 1001 / Report.wer of one crash ----

    [Fact]
    public void ApplicationError1000_AndWer1001_ShareAReportGuid_AndMergeIntoOneCrash_DatedByTheOccurrence()
    {
        var crash = Assert.Single(WindowsCrashCorrelator.Merge(
        [
            WindowsCrashEvidenceTool.FromApplicationError(Item(WerGoldenFixtures.ApplicationError1000Named)),
            WindowsCrashEvidenceTool.FromWer(Item(WerGoldenFixtures.Wer1001Clr20r3)),
        ]));

        Assert.Equal(EvidenceTimestampKind.Occurred, crash.TimestampKind);
        Assert.Equal(DateTimeOffset.Parse("2026-09-30T21:07:20.8211183Z"), crash.TimestampUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-09-30T21:07:22.8343190Z"), crash.ReportedUtc);
        Assert.Equal(WindowsCrashEvidenceTool.ApplicationErrorSource, crash.Source);
        Assert.Equal(ProbeReport, crash.ReportId);
        Assert.Equal((CrashKinds.ApplicationCrash, "probe.exe", (int?)3844, "KERNELBASE.dll", "0xe0434352", "CLR20r3"),
            (crash.Kind, crash.Process, crash.Pid, crash.FaultModule, crash.ExceptionCode, crash.EventName));
        Assert.EndsWith(".tmp.dmp", crash.DumpPath, StringComparison.Ordinal);
        Assert.Equal([WindowsCrashEvidenceTool.ApplicationErrorSource, WindowsCrashEvidenceTool.WerEventSource], crash.EvidenceSources);
    }

    [Fact]
    public void Wer1001_AndReportWer_ShareAReportGuid_AndTheReportWerEventTimeIsTheOccurrence()
    {
        var report = ReportWerNative(WerGoldenFixtures.ReportWer());
        var crash = Assert.Single(WindowsCrashCorrelator.Merge([report, WindowsCrashEvidenceTool.FromWer(Item(WerGoldenFixtures.Wer1001Clr20r3))]));

        Assert.Equal(EvidenceTimestampKind.Occurred, crash.TimestampKind);
        Assert.Equal(DateTimeOffset.FromFileTime(134352760409584853).ToUniversalTime(), crash.TimestampUtc);
        Assert.Equal("windows.wer.programdata.reportarchive", crash.Source);
        Assert.Equal(DateTimeOffset.Parse("2026-09-30T21:07:22.8343190Z"), crash.ReportedUtc);
        Assert.Equal(ProbeReport, crash.ReportId); // the WER 1001 ReportId wins over the Report.wer ReportIdentifier
        Assert.Equal("1570137938940312820", crash.Bucket);
    }

    [Fact]
    public void Correlation_IsTransitive_ThroughAReportWerThatCarriesBothGuids()
    {
        // 1000 carries only X, the WER 1001 carries only Y, the Report.wer carries X and Y: one crash, although 1000 and 1001 share nothing.
        var x = Guid.NewGuid().ToString();
        var y = Guid.NewGuid().ToString();
        var application = WindowsCrashEvidenceTool.FromApplicationError(Item(WerGoldenFixtures.ApplicationError1000Named.Replace(ProbeReport, x, StringComparison.Ordinal)));
        var wer = WindowsCrashEvidenceTool.FromWer(Item(WerGoldenFixtures.Wer1001Clr20r3.Replace(ProbeReport, y, StringComparison.Ordinal)));
        var report = ReportWerNative(WerGoldenFixtures.ReportWer(reportIdentifier: y, integrator: x));

        Assert.Equal(2, WindowsCrashCorrelator.Merge([application, wer]).Count);
        var crash = Assert.Single(WindowsCrashCorrelator.Merge([application, wer, report]));
        Assert.Equal(3, crash.EvidenceSources.Count);
        Assert.Equal(y, crash.ReportId);
    }

    [Fact]
    public void MismatchingGuids_AreNeverMerged_EvenForTheSameApplicationAtTheSameInstant()
    {
        var application = WindowsCrashEvidenceTool.FromApplicationError(Item(WerGoldenFixtures.ApplicationError1000Named));
        var other = WindowsCrashEvidenceTool.FromWer(Item(WerGoldenFixtures.Wer1001Clr20r3.Replace(ProbeReport, Guid.NewGuid().ToString(), StringComparison.Ordinal)
            .Replace("2026-09-30T21:07:22.8343190Z", "2026-09-30T21:07:20.8211183Z", StringComparison.Ordinal)));

        var crashes = WindowsCrashCorrelator.Merge([application, other]);

        Assert.Equal(2, crashes.Count);
        Assert.All(crashes, crash => Assert.Single(crash.EvidenceSources));
        Assert.All(crashes, crash => Assert.NotNull(crash.ReportId));
    }

    [Fact]
    public void MissingGuids_AreNeverMerged_AndAreCountedAsUncorrelated_WhileUnmergedGuidsAreNot()
    {
        var noGuidApplication = WindowsCrashEvidenceTool.FromApplicationError(Item(WerGoldenFixtures.ApplicationError1000Named.Replace(ProbeReport, string.Empty, StringComparison.Ordinal)));
        var noGuidApplication2 = WindowsCrashEvidenceTool.FromApplicationError(Item(WerGoldenFixtures.ApplicationError1000Named.Replace(ProbeReport, "00000000-0000-0000-0000-000000000000", StringComparison.Ordinal)
            .Replace("<EventRecordID>100508</EventRecordID>", "<EventRecordID>100510</EventRecordID>", StringComparison.Ordinal)));
        var withGuidAlone = WindowsCrashEvidenceTool.FromApplicationError(Item(WerGoldenFixtures.ApplicationError1000Named.Replace(ProbeReport, Guid.NewGuid().ToString(), StringComparison.Ordinal)
            .Replace("<EventRecordID>100508</EventRecordID>", "<EventRecordID>100511</EventRecordID>", StringComparison.Ordinal)));

        var crashes = WindowsCrashCorrelator.Merge([noGuidApplication, noGuidApplication2, withGuidAlone]);
        Assert.Equal(3, crashes.Count);

        var groups = SystemCrashFormatting.Aggregate(crashes);
        var group = Assert.Single(groups);
        Assert.Equal(3, group["count"]!.GetValue<int>());
        // Two members carry no usable report GUID: they may duplicate a crash another source recorded, which bOps does not guess.
        // The member with a GUID that merged with nothing is not "uncorrelated"; its single evidence source says it was seen once.
        Assert.Equal(2, group["uncorrelatedCount"]!.GetValue<int>());
    }

    [Fact]
    public void AWerReprocessingBurst_OfOneReport_CountsOnce_AtItsEarliestProcessingTime()
    {
        var reportId = Guid.NewGuid().ToString();
        var burst = Enumerable.Range(0, 5)
            .Select(index => WindowsCrashEvidenceTool.FromWer(Item(WerGoldenFixtures.Wer1001LiveKernel("193", reportId, Now.AddDays(-index), recordId: 1000 + index))))
            .ToArray();

        var crash = Assert.Single(WindowsCrashCorrelator.Merge(burst));
        Assert.Equal(Now.AddDays(-4), crash.TimestampUtc);
        Assert.Equal(EvidenceTimestampKind.Reported, crash.TimestampKind);
        Assert.Null(crash.ReportedUtc);
    }

    [Fact]
    public void TheSameNativeRecordReadTwice_IsOneRecord()
    {
        var record = WindowsCrashEvidenceTool.FromApplicationError(Item(WerGoldenFixtures.ApplicationError1000Unnamed.Replace(ProbeReport, string.Empty, StringComparison.Ordinal)));

        Assert.Single(WindowsCrashCorrelator.Merge([record, record]));
    }

    [Fact]
    public void AReportWerWithoutEventTime_IsDatedByItsFileTime_AsReported()
    {
        var report = ReportWerNative(WerGoldenFixtures.ReportWer(withEventTime: false), lastWrite: Now.AddHours(-3));
        var crash = Assert.Single(WindowsCrashCorrelator.Merge([report]));

        Assert.Equal(EvidenceTimestampKind.Reported, crash.TimestampKind);
        Assert.Equal(Now.AddHours(-3), crash.TimestampUtc);
    }

    // ---- the evidence-driven correction of ADR-0041 §6 (operator decision, 2026-10-02) ----

    [Fact]
    public void ABlueScreenReportWerEventTime_IsReported_BecauseItIsWrittenAfterTheReboot()
    {
        // Recorded on the operator workstation: the 0x50 report's EventTime is 2026-09-04T08:45:27.8899582Z, 50 s after the
        // Kernel-Power 41 of the next boot (08:44:37Z) and 40 s after the minidump was written (08:44:47Z).
        var eventTime = DateTimeOffset.Parse("2026-09-04T08:45:27.8899582Z");
        var report = ReportWerNative(KernelReportWer("BlueScreen", "50", eventTime, "07e3c759-e445-4da7-8efa-2694d7dea04c"));
        var wer = WindowsCrashEvidenceTool.FromWer(Item(WerGoldenFixtures.Wer1001BlueScreen50));

        Assert.Equal((CrashKinds.KernelBugcheck, EvidenceTimestampKind.Reported), (report.Kind, report.TimestampKind));
        var crash = Assert.Single(WindowsCrashCorrelator.Merge([report, wer]));
        Assert.Equal(EvidenceTimestampKind.Reported, crash.TimestampKind);
        Assert.Equal(eventTime, crash.TimestampUtc); // still the Report.wer time, the earliest evidence, but never called an occurrence
        Assert.Equal("windows.wer.programdata.reportarchive", crash.Source);
        Assert.Null(crash.ReportedUtc);
        Assert.Equal("0x50", crash.Code);
        Assert.Equal(2, crash.EvidenceSources.Count);
    }

    [Fact]
    public void ALiveKernelEventReportWerEventTime_StaysAnOccurrence_AndTheWerProcessingTimeIsReported()
    {
        // Recorded: the 0x193 live dump WATCHDOG-20260730-1010.dmp has EventTime 2026-07-30T08:10:22Z; WER processed it on 2026-10-02.
        var eventTime = DateTimeOffset.Parse("2026-07-30T08:10:22.6128076Z");
        var report = ReportWerNative(KernelReportWer("LiveKernelEvent", "193", eventTime, "aed731f5-377d-4b0a-bc3f-fd4256d44e8e"));
        var crash = Assert.Single(WindowsCrashCorrelator.Merge([report, WindowsCrashEvidenceTool.FromWer(Item(WerGoldenFixtures.Wer1001LiveKernel193))]));

        Assert.Equal((EvidenceTimestampKind.Occurred, eventTime), (crash.TimestampKind, crash.TimestampUtc));
        Assert.Equal(DateTimeOffset.Parse("2026-10-02T08:43:34.0533512Z"), crash.ReportedUtc);
        Assert.Equal("0x193", crash.Code);
    }

    [Fact]
    public void AnApplicationReportWerEventTime_StaysAnOccurrence()
    {
        var report = ReportWerNative(WerGoldenFixtures.ReportWer());

        Assert.Equal((CrashKinds.ApplicationCrash, EvidenceTimestampKind.Occurred), (report.Kind, report.TimestampKind));
    }

    [Fact]
    public void ABlueScreenWithoutAnyOccurrenceSource_IsNeverPromoted_ByOtherTimes()
    {
        var crash = Assert.Single(WindowsCrashCorrelator.Merge([WindowsCrashEvidenceTool.FromWer(Item(WerGoldenFixtures.Wer1001BlueScreen50))]));

        Assert.Equal(EvidenceTimestampKind.Reported, crash.TimestampKind);
    }

    private static byte[] KernelReportWer(string eventType, string code, DateTimeOffset eventTime, string reportIdentifier)
    {
        var text = $"Version=1\r\nEventType={eventType}\r\nEventTime={eventTime.ToFileTime()}\r\nReportType=0\r\nReportIdentifier={reportIdentifier}\r\nSig[0].Value={code}\r\nDynamicSig[1].Value=10.0.28000\r\n";
        return [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)];
    }

    // ---- the incident dataset (plan §2.3): aggregate groups instead of 100 near-identical rows ----

    [Fact]
    public void TheIncidentDataset_AggregatesIntoSevenSignatures_WithCountsPerCode()
    {
        var natives = IncidentWerRecords(distinctReports: true).Select(WindowsCrashEvidenceTool.FromWer).ToArray();
        var crashes = WindowsCrashCorrelator.Merge(natives);

        var groups = SystemCrashFormatting.Aggregate(crashes);

        Assert.Equal(100, crashes.Count);
        Assert.Equal(
            [
                // count descending, then the newest last time first (the burst wrote the codes in this order: 193, 1e, 7f, 101, 1a1, 3b, 50).
                ("kernel-live-dump", "0x193", 76),
                ("kernel-bugcheck", "0x7f", 6),
                ("kernel-bugcheck", "0x1e", 6),
                ("kernel-bugcheck", "0x50", 3),
                ("kernel-bugcheck", "0x3b", 3),
                ("kernel-live-dump", "0x1a1", 3),
                ("kernel-bugcheck", "0x101", 3),
            ],
            groups.Select(group => (group["kind"]!.GetValue<string>(), group["code"]!.GetValue<string>(), group["count"]!.GetValue<int>())).ToArray());
        Assert.All(groups, group => Assert.Equal("reported", group["timestampKind"]!.GetValue<string>()));
        Assert.All(groups, group => Assert.Equal(0, group["uncorrelatedCount"]!.GetValue<int>()));
        Assert.All(groups, group => Assert.Null(group["eventName"])); // the event name is part of the key only for kind wer
    }

    [Fact]
    public void TheIncidentDataset_AsARedeliveredBurst_CountsEachReportOnce()
    {
        // As on the operator workstation, the 100 rows of the incident were re-deliveries of few reports.
        var crashes = WindowsCrashCorrelator.Merge(IncidentWerRecords(distinctReports: false).Select(WindowsCrashEvidenceTool.FromWer));

        Assert.Equal(7, crashes.Count);
        Assert.Equal(7, SystemCrashFormatting.Aggregate(crashes).Count);
    }

    // ---- Report.wer: bounded streaming key scan (H-7) ----

    [Fact]
    public void AReportWerFarAbove32KiB_IsScanned_NotSkipped_AndTheScanStopsAtTheEndOfTheHeader()
    {
        var bytes = WerGoldenFixtures.ReportWer(loadedModules: 3_000);
        Assert.True(bytes.Length > 150_000);

        using var stream = new MemoryStream(bytes);
        var scan = WindowsReportWerScanner.Scan(stream, WindowsReportWerScanner.PerFileBytes);

        Assert.True(scan.HasNeededKeys);
        Assert.False(scan.ReachedCap);
        Assert.True(scan.BytesRead < 8_192, $"the header ends early, read {scan.BytesRead} bytes");
        Assert.Equal("CLR20r3", scan.Values["EventType"]);
        Assert.Equal(ProbeArchive, scan.Values["ReportIdentifier"]);
        Assert.Equal(ProbeReport, scan.Values["IntegratorReportIdentifier"]);
        Assert.Equal("System.Management", scan.Values["Sig[3].Value"]);
        Assert.False(scan.Values.ContainsKey("AppPath"));
    }

    [Fact]
    public void NeededKeysBeyondTheCap_AreNotFound_AndTheCapIsReported()
    {
        var text = new StringBuilder();
        for (var index = 0; index < 20_000; index++)
        {
            text.Append("Padding").Append(index).Append("=xxxxxxxxxxxxxxxx\r\n");
        }

        text.Append("EventType=APPCRASH\r\n");
        using var stream = new MemoryStream([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text.ToString())]);

        var scan = WindowsReportWerScanner.Scan(stream, WindowsReportWerScanner.PerFileBytes);

        Assert.False(scan.HasNeededKeys);
        Assert.True(scan.ReachedCap);
        Assert.Equal(WindowsReportWerScanner.PerFileBytes, scan.BytesRead);
    }

    [Fact]
    public void ALineCutByTheCap_IsNotUsed()
    {
        var bytes = Encoding.UTF8.GetBytes("EventType=APPCRASH\nSig[3].Value=KERNELBASE.dll\nSig[4].Value=10.0.0.0\n");
        var cap = Encoding.UTF8.GetByteCount("EventType=APPCRASH\nSig[3].Value=KERNEL");
        using var stream = new MemoryStream(bytes);

        var scan = WindowsReportWerScanner.Scan(stream, cap);

        Assert.Equal("APPCRASH", scan.Values["EventType"]);
        Assert.False(scan.Values.ContainsKey("Sig[3].Value"));
        Assert.True(scan.ReachedCap);
    }

    [Fact]
    public async Task TheWerDirectoryScan_ReadsLargeReports_UsesDistinctRootNames_AndSkipsReportsWithoutNeededKeysAsPartial()
    {
        var root = Directory.CreateTempSubdirectory("bops-h7-wer-").FullName;
        try
        {
            var archive = Directory.CreateDirectory(Path.Combine(root, "archive")).FullName;
            await File.WriteAllBytesAsync(Path.Combine(Directory.CreateDirectory(Path.Combine(archive, "AppCrash_probe")).FullName, "Report.wer"), WerGoldenFixtures.ReportWer(loadedModules: 3_000));
            await File.WriteAllTextAsync(Path.Combine(Directory.CreateDirectory(Path.Combine(archive, "broken")).FullName, "Report.wer"), "Version=1\r\nNoEventType=1\r\n");
            var missing = Path.Combine(root, "missing");
            using var slice = new EvidenceTimeBudget(TimeSpan.FromSeconds(20), TimeProvider.System).Start(1, CancellationToken.None);
            long callBytes = 0;

            var read = WindowsCrashEvidenceTool.ReadWerDirectory(new WerDirectory("windows.wer.programdata.reportarchive", archive), Now.AddDays(-1), slice, ref callBytes);
            var absent = WindowsCrashEvidenceTool.ReadWerDirectory(new WerDirectory("windows.wer.localappdata.reportarchive", missing), Now.AddDays(-1), slice, ref callBytes);

            var record = Assert.Single(read.Records);
            Assert.Equal("windows.wer.programdata.reportarchive", record.Source);
            Assert.Equal(InventorySourceStatus.Partial, read.Source.Status);
            Assert.Contains("no EventType", read.Source.Detail, StringComparison.Ordinal);
            Assert.Equal(Now.AddDays(-1), read.Source.ExaminedFromUtc);
            Assert.False(read.Truncated);
            Assert.True(callBytes < 64 * 1024);
            Assert.Equal(InventorySourceStatus.NotApplicable, absent.Source.Status);
            Assert.Equal("windows.wer.localappdata.reportarchive", absent.Source.Name);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ThePerCallReportWerBudget_StopsTheScan_AsPartialAndTruncated()
    {
        var root = Directory.CreateTempSubdirectory("bops-h7-budget-").FullName;
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(Directory.CreateDirectory(Path.Combine(root, "a")).FullName, "Report.wer"), WerGoldenFixtures.ReportWer());
            using var slice = new EvidenceTimeBudget(TimeSpan.FromSeconds(20), TimeProvider.System).Start(1, CancellationToken.None);
            var callBytes = WindowsReportWerScanner.PerCallBytes;

            var read = WindowsCrashEvidenceTool.ReadWerDirectory(new WerDirectory("windows.wer.programdata.reportqueue", root), Now.AddDays(-1), slice, ref callBytes);

            Assert.Empty(read.Records);
            Assert.True(read.Truncated);
            Assert.Equal(InventorySourceStatus.Partial, read.Source.Status);
            Assert.Null(read.Source.ExaminedFromUtc);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [WindowsOnlyFact]
    public async Task AReportWerThisIdentityMayNotRead_IsCountedAsDenied_NeverTreatedAsAbsent()
    {
        // On the operator workstation 472 of 500 Report.wer files are unreadable without elevation, and File.Exists says "false" for
        // them: an existence test would have reported the source as complete while it had read almost nothing.
        var root = Directory.CreateTempSubdirectory("bops-h7-denied-").FullName;
        var denied = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "AppCrash_denied")).FullName, "Report.wer");
        await File.WriteAllBytesAsync(denied, WerGoldenFixtures.ReportWer());
        await File.WriteAllBytesAsync(Path.Combine(Directory.CreateDirectory(Path.Combine(root, "AppCrash_readable")).FullName, "Report.wer"), WerGoldenFixtures.ReportWer(reportIdentifier: Guid.NewGuid().ToString(), integrator: Guid.NewGuid().ToString()));
        var user = global::System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var rule = new global::System.Security.AccessControl.FileSystemAccessRule(user, global::System.Security.AccessControl.FileSystemRights.ReadData, global::System.Security.AccessControl.AccessControlType.Deny);
        var security = new FileInfo(denied).GetAccessControl();
        security.AddAccessRule(rule);
        new FileInfo(denied).SetAccessControl(security);
        try
        {
            Assert.False(File.Exists(denied) && TryOpen(denied), "the deny rule must make the file unreadable for this test to mean anything");
            using var slice = new EvidenceTimeBudget(TimeSpan.FromSeconds(20), TimeProvider.System).Start(1, CancellationToken.None);
            long callBytes = 0;

            var read = WindowsCrashEvidenceTool.ReadWerDirectory(new WerDirectory("windows.wer.programdata.reportarchive", root), Now.AddDays(-1), slice, ref callBytes);

            Assert.Single(read.Records);
            Assert.Equal(InventorySourceStatus.Partial, read.Source.Status);
            Assert.Contains("1 report(s) are not readable by this identity", read.Source.Detail, StringComparison.Ordinal);
        }
        finally
        {
            security.RemoveAccessRule(rule);
            new FileInfo(denied).SetAccessControl(security);
            Directory.Delete(root, recursive: true);
        }
    }

    [WindowsOnlyFact]
    public void AWerDirectoryThisIdentityMayNotList_IsUnavailable_NeverNotApplicable()
    {
        // Directory.Exists is false for a directory that cannot be listed, exactly as for one that is not there.
        var root = Directory.CreateTempSubdirectory("bops-h7-denied-dir-").FullName;
        var user = global::System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var rule = new global::System.Security.AccessControl.FileSystemAccessRule(user, global::System.Security.AccessControl.FileSystemRights.ListDirectory, global::System.Security.AccessControl.AccessControlType.Deny);
        var security = new DirectoryInfo(root).GetAccessControl();
        security.AddAccessRule(rule);
        new DirectoryInfo(root).SetAccessControl(security);
        try
        {
            using var slice = new EvidenceTimeBudget(TimeSpan.FromSeconds(20), TimeProvider.System).Start(1, CancellationToken.None);
            long callBytes = 0;

            var read = WindowsCrashEvidenceTool.ReadWerDirectory(new WerDirectory("windows.wer.programdata.reportqueue", root), Now.AddDays(-1), slice, ref callBytes);
            var minidumps = WindowsStabilityTool.ReadMinidumps(root, Now.AddDays(-1), slice);

            Assert.Equal(InventorySourceStatus.Unavailable, read.Source.Status);
            Assert.Equal(InventorySourceStatus.Unavailable, minidumps.Source.Status);
        }
        finally
        {
            security.RemoveAccessRule(rule);
            new DirectoryInfo(root).SetAccessControl(security);
            Directory.Delete(root, recursive: true);
        }
    }

    private static bool TryOpen(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    [Fact]
    public void TheKnownWerDirectories_HaveDistinctSourceNamesPerRoot()
    {
        var names = WindowsCrashEvidenceTool.KnownWerDirectories().Select(directory => directory.Source).ToArray();

        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("windows.wer.programdata.reportarchive", names);
        Assert.Contains("windows.wer.programdata.reportqueue", names);
        Assert.Contains("windows.wer.localappdata.reportarchive", names);
        Assert.Contains("windows.wer.localappdata.reportqueue", names);
    }

    // ---- the whole tool, with scripted sources ----

    [Fact]
    public async Task TheTool_MergesTheSourcesOfOneCrash_ReportsCoverage_AndDefaultsToAggregate()
    {
        var tool = ScriptedTool(
            new FakeTimeProvider(Now),
            application: [Item(WerGoldenFixtures.ApplicationError1000Named)],
            wer: [Item(WerGoldenFixtures.Wer1001Clr20r3), Item(WerGoldenFixtures.Wer1001BlueScreen50)],
            oldestApplication: Now.AddDays(-30));

        var result = await tool.ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["sinceDays"] = 7 }));
        var json = JsonNode.Parse(result.Output!)!.AsObject();

        Assert.Equal(2, json["schemaVersion"]!.GetValue<int>());
        Assert.Equal("aggregate", json["mode"]!.GetValue<string>());
        Assert.Equal(2, json["observedItems"]!.GetValue<int>());
        Assert.Equal("complete", json["coverage"]!["state"]!.GetValue<string>());
        Assert.Equal(["windows-event-application-error", "windows-event-wer"], json["sources"]!.AsArray().Select(source => source!["name"]!.GetValue<string>()).ToArray());
        Assert.True(json["complete"]!.GetValue<bool>());
        Assert.Equal(ToolResultCompleteness.Complete, result.Completeness);
        var groups = json["groups"]!.AsArray();
        Assert.Equal(2, groups.Count);
        Assert.Contains(groups, group => group!["kind"]!.GetValue<string>() == "kernel-bugcheck" && group["code"]!.GetValue<string>() == "0x50" && group["dumpReferenceCount"]!.GetValue<int>() == 1);
        Assert.Contains(groups, group => group!["kind"]!.GetValue<string>() == "application-crash" && group["timestampKind"]!.GetValue<string>() == "occurred"
            && group["evidenceSources"]!.AsArray().Count == 2);
    }

    [Fact]
    public async Task ARequestOlderThanTheRetainedApplicationLog_IsPartialCoverage_WithTheOldestAvailableTime()
    {
        var tool = ScriptedTool(new FakeTimeProvider(Now), application: [], wer: [], oldestApplication: Now.AddDays(-30));

        var result = await tool.ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["sinceDays"] = 180 }));
        var json = JsonNode.Parse(result.Output!)!.AsObject();

        Assert.Equal("complete", json["status"]!.GetValue<string>());
        Assert.Equal("partial", json["coverage"]!["state"]!.GetValue<string>());
        var store = json["coverage"]!["stores"]![0]!;
        Assert.Equal("windows.channel.Application", store["name"]!.GetValue<string>());
        Assert.Equal("eventLog", store["basis"]!.GetValue<string>());
        Assert.Equal(EvidenceTime.Format(Now.AddDays(-30)), store["oldestAvailableUtc"]!.GetValue<string>());
        Assert.Equal(20_971_520, store["logMaximumBytes"]!.GetValue<long>());
        Assert.False(json["complete"]!.GetValue<bool>());
        Assert.Equal(ToolResultCompleteness.Partial, result.Completeness);
    }

    [Fact]
    public async Task ACrashWhoseOccurrenceIsOutsideTheWindow_IsOutsideIt_EvenIfItWasReportedInside()
    {
        var root = Directory.CreateTempSubdirectory("bops-h7-window-").FullName;
        try
        {
            // Report.wer EventTime (2026-09-30) is older than a one-hour window; its WER 1001 is inside it.
            await File.WriteAllBytesAsync(Path.Combine(Directory.CreateDirectory(Path.Combine(root, "AppCrash_probe")).FullName, "Report.wer"), WerGoldenFixtures.ReportWer());
            var werInside = Item(WerGoldenFixtures.Wer1001Clr20r3.Replace("2026-09-30T21:07:22.8343190Z", "2026-10-02T11:30:00.0000000Z", StringComparison.Ordinal));
            var tool = ScriptedTool(new FakeTimeProvider(Now), application: [], wer: [werInside], oldestApplication: Now.AddDays(-30), directories: [new("windows.wer.programdata.reportarchive", root)]);

            var json = JsonNode.Parse((await tool.ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["sinceMinutes"] = 60, ["mode"] = "raw" }))).Output!)!;

            Assert.Equal(0, json["observedItems"]!.GetValue<int>());
            Assert.Empty(json["items"]!.AsArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TheRawEnvelope_IsTheSchema1RowPlusTheSchema2Fields_InAFixedOrder()
    {
        var tool = ScriptedTool(new FakeTimeProvider(Now), application: [Item(WerGoldenFixtures.ApplicationError1000Named)], wer: [Item(WerGoldenFixtures.Wer1001Clr20r3)], oldestApplication: Now.AddDays(-30));

        var json = JsonNode.Parse((await tool.ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["mode"] = "raw", ["sinceMinutes"] = 10_080 }))).Output!)!.AsObject();

        Assert.Equal(
            ["schemaVersion", "mode", "status", "complete", "truncated", "window", "coverage", "observedItems", "returnedItems", "catalogAge", "sources", "warnings", "items"],
            json.Select(pair => pair.Key).ToArray());
        Assert.Equal(2, json["schemaVersion"]!.GetValue<int>());
        Assert.Equal(["fromUtc", "toUtc"], json["window"]!.AsObject().Select(pair => pair.Key).ToArray());
        Assert.Equal(["requestedFromUtc", "requestedToUtc", "state", "stores"], json["coverage"]!.AsObject().Select(pair => pair.Key).ToArray());
        Assert.Equal(["name", "status", "detail", "examinedFromUtc"], json["sources"]![0]!.AsObject().Select(pair => pair.Key).ToArray());
        var row = Assert.Single(json["items"]!.AsArray())!.AsObject();
        Assert.Equal(
            ["timestampUtc", "process", "pid", "kind", "dumpPath", "eventIdOrCrashId", "summary", "source",
             "timestampKind", "reportedUtc", "reportId", "eventName", "code", "bugcheckCode", "liveDumpCode", "exceptionCode", "faultModule", "bucket", "evidenceSources"],
            row.Select(pair => pair.Key).ToArray());
        // observedItems counts merged crash records: two native records, one crash.
        Assert.Equal(1, json["observedItems"]!.GetValue<int>());
        Assert.Equal(1, json["returnedItems"]!.GetValue<int>());
        Assert.Equal("CLR20r3; 0xe0434352; KERNELBASE.dll", row["summary"]!.GetValue<string>());
        Assert.Equal(ProbeReport, row["eventIdOrCrashId"]!.GetValue<string>());
    }

    [Fact]
    public async Task ATimedOutSource_BecomesPartialEvidence_AndDoesNotStarveTheSourcesAfterIt()
    {
        var clock = new FakeTimeProvider(Now);
        var slices = new List<TimeSpan>();
        var tool = new WindowsCrashEvidenceTool(
            clock,
            () => [],
            (request, slice) =>
            {
                slices.Add(slice.Length);
                if (request.XPath.Contains("Application Error", StringComparison.Ordinal))
                {
                    // A flooded source: it runs until its slice runs out, as a native read would.
                    clock.Advance(slice.Length);
                    Assert.True(slice.TimedOut);
                    return new EventLogScan([], InventorySourceStatus.Partial, "The read did not finish within its time bound and was stopped.", true, null);
                }

                return new EventLogScan([Item(WerGoldenFixtures.Wer1001BlueScreen50)], InventorySourceStatus.Available, null, false, request.WindowFromUtc);
            },
            (backed, slice) => new CoverageStore("windows.channel.Application", CoverageBasis.EventLog, Now.AddDays(-30), 20_971_520, backed));

        var result = await tool.ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["sinceDays"] = 7 }));
        var json = JsonNode.Parse(result.Output!)!.AsObject();

        Assert.True(result.Succeeded);
        Assert.Equal(2, slices.Count);
        Assert.All(slices, slice => Assert.True(slice > TimeSpan.Zero && slice <= WindowsCrashEvidenceTool.EventLogReadCap));
        Assert.True(clock.GetUtcNow() - Now <= WindowsCrashEvidenceTool.CallBudget);
        Assert.Equal("partial", json["status"]!.GetValue<string>());
        Assert.True(json["truncated"]!.GetValue<bool>());
        Assert.Equal(1, json["observedItems"]!.GetValue<int>());
        Assert.Equal(ToolResultCompleteness.Partial, result.Completeness);
    }

    [Fact]
    public async Task TheFixed64KiBBudget_CutsRowsFromTheEnd_AndSaysSo()
    {
        var many = Enumerable.Range(0, 400).Select(index => Item(WerGoldenFixtures.Wer1001AppCrash
            .Replace("e28f09dd-6af6-4e72-b597-eb72d502a361", Guid.NewGuid().ToString(), StringComparison.Ordinal)
            .Replace("<EventRecordID>99882</EventRecordID>", $"<EventRecordID>{index}</EventRecordID>", StringComparison.Ordinal)
            .Replace("2026-09-30T08:46:49.3094095Z", EvidenceTime.Format(Now.AddMinutes(-index - 1)), StringComparison.Ordinal))).ToArray();
        var tool = ScriptedTool(new FakeTimeProvider(Now), application: [], wer: many, oldestApplication: Now.AddDays(-30));

        var result = await tool.ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["mode"] = "raw", ["sinceMinutes"] = 10_080, ["limit"] = 1_000 }));
        var json = JsonNode.Parse(result.Output!)!.AsObject();

        Assert.True(Encoding.UTF8.GetByteCount(result.Output!) <= SystemMaintenanceLimits.CrashOutputBytes);
        Assert.Equal(400, json["observedItems"]!.GetValue<int>());
        Assert.InRange(json["returnedItems"]!.GetValue<int>(), 1, 399);
        Assert.Equal(json["items"]!.AsArray().Count, json["returnedItems"]!.GetValue<int>());
        Assert.True(json["truncated"]!.GetValue<bool>());
        Assert.False(json["complete"]!.GetValue<bool>());
    }

    [Fact]
    public async Task TheAuditSummary_CarriesCountsAndStatus_NeverANameAPathOrAnId()
    {
        var tool = ScriptedTool(new FakeTimeProvider(Now), application: [Item(WerGoldenFixtures.ApplicationError1000Named)], wer: [Item(WerGoldenFixtures.Wer1001Clr20r3), Item(WerGoldenFixtures.Wer1001BlueScreen50)], oldestApplication: Now.AddDays(-30));
        var arguments = ToolArguments.FromJson(new JsonObject { ["mode"] = "raw", ["sinceDays"] = 1 });
        var raw = await tool.ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["mode"] = "raw", ["sinceMinutes"] = 10_080 }));

        var summary = tool.CreateAuditSummary(arguments, raw)!.ToJsonString();

        Assert.Contains("\"byKind\"", summary, StringComparison.Ordinal);
        Assert.Contains("\"coverageState\"", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("probe.exe", summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("KERNELBASE", summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".dmp", summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ProbeReport, summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("1570137938940312820", summary, StringComparison.Ordinal);
    }

    [WindowsOnlyFact]
    public async Task RealWindowsSmoke_ReadsOnlyBoundedCrashEvidence_AndReturnsTheSchema2Shape()
    {
        var result = await new WindowsCrashEvidenceTool().ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["sinceDays"] = 30 }));
        Assert.True(result.Succeeded, result.ErrorMessage);
        var json = JsonNode.Parse(result.Output!)!.AsObject();
        Assert.Equal(2, json["schemaVersion"]!.GetValue<int>());
        Assert.Equal("aggregate", json["mode"]!.GetValue<string>());
        Assert.NotNull(json["coverage"]);
        Assert.True(Encoding.UTF8.GetByteCount(result.Output!) <= SystemMaintenanceLimits.CrashOutputBytes);
        Assert.Contains(json["sources"]!.AsArray(), source => source!["name"]!.GetValue<string>() == "windows-event-wer");
        Assert.DoesNotContain(json["sources"]!.AsArray(), source => source!["name"]!.GetValue<string>() == "windows.wer.reportarchive");
    }

    // ---- helpers ----

    internal static EventLogItem Item(string xml) => WindowsEventLogEvidence.ParseXml(xml.Trim(), "Application");

    private static CrashNative ReportWerNative(byte[] bytes, DateTimeOffset? lastWrite = null)
    {
        using var stream = new MemoryStream(bytes);
        var scan = WindowsReportWerScanner.Scan(stream, WindowsReportWerScanner.PerFileBytes);
        return WindowsCrashEvidenceTool.FromReportWer(scan.Values, "windows.wer.programdata.reportarchive", @"C:\ProgramData\Microsoft\Windows\WER\ReportArchive\AppCrash_probe", lastWrite ?? Now);
    }

    /// <summary>The incident of plan §2.3: 76 × 0x193, 6 × 0x1e, 6 × 0x7f, 3 × 0x101, 3 × 0x1a1, 3 × 0x3b, 3 × 0x50, all within seconds of a WER re-processing burst.</summary>
    private static IEnumerable<EventLogItem> IncidentWerRecords(bool distinctReports)
    {
        var burst = new DateTimeOffset(2026, 9, 25, 6, 9, 36, TimeSpan.Zero);
        var record = 0L;
        foreach (var (eventName, code, count) in new[] { ("LiveKernelEvent", "193", 76), ("BlueScreen", "1e", 6), ("BlueScreen", "7f", 6), ("BlueScreen", "101", 3), ("LiveKernelEvent", "1a1", 3), ("BlueScreen", "3b", 3), ("BlueScreen", "50", 3) })
        {
            var shared = Guid.NewGuid().ToString();
            for (var index = 0; index < count; index++)
            {
                record++;
                var xml = WerGoldenFixtures.Wer1001LiveKernel(code, distinctReports ? Guid.NewGuid().ToString() : shared, burst.AddMilliseconds(record * 50), record)
                    .Replace("<Data Name='EventName'>LiveKernelEvent</Data>", $"<Data Name='EventName'>{eventName}</Data>", StringComparison.Ordinal);
                yield return Item(xml);
            }
        }
    }

    private static WindowsCrashEvidenceTool ScriptedTool(
        FakeTimeProvider clock,
        IReadOnlyList<EventLogItem> application,
        IReadOnlyList<EventLogItem> wer,
        DateTimeOffset oldestApplication,
        IReadOnlyList<WerDirectory>? directories = null) =>
        new(
            clock,
            () => directories ?? [],
            (request, _) =>
            {
                var records = request.XPath.Contains("Application Error", StringComparison.Ordinal) ? application : wer;
                return new EventLogScan(records.Where(item => item.TimeCreatedUtc >= request.WindowFromUtc).ToArray(), InventorySourceStatus.Available, null, false, request.WindowFromUtc);
            },
            (backed, _) => new CoverageStore("windows.channel.Application", CoverageBasis.EventLog, oldestApplication, 20_971_520, backed));
}
