// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace bOps.Packages.System.Windows.Tests;

/// <summary>
/// Golden Windows Event Log records for HARDEN-7. "Recorded" fixtures are <c>EventRecord.ToXml()</c> output captured read-only on the
/// operator workstation on 2026-10-02 and anonymized (computer name, user SIDs and user-profile paths replaced; report GUIDs kept, as
/// they are random and identify nothing). "Synthetic" fixtures follow the recorded shape of the same provider where the workstation had
/// no such record (LiveKernelEvent 0x117/0x141, Display 4101, disk/stornvme, a fatal WHEA record) or reproduce an older layout by
/// removing the field names of a recorded record (the unnamed fallbacks).
/// </summary>
internal static class WerGoldenFixtures
{
    private const string Ns = "http://schemas.microsoft.com/win/2004/08/events/event";

    // ---- WER 1001 (Application, Windows Error Reporting), named layout ----

    /// <summary>Recorded. BlueScreen, P1 = 50 (PAGE_FAULT_IN_NONPAGED_AREA), with a minidump in AttachedFiles.</summary>
    public const string Wer1001BlueScreen50 = $$"""
        <Event xmlns='{{Ns}}'><System><Provider Name='Windows Error Reporting' Guid='{0ead09bd-2157-539a-8d6d-c87f95b64d70}'/><EventID>1001</EventID><Version>0</Version><Level>4</Level><Task>0</Task><Opcode>0</Opcode><Keywords>0x8000000000000000</Keywords><TimeCreated SystemTime='2026-10-02T08:43:34.3323853Z'/><EventRecordID>101202</EventRecordID><Correlation/><Execution ProcessID='43168' ThreadID='12684'/><Channel>Application</Channel><Computer>HOST.example.test</Computer><Security UserID='S-1-5-18'/></System><EventData><Data Name='Bucket'></Data><Data Name='BucketType'>0</Data><Data Name='EventName'>BlueScreen</Data><Data Name='Response'>Not available</Data><Data Name='CabId'>0</Data><Data Name='P1'>50</Data><Data Name='P2'>fffff801013caad3</Data><Data Name='P3'>10</Data><Data Name='P4'>fffff8017ba5f4a1</Data><Data Name='P5'>2</Data><Data Name='P6'>10_0_28000</Data><Data Name='P7'>0_0</Data><Data Name='P8'>256_1</Data><Data Name='P9'></Data><Data Name='P10'></Data><Data Name='AttachedFiles'>
        \\?\C:\WINDOWS\Minidump\090426-53171-01.dmp
        \\?\C:\WINDOWS\SystemTemp\WER-53171-0.sysdata.xml
        \\?\C:\DumpStack.log
        \\?\C:\ProgramData\Microsoft\Windows\WER\Temp\WER.189d311a-c109-4c06-b11a-222888c156e1.tmp.xml</Data><Data Name='StorePath'>\\?\C:\ProgramData\Microsoft\Windows\WER\ReportQueue\Kernel_50_2cb4c1fdbf1914ff6c71fb1880c0cacbef79dd2_00000000_e3c5b847-371a-49ca-bd6b-255e69a03845</Data><Data Name='AnalysisSymbol'></Data><Data Name='Rechecking'>0</Data><Data Name='ReportId'>07e3c759-e445-4da7-8efa-2694d7dea04c</Data><Data Name='ReportStatus'>2051</Data><Data Name='HashedBucket'></Data><Data Name='CabGuid'>0</Data></EventData></Event>
        """;

