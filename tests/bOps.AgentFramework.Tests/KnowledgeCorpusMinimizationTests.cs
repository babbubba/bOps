// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Sys.Core;

namespace bOps.AgentFramework.Tests;

/// <summary>Offline checks of the corpus minimization (D2): what a diagnosis keeps and what must never reach a frozen corpus.</summary>
public sealed class KnowledgeCorpusMinimizationTests
{
    private static readonly string[] RequiredProviders =
        ["Microsoft-Windows-Kernel-Power", "Microsoft-Windows-WHEA-Logger", "Application Error", "Windows Error Reporting", "EventLog", ".NET Runtime"];

    private static JsonObject Minimized(string? output = null)
    {
        var (corpus, _, _) = CorpusFixtures.Captured(output);
        return JsonNode.Parse(corpus["output"]!.GetValue<string>())!.AsObject();
    }

    private static JsonObject Event(JsonObject output, string provider, string eventId) =>
        output["events"]!.AsArray().OfType<JsonObject>().Single(record =>
            record["source"]!.GetValue<string>() == provider && record["eventId"]!.GetValue<string>() == eventId);

    [Fact]
    public void AllowlistedProvidersAndStructuredDetails_ArePreserved()
    {
        var output = Minimized();

        var power = Event(output, "Microsoft-Windows-Kernel-Power", "41");
        Assert.Equal("critical", power["severity"]!.GetValue<string>());
        Assert.Equal("System", power["channel"]!.GetValue<string>());
        Assert.Equal("1970-01-01T23:50:00.0000000Z", power["timestampUtc"]!.GetValue<string>());
        Assert.Equal("159", power["details"]!["bugcheckCode"]!.GetValue<string>());

        var whea = Event(output, "Microsoft-Windows-WHEA-Logger", "18")["details"]!;
        Assert.Equal("fatal", whea["errorClass"]!.GetValue<string>());
        Assert.Equal("Processor Core", whea["component"]!.GetValue<string>());
        Assert.Equal("Machine Check Exception", whea["errorSource"]!.GetValue<string>());

        var crash = Event(output, "Application Error", "1000")["details"]!;
        Assert.Equal("contoso.exe", crash["applicationName"]!.GetValue<string>());
        Assert.Equal("KERNELBASE.dll", crash["moduleName"]!.GetValue<string>());
        Assert.Equal("0xe0434352", crash["exceptionCode"]!.GetValue<string>());
        Assert.Equal("0x000000000003b3d2", crash["faultOffset"]!.GetValue<string>());

        var report = Event(output, "Windows Error Reporting", "1001")["details"]!;
        Assert.Equal("APPCRASH", report["eventName"]!.GetValue<string>());
        Assert.Equal("5", report["faultBucketType"]!.GetValue<string>());

        var shutdown = Event(output, "EventLog", "6008");
        Assert.Null(shutdown["details"]); // The message carries only a localized time; nothing structured is invented from it.

        var runtime = Event(output, ".NET Runtime", "1026")["details"]!;
        Assert.Equal("contoso.exe", runtime["applicationName"]!.GetValue<string>());
        Assert.Equal("v4.0.30319", runtime["frameworkVersion"]!.GetValue<string>());
        Assert.Equal("System.NullReferenceException", runtime["exceptionType"]!.GetValue<string>());
    }

    [Fact]
    public void SensitiveContentAndUnknownProviders_AreRedacted()
    {
        var output = Minimized();
        var json = output.ToJsonString();

        foreach (var secret in new[] { "jdoe", "C:\\\\Users", "C:\\Users", "AppData", "WORKSTATION-77", "10.20.30.40", "192.168.1.20",
                     "contoso.example", "intranet", "sk-test-123456", "hunter2", "Contoso.Secret", "x.cs", "Vault", "memory.hdmp",
                     "Contoso-Internal-Agent", "Another-Private-Source", "Faulting application path", "Report Id" })
        {
            Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);
        }

