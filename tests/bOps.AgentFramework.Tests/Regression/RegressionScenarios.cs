// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Sys.Core;

namespace bOps.AgentFramework.Tests.Regression;

/// <summary>One tool of a regression scenario: a fixed persisted result, facts included.</summary>
internal sealed record ScenarioTool(string Name, ToolCallResult Result);

/// <summary>
/// One piece of evidence the final reasoning path must be able to see. It is visible when ANY marker occurs in the text the
/// model was offered on its final call (system prompt, grounding, limitations, history, evidence-read replies).
/// </summary>
internal sealed record ExpectedEvidence(string Label, params string[] Markers);

/// <summary>
/// A deterministic regression workload: the tools, the order the plan lists them, the evidence re-reads the scripted model asks
/// for before a step, and what the final answer must still be able to ground (DETERMINISTIC FIXTURE, never a live model).
/// </summary>
internal sealed record RegressionScenario(
    string Name,
    string Goal,
    IReadOnlyList<ScenarioTool> Tools,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ReadsBefore,
    IReadOnlyList<ExpectedEvidence> ExpectedFacts,
    IReadOnlyList<ExpectedEvidence> ExpectedLimitations,
    bool ModelIgnoresExhaustion = false);

/// <summary>
/// Builds the scenarios. The c508-equivalent workload reproduces the SEMANTIC pressure of the historical task
/// c508f188-3306-4e29-bf86-118925ced418 (large outputs, partial/truncated evidence, high-density facts, repeated EvidenceRead,
/// later CPU/memory/storage work). The numbers below are regression evidence, not product constants.
/// </summary>
internal static class RegressionScenarios
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The c508-equivalent diagnostic.</summary>
    internal static RegressionScenario C508Equivalent()
    {
        var stability = Stability();
        var crashes = Crashes();
        var events = Events();
        var tools = new[]
        {
            new ScenarioTool("system.info", Complete(Info())),
            stability,
            crashes,
            events,
            new ScenarioTool("system.cpu", Complete("{\"sample\":\"current\",\"usagePercent\":4.7,\"cores\":16}")),
            new ScenarioTool("system.memory", Complete(
                "{\"sample\":\"current\",\"memoryUsedMb\":28464,\"memoryTotalMb\":32475,\"memoryAvailableMb\":4011}")),
            new ScenarioTool("storage.health", Partial(
                "{\"health\":\"healthy\",\"partial\":true,\"reliabilityCounters\":\"requires elevation\",\"devices\":2}")),
        };

        // The historical pathology: after the three large reads the model kept re-reading them (more than the four reads one
        // logical call may be served) before it would continue to CPU, memory and storage.
        var reads = new Dictionary<string, IReadOnlyList<string>>
        {
            ["system.cpu"] = ["system.stability", "system.crashes", "system.events", "system.stability", "system.crashes", "system.events"],
        };

        return new RegressionScenario(
            "c508-equivalent",
            "Diagnose why this Windows machine keeps freezing and restarting; check stability, crashes, events, CPU, memory and storage.",
            tools,
            reads,
            [
                new("unexpectedShutdown=48", "key \"unexpectedShutdown\" = Integer 48", "\"unexpectedShutdown\":48"),
                new("kernelCrash=6", "key \"kernelCrash\" = Integer 6", "\"kernelCrash\":6"),
                new("hardwareError=365", "key \"hardwareError\" = Integer 365", "\"hardwareError\":365"),
                new("displayFault=26", "key \"displayFault\" = Integer 26", "\"displayFault\":26"),
                new("crashes observed=126", "type \"system.crashes.observed-count\"", "\"observed\":126"),
                new("crash groups", "type \"system.crashes.group-count\"", "\"groupCount\":31"),
                new("minidump/dump references", "type \"system.crashes.dump-reference-count\"", "\"dumpReferences\":5"),
                new("kernel dump path", "type \"system.crashes.kernel-dump-path\"", "newestKernelDump"),
                new("events groups", "type \"system.events.matched-count\"", "\"groups\":56"),
            ],
            [
                new("events partial/truncated", "system.events — completeness Partial"),
                new("crashes partial", "system.crashes — completeness Partial"),
                new("stability partial", "system.stability — completeness Partial"),
            ]);
    }

    /// <summary>
    /// The c508-equivalent diagnostic followed by <paramref name="fillers"/> further large, fact-less diagnostics before the
    /// CPU/memory/storage steps, so the final call is made under real compaction pressure.
    /// </summary>
    internal static RegressionScenario C508Extended(int fillers = 10)
    {
        var baseline = C508Equivalent();
        var tools = baseline.Tools.Take(4)
            .Concat(Enumerable.Range(0, fillers).Select(i => new ScenarioTool($"system.extra{i}", Complete(Filler(i)))))
            .Concat(baseline.Tools.Skip(4))
            .ToList();
        return baseline with { Name = $"c508-extended-{fillers}", Tools = tools };
    }

    private static string Filler(int index) => new JsonObject
    {
        ["sample"] = index,
        ["items"] = new JsonArray(Enumerable.Range(0, 40).Select(i => (JsonNode)new JsonObject
        {
            ["name"] = $"item-{index}-{i}", ["state"] = "ok", ["detail"] = $"detail text for extra diagnostic {index} entry {i} padding padding padding",
        }).ToArray()),
    }.ToJsonString();

    /// <summary>The sentinel the oldest result of <see cref="CompactedSmallResults"/> carries at <see cref="SmallResultSentinelOffset"/>.</summary>
    internal const string SmallResultSentinel = "T0-RAW-EVIDENCE-SENTINEL";

    /// <summary>Where <see cref="SmallResultSentinel"/> sits in the oldest result, so an exact drill-down can hit it.</summary>
    internal const int SmallResultSentinelOffset = 1400;

    /// <summary>
    /// AF-05G: <paramref name="tools"/> fact-less diagnostics of about 2000 characters each, every one far below
    /// <c>MaxObservationCharacters</c> (4000). With <c>MinimumPreservedToolGroups = 3</c> the MAF layer compacts the oldest groups
    /// although no runtime observation was ever shortened, and the last tool first asks to re-read the oldest result.
    /// </summary>
    internal static RegressionScenario CompactedSmallResults(int tools = 5)
    {
        var list = Enumerable.Range(0, tools).Select(i =>
        {
            var text = new string((char)('a' + i), 2000);
            if (i == 0)
            {
                text = text[..SmallResultSentinelOffset] + SmallResultSentinel + text[(SmallResultSentinelOffset + SmallResultSentinel.Length)..];
            }

            return new ScenarioTool($"diag.t{i}", Complete(text));
        }).ToList();
        return new RegressionScenario(
            "compacted-small-results",
            "Collect five small diagnostics and report them.",
            list,
            new Dictionary<string, IReadOnlyList<string>> { [list[^1].Name] = [list[0].Name] },
            [],
            []);
    }

    /// <summary>The short, healthy diagnostic: system.info, system.cpu, system.memory, then a final answer.</summary>
    internal static RegressionScenario ShortDiagnostic() => new(
        "short-diagnostic",
        "Is this machine healthy right now?",
        [
            new("system.info", Complete("{\"host\":\"ws-01\",\"os\":\"Windows 11\",\"build\":\"26100\",\"uptimeHours\":412}")),
            new("system.cpu", Complete("{\"sample\":\"current\",\"usagePercent\":4.7}")),
            new("system.memory", Complete("{\"sample\":\"current\",\"memoryUsedMb\":9000,\"memoryTotalMb\":32475}")),
        ],
        new Dictionary<string, IReadOnlyList<string>>(),
        [
            new("cpu sample", "\"usagePercent\":4.7"),
            new("memory sample", "\"memoryUsedMb\":9000"),
            new("host", "\"host\":\"ws-01\""),
        ],
        []);

    private static ToolCallResult Complete(string output) =>
        ToolCallResult.Success(output) with { Completeness = ToolResultCompleteness.Complete };

    private static ToolCallResult Partial(string output) =>
        ToolCallResult.Success(output) with { Completeness = ToolResultCompleteness.Partial };

    private static string Info()
    {
        var root = new JsonObject
        {
            ["host"] = "ws-01",
            ["os"] = "Windows 11 Pro",
            ["build"] = "26100.4652",
            ["manufacturer"] = "LENOVO",
            ["model"] = "ThinkPad P16",
            ["bios"] = new JsonObject { ["version"] = "N3JET41W", ["date"] = "2025-03-14" },
            ["uptimeHours"] = 412,
            ["cpu"] = new JsonObject { ["name"] = "Intel Core i9-13950HX", ["cores"] = 24 },
            ["memoryTotalMb"] = 32475,
            ["disks"] = new JsonArray(Enumerable.Range(0, 3).Select(i => (JsonNode)new JsonObject
            {
                ["name"] = $"disk{i}", ["sizeGb"] = 512 * (i + 1), ["bus"] = "NVMe", ["firmware"] = $"FW{i}.4.2",
            }).ToArray()),
        };
        return root.ToJsonString();
    }

    /// <summary>system.stability: eight categories; counts follow the historical task; groups make the raw JSON large.</summary>
    private static ScenarioTool Stability()
    {
        (string Name, int Count, string Status)[] categories =
        [
            ("unexpectedShutdown", 48, "available"),
            ("kernelCrash", 6, "available"),
            ("kernelFault", 0, "notApplicable"),
            ("hardwareError", 365, "partial"),
            ("displayFault", 26, "available"),
            ("storageError", 0, "available"),
            ("oomKill", 0, "notApplicable"),
            ("serviceFailure", 0, "available"),
        ];
        var categoryArray = new JsonArray(categories.Select(c => (JsonNode)new JsonObject
        {
            ["category"] = c.Name,
            ["applicability"] = c.Status == "notApplicable" ? "notApplicable" : "applicable",
            ["status"] = c.Status == "notApplicable" ? "notApplicable" : c.Status,
            ["count"] = c.Count,
        }).ToArray());
        var groups = new JsonArray(Enumerable.Range(0, 24).Select(i => (JsonNode)new JsonObject
        {
            ["category"] = i % 3 == 0 ? "hardwareError" : i % 3 == 1 ? "unexpectedShutdown" : "displayFault",
            ["provider"] = i % 2 == 0 ? "Microsoft-Windows-WHEA-Logger" : "Microsoft-Windows-Kernel-Power",
            ["eventId"] = (17 + i).ToString(CultureInfo.InvariantCulture),
            ["component"] = $"PCI\\VEN_8086&DEV_{1000 + i:X4}&SUBSYS_00000000&REV_01 (device group {i})",
            ["count"] = 5 + (i * 3),
            ["firstSeenUtc"] = Now.AddDays(-170 + i).ToString("O", CultureInfo.InvariantCulture),
            ["lastSeenUtc"] = Now.AddDays(-2 - (i % 5)).ToString("O", CultureInfo.InvariantCulture),
        }).ToArray());
        var output = new JsonObject
        {
            ["windowDays"] = 180,
            ["status"] = "partial",
            ["complete"] = false,
            ["truncated"] = true,
            ["unexpectedShutdown"] = 48,
            ["kernelCrash"] = 6,
            ["hardwareError"] = 365,
            ["displayFault"] = 26,
            ["storageError"] = 0,
            ["minidumps"] = new JsonObject { ["count"] = 5, ["directory"] = "C:\\Windows\\Minidump" },
            ["categories"] = categoryArray,
            ["groups"] = groups,
        }.ToJsonString();
        var facts = StabilityFacts.From(output, new StabilityQuery(180, Now.AddDays(-180), Now, 50));
        return new ScenarioTool("system.stability", Partial(output) with { Facts = facts });
    }

    /// <summary>system.crashes: 126 observed crashes forming groups; dump references for the five minidumps.</summary>
    private static ScenarioTool Crashes()
    {
        var items = new List<CrashEvidence>();
        void Add(int count, string kind, string? code, string? process, int dumpEvery = 0, int groupSpread = 1)
        {
            for (var i = 0; i < count; i++)
            {
                var spread = groupSpread == 1 ? process : $"{process}{i % groupSpread}.exe";
                var crash = new CrashEvidence(Now.AddHours(-(items.Count + 1) * 7), EvidenceTimestampKind.Occurred, "wer", kind)
                {
                    Process = spread,
                    BugcheckCode = kind == CrashKinds.KernelBugcheck ? code : null,
                    LiveDumpCode = kind == CrashKinds.KernelLiveDump ? code : null,
                    ExceptionCode = kind is CrashKinds.ApplicationCrash or CrashKinds.ApplicationHang ? code : null,
                    DumpPath = dumpEvery > 0 && i % dumpEvery == 0 ? $"C:\\Windows\\Minidump\\101026-{items.Count}-01.dmp" : null,
                };
                items.Add(crash);
            }
        }

        Add(3, CrashKinds.KernelBugcheck, "0x3b", null, dumpEvery: 1);
        Add(2, CrashKinds.KernelBugcheck, "0x133", null, dumpEvery: 1);
        Add(1, CrashKinds.KernelBugcheck, "0x9f", null);
        Add(12, CrashKinds.KernelLiveDump, "0x193", null);
        Add(60, CrashKinds.ApplicationCrash, "0xc0000005", "app", groupSpread: 14);
        Add(30, CrashKinds.ApplicationHang, "0x0", "hang", groupSpread: 8);
        Add(18, CrashKinds.Wer, null, "wer", groupSpread: 4);
        var snapshot = new CrashEvidenceSnapshot(items, [], CollectionTruncated: true);
        var query = new CrashesQuery(EvidenceMode.Aggregate, Now.AddDays(-180), Now, 50);
        var facts = SystemCrashFacts.From(snapshot, query);
        var groups = new JsonArray(Enumerable.Range(0, 31).Select(i => (JsonNode)new JsonObject
        {
            ["kind"] = i < 6 ? "kernel-bugcheck" : i < 10 ? "kernel-live-dump" : i < 24 ? "application-crash" : "application-hang",
            ["code"] = i < 6 ? "0x3b" : i < 10 ? "0x193" : "0xc0000005",
            ["process"] = $"C:\\Program Files\\Vendor{i}\\Application{i}\\application{i}.exe",
            ["count"] = 1 + (i % 9),
            ["faultModule"] = $"module{i}.dll",
            ["bucket"] = $"{Guid.NewGuid():N}",
            ["firstSeenUtc"] = Now.AddDays(-160 + i).ToString("O", CultureInfo.InvariantCulture),
            ["lastSeenUtc"] = Now.AddDays(-1 - i % 6).ToString("O", CultureInfo.InvariantCulture),
        }).ToArray());
        var output = new JsonObject
        {
            ["status"] = "partial",
            ["complete"] = false,
            ["truncated"] = true,
            ["observed"] = items.Count,
            ["dumpReferences"] = items.Count(item => item.DumpPath is not null),
            ["groupCount"] = 31,
            ["newestKernelDump"] = items.First(item => item.DumpPath is not null).DumpPath,
            ["groups"] = groups,
        }.ToJsonString();
        return new ScenarioTool("system.crashes", Partial(output) with { Facts = facts });
    }

    /// <summary>system.events: 2869 matched records in 56 (source, event id) pairs, 50 returned, partial and truncated.</summary>
    private static ScenarioTool Events()
    {
        var records = new List<SystemEventRecord>();
        for (var pair = 0; pair < 56; pair++)
        {
            var count = pair < 4 ? 600 - (pair * 80) : Math.Max(1, 60 - pair);
            for (var i = 0; i < count && records.Count < 2869; i++)
            {
                records.Add(new SystemEventRecord(
                    Now.AddMinutes(-(records.Count + 1)),
                    pair < 6 ? SystemEventSeverity.Error : SystemEventSeverity.Warning,
                    $"Provider-{pair}",
                    (1000 + pair).ToString(CultureInfo.InvariantCulture),
                    "System",
                    "message",
                    null,
                    null));
            }
        }

        while (records.Count < 2869)
        {
            records.Add(new SystemEventRecord(Now.AddMinutes(-(records.Count + 1)), SystemEventSeverity.Warning, "Provider-9", "1009", "System", "message", null, null));
        }

        var snapshot = new SystemEventSnapshot(records, [], CollectionTruncated: true);
        var query = new SystemEventQuery(Now.AddDays(-30), Now, SystemEventSeverity.Warning, null, null, null, null, 100000);
        var facts = SystemEventFacts.From(snapshot, query);
        var groups = new JsonArray(Enumerable.Range(0, 50).Select(i => (JsonNode)new JsonObject
        {
            ["source"] = $"Provider-{i}",
            ["eventId"] = (1000 + i).ToString(CultureInfo.InvariantCulture),
            ["severity"] = i < 6 ? "error" : "warning",
            ["count"] = Math.Max(1, 60 - i),
            ["newestUtc"] = Now.AddMinutes(-i).ToString("O", CultureInfo.InvariantCulture),
            ["sample"] = $"The driver \\Driver\\Component{i} detected a controller error on \\Device\\Harddisk{i}\\DR{i} during operation.",
        }).ToArray());
        var output = new JsonObject
        {
            ["status"] = "partial",
            ["complete"] = false,
            ["truncated"] = true,
            ["events"] = 2869,
            ["groups"] = 56,
            ["returned"] = 50,
            ["items"] = groups,
        }.ToJsonString();
        return new ScenarioTool("system.events", Partial(output) with { Facts = facts });
    }
}