    /// <summary>Recorded. LiveKernelEvent, P1 = 193 (VIDEO_DXGKRNL_LIVEDUMP).</summary>
    public const string Wer1001LiveKernel193 = $$"""
        <Event xmlns='{{Ns}}'><System><Provider Name='Windows Error Reporting' Guid='{0ead09bd-2157-539a-8d6d-c87f95b64d70}'/><EventID>1001</EventID><Version>0</Version><Level>4</Level><Task>0</Task><Opcode>0</Opcode><Keywords>0x8000000000000000</Keywords><TimeCreated SystemTime='2026-10-02T08:43:34.0533512Z'/><EventRecordID>101197</EventRecordID><Correlation/><Execution ProcessID='43168' ThreadID='12684'/><Channel>Application</Channel><Computer>HOST.example.test</Computer><Security UserID='S-1-5-18'/></System><EventData><Data Name='Bucket'></Data><Data Name='BucketType'>0</Data><Data Name='EventName'>LiveKernelEvent</Data><Data Name='Response'>Not available</Data><Data Name='CabId'>0</Data><Data Name='P1'>193</Data><Data Name='P2'>810</Data><Data Name='P3'>ffffb3862cfd7000</Data><Data Name='P4'>ffffb386356b8080</Data><Data Name='P5'>b2</Data><Data Name='P6'>10_0_28000</Data><Data Name='P7'>0_0</Data><Data Name='P8'>256_1</Data><Data Name='P9'></Data><Data Name='P10'></Data><Data Name='AttachedFiles'>
        \\?\C:\WINDOWS\LiveKernelReports\WATCHDOG\WATCHDOG-20260730-1010.dmp
        \\?\C:\WINDOWS\SystemTemp\WER-548921-0.sysdata.xml
        \\?\C:\ProgramData\Microsoft\Windows\WER\Temp\WER.3265a40a-1cb2-40b1-9e80-7c1887b73829.tmp.WERInternalMetadata.xml</Data><Data Name='StorePath'>\\?\C:\ProgramData\Microsoft\Windows\WER\ReportQueue\Kernel_193_8c1eb645c8b68d3a67d3095e975de55c1d550e5_00000000_aed731f5-377d-4b0a-bc3f-fd4256d44e8e</Data><Data Name='AnalysisSymbol'></Data><Data Name='Rechecking'>0</Data><Data Name='ReportId'>aed731f5-377d-4b0a-bc3f-fd4256d44e8e</Data><Data Name='ReportStatus'>2051</Data><Data Name='HashedBucket'></Data><Data Name='CabGuid'>0</Data></EventData></Event>
        """;

    /// <summary>Recorded. LiveKernelEvent, P1 = 1a1 (WIN32K_CALLOUT_WATCHDOG_LIVEDUMP, a hang signature, not display evidence).</summary>
    public const string Wer1001LiveKernel1a1 = $$"""
        <Event xmlns='{{Ns}}'><System><Provider Name='Windows Error Reporting' Guid='{0ead09bd-2157-539a-8d6d-c87f95b64d70}'/><EventID>1001</EventID><Version>0</Version><Level>4</Level><Task>0</Task><Opcode>0</Opcode><Keywords>0x8000000000000000</Keywords><TimeCreated SystemTime='2026-10-02T08:43:33.3458012Z'/><EventRecordID>101184</EventRecordID><Correlation/><Execution ProcessID='43168' ThreadID='12684'/><Channel>Application</Channel><Computer>HOST.example.test</Computer><Security UserID='S-1-5-18'/></System><EventData><Data Name='Bucket'></Data><Data Name='BucketType'>0</Data><Data Name='EventName'>LiveKernelEvent</Data><Data Name='Response'>Not available</Data><Data Name='CabId'>0</Data><Data Name='P1'>1a1</Data><Data Name='P2'>ffff960205817300</Data><Data Name='P3'>0</Data><Data Name='P4'>0</Data><Data Name='P5'>0</Data><Data Name='P6'>10_0_28000</Data><Data Name='P7'>0_0</Data><Data Name='P8'>256_1</Data><Data Name='P9'></Data><Data Name='P10'></Data><Data Name='AttachedFiles'>
        \\?\C:\WINDOWS\LiveKernelReports\PoW32kWatchdog\PoW32kWatchdog-20260513-1846.dmp
        \\?\C:\WINDOWS\SystemTemp\WER-20312-0.sysdata.xml
        \\?\C:\WINDOWS\LiveKernelReports\PoW32kWatchdog-20260513-1846.dmp</Data><Data Name='StorePath'>\\?\C:\ProgramData\Microsoft\Windows\WER\ReportQueue\Kernel_1a1_13836555b1615c6671bfd6f722c636ca76116a_00000000_e5095a5a-1bee-4ea2-87f9-459deb7713cc</Data><Data Name='AnalysisSymbol'></Data><Data Name='Rechecking'>0</Data><Data Name='ReportId'>e5095a5a-1bee-4ea2-87f9-459deb7713cc</Data><Data Name='ReportStatus'>2051</Data><Data Name='HashedBucket'></Data><Data Name='CabGuid'>0</Data></EventData></Event>
        """;

