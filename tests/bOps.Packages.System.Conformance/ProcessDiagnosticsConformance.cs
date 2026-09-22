// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using Xunit;

namespace bOps.Packages.Sys.Conformance;

/// <summary>
/// Assertions any implementation of the V1.3-C process tools must satisfy, regardless of which OS
/// package produced it (ADR-0034). The LLM reads this output, so a difference in shape between
/// Windows and Linux is a difference in behaviour (agentic/01-architecture-rules.md, rule A8).
/// Structure and invariants only: rates are non-negative, percentages are within 0–100, a bound is
/// honoured, an out-of-range argument is refused and no output ever carries an environment
/// variable.
/// </summary>
public static class ProcessDiagnosticsConformance
{
    /// <summary>
    /// A PID large enough not to be a real running process on any machine this suite runs on — the
    /// same pragmatic trick <c>SystemToolConformance</c> already uses for a definite absence.
    /// </summary>
    private const int UnlikelyPid = 2_000_000;

    private static readonly string[] ModuleStatuses = ["available", "partial", "unavailable", "unsupported", "notApplicable"];

    /// <summary>The additive <c>process.inspect</c> fields of V1.3-C, on the calling process itself.</summary>
    public static async Task AssertProcessInspectReportsTheAddedFieldsAsync(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        var json = await RunAsync(tool, new JsonObject { ["pid"] = Environment.ProcessId });

        Assert.True(json["exists"]!.GetValue<bool>());
        foreach (var field in new[]
                 {
                     "parentPid", "executablePath", "commandLine", "user", "privateMemoryMb",
                     "virtualMemoryMb", "handleOrFdCount", "cpuTotalMs", "ioReadBytes", "ioWriteBytes",
                 })
        {
            Assert.True(json.AsObject().ContainsKey(field), $"process.inspect must always report the key '{field}', even as null.");
        }

        // The test process can read its own everything, so these are the fields whose absence would
        // mean the collector is broken rather than that the identity was refused.
        Assert.True(json["parentPid"]!.GetValue<int>() > 0);
        Assert.False(string.IsNullOrWhiteSpace(json["executablePath"]?.GetValue<string>()));
        Assert.False(string.IsNullOrWhiteSpace(json["commandLine"]?.GetValue<string>()));
        Assert.False(string.IsNullOrWhiteSpace(json["user"]?.GetValue<string>()));
        Assert.True(json["handleOrFdCount"]!.GetValue<int>() > 0);
        Assert.True(json["cpuTotalMs"]!.GetValue<long>() >= 0);
        Assert.True(json["virtualMemoryMb"]!.GetValue<long>() >= 0);
    }

    /// <summary>A missing process still reports every added field, as null, rather than failing.</summary>
    public static async Task AssertProcessInspectReportsMissingWithTheAddedFieldsAsync(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        var json = await RunAsync(tool, new JsonObject { ["pid"] = UnlikelyPid });

        Assert.False(json["exists"]!.GetValue<bool>());
        Assert.Null(json["parentPid"]);
        Assert.Null(json["commandLine"]);
        Assert.Null(json["user"]);
    }

    /// <summary>The <c>process.metrics</c> manifest, and one real sample of the calling process.</summary>
    public static async Task AssertProcessMetricsConformsAsync(ITool tool, string platform)
    {
        ArgumentNullException.ThrowIfNull(tool);

        SystemToolConformance.AssertManifestIsWellFormed(tool.Manifest, platform, "process.metrics");
        Assert.Contains(tool.Manifest.Parameters, parameter => parameter is { Name: "pid", Required: true });
        Assert.Contains(tool.Manifest.Parameters, parameter => parameter is { Name: "sampleMilliseconds", Required: false });

        var json = await RunAsync(tool, new JsonObject { ["pid"] = Environment.ProcessId, ["sampleMilliseconds"] = 200 });

        Assert.Equal(Environment.ProcessId, json["pid"]!.GetValue<int>());
        Assert.True(json["exists"]!.GetValue<bool>());
        Assert.Equal(200, json["sampleMilliseconds"]!.GetValue<int>());
        Assert.NotNull(json["partial"]);

        Assert.InRange(json["cpuPercent"]!.GetValue<double>(), 0, 100);
        foreach (var field in new[] { "workingSetMb", "privateMemoryMb", "virtualMemoryMb", "threadCount", "handleOrFdCount" })
        {
            Assert.True(json.AsObject().ContainsKey(field), $"process.metrics must always report the key '{field}'.");
            if (json[field] is { } value)
            {
                Assert.True(value.GetValue<long>() >= 0, $"{field} must not be negative.");
            }
        }

        foreach (var field in new[] { "readBytesPerSec", "writeBytesPerSec", "pageFaultsPerSec" })
        {
            Assert.True(json.AsObject().ContainsKey(field), $"process.metrics must always report the key '{field}'.");
            if (json[field] is { } value)
            {
                Assert.True(value.GetValue<double>() >= 0, $"{field} must not be negative.");
            }
        }
    }

