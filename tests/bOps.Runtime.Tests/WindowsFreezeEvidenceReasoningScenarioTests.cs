// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// Compact regression from a 12-step, 269452-token diagnostic of months of Windows freezes.
/// It tests the accepted HARDEN-9 prompt and limitation contract, not model compliance with it.
/// </summary>
public sealed class WindowsFreezeEvidenceReasoningScenarioTests
{
    private const string BadAnswer =
        "Swap: 0 MB; no paging file. Current memory pressure caused months of freezes. " +
        "P:/T: full, causing write freezes. Stopped services caused missing patches. " +
        "Qualcomm/Realtek drivers are old. The 0x193 reports are kernel crashes on 10/10/2026 within 2 minutes.\n\n" +
        "Evidence limitations\n- system.stability and system.crashes are partial.";

    [Fact]
    public async Task MonthsOfWindowsFreezes_WithPartialWheaAndLiveKernelDumpEvidence_CarriesAcceptedReasoningRules()
    {
        var evidence = new[]
        {
            new ResultTool("system.cpu", EvidenceScenario.Complete("{\"sample\":\"current\",\"usagePercent\":4.7}")),
            new ResultTool("system.memory", EvidenceScenario.Complete(
                "{\"sample\":\"current\",\"memoryUsedMb\":28464,\"memoryTotalMb\":32475,\"memoryAvailableMb\":4011}")),
            new ResultTool("system.stability", EvidenceScenario.Partial(
                "{\"windowDays\":180,\"status\":\"partial\",\"complete\":false,\"truncated\":true,\"unexpectedShutdown\":26,\"kernelCrash\":3,\"hardwareError\":322,\"whea19Corrected\":304,\"whea2Corrected\":18,\"displayFault\":26,\"minidumps\":\"unavailable: requires elevation\"}")),
            new ResultTool("system.crashes", EvidenceScenario.Partial(
                "{\"status\":\"partial\",\"complete\":false,\"truncated\":true,\"groups\":[{\"kind\":\"kernel-live-dump\",\"code\":\"0x193\",\"count\":26,\"timestampKind\":\"reported\",\"firstSeenUtc\":\"2026-10-02T08:28:44.5771754Z\",\"lastSeenUtc\":\"2026-10-02T08:43:32.7516454Z\"},{\"kind\":\"kernel-bugcheck\",\"code\":\"0x3b\",\"timestampKind\":\"reported\"}]}")),
            new ResultTool("system.disk", EvidenceScenario.Complete(
                "{\"CUsedPercent\":40,\"P\":{\"usedMb\":805914,\"totalMb\":921581,\"freeMb\":115667},\"T\":{\"usedMb\":805914,\"totalMb\":921581,\"freeMb\":115667}}")),
            new ResultTool("service.list", EvidenceScenario.Complete(
                "{\"stopped\":[\"wuauserv\",\"WSearch\",\"com.docker.service\"]}")),
            new ResultTool("system.updates", EvidenceScenario.Complete(
                "{\"available\":[\"Lenovo firmware 1.31.0.0\",\"Qualcomm device updates\",\"Realtek network update\"]}")),
            new ResultTool("storage.health", EvidenceScenario.Partial(
                "{\"health\":\"healthy\",\"partial\":true,\"reliabilityCounters\":\"requires elevation\"}")),
        };
        var model = new FakeChatModel([
            PlanningTestSupport.PlanResponse(stepCount: evidence.Length),
            .. evidence.Select((tool, index) => EvidenceScenario.Call(tool.Manifest.Name, $"call-{index}")),
            EvidenceScenario.Final(BadAnswer)]);

        var state = await EvidenceScenario.Runner(model, EvidenceScenario.Registry(evidence), new RecordingAuditSink())
            .RunAsync("Diagnose months of freezes", EvidenceScenario.Actor);

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        var finalRequest = model.Requests[^1];
        var rule = finalRequest.SystemPrompt;
        Assert.Contains(EvidenceRule.Paragraph, rule, StringComparison.Ordinal);
        Assert.Contains("One sample is not a trend", rule, StringComparison.Ordinal);
        Assert.Contains("never present a reported time as when it happened", rule, StringComparison.Ordinal);
        Assert.Contains("its counts are minimums", rule, StringComparison.Ordinal);
        Assert.Contains("No result proves something did not happen", rule, StringComparison.Ordinal);
        Assert.Contains("Closeness in time, co-occurrence, frequency or similarity never proves a cause", rule, StringComparison.Ordinal);
        Assert.Contains("Never state a cause as your own conclusion", rule, StringComparison.Ordinal);
        Assert.Contains("- step 2: system.stability — completeness Partial", rule, StringComparison.Ordinal);
        Assert.Contains("- step 3: system.crashes — completeness Partial", rule, StringComparison.Ordinal);
        Assert.Contains("- step 7: storage.health — completeness Partial", rule, StringComparison.Ordinal);

        var observations = string.Join("\n", finalRequest.History.Where(turn => turn.Role == ChatRole.Tool).Select(turn => turn.Content));
        var bounded = string.Join("\n", finalRequest.History.Where(turn => turn.Role == ChatRole.User).Select(turn => turn.Content));
        Assert.Contains("BOPS_HISTORY/v1", bounded, StringComparison.Ordinal);
        Assert.Contains($"ev1:{state.Id:N}:2", bounded, StringComparison.Ordinal);
        Assert.Contains($"ev1:{state.Id:N}:3", bounded, StringComparison.Ordinal);
        Assert.DoesNotContain("\"kernelCrash\":3", observations, StringComparison.Ordinal);
        Assert.DoesNotContain("\"kind\":\"kernel-live-dump\"", observations, StringComparison.Ordinal);
        Assert.DoesNotContain("pagefile", observations, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("installedDriverVersion", observations, StringComparison.Ordinal);

        // E8 checks disclosure structure only. An intentionally noncompliant model can still emit this prose.
        Assert.Equal(BadAnswer, state.Steps[^1].Observation);
    }
}