    /// <summary>Recorded. APPCRASH: P1 application, P4 faulting module, P7 exception code.</summary>
    public const string Wer1001AppCrash = $$"""
        <Event xmlns='{{Ns}}'><System><Provider Name='Windows Error Reporting' Guid='{0ead09bd-2157-539a-8d6d-c87f95b64d70}'/><EventID>1001</EventID><Version>0</Version><Level>4</Level><Task>0</Task><Opcode>0</Opcode><Keywords>0x8000000000000000</Keywords><TimeCreated SystemTime='2026-09-30T08:46:49.3094095Z'/><EventRecordID>99882</EventRecordID><Correlation/><Execution ProcessID='33616' ThreadID='36764'/><Channel>Application</Channel><Computer>HOST.example.test</Computer><Security UserID='S-1-5-21-1000000000-1000000000-1000000000-1001'/></System><EventData><Data Name='Bucket'>2242631497190538527</Data><Data Name='BucketType'>4</Data><Data Name='EventName'>APPCRASH</Data><Data Name='Response'>Not available</Data><Data Name='CabId'>0</Data><Data Name='P1'>testhost.exe</Data><Data Name='P2'>17.1400.125.30202</Data><Data Name='P3'>67ac0000</Data><Data Name='P4'>coreclr.dll</Data><Data Name='P5'>10.0.1226.42308</Data><Data Name='P6'>6a89b724</Data><Data Name='P7'>c000001d</Data><Data Name='P8'>00000000003596cf</Data><Data Name='P9'></Data><Data Name='P10'></Data><Data Name='AttachedFiles'>
        \\?\C:\ProgramData\Microsoft\Windows\WER\Temp\WER.978b2804-c6bb-468f-a801-6eb84f5f52d4.tmp.dmp
        \\?\C:\ProgramData\Microsoft\Windows\WER\Temp\WER.804f1d45-bb43-4601-9f14-fbc9b70dcac3.tmp.WERInternalMetadata.xml</Data><Data Name='StorePath'>\\?\C:\ProgramData\Microsoft\Windows\WER\ReportArchive\AppCrash_testhost.exe_a141d96fafffb9abca227574d94f972ea83a1fbb_d8133b68_8b207cca-0886-4abe-b200-3c09e9b22e81</Data><Data Name='AnalysisSymbol'></Data><Data Name='Rechecking'>0</Data><Data Name='ReportId'>e28f09dd-6af6-4e72-b597-eb72d502a361</Data><Data Name='ReportStatus'>268435456</Data><Data Name='HashedBucket'>c89faeccf26e202a7f1f6d76f824fd1f</Data><Data Name='CabGuid'>0</Data></EventData></Event>
        """;

    /// <summary>Recorded. AppHangB1: P1 application; no faulting module.</summary>
    public const string Wer1001AppHangB1 = $$"""
        <Event xmlns='{{Ns}}'><System><Provider Name='Windows Error Reporting' Guid='{0ead09bd-2157-539a-8d6d-c87f95b64d70}'/><EventID>1001</EventID><Version>0</Version><Level>4</Level><Task>0</Task><Opcode>0</Opcode><Keywords>0x8000000000000000</Keywords><TimeCreated SystemTime='2026-09-14T11:54:09.4179246Z'/><EventRecordID>92084</EventRecordID><Correlation/><Execution ProcessID='30244' ThreadID='37804'/><Channel>Application</Channel><Computer>HOST.example.test</Computer><Security UserID='S-1-5-18'/></System><EventData><Data Name='Bucket'>1929432966505079360</Data><Data Name='BucketType'>5</Data><Data Name='EventName'>AppHangB1</Data><Data Name='Response'>Not available</Data><Data Name='CabId'>0</Data><Data Name='P1'>explorer.exe</Data><Data Name='P2'>10.0.28000.2952</Data><Data Name='P3'>b4886922</Data><Data Name='P4'>9b56</Data><Data Name='P5'>33554432</Data><Data Name='P6'></Data><Data Name='P7'></Data><Data Name='P8'></Data><Data Name='P9'></Data><Data Name='P10'></Data><Data Name='AttachedFiles'>
        \\?\C:\ProgramData\Microsoft\Windows\WER\Temp\WER.b0825dfe-49aa-4df3-bb62-8ad9b78f426d.tmp.WERInternalMetadata.xml</Data><Data Name='StorePath'>\\?\C:\ProgramData\Microsoft\Windows\WER\ReportArchive\AppHang_explorer.exe_a94e379d1bb4928aee39f5af7b9ac2219b6921e_ba1e8f87_dd9456c7-2afa-4c9b-b67a-5caf6c965546</Data><Data Name='AnalysisSymbol'></Data><Data Name='Rechecking'>0</Data><Data Name='ReportId'>2c108fb0-7e92-46dd-a3eb-4a60425e8b74</Data><Data Name='ReportStatus'>268435456</Data><Data Name='HashedBucket'>ddc27e92aa02e40a7ac6b90f7a76a240</Data><Data Name='CabGuid'>0</Data></EventData></Event>
        """;

