// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Knowledge.Tests;
using bOps.Packages.Sys.Core;

namespace bOps.AgentFramework.Tests;

/// <summary>Offline stand-ins for what the native tool returns. Nothing here reads an Event Log; every value is invented test data.</summary>
internal static class CorpusFixtures
{
    internal static DateTimeOffset Now { get; } = DateTimeOffset.UnixEpoch.AddDays(1);

    // Deliberately carries paths, a user name, an address, a machine name, a command line and an e-mail next to the diagnostic fields.
    internal const string AppErrorMessage =
        "Faulting application name: contoso.exe, version: 1.0.0.0, time stamp: 0x5f3a1c2b\r\n" +
        "Faulting module name: KERNELBASE.dll, version: 10.0.19041.1, time stamp: 0x9a3b1c2d\r\n" +
        "Exception code: 0xe0434352\r\nFault offset: 0x000000000003b3d2\r\nFaulting process id: 0x1a2c\r\n" +
        "Faulting application path: C:\\Users\\jdoe\\AppData\\Local\\Contoso\\contoso.exe\r\n" +
        "Faulting module path: C:\\Windows\\System32\\KERNELBASE.dll\r\nReport Id: 7e1d-WORKSTATION-77-jdoe@contoso.example";

    internal static SystemEventRecord[] DiagnosticEvents() =>
    [
        Record(10, SystemEventSeverity.Critical, "Microsoft-Windows-Kernel-Power", "41", "System",
            "The system has rebooted without cleanly shutting down first. BugcheckCode 159 for \\\\WORKSTATION-77 by jdoe"),
        Record(20, SystemEventSeverity.Error, "Microsoft-Windows-WHEA-Logger", "18", "System",
            "A fatal hardware error has occurred.\r\n\r\nReported by component: Processor Core\r\nError Source: Machine Check Exception\r\n" +
            "Primary Bus:Device:Function: 0x0:0x0:0x0\r\nUser: CONTOSO\\jdoe from 10.20.30.40"),
        Record(30, SystemEventSeverity.Error, "Application Error", "1000", "Application", AppErrorMessage),
        Record(40, SystemEventSeverity.Warning, "Windows Error Reporting", "1001", "Application",
            "Fault bucket 1234567, type 5\r\nEvent Name: APPCRASH\r\nResponse: Not available\r\nCab Id: 0\r\nProblem signature:\r\nP1: contoso.exe\r\n" +
            "Attached files:\r\n\\\\?\\C:\\Users\\jdoe\\AppData\\Local\\Temp\\WER\\memory.hdmp"),
        Record(50, SystemEventSeverity.Error, "EventLog", "6008", "System",
            "The previous system shutdown at 10:12:11 PM on 10/12/2026 was unexpected. Host WORKSTATION-77."),
        Record(60, SystemEventSeverity.Error, ".NET Runtime", "1026", "Application",
            "Application: contoso.exe\r\nFramework Version: v4.0.30319\r\nDescription: The process was terminated due to an unhandled exception.\r\n" +
            "Exception Info: System.NullReferenceException\r\n   at Contoso.Secret.Run(String path) in C:\\src\\Vault\\x.cs:line 3 password=hunter2"),
        Record(70, SystemEventSeverity.Warning, "Contoso-Internal-Agent", "7", "System",
            "Token sk-test-123456 sent to https://intranet.contoso.example/api?user=jdoe"),
        Record(80, SystemEventSeverity.Warning, "Another-Private-Source", "9", "Application", "jdoe logged in from 192.168.1.20"),
        Record(90, SystemEventSeverity.Warning, "Contoso-Internal-Agent", "8", "System", "second event of the same unknown provider"),
    ];

    internal static SystemEventRecord Record(int minutesAgo, SystemEventSeverity severity, string provider, string eventId, string channel, string message) =>
        new(Now.AddMinutes(-minutesAgo), severity, provider, eventId, channel, message, 4242, "contoso.exe");

    /// <summary>The native tool's JSON for <paramref name="events"/>, with controllable source status, coverage and truncation.</summary>
    internal static string Output(IReadOnlyList<SystemEventRecord> events, bool truncated = false,
        InventorySourceStatus system = InventorySourceStatus.Available, InventorySourceStatus application = InventorySourceStatus.Available,
        bool includeApplication = true, bool deepHistory = true, ToolArguments? arguments = null)
    {
        SystemEventsArguments.TryRead(arguments ?? KnowledgeBenchmark.Arguments, Now, out var query, out var mode, out var limit, out var bytes, out _);
        var oldest = deepHistory ? Now.AddDays(-30) : Now.AddMinutes(-10);
        var sources = new List<InventorySourceResult> { new("windows.channel.System", system, null) };
        var stores = new List<CoverageStore> { new("windows.channel.System", CoverageBasis.EventLog, oldest, null, ["windows.channel.System"]) };
        if (includeApplication)
        {
            sources.Add(new("windows.channel.Application", application, null));
            stores.Add(new("windows.channel.Application", CoverageBasis.EventLog, oldest, null, ["windows.channel.Application"]));
        }

        var snapshot = new SystemEventSnapshot(events, sources, truncated) { Stores = stores };
        return SystemEventFormatting.Format(snapshot, query!, mode, limit, bytes);
    }

    /// <summary>What the trusted native capture would have stored for <paramref name="output"/> (the same code path, offline input).</summary>
    internal static (JsonObject Corpus, string Hash, CorpusQuality Quality) Captured(string? output = null, ToolArguments? arguments = null) =>
        CanaryEventsCorpus.BuildCapture(arguments ?? KnowledgeBenchmark.Arguments, output ?? Output(DiagnosticEvents()), Guid.NewGuid().ToString("D"), Now);

    /// <summary>Writes the corpus where the benchmark expects it; a distinct <paramref name="name"/> allows several files per directory.</summary>
    internal static string Write(TempDirectory temporary, JsonObject corpus, string name = "frozen")
    {
        var directory = temporary.Sub(".bops-event-corpus");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name + CorpusAttestation.CorpusSuffix);
        File.WriteAllBytes(path, CanaryEventsCorpus.Encode(corpus, CanaryEventsCorpus.Hash(corpus)));
        return path;
    }

    /// <summary>A saved, quality-passing corpus approved by the operator: path, corpus hash and attestation hash.</summary>
    internal static (string Path, string Hash, string Attestation) Approved(TempDirectory temporary, string name = "frozen")
    {
        var (corpus, hash, quality) = Captured();
        Assert.True(quality.Suitable, quality.Describe());
        var path = Write(temporary, corpus, name);
        return (path, hash, CorpusAttestation.Approve(path, hash, hash, Now.AddHours(1)));
    }
}