    /// <summary>The default sample interval is the documented 500 ms.</summary>
    public static async Task AssertProcessMetricsDefaultsToFiveHundredMillisecondsAsync(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        var json = await RunAsync(tool, new JsonObject { ["pid"] = Environment.ProcessId });

        Assert.Equal(500, json["sampleMilliseconds"]!.GetValue<int>());
    }

    /// <summary>A process that is not running is a successful observation of an absence, and it is partial.</summary>
    public static async Task AssertProcessMetricsReportsMissingAsync(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        var json = await RunAsync(tool, new JsonObject { ["pid"] = UnlikelyPid, ["sampleMilliseconds"] = 200 });

        Assert.False(json["exists"]!.GetValue<bool>());
        Assert.True(json["partial"]!.GetValue<bool>());
        Assert.Null(json["cpuPercent"]);
    }

    /// <summary><c>sampleMilliseconds</c> is refused outside 200–5000, never clamped.</summary>
    public static async Task AssertProcessMetricsRejectsOutOfRangeSamplesAsync(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        foreach (var sample in new[] { 199, 5_001, 0, -1 })
        {
            var result = await tool.ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["pid"] = Environment.ProcessId, ["sampleMilliseconds"] = sample }));
            Assert.False(result.Succeeded, $"sampleMilliseconds {sample} must be refused.");
            Assert.Contains("sampleMilliseconds", result.ErrorMessage!, StringComparison.Ordinal);
        }

        foreach (var sample in new[] { 200, 5_000 })
        {
            var result = await tool.ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["pid"] = Environment.ProcessId, ["sampleMilliseconds"] = sample }));
            Assert.True(result.Succeeded, $"sampleMilliseconds {sample} is inside the documented range and must be accepted.");
        }
    }

    /// <summary>The <c>process.tree</c> manifest, and the calling process as the root of its own walk.</summary>
    public static async Task AssertProcessTreeConformsAsync(ITool tool, string platform)
    {
        ArgumentNullException.ThrowIfNull(tool);

        SystemToolConformance.AssertManifestIsWellFormed(tool.Manifest, platform, "process.tree");
        Assert.Contains(tool.Manifest.Parameters, parameter => parameter is { Name: "rootPid", Required: false });
        Assert.Contains(tool.Manifest.Parameters, parameter => parameter is { Name: "maxDepth", Required: false });
        Assert.Contains(tool.Manifest.Parameters, parameter => parameter is { Name: "limit", Required: false });

        var json = await RunAsync(tool, new JsonObject { ["rootPid"] = Environment.ProcessId });

        Assert.True(json["rootFound"]!.GetValue<bool>());
        Assert.Equal(4, json["maxDepth"]!.GetValue<int>());
        Assert.True(json["skipped"]!.GetValue<int>() >= 0);
        Assert.NotNull(json["truncated"]);
        Assert.NotNull(json["complete"]);

        var rows = json["processes"]!.AsArray();
        Assert.NotEmpty(rows);
        Assert.Equal(rows.Count, json["returnedProcesses"]!.GetValue<int>());

        var self = rows[0]!.AsObject();
        Assert.Equal(Environment.ProcessId, self["pid"]!.GetValue<int>());
        Assert.Equal(0, self["depth"]!.GetValue<int>());
        Assert.True(self.ContainsKey("parentPid"));
        Assert.True(self.ContainsKey("name"));
        Assert.True(self.ContainsKey("user"));

        foreach (var row in rows)
        {
            Assert.True(row!["pid"]!.GetValue<int>() > 0);
            Assert.True(row["depth"]!.GetValue<int>() >= 0);
        }
    }

    /// <summary>A child this test spawned appears one generation below its parent, in the same walk.</summary>
    public static async Task AssertProcessTreeShowsTheParentChildRelationAsync(ITool tool, int parentPid, int childPid)
    {
        ArgumentNullException.ThrowIfNull(tool);

        var json = await RunAsync(tool, new JsonObject { ["rootPid"] = parentPid });
        var rows = json["processes"]!.AsArray();

        var child = rows.FirstOrDefault(row => row!["pid"]!.GetValue<int>() == childPid);
        Assert.NotNull(child);
        Assert.Equal(parentPid, child!["parentPid"]!.GetValue<int>());
        Assert.Equal(1, child["depth"]!.GetValue<int>());
    }

    /// <summary>A root that is not running is reported as not found, not as an empty machine.</summary>
    public static async Task AssertProcessTreeReportsAMissingRootAsync(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        var json = await RunAsync(tool, new JsonObject { ["rootPid"] = UnlikelyPid });

        Assert.False(json["rootFound"]!.GetValue<bool>());
        Assert.False(json["complete"]!.GetValue<bool>());
        Assert.Empty(json["processes"]!.AsArray());
    }

    /// <summary>The row limit and the depth are honoured, and both say so through <c>truncated</c>.</summary>
    public static async Task AssertProcessTreeRespectsBoundsAsync(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        var limited = await RunAsync(tool, new JsonObject { ["limit"] = 1 });
        Assert.Single(limited["processes"]!.AsArray());
        Assert.True(limited["truncated"]!.GetValue<bool>());
        Assert.False(limited["complete"]!.GetValue<bool>());

        var shallow = await RunAsync(tool, new JsonObject { ["maxDepth"] = 0, ["limit"] = 2_000 });
        Assert.All(shallow["processes"]!.AsArray(), row => Assert.Equal(0, row!["depth"]!.GetValue<int>()));

        var deep = await RunAsync(tool, new JsonObject { ["maxDepth"] = 16, ["limit"] = 2_000 });
        Assert.All(deep["processes"]!.AsArray(), row => Assert.InRange(row!["depth"]!.GetValue<int>(), 0, 16));
    }

    /// <summary><c>maxDepth</c> and <c>limit</c> are refused outside their documented ranges.</summary>
    public static async Task AssertProcessTreeRejectsOutOfRangeBoundsAsync(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        await AssertRefusedAsync(tool, new JsonObject { ["maxDepth"] = 17 }, "maxDepth");
        await AssertRefusedAsync(tool, new JsonObject { ["maxDepth"] = -1 }, "maxDepth");
        await AssertRefusedAsync(tool, new JsonObject { ["limit"] = 0 }, "limit");
        await AssertRefusedAsync(tool, new JsonObject { ["limit"] = 2_001 }, "limit");
        await AssertRefusedAsync(tool, new JsonObject { ["rootPid"] = 0 }, "rootPid");

        Assert.True((await tool.ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["maxDepth"] = 16, ["limit"] = 2_000 }))).Succeeded);
    }

    /// <summary>The <c>process.modules</c> manifest, and the modules of the calling process.</summary>
    public static async Task AssertProcessModulesConformAsync(ITool tool, string platform)
    {
        ArgumentNullException.ThrowIfNull(tool);

        SystemToolConformance.AssertManifestIsWellFormed(tool.Manifest, platform, "process.modules");
        Assert.Contains(tool.Manifest.Parameters, parameter => parameter is { Name: "pid", Required: true });
        Assert.Contains(tool.Manifest.Parameters, parameter => parameter is { Name: "limit", Required: false });
        Assert.Contains(tool.Manifest.Parameters, parameter => parameter is { Name: "maxOutputBytes", Required: false });

        var json = await RunAsync(tool, new JsonObject { ["pid"] = Environment.ProcessId });

        Assert.Equal(Environment.ProcessId, json["pid"]!.GetValue<int>());
        Assert.True(json["exists"]!.GetValue<bool>());
        Assert.Contains(json["status"]!.GetValue<string>(), ModuleStatuses);

        var modules = json["modules"]!.AsArray();
        Assert.NotEmpty(modules); // a .NET test host has loaded its own runtime at the very least
        Assert.Equal(modules.Count, json["returnedModules"]!.GetValue<int>());

        var paths = new List<string>();
        foreach (var module in modules)
        {
            var entry = module!.AsObject();
            Assert.False(string.IsNullOrWhiteSpace(entry["name"]!.GetValue<string>()));
            var path = entry["path"]!.GetValue<string>();
            Assert.False(string.IsNullOrWhiteSpace(path));
            Assert.True(entry.ContainsKey("baseAddress"));
            Assert.True(entry.ContainsKey("sizeBytes"));
            Assert.True(entry.ContainsKey("version"));
            if (entry["sizeBytes"] is { } size)
            {
                Assert.True(size.GetValue<long>() >= 0);
            }

            paths.Add(path);
        }

        Assert.Equal(paths.Count, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>The module count and the UTF-8 byte budget are both honoured.</summary>
    public static async Task AssertProcessModulesRespectBoundsAsync(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        var limited = await RunAsync(tool, new JsonObject { ["pid"] = Environment.ProcessId, ["limit"] = 1 });
        Assert.Single(limited["modules"]!.AsArray());

        var result = await tool.ExecuteAsync(ToolArguments.FromJson(new JsonObject
        {
            ["pid"] = Environment.ProcessId,
            ["limit"] = 1_000,
            ["maxOutputBytes"] = 4_096,
        }));
        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.True(Encoding.UTF8.GetByteCount(result.Output!) <= 4_096);
    }

    /// <summary><c>limit</c> and <c>maxOutputBytes</c> are refused outside their documented ranges.</summary>
    public static async Task AssertProcessModulesRejectOutOfRangeBoundsAsync(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        var pid = Environment.ProcessId;
        await AssertRefusedAsync(tool, new JsonObject { ["pid"] = pid, ["limit"] = 0 }, "limit");
        await AssertRefusedAsync(tool, new JsonObject { ["pid"] = pid, ["limit"] = 1_001 }, "limit");
        await AssertRefusedAsync(tool, new JsonObject { ["pid"] = pid, ["maxOutputBytes"] = 4_095 }, "maxOutputBytes");
        await AssertRefusedAsync(tool, new JsonObject { ["pid"] = pid, ["maxOutputBytes"] = 131_073 }, "maxOutputBytes");
        await AssertRefusedAsync(tool, new JsonObject { ["pid"] = 0 }, "pid");

        Assert.True((await tool.ExecuteAsync(ToolArguments.FromJson(new JsonObject
        {
            ["pid"] = pid,
            ["limit"] = 1_000,
            ["maxOutputBytes"] = 131_072,
        }))).Succeeded);
    }

    /// <summary>A process that is not running is an absence, not a read failure.</summary>
    public static async Task AssertProcessModulesReportMissingAsync(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        var json = await RunAsync(tool, new JsonObject { ["pid"] = UnlikelyPid });

        Assert.False(json["exists"]!.GetValue<bool>());
        Assert.False(json["complete"]!.GetValue<bool>());
        Assert.Empty(json["modules"]!.AsArray());
    }

    /// <summary>
    /// Nothing any process tool returns carries an environment variable. The caller sets a marked
    /// variable in this process first, so the check is against a value that would definitely be
    /// there if a collector ever started reading the environment block.
    /// </summary>
    public static async Task AssertNoEnvironmentVariableIsEverReturnedAsync(
        ITool inspect, ITool metrics, ITool tree, ITool modules, string variableName, string variableValue)
    {
        ArgumentNullException.ThrowIfNull(inspect);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(modules);

        var pid = Environment.ProcessId;
        var outputs = new List<string?>
        {
            (await inspect.ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["pid"] = pid }))).Output,
            (await metrics.ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["pid"] = pid, ["sampleMilliseconds"] = 200 }))).Output,
            (await tree.ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["rootPid"] = pid }))).Output,
            (await modules.ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["pid"] = pid }))).Output,
        };

        foreach (var output in outputs)
        {
            Assert.NotNull(output);
            Assert.DoesNotContain(variableName, output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(variableValue, output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"environment\"", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"env\"", output, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Every one of the four tools honours cancellation rather than running to completion.</summary>
    public static async Task AssertCancellationIsHonouredAsync(ITool inspect, ITool metrics, ITool tree, ITool modules)
    {
        ArgumentNullException.ThrowIfNull(inspect);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(modules);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var pid = Environment.ProcessId;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => inspect.ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["pid"] = pid }), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => metrics.ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["pid"] = pid }), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => tree.ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["rootPid"] = pid }), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => modules.ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["pid"] = pid }), cancelled.Token));
    }

    private static async Task<JsonNode> RunAsync(ITool tool, JsonObject arguments)
    {
        var result = await tool.ExecuteAsync(ToolArguments.FromJson(arguments));
        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.NotNull(result.Output);
        return JsonNode.Parse(result.Output!)!;
    }

    private static async Task AssertRefusedAsync(ITool tool, JsonObject arguments, string expectedInMessage)
    {
        var result = await tool.ExecuteAsync(ToolArguments.FromJson(arguments));

        Assert.False(result.Succeeded, $"{arguments.ToJsonString()} must be refused.");
        Assert.Contains(expectedInMessage, result.ErrorMessage!, StringComparison.Ordinal);
    }
}