    /// <summary>Recorded. CLR20r3 of the crash also recorded as <see cref="ApplicationError1000Named"/>: its ReportId is that record's IntegratorReportId.</summary>
    public const string Wer1001Clr20r3 = $$"""
        <Event xmlns='{{Ns}}'><System><Provider Name='Windows Error Reporting' Guid='{0ead09bd-2157-539a-8d6d-c87f95b64d70}'/><EventID>1001</EventID><Version>0</Version><Level>4</Level><Task>0</Task><Opcode>0</Opcode><Keywords>0x8000000000000000</Keywords><TimeCreated SystemTime='2026-09-30T21:07:22.8343190Z'/><EventRecordID>100509</EventRecordID><Correlation/><Execution ProcessID='28576' ThreadID='11316'/><Channel>Application</Channel><Computer>HOST.example.test</Computer><Security UserID='S-1-5-21-1000000000-1000000000-1000000000-1001'/></System><EventData><Data Name='Bucket'>1570137938940312820</Data><Data Name='BucketType'>5</Data><Data Name='EventName'>CLR20r3</Data><Data Name='Response'>Not available</Data><Data Name='CabId'>0</Data><Data Name='P1'>probe.exe</Data><Data Name='P2'>1.0.0.0</Data><Data Name='P3'>6a890000</Data><Data Name='P4'>System.Management</Data><Data Name='P5'>10.0.25.52411</Data><Data Name='P6'>eed6935d</Data><Data Name='P7'>e8</Data><Data Name='P8'>3f</Data><Data Name='P9'>System.Management.Management</Data><Data Name='P10'></Data><Data Name='AttachedFiles'>
        \\?\C:\ProgramData\Microsoft\Windows\WER\Temp\WER.36978f1c-d3ee-4f7f-8f3c-f6f59c0f2be7.tmp.dmp
        \\?\C:\ProgramData\Microsoft\Windows\WER\Temp\WER.aead0c1b-28c0-4f22-8a7b-3993e42c7e94.tmp.WERInternalMetadata.xml</Data><Data Name='StorePath'>\\?\C:\ProgramData\Microsoft\Windows\WER\ReportArchive\AppCrash_probe.exe_5b6d16f2239079adcae6c5fe62db551f6724_09625213_279c417a-ebf9-4cdb-97d1-3f8c5ec3001d</Data><Data Name='AnalysisSymbol'></Data><Data Name='Rechecking'>0</Data><Data Name='ReportId'>d25f62a3-ce7f-4702-8453-9bd1d0e36482</Data><Data Name='ReportStatus'>268435456</Data><Data Name='HashedBucket'>e450560e237eff4ca5ca4022f2e6bcf4</Data><Data Name='CabGuid'>0</Data></EventData></Event>
        """;

