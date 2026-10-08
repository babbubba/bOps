// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Packages.Sys.Core;

/// <summary>
/// The shared tool shell for <c>system.dump_analyze</c> (ADR-0048): manifest, argument reading, the outcome mapping, bounded
/// JSON output and the aggregate audit summary are written once here; the OS package implements only path authorization,
/// debugger discovery, the fixed debugger invocation and parsing.
/// </summary>
public abstract class SystemDumpAnalyzeToolBase : IToolAuditSummaryProvider
{
    /// <summary>Creates the shell for <paramref name="platform"/>, with one required and an optional supporting prerequisite.</summary>
    protected SystemDumpAnalyzeToolBase(string platform, string requiredCapability, string? optionalCapability = null) =>
        Manifest = SystemToolManifests.DumpAnalyze(platform, requiredCapability, optionalCapability);

    /// <inheritdoc />
    public ToolManifest Manifest { get; }

    /// <summary>
    /// Authorizes <paramref name="path"/> and analyzes it. Returns a report for every expected condition, including a rejected
    /// path (<see cref="DumpAnalysisFailure.InvalidPath"/>); throws only on caller cancellation.
    /// </summary>
    protected abstract Task<DumpAnalysisReport> AnalyzeAsync(string path, CancellationToken ct);

    /// <inheritdoc />
    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!DumpAnalysisArguments.TryRead(arguments, out var path, out var error))
        {
            return ToolCallResult.Failure(error!) with { FailureKind = ToolFailureKind.Validation };
        }

        ct.ThrowIfCancellationRequested();
        var report = await AnalyzeAsync(path!, ct).ConfigureAwait(false);
        return ToResult(report);
    }

    /// <summary>Maps a report to the typed tool outcome (ADR-0048 §8).</summary>
    internal static ToolCallResult ToResult(DumpAnalysisReport report)
    {
        if (report.Failure == DumpAnalysisFailure.InvalidPath)
        {
            var reason = report.Warnings.Count > 0 ? report.Warnings[0] : "path-not-approved";
            return ToolCallResult.Failure(
                    $"path is not an approved local kernel-dump location ({reason}). Accepted: absolute .dmp or .mdmp files under "
                    + "%SystemRoot%\\Minidump, %SystemRoot%\\LiveKernelReports, or exactly %SystemRoot%\\MEMORY.DMP.")
                with { FailureKind = ToolFailureKind.Validation };
        }

        var output = DumpAnalysisFormatting.Format(report);
        var completeness = Completeness(output);
        if (report.Failure == DumpAnalysisFailure.Timeout && report.Status == DumpAnalysisStatus.Unavailable)
        {
            return new ToolCallResult(ToolOutcome.Timeout, output, "system.dump_analyze: the debugger did not finish within its bound.")
            {
                FailureKind = ToolFailureKind.Timeout,
                Completeness = completeness,
            };
        }

        return ToolCallResult.Success(output) with { Completeness = completeness };
    }

    private static ToolResultCompleteness Completeness(string output)
    {
        var status = JsonNode.Parse(output)!["status"]!.GetValue<string>();
        return status switch
        {
            "complete" => ToolResultCompleteness.Complete,
            "partial" => ToolResultCompleteness.Partial,
            _ => ToolResultCompleteness.Unavailable,
        };
    }

    /// <inheritdoc />
    public JsonObject? CreateAuditSummary(ToolArguments arguments, ToolCallResult result)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(result);
        if (result.Output is null)
        {
            return null;
        }

        JsonObject root;
        try
        {
            root = JsonNode.Parse(result.Output)!.AsObject();
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidOperationException or NullReferenceException)
        {
            return null;
        }

        // Aggregate only (ADR-0048 §9): never the path, a stack frame, a module, a device id or any debugger text.
        return new JsonObject
        {
            ["status"] = root["status"]?.GetValue<string>(),
            ["failure"] = root["failure"]?.GetValue<string>(),
            ["truncated"] = root["truncated"]?.GetValue<bool>(),
            ["dumpKind"] = root["dump"]?["kind"]?.GetValue<string>(),
            ["bugcheckCode"] = root["bugcheck"]?["code"]?.GetValue<string>(),
            ["symbolsStatus"] = root["symbols"]?["status"]?.GetValue<string>(),
            ["dumpCheckUsed"] = root["debugger"]?["dumpCheckUsed"]?.GetValue<bool>(),
            ["pnpBlackboxAvailable"] = root["blackbox"]?["pnp"]?["available"]?.GetValue<bool>(),
            ["bsdBlackboxAvailable"] = root["blackbox"]?["bsd"]?["available"]?.GetValue<bool>(),
            ["stackFrames"] = root["stack"]?.AsArray().Count,
        };
    }
}
