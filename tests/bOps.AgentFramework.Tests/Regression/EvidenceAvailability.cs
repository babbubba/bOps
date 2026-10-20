// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Runtime;

namespace bOps.AgentFramework.Tests.Regression;

/// <summary>
/// How one piece of expected authoritative evidence reached the reasoning model on its final call (AF-05G). The architectural
/// property is that every expected signal is at least one of the three; a recorded-result stub is none of them.
/// </summary>
internal sealed record EvidenceAvailability(
    string Label,
    bool Direct,
    bool Grounded,
    bool Addressable)
{
    /// <summary>Visible, grounded as a typed fact, or exactly readable through <c>runtime.evidence_read</c>.</summary>
    internal bool Available => Direct || Grounded || Addressable;

    internal string Describe() => string.Join('+', new[] { Direct ? "visible" : null, Grounded ? "grounded" : null, Addressable ? "addressable" : null }
        .Where(part => part is not null)) is { Length: > 0 } text ? text : "MISSING";

    /// <summary>The availability of every expected fact of the run's scenario on the final reasoning call.</summary>
    internal static IReadOnlyList<EvidenceAvailability> Of(RegressionRun run)
    {
        var request = run.Model.FinalRequest ?? throw new InvalidOperationException("the run made no final reasoning call");
        var history = string.Join('\n', request.History.Select(turn => turn.Content));
        var grounding = GroundingBlock(request.SystemPrompt);
        var offered = request.AvailableTools.Any(tool => tool.Name == EvidenceRead.ControlFunctionName);
        return [.. run.Scenario.ExpectedFacts.Select(expected => new EvidenceAvailability(
            expected.Label,
            Direct: expected.Markers.Any(marker => history.Contains(marker, StringComparison.Ordinal)),
            Grounded: expected.Markers.Any(marker => grounding.Contains(marker, StringComparison.Ordinal)),
            Addressable: offered && IsExactlyReadable(run, request.SystemPrompt, expected)))];
    }

    private static string GroundingBlock(string systemPrompt)
    {
        var start = systemPrompt.IndexOf(EvidenceGroundingLedger.OpenMarker, StringComparison.Ordinal);
        var end = systemPrompt.IndexOf(EvidenceGroundingLedger.CloseMarker, StringComparison.Ordinal);
        return start >= 0 && end > start ? systemPrompt[start..end] : string.Empty;
    }

    /// <summary>
    /// A persisted step holds the evidence in its raw result, the final prompt states the rule that addresses it, and an actual
    /// <see cref="EvidenceRead.Read"/> at the evidence's offset returns the exact fragment.
    /// </summary>
    private static bool IsExactlyReadable(RegressionRun run, string systemPrompt, ExpectedEvidence expected)
    {
        if (!systemPrompt.Contains(EvidenceIndex.OpenMarker, StringComparison.Ordinal)
            || !systemPrompt.Contains($"addressable as ev1:{run.State.Id:N}:n", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var step in run.State.Steps.Where(EvidenceIndex.IsAddressable))
        {
            var output = step.Result!.Output ?? string.Empty;
            foreach (var marker in expected.Markers)
            {
                var offset = output.IndexOf(marker, StringComparison.Ordinal);
                if (offset < 0)
                {
                    continue;
                }

                var read = EvidenceRead.Read(run.State.Id, run.State.Steps,
                    new EvidenceReadDirective(EvidenceRead.EvidenceId(run.State.Id, step.Index), "result", offset, EvidenceRead.ChunkCharacters));
                if (read.Code == EvidenceReadResultCode.Success && read.Fragment!.Contains(marker, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