        Assert.All(output["events"]!.AsArray().OfType<JsonObject>(), record =>
        {
            Assert.Null(record["message"]);
            Assert.Null(record["processId"]);
            Assert.Null(record["processName"]);
            Assert.Null(record["unit"]);
        });
        // Unknown providers are numbered by first appearance (oldest event first as emitted), and one provider keeps one number.
        var unknown = output["events"]!.AsArray().OfType<JsonObject>()
            .Where(record => CorpusMinimizer.IsRedactedProvider(record["source"]!.GetValue<string>())).ToArray();
        Assert.Equal(3, unknown.Length);
        Assert.Equal(2, unknown.Select(record => record["source"]!.GetValue<string>()).Distinct().Count());
        Assert.All(unknown, record => Assert.Null(record["details"]));
        Assert.Equal(CorpusMinimizer.Version, output["benchmarkCorpus"]!["minimization"]!.GetValue<string>());
    }

    [Fact]
    public void DetailsAreNeverInvented_AndAValueOutsideItsShapeIsDropped()
    {
        var events = new[]
        {
            // A reworded/localized message: no label matches, so no detail exists.
            CorpusFixtures.Record(5, SystemEventSeverity.Error, "Application Error", "1000", "Application", "Applicazione in errore: contoso.exe, codice 0xc0000005"),
            // Labels present, but the values are a path, a non-hex code and a script: none passes its shape.
            CorpusFixtures.Record(6, SystemEventSeverity.Error, "Application Error", "1000", "Application",
                "Faulting application name: C:\\Users\\jdoe\\x.exe, version: 1\r\nFaulting module name: <script>.dll\r\nException code: 0xZZ\r\nFault offset: zz"),
            // Only one field is present: only that one is kept.
            CorpusFixtures.Record(7, SystemEventSeverity.Error, "Application Error", "1000", "Application", "Exception code: 0xc0000005"),
            // A real label under an event ID the rules do not know is not extracted.
            CorpusFixtures.Record(8, SystemEventSeverity.Error, "Application Error", "9999", "Application", "Exception code: 0xc0000005"),
        };
        var output = Minimized(CorpusFixtures.Output(events));

        var records = output["events"]!.AsArray().OfType<JsonObject>().OrderBy(record => record["timestampUtc"]!.GetValue<string>(), StringComparer.Ordinal).ToArray();
        Assert.Equal(4, records.Length);
        Assert.All(records, record => Assert.Equal("Application Error", record["source"]!.GetValue<string>()));
        var byId = records.GroupBy(record => record["eventId"]!.GetValue<string>()).ToDictionary(g => g.Key, g => g.ToArray());
        Assert.Equal(3, byId["1000"].Length);
        Assert.Single(byId["9999"]);
        Assert.Null(byId["9999"][0]["details"]);
        var withDetails = byId["1000"].Where(record => record["details"] is not null).ToArray();
        var only = Assert.Single(withDetails);
        Assert.Equal("0xc0000005", only["details"]!["exceptionCode"]!.GetValue<string>());
        Assert.Single(only["details"]!.AsObject());
    }

    [Fact]
    public void Minimization_IsDeterministicAndIdempotent()
    {
        var output = CorpusFixtures.Output(CorpusFixtures.DiagnosticEvents());
        var first = CorpusFixtures.Captured(output).Corpus["output"]!.GetValue<string>();
        var second = CorpusFixtures.Captured(output).Corpus["output"]!.GetValue<string>();
        Assert.Equal(first, second);

        // A stored corpus is rebuilt from its own sanitized output when read: applying the transformation again must change nothing.
        var again = JsonNode.Parse(first)!.AsObject();
        CorpusMinimizer.Apply(again);
        Assert.Equal(first, CanaryEventsCorpus.Canonical(again));
    }

    [Fact]
    public void AggregateGroups_AreMinimizedWithTheSameRules()
    {
        var root = new JsonObject
        {
            ["groups"] = new JsonArray(new JsonObject
            {
                ["channel"] = "Application", ["source"] = "Application Error", ["unit"] = null, ["eventId"] = "1000", ["severity"] = "error", ["count"] = 3,
                ["sampleMessage"] = CorpusFixtures.AppErrorMessage, ["sampleMessageTruncated"] = false,
            }, new JsonObject
            {
                ["channel"] = "Security", ["source"] = "Private Provider", ["eventId"] = "not-a-number", ["severity"] = "bogus", ["sampleMessage"] = "secret",
            }),
            ["sources"] = new JsonArray(new JsonObject { ["name"] = "windows.channel.System", ["status"] = "available", ["detail"] = "C:\\secret" }),
        };
        CorpusMinimizer.Apply(root);

        var known = root["groups"]![0]!;
        Assert.Equal("Application Error", known["source"]!.GetValue<string>());
        Assert.Equal("0xe0434352", known["details"]!["exceptionCode"]!.GetValue<string>());
        Assert.Null(known["sampleMessage"]);
        var other = root["groups"]![1]!;
        Assert.Equal("[redacted-provider-1]", other["source"]!.GetValue<string>());
        Assert.Null(other["channel"]);
        Assert.Null(other["eventId"]);
        Assert.Null(other["severity"]);
        Assert.Null(other["sampleMessage"]);
        Assert.Equal("windows.channel.System", root["sources"]![0]!["name"]!.GetValue<string>());
        Assert.Null(root["sources"]![0]!["detail"]);
    }

    [Fact]
    public void TheRequiredDiagnosticProviders_AreOnTheExplicitAllowlist()
    {
        Assert.Equal(RequiredProviders, CorpusMinimizer.ProviderAllowlist.Take(6));
        Assert.False(CorpusMinimizer.IsRetainedProvider("Contoso-Internal-Agent"));
        Assert.False(CorpusMinimizer.IsRetainedProvider(null));
        Assert.True(CorpusMinimizer.IsRetainedProvider("eventlog")); // Matching ignores case; the stored spelling is the canonical one.
        Assert.Equal("EventLog", Event(Minimized(CorpusFixtures.Output(
            [CorpusFixtures.Record(5, SystemEventSeverity.Error, "EVENTLOG", "6008", "System", "x")])), "EventLog", "6008")["source"]!.GetValue<string>());
    }
}
