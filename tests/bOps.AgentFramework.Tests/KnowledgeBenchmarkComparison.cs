// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Runtime;

namespace bOps.AgentFramework.Tests;

/// <summary>Test-only coordinator. Task outcomes and experiment integrity are separate decisions.</summary>
internal static class KnowledgeBenchmarkComparison
{
    internal static async Task RunAsync(string root, Func<bool, KnowledgeBenchmarkExport, Task> execute,
        Action<bool, KnowledgeBenchmarkExport> validate)
    {
        var reportPath = Path.Combine(root, "comparison.json");
        // Reserve the report before running either arm; never overwrite prior evidence.
        using var report = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var outcomes = new List<KnowledgeBenchmarkExport>();
        var complete = false;
        try
        {
            foreach (var knowledge in new[] { false, true })
            {
                KnowledgeBenchmarkExport? observed = null;
                try
                {
                    await KnowledgeBenchmarkExport.RunAsync(root, knowledge ? "B" : "A", async export =>
                    {
                        observed = export;
                        outcomes.Add(export);
                        await execute(knowledge, export);
                    });
                }
                catch (ModelProtocolException) when (OrdinaryFailure(observed)) { }
                catch (HttpRequestException) when (OrdinaryFailure(observed)) { }
                catch (TimeoutException) when (OrdinaryFailure(observed)) { }
                catch (OperationCanceledException) when (OrdinaryFailure(observed)) { }
                // Every other exception propagates after the export boundary preserves available evidence.
                RequireSafeOutcome(knowledge, observed!);
                using var result = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(root, knowledge ? "B" : "A", "result.json")));
                if (result.RootElement.GetProperty("auditStatus").GetString() != "Complete" ||
                    result.RootElement.GetProperty("stateRecoveryError").ValueKind != JsonValueKind.Null ||
                    result.RootElement.GetProperty("status").GetString() != observed!.State!.Status.ToString() ||
                    result.RootElement.GetProperty("terminalKind").GetString() != observed.State.TerminalReason?.Kind.ToString() ||
                    result.RootElement.GetProperty("failureKind").GetString() != observed.State.TerminalReason?.FailureKind?.ToString())
                    throw new InvalidOperationException("Incomplete benchmark evidence.");
                _ = await File.ReadAllBytesAsync(Path.Combine(root, knowledge ? "B" : "A", "audit.jsonl"));
            }
            var a = outcomes[0];
            var b = outcomes[1];
            if (a.RunId == b.RunId || ReferenceEquals(a.World, b.World) || ReferenceEquals(a.Store, b.Store) ||
                ReferenceEquals(a.ModelObservation, b.ModelObservation) ||
                (a.ModelObservation.Http is not null && ReferenceEquals(a.ModelObservation.Http, b.ModelObservation.Http)) ||
                a.CorpusSha256 is null || a.CorpusSha256 != b.CorpusSha256 ||
                !JsonNode.DeepEquals(a.CorpusProvenance, b.CorpusProvenance) ||
                a.Configuration is null || !JsonNode.DeepEquals(a.Configuration, b.Configuration) ||
                a.Commit != b.Commit || a.RequestedCommit != b.RequestedCommit ||
                a.ProviderRequestTimeout != b.ProviderRequestTimeout || a.State!.Goal != b.State!.Goal)
                throw new InvalidOperationException("A/B inputs or isolation mismatch.");
            validate(false, a);
            validate(true, b);
            complete = true;
        }
        finally
        {
            // Only fixed classifications and the same payload-free outcome fields used by export v2.
            var arms = new JsonArray();
            foreach (var condition in new[] { "A", "B" })
            {
                var path = Path.Combine(root, condition, "result.json");
                var arm = new JsonObject { ["condition"] = condition,
                    ["taskExecuted"] = outcomes.ElementAtOrDefault(condition == "A" ? 0 : 1)?.State is not null,
                    ["exportPresent"] = File.Exists(path) && File.Exists(Path.Combine(root, condition, "audit.jsonl")) };
                if (File.Exists(path))
                {
                    var result = JsonNode.Parse(await File.ReadAllBytesAsync(path))!;
                    foreach (var name in new[] { "status", "terminalCause", "terminalKind", "failureKind" })
                        arm[name] = result[name]?.DeepClone();
                    // D7: whether the quality of this arm can be judged at all; the id, hash and text stay out of the comparison.
                    foreach (var name in new[] { "availability", "archiveStatus", "qualityMeasurable" })
                        arm["diagnosis" + char.ToUpperInvariant(name[0]) + name[1..]] = result["diagnosis"]?[name]?.DeepClone();
                }
                arms.Add(arm);
            }
            var summary = new JsonObject { ["schemaVersion"] = 2,
                ["comparisonStatus"] = complete ? "Complete" : "InterruptedInfrastructureError",
                ["benchmarkExecutionSucceeded"] = complete,
                ["bothArmsExecuted"] = outcomes.Count == 2 && outcomes.All(o => o.State is not null),
                ["interpretation"] = "Exploratory single pair; no causal attribution to Knowledge", ["arms"] = arms };
            await JsonSerializer.SerializeAsync(report, summary, KnowledgeBenchmark.JsonOptions);
        }
    }

    private static bool OrdinaryFailure(KnowledgeBenchmarkExport? export) => export?.State is
        { Status: AgentTaskStatus.Failed, TerminalReason.Kind: TaskTerminalKind.ModelFailure or
            TaskTerminalKind.EmptyResponse or TaskTerminalKind.StepLimit or TaskTerminalKind.LifetimeStepLimit or
            TaskTerminalKind.TokenBudget or TaskTerminalKind.ReplanLimit or TaskTerminalKind.LifetimeReplanLimit or
            TaskTerminalKind.AttemptDurationBudget or TaskTerminalKind.Cancelled };

    private static void RequireSafeOutcome(bool knowledge, KnowledgeBenchmarkExport export)
    {
        var state = export.State;
        var events = export.World?.Events ?? throw new InvalidOperationException("Task audit missing.");
        if (state is null || state.Id != export.RunId ||
            (state.Status != AgentTaskStatus.Completed && !OrdinaryFailure(export)) ||
            events.Any(e => e.TaskId != export.RunId || e.Actor != KnowledgeRunnerWorld.ActorA) ||
            state.TerminalReason?.FailureKind == ModelFailureKind.Authentication ||
            events.OfType<ModelCallAuditEvent>().Any(e => e.FailureKind == ModelFailureKind.Authentication) ||
            events.OfType<KnowledgePackAdmissionAuditEvent>().Any(e => e.Code != KnowledgeAdmissionCode.Admitted || e.TenantId != "tenant-a") ||
            events.OfType<PolicyDecisionAuditEvent>().Any(e => e.Mode != PolicyMode.Automatic) ||
            events.OfType<EntitlementDecisionAuditEvent>().Any(e => e.Result == EntitlementDecisionKind.Denied) ||
            events.OfType<KnowledgeReadAuditEvent>().Any(e => e.ResultCode is KnowledgeReadResultCode.IntegrityMismatch or KnowledgeReadResultCode.InfrastructureFailure or
                KnowledgeReadResultCode.NotAuthorized or KnowledgeReadResultCode.SnapshotUnavailable ||
                (e.ResultCode == KnowledgeReadResultCode.Success && (e.TenantId != "tenant-a" || e.PackHash != state.KnowledgeBinding?.ManifestHash))) ||
            events.OfType<ToolCallAuditEvent>().Any(e => e.Outcome == ToolOutcome.Denied ||
                e.FailureKind == ToolFailureKind.Authorization ||
                e.Summary?["replayRefusalCode"]?.GetValue<string>() is "IdentityMismatch" or "IntegrityOrProvenance") ||
            (!knowledge && (state.KnowledgeBinding is not null || events.OfType<KnowledgeReadAuditEvent>().Any() ||
                events.OfType<KnowledgePackAdmissionAuditEvent>().Any())) ||
            (knowledge && (state.KnowledgeBinding is null || state.KnowledgeBinding.TenantId != "tenant-a" ||
                state.KnowledgeBinding.AuthorizedBy != KnowledgeRunnerWorld.ActorA ||
                state.KnowledgeBinding.PackId != KnowledgeBenchmark.Reference.PackId ||
                state.KnowledgeBinding.Version != KnowledgeBenchmark.Reference.Version || state.KnowledgeBinding.Kind != "Knowledge")))
            throw new InvalidOperationException("Benchmark integrity, identity, authorization or terminal outcome invalid.");
    }
}
