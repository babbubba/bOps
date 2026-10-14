// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Knowledge;
using bOps.Runtime;

namespace bOps.AgentFramework.Tests;

/// <summary>
/// KNOW-M1-D-VERIFY01: the runtime states Part C (ADR-0057 section C.5) itself because it cannot reference <c>bOps.Knowledge</c>.
/// The two wordings, and the two size ceilings the runtime mirrors, must never drift apart.
/// </summary>
public sealed class KnowledgeIndexControlEquivalenceTests
{
    private const string Function = "runtime.knowledge_read";

    private static KnowledgeIndexRendering Rendering(IReadOnlyList<string> refs) => new(ChatTurn.FromUser("x"), refs, 0, new string('a', 64));

    public static TheoryData<string, int, int?, bool> Published() => new()
    {
        { "all-listed", 2, null, false },
        { "all-listed-aggressive", 2, 3, true },
        { "some-omitted", 5, 4, false },
        { "some-omitted-aggressive", 5, 1, true },
        { "budget-spent", 5, 0, false },
        { "budget-spent-aggressive", 5, 0, true },
    };

    [Theory]
    [MemberData(nameof(Published))]
    public void APublishedControlBlockIsTheOneTheRendererProduces(string name, int total, int? reads, bool aggressive)
    {
        var normal = Enumerable.Range(1, Math.Min(total, 4)).Select(i => $"K{i}").ToArray();
        var reduced = normal.Take(2).ToArray();
        var view = new KnowledgeIndexView(
            KnowledgeIndexStatus.Published, null, null, Rendering(normal), Rendering(reduced), [], total, "m1-lexical");

        var runtime = KnowledgeIndexPublication.RenderControl(view, aggressive, Function, reads);
        var host = KnowledgeIndexRenderer.RenderControl(new KnowledgeIndexControlInput(
            KnowledgeIndexStatus.Published, null, Function, aggressive ? reduced : normal, total, reads));

        Assert.True(runtime == host, name);
    }

    [Theory]
    [InlineData(KnowledgeIndexSuppression.NoEntries)]
    [InlineData(KnowledgeIndexSuppression.AuthorityWithdrawn)]
    [InlineData(KnowledgeIndexSuppression.SnapshotUnavailable)]
    [InlineData(KnowledgeIndexSuppression.ProvenanceRejected)]
    [InlineData(KnowledgeIndexSuppression.SessionFault)]
    [InlineData(KnowledgeIndexSuppression.LimitsUnsatisfiable)]
    public void ASuppressedControlBlockIsTheOneTheRendererProduces(KnowledgeIndexSuppression reason)
    {
        var view = new KnowledgeIndexView(KnowledgeIndexStatus.Suppressed, reason, null, null, null, [], 0, string.Empty);

        foreach (int? reads in new int?[] { null, 0, 3 })
        {
            Assert.Equal(
                KnowledgeIndexRenderer.RenderControl(new KnowledgeIndexControlInput(KnowledgeIndexStatus.Suppressed, reason, Function, [], 0, reads)),
                KnowledgeIndexPublication.RenderControl(view, aggressive: false, Function, reads));
        }
    }

    [Fact]
    public void TheCeilingsTheRuntimeMirrorsAreTheHostsDefaults()
    {
        Assert.Equal(KnowledgeIndexPublication.MaxNormalCharacters, KnowledgeIndexLimits.Default.MaxDataChars);
        Assert.Equal(KnowledgeIndexPublication.MaxAggressiveCharacters, KnowledgeIndexLimits.Default.AggressiveMaxDataChars);
    }
}