    /// <summary>Recorded. BEX64 is not one of the event names the contract maps, so it stays kind <c>wer</c> with its name kept.</summary>
    public const string Wer1001Bex64 = $$"""
        <Event xmlns='{{Ns}}'><System><Provider Name='Windows Error Reporting' Guid='{0ead09bd-2157-539a-8d6d-c87f95b64d70}'/><EventID>1001</EventID><Version>0</Version><Level>4</Level><Task>0</Task><Opcode>0</Opcode><Keywords>0x8000000000000000</Keywords><TimeCreated SystemTime='2026-09-21T12:07:44.2879621Z'/><EventRecordID>95873</EventRecordID><Correlation/><Execution ProcessID='6672' ThreadID='6664'/><Channel>Application</Channel><Computer>HOST.example.test</Computer><Security UserID='S-1-5-18'/></System><EventData><Data Name='Bucket'>1749660973297870284</Data><Data Name='BucketType'>5</Data><Data Name='EventName'>BEX64</Data><Data Name='Response'>Not available</Data><Data Name='CabId'>0</Data><Data Name='P1'>MsMpEng.exe</Data><Data Name='P2'>4.18.26080.4</Data><Data Name='P3'>348553e0</Data><Data Name='P4'>mpengine.dll</Data><Data Name='P5'>1.1.26080.3</Data><Data Name='P6'>01b1f900</Data><Data Name='P7'>000000000005b183</Data><Data Name='P8'>c0000409</Data><Data Name='P9'>0000000000000039</Data><Data Name='P10'></Data><Data Name='AttachedFiles'>
        \\?\C:\ProgramData\Microsoft\Windows\WER\Temp\WER.9f6eba7a-9c0f-4827-bfca-e46d6c310edf.tmp.WERInternalMetadata.xml</Data><Data Name='StorePath'>\\?\C:\ProgramData\Microsoft\Windows\WER\ReportArchive\AppCrash_MsMpEng.exe_de8c33c175622678ef5b4acd726e93ed718e7b3_8a9a605b_17c87167-783f-4c28-862c-42e654820297</Data><Data Name='AnalysisSymbol'></Data><Data Name='Rechecking'>0</Data><Data Name='ReportId'>bbd17798-6408-40f8-911a-3f96144d5db0</Data><Data Name='ReportStatus'>268435456</Data><Data Name='HashedBucket'>11edad019ec31d0508480b6296c8a5cc</Data><Data Name='CabGuid'>0</Data></EventData></Event>
        """;

    // ---- Application Error 1000 ----

    /// <summary>Recorded (AppPath anonymized). ProcessId 0xf04 = 3844; IntegratorReportId is the ReportId of <see cref="Wer1001Clr20r3"/>.</summary>
    public const string ApplicationError1000Named = $$"""
        <Event xmlns='{{Ns}}'><System><Provider Name='Application Error' Guid='{a0e9b465-b939-57d7-b27d-95d8e925ff57}'/><EventID>1000</EventID><Version>0</Version><Level>2</Level><Task>100</Task><Opcode>0</Opcode><Keywords>0x8000000000000000</Keywords><TimeCreated SystemTime='2026-09-30T21:07:20.8211183Z'/><EventRecordID>100508</EventRecordID><Correlation/><Execution ProcessID='28576' ThreadID='11316'/><Channel>Application</Channel><Computer>HOST.example.test</Computer><Security UserID='S-1-5-21-1000000000-1000000000-1000000000-1001'/></System><EventData><Data Name='AppName'>probe.exe</Data><Data Name='AppVersion'>1.0.0.0</Data><Data Name='AppTimeStamp'>6a890000</Data><Data Name='ModuleName'>KERNELBASE.dll</Data><Data Name='ModuleVersion'>10.0.28000.2952</Data><Data Name='ModuleTimeStamp'>be1f9640</Data><Data Name='ExceptionCode'>e0434352</Data><Data Name='FaultingOffset'>00000000000c80ec</Data><Data Name='ProcessId'>0xf04</Data><Data Name='ProcessCreationTime'>0x1dd511fae7fe93b</Data><Data Name='AppPath'>C:\Users\user\AppData\Local\Temp\probe\probe.exe</Data><Data Name='ModulePath'>C:\WINDOWS\System32\KERNELBASE.dll</Data><Data Name='IntegratorReportId'>d25f62a3-ce7f-4702-8453-9bd1d0e36482</Data><Data Name='PackageFullName'></Data><Data Name='PackageRelativeAppId'></Data></EventData></Event>
        """;

    /// <summary>Synthetic: <see cref="ApplicationError1000Named"/> with its field names removed, as an older provider manifest writes it.</summary>
    public static string ApplicationError1000Unnamed => Unnamed(ApplicationError1000Named);

    /// <summary>Synthetic: <see cref="Wer1001BlueScreen50"/> with its field names removed.</summary>
    public static string Wer1001BlueScreen50Unnamed => Unnamed(Wer1001BlueScreen50);

    /// <summary>Synthetic: <see cref="Wer1001AppCrash"/> with its field names removed.</summary>
    public static string Wer1001AppCrashUnnamed => Unnamed(Wer1001AppCrash);

    /// <summary>Synthetic: a LiveKernelEvent of the recorded 0x193 shape with another code and report id.</summary>
    public static string Wer1001LiveKernel(string code, string reportId, DateTimeOffset time, long recordId = 1) =>
        Wer1001LiveKernel193
            .Replace("<Data Name='P1'>193</Data>", $"<Data Name='P1'>{code}</Data>", StringComparison.Ordinal)
            .Replace("aed731f5-377d-4b0a-bc3f-fd4256d44e8e", reportId, StringComparison.Ordinal)
            .Replace("2026-10-02T08:43:34.0533512Z", time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", global::System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("<EventRecordID>101197</EventRecordID>", $"<EventRecordID>{recordId}</EventRecordID>", StringComparison.Ordinal);

    // ---- System log ----

    /// <summary>Recorded. Kernel-Power 41 with BugcheckCode 0 and no power-button press.</summary>
    public const string KernelPower41 = $$"""
        <Event xmlns='{{Ns}}'><System><Provider Name='Microsoft-Windows-Kernel-Power' Guid='{331c3b3a-2005-44c2-ac5e-77220c37d6b4}'/><EventID>41</EventID><Version>9</Version><Level>1</Level><Task>63</Task><Opcode>0</Opcode><Keywords>0x8000400000000002</Keywords><TimeCreated SystemTime='2026-09-30T18:42:00.1234567Z'/><EventRecordID>500001</EventRecordID><Correlation/><Execution ProcessID='4' ThreadID='8'/><Channel>System</Channel><Computer>HOST.example.test</Computer><Security UserID='S-1-5-18'/></System><EventData><Data Name='BugcheckCode'>0</Data><Data Name='BugcheckParameter1'>0x0</Data><Data Name='BugcheckParameter2'>0x0</Data><Data Name='BugcheckParameter3'>0x0</Data><Data Name='BugcheckParameter4'>0x0</Data><Data Name='SleepInProgress'>0</Data><Data Name='PowerButtonTimestamp'>0</Data><Data Name='BootAppStatus'>0</Data><Data Name='Checkpoint'>0</Data><Data Name='ConnectedStandbyInProgress'>false</Data><Data Name='SystemSleepTransitionsToOn'>0</Data><Data Name='CsEntryScenarioInstanceId'>10</Data><Data Name='BugcheckInfoFromEFI'>false</Data><Data Name='CheckpointStatus'>0</Data><Data Name='CsEntryScenarioInstanceIdV2'>10</Data><Data Name='LongPowerButtonPressDetected'>false</Data><Data Name='LidReliability'>false</Data><Data Name='InputSuppressionState'>0</Data><Data Name='PowerButtonSuppressionState'>0</Data><Data Name='LidState'>1</Data><Data Name='WHEABootErrorCount'>0</Data></EventData></Event>
        """;

    /// <summary>Recorded values (BugcheckCode 268435583 = 0x1000007f, a non-zero PowerButtonTimestamp) in the shape of <see cref="KernelPower41"/>.</summary>
    public static string KernelPower41Bugcheck =>
        KernelPower41
            .Replace("<Data Name='BugcheckCode'>0</Data>", "<Data Name='BugcheckCode'>268435583</Data>", StringComparison.Ordinal)
            .Replace("<Data Name='PowerButtonTimestamp'>0</Data>", "<Data Name='PowerButtonTimestamp'>134333208460756571</Data>", StringComparison.Ordinal)
            .Replace("<EventRecordID>500001</EventRecordID>", "<EventRecordID>500002</EventRecordID>", StringComparison.Ordinal);

    /// <summary>Recorded (unnamed data, localized time text is never parsed). EventLog 6008.</summary>
    public const string EventLog6008 = $$"""
        <Event xmlns='{{Ns}}'><System><Provider Name='EventLog'/><EventID Qualifiers='32768'>6008</EventID><Version>0</Version><Level>2</Level><Task>0</Task><Opcode>0</Opcode><Keywords>0x80000000000000</Keywords><TimeCreated SystemTime='2026-09-30T18:42:14.0000000Z'/><EventRecordID>500003</EventRecordID><Correlation/><Execution ProcessID='0' ThreadID='0'/><Channel>System</Channel><Computer>HOST.example.test</Computer><Security/></System><EventData><Data>20:15:08</Data><Data>30/09/2026</Data><Data></Data><Data></Data><Data>40520</Data><Data></Data><Data></Data><Binary>EA07090003001E0014000F0008000A00</Binary></EventData></Event>
        """;

    /// <summary>Recorded. The System-log bugcheck record: param1 carries the code as its leading 0x token.</summary>
    public const string SystemBugcheck1001 = $$"""
        <Event xmlns='{{Ns}}'><System><Provider Name='Microsoft-Windows-WER-SystemErrorReporting' Guid='{abce23e7-de45-4366-8631-84fa6c525952}' EventSourceName='BugCheck'/><EventID Qualifiers='16384'>1001</EventID><Version>0</Version><Level>2</Level><Task>0</Task><Opcode>0</Opcode><Keywords>0x80000000000000</Keywords><TimeCreated SystemTime='2026-09-04T08:45:24.0000000Z'/><EventRecordID>500004</EventRecordID><Correlation/><Execution ProcessID='0' ThreadID='0'/><Channel>System</Channel><Computer>HOST.example.test</Computer><Security/></System><EventData><Data Name='param1'>0x00000050 (0xfffff801013caad3, 0x0000000000000010, 0xfffff8017ba5f4a1, 0x0000000000000002)</Data><Data Name='param2'>C:\WINDOWS\Minidump\090426-53171-01.dmp</Data><Data Name='param3'>07e3c759-e445-4da7-8efa-2694d7dea04c</Data></EventData></Event>
        """;

    /// <summary>Recorded (RawData shortened). WHEA-Logger 19, a corrected machine check, logged at warning level.</summary>
    public const string Whea19Corrected = $$"""
        <Event xmlns='{{Ns}}'><System><Provider Name='Microsoft-Windows-WHEA-Logger' Guid='{c26c4f3c-3f66-4e99-8f8a-39405cfed220}'/><EventID>19</EventID><Version>0</Version><Level>3</Level><Task>0</Task><Opcode>0</Opcode><Keywords>0x8000000000000000</Keywords><TimeCreated SystemTime='2026-10-01T21:28:39.0000000Z'/><EventRecordID>500005</EventRecordID><Correlation/><Execution ProcessID='4' ThreadID='100'/><Channel>System</Channel><Computer>HOST.example.test</Computer><Security UserID='S-1-5-19'/></System><EventData><Data Name='ErrorSource'>1</Data><Data Name='ApicId'>1</Data><Data Name='MCABank'>0</Data><Data Name='MciStat'>0x8000004000050005</Data><Data Name='MciAddr'>0x0</Data><Data Name='MciMisc'>0x0</Data><Data Name='ErrorType'>12</Data><Data Name='Length'>2799</Data><Data Name='RawData'>435045521002FFFFFFFF</Data></EventData></Event>
        """;

    /// <summary>Synthetic, recorded shape of <see cref="Whea19Corrected"/>: WHEA-Logger 18, a fatal hardware error, logged at error level after the restart.</summary>
    public static string Whea18Fatal =>
        Whea19Corrected
            .Replace("<EventID>19</EventID>", "<EventID>18</EventID>", StringComparison.Ordinal)
            .Replace("<Level>3</Level>", "<Level>2</Level>", StringComparison.Ordinal)
            .Replace("<EventRecordID>500005</EventRecordID>", "<EventRecordID>500006</EventRecordID>", StringComparison.Ordinal);

    /// <summary>Synthetic, recorded shape of <see cref="Whea19Corrected"/> at the informational level: neither corrected nor fatal is proven.</summary>
    public static string WheaInformational =>
        Whea19Corrected
            .Replace("<EventID>19</EventID>", "<EventID>46</EventID>", StringComparison.Ordinal)
            .Replace("<Level>3</Level>", "<Level>4</Level>", StringComparison.Ordinal)
            .Replace("<EventRecordID>500005</EventRecordID>", "<EventRecordID>500007</EventRecordID>", StringComparison.Ordinal);

    /// <summary>Synthetic. Display 4101 (the display driver stopped responding and recovered): its first data value is the driver.</summary>
    public const string Display4101 = $$"""
        <Event xmlns='{{Ns}}'><System><Provider Name='Display'/><EventID Qualifiers='0'>4101</EventID><Version>0</Version><Level>3</Level><Task>0</Task><Opcode>0</Opcode><Keywords>0x80000000000000</Keywords><TimeCreated SystemTime='2026-09-29T10:00:00.0000000Z'/><EventRecordID>500008</EventRecordID><Correlation/><Execution ProcessID='0' ThreadID='0'/><Channel>System</Channel><Computer>HOST.example.test</Computer><Security/></System><EventData><Data>nvlddmkm</Data><Data></Data></EventData></Event>
        """;

    /// <summary>Synthetic. disk 153 (an I/O operation was retried): its first data value is the device.</summary>
    public const string Disk153 = $$"""
        <Event xmlns='{{Ns}}'><System><Provider Name='disk'/><EventID Qualifiers='32772'>153</EventID><Version>0</Version><Level>3</Level><Task>0</Task><Opcode>0</Opcode><Keywords>0x80000000000000</Keywords><TimeCreated SystemTime='2026-09-28T10:00:00.0000000Z'/><EventRecordID>500009</EventRecordID><Correlation/><Execution ProcessID='4' ThreadID='0'/><Channel>System</Channel><Computer>HOST.example.test</Computer><Security/></System><EventData><Data>\Device\Harddisk1\DR1</Data><Data>0x15d08b000</Data><Data>1</Data></EventData></Event>
        """;

    /// <summary>Synthetic. stornvme 129 (a reset was issued to the device): its first data value is the port.</summary>
    public const string Stornvme129 = $$"""
        <Event xmlns='{{Ns}}'><System><Provider Name='stornvme'/><EventID Qualifiers='32772'>129</EventID><Version>0</Version><Level>3</Level><Task>0</Task><Opcode>0</Opcode><Keywords>0x80000000000000</Keywords><TimeCreated SystemTime='2026-09-28T11:00:00.0000000Z'/><EventRecordID>500010</EventRecordID><Correlation/><Execution ProcessID='4' ThreadID='0'/><Channel>System</Channel><Computer>HOST.example.test</Computer><Security/></System><EventData><Data>\Device\RaidPort0</Data></EventData></Event>
        """;

    /// <summary>
    /// Recorded keys of the Report.wer of the <see cref="Wer1001Clr20r3"/> crash, written the way WER writes the file (UTF-16 LE with
    /// a byte-order mark), padded with <paramref name="loadedModules"/> <c>LoadedModule[n]</c> lines after the header so a test can push
    /// it past the old 32 KiB limit.
    /// </summary>
    public static byte[] ReportWer(int loadedModules = 0, bool withEventTime = true, string reportIdentifier = "279c417a-ebf9-4cdb-97d1-3f8c5ec3001d", string integrator = "d25f62a3-ce7f-4702-8453-9bd1d0e36482")
    {
        var text = new StringBuilder();
        text.Append("Version=1\r\nEventType=CLR20r3\r\n");
        if (withEventTime)
        {
            text.Append("EventTime=134352760409584853\r\n");
        }

        text.Append("ReportType=2\r\nConsent=1\r\nUploadTime=134352760415364815\r\nReportStatus=268435456\r\n");
        text.Append("ReportIdentifier=").Append(reportIdentifier).Append("\r\n");
        text.Append("IntegratorReportIdentifier=").Append(integrator).Append("\r\n");
        text.Append("Wow64Host=34404\r\nNsAppName=probe.exe\r\nOriginalFilename=probe.dll\r\n");
        text.Append("Response.BucketId=e450560e237eff4ca5ca4022f2e6bcf4\r\nResponse.BucketTable=5\r\nResponse.LegacyBucketId=1570137938940312820\r\nResponse.type=4\r\n");
        string[] sig = ["probe.exe", "1.0.0.0", "6a890000", "System.Management", "10.0.25.52411", "eed6935d", "e8", "3f", "System.Management.Management"];
        for (var index = 0; index < sig.Length; index++)
        {
            text.Append("Sig[").Append(index).Append("].Name=Problem signature ").Append(index + 1).Append("\r\n");
            text.Append("Sig[").Append(index).Append("].Value=").Append(sig[index]).Append("\r\n");
        }

        text.Append("DynamicSig[1].Name=OS Version\r\nDynamicSig[1].Value=10.0.28000.2.0.0.256.48\r\n");
        for (var index = 0; index < loadedModules; index++)
        {
            text.Append("LoadedModule[").Append(index).Append("]=C:\\WINDOWS\\System32\\module").Append(index).Append(".dll\r\n");
        }

        text.Append("FriendlyEventName=Stopped working\r\nAppName=probe\r\nAppPath=C:\\Users\\user\\AppData\\Local\\Temp\\probe\\probe.exe\r\n");
        return [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text.ToString())];
    }

    private static string Unnamed(string xml) =>
        global::System.Text.RegularExpressions.Regex.Replace(xml, "<Data Name='[^']*'>", "<Data>");
}
